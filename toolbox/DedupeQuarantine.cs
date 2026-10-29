using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Web.Script.Serialization;

namespace NeteaseToolbox
{
    internal static class DedupeQuarantine
    {
        public const string ConflictCancel = "cancel";
        public const string ConflictRename = "rename";
        public const string ConflictCustomDirectory = "custom";

        public static string OperationDirectory { get { return Path.Combine(DedupeConversionStore.Root, "operations"); } }

        public static string OperationPath(string operationId)
        {
            if (!IsSafeOperationId(operationId)) throw new ArgumentException("操作 ID 非法。", "operationId");
            return Path.Combine(OperationDirectory, operationId + ".json");
        }

        private static bool IsSafeOperationId(string operationId)
        {
            if (string.IsNullOrWhiteSpace(operationId)) return false;
            if (operationId.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0) return false;
            if (operationId.IndexOf(Path.DirectorySeparatorChar) >= 0) return false;
            if (operationId.IndexOf(Path.AltDirectorySeparatorChar) >= 0) return false;
            if (operationId.Contains("..")) return false;
            return true;
        }

        public static string TrashRootFor(string managedRoot)
        {
            return Path.Combine(DedupeFileSystem.Normalize(managedRoot), DedupeFileSystem.TrashFolderName);
        }

        public static DedupeQuarantineResult Execute(DedupePlan plan, Action<string> progress, string operationId = null)
        {
            var result = new DedupeQuarantineResult { OperationId = "", Status = DedupeStatuses.OperationFailed };
            if (plan == null) { result.Error = "处理计划为空。"; return result; }
            var selectionErrors = DedupePlanner.ValidateSelection(plan);
            if (selectionErrors.Count > 0)
            {
                result.Error = string.Join(Environment.NewLine, selectionErrors.Take(20).ToArray());
                return result;
            }
            List<DedupeOperationItem> items;
            List<string> errors;
            var operation = CreateOperation(plan, operationId, out items, out errors);
            if (errors.Count > 0)
            {
                result.Error = string.Join(Environment.NewLine, errors.Take(20).ToArray());
                return result;
            }
            if (items.Count == 0)
            {
                result.Error = "没有可移入回收站的文件。";
                return result;
            }
            operation.Items = items;
            operation.Status = DedupeStatuses.OperationPending;
            result.OperationId = operation.OperationId;
            try
            {
                SaveOperation(operation);
            }
            catch (Exception ex)
            {
                result.Error = "无法先写入回收站操作日志，已取消处理：" + ex.Message;
                return result;
            }
            operation.Status = DedupeStatuses.OperationInProgress;
            try { SaveOperation(operation); }
            catch (Exception ex)
            {
                operation.Status = DedupeStatuses.OperationFailed;
                operation.Error = "无法更新操作日志，已取消处理：" + ex.Message;
                TrySave(operation);
                result.Error = operation.Error;
                return result;
            }

            foreach (var item in operation.Items)
            {
                if (progress != null) progress("正在移入回收站：" + Path.GetFileName(item.OriginalPath));
                try
                {
                    var source = DedupeFileSystem.Normalize(item.OriginalPath);
                    var root = DedupeFileSystem.Normalize(item.Root);
                    if (!DedupeFileSystem.IsLocalManagedRoot(root) || !DedupeFileSystem.IsUnder(root, source))
                        throw new InvalidOperationException("原始文件不在受管理的本机目录内。");
                    if (DedupeFileSystem.IsInsideTrash(source))
                        throw new InvalidOperationException("不能移动工具箱回收站内的文件。");
                    if (DedupeFileSystem.HasReparsePointInPath(source, root))
                        throw new InvalidOperationException("文件路径包含重解析点，已拒绝处理。");
                    if (!File.Exists(source)) throw new FileNotFoundException("源文件已不存在。", source);
                    var sourceInfo = new FileInfo(source);
                    if (sourceInfo.Length != item.Size)
                        throw new InvalidOperationException("文件大小在扫描后发生变化，请重新扫描。");
                    if (!string.IsNullOrWhiteSpace(item.OriginalModifiedUtc)
                        && !DedupeFileSystem.TimestampMatches(sourceInfo.LastWriteTimeUtc.ToString("o", CultureInfo.InvariantCulture), item.OriginalModifiedUtc, 2))
                        throw new InvalidOperationException("文件修改时间在扫描后发生变化，请重新扫描。");
                    var actualHash = DedupeFileSystem.Sha256(source);
                    if (!string.IsNullOrWhiteSpace(item.Sha256) && !actualHash.Equals(item.Sha256, StringComparison.OrdinalIgnoreCase))
                        throw new InvalidOperationException("文件内容在确认后发生变化，已拒绝处理。");

                    var taskRoot = EnsureTrashTaskDirectory(root, operation.OperationId);
                    var relative = source.Substring(root.Length).TrimStart(Path.DirectorySeparatorChar);
                    var relativeDirectory = Path.GetDirectoryName(relative) ?? "";
                    var targetDirectory = Path.Combine(taskRoot, "files", relativeDirectory);
                    var target = Path.Combine(targetDirectory, item.ItemId + "_" + Path.GetFileName(relative));
                    if (!DedupeFileSystem.IsUnder(taskRoot, target)) throw new InvalidOperationException("回收站目标路径越界。");
                    Directory.CreateDirectory(targetDirectory);
                    if (DedupeFileSystem.HasReparsePointInPath(targetDirectory, taskRoot))
                        throw new InvalidOperationException("回收站目标路径包含重解析点，已拒绝处理。");
                    if (File.Exists(target)) throw new IOException("回收站目标文件已存在，未覆盖。");
                    MoveFile(source, target);
                    item.QuarantinePath = DedupeFileSystem.Normalize(target);
                    item.MovedUtc = DateTime.UtcNow.ToString("o", CultureInfo.InvariantCulture);
                    item.Status = DedupeStatuses.Moved;
                    item.Error = "";
                    result.MovedCount++;
                    result.MovedBytes += item.Size;
                    try
                    {
                        SaveOperation(operation);
                    }
                    catch (Exception journalError)
                    {
                        item.Status = DedupeStatuses.Failed;
                        item.Error = "移动成功但无法更新回收记录：" + journalError.Message;
                        try
                        {
                            MoveFile(item.QuarantinePath, source);
                            item.QuarantinePath = "";
                            item.Error = "写入回收记录失败，已尝试把文件移回原位：" + journalError.Message;
                        }
                        catch (Exception rollbackError)
                        {
                            item.Error = "写入回收记录失败且回滚失败。请手动恢复：" + item.QuarantinePath + "（" + rollbackError.Message + "）";
                        }
                        TrySave(operation);
                        result.MovedCount--;
                        result.MovedBytes -= item.Size;
                        result.FailedCount++;
                        result.Failures.Add(Path.GetFileName(item.OriginalPath) + "：" + item.Error);
                    }
                }
                catch (Exception ex)
                {
                    item.Status = DedupeStatuses.Failed;
                    item.Error = ex.Message;
                    result.FailedCount++;
                    result.Failures.Add(Path.GetFileName(item.OriginalPath) + "：" + ex.Message);
                    TrySave(operation);
                }
            }
            operation.CompletedUtc = DateTime.UtcNow.ToString("o", CultureInfo.InvariantCulture);
            if (result.FailedCount == 0) operation.Status = DedupeStatuses.OperationCompleted;
            else if (result.MovedCount > 0) operation.Status = DedupeStatuses.OperationPartialFailure;
            else operation.Status = DedupeStatuses.OperationFailed;
            TrySave(operation);
            result.Status = operation.Status;
            if (result.FailedCount > 0)
                result.Error = result.MovedCount > 0
                    ? "已移入回收站 " + result.MovedCount + " 个，失败 " + result.FailedCount + " 个。"
                    : "没有文件被移动，失败 " + result.FailedCount + " 个。";
            else
                result.Error = "";
            return result;
        }

        private static DedupeOperation CreateOperation(DedupePlan plan, string operationId, out List<DedupeOperationItem> items, out List<string> errors)
        {
            items = new List<DedupeOperationItem>();
            errors = new List<string>();
            if (string.IsNullOrWhiteSpace(operationId))
                operationId = DateTime.Now.ToString("yyyyMMdd-HHmmss", CultureInfo.InvariantCulture) + "-" + Guid.NewGuid().ToString("N").Substring(0, 8);
            if (!IsSafeOperationId(operationId))
                throw new ArgumentException("操作 ID 包含非法路径字符。", "operationId");
            var operation = new DedupeOperation
            {
                Version = 1,
                OperationId = operationId,
                CreatedUtc = DateTime.UtcNow.ToString("o", CultureInfo.InvariantCulture),
                InputRoot = plan.Options.Root,
                OutputRoot = plan.Options.OutputRoot,
                Status = DedupeStatuses.OperationPending,
                Error = ""
            };
            foreach (var group in plan.Groups)
            {
                var selectedFiles = group.Files.Where(file => file.IsSelected).ToList();
                var selectedAssociated = group.Associated.Where(file => file.IsSelected).ToList();
                if (selectedFiles.Count == 0 && selectedAssociated.Count == 0) continue;
                var keep = group.Files.FirstOrDefault(file => file.IsKeep);
                if (keep == null)
                {
                    errors.Add(group.GroupId + " 没有保留任何原始文件。");
                    continue;
                }
                if (keep.IsSelected)
                {
                    errors.Add(group.GroupId + " 的保留文件也被标记为清理。");
                    continue;
                }
                var keepValidation = ValidateSnapshot(keep.Path, keep.Size, keep.ModifiedUtc, "", true);
                if (keepValidation != null)
                {
                    errors.Add(group.GroupId + " 的保留文件已变化，请重新扫描：" + Path.GetFileName(keep.Path) + "（" + keepValidation + "）");
                    continue;
                }
                foreach (var file in selectedFiles)
                {
                    var root = ResolveRoot(plan, file.Path, DedupeFileKind.Ncm, errors);
                    var validation = ValidateSnapshot(file.Path, file.Size, file.ModifiedUtc, "", true);
                    if (validation != null)
                    {
                        errors.Add(group.GroupId + " 的 NCM 源文件已变化，请重新扫描：" + Path.GetFileName(file.Path) + "（" + validation + "）");
                        continue;
                    }
                    if (root == null) continue;
                    var hash = SafeHash(file.Path);
                    if (string.IsNullOrEmpty(hash))
                    {
                        errors.Add("无法读取文件指纹：" + file.Path);
                        continue;
                    }
                    items.Add(new DedupeOperationItem
                    {
                        ItemId = "I" + (items.Count + 1).ToString("D4", CultureInfo.InvariantCulture),
                        GroupId = group.GroupId,
                        SourcePath = "",
                        OriginalPath = DedupeFileSystem.Normalize(file.Path),
                        Root = root,
                        Kind = DedupeFileKind.Ncm,
                        KindLabel = DedupePlanner.KindLabel(DedupeFileKind.Ncm),
                        Size = file.Size,
                        OriginalModifiedUtc = file.ModifiedUtc,
                        Sha256 = hash,
                        Status = DedupeStatuses.Pending,
                        Error = ""
                    });
                }
                foreach (var file in selectedAssociated)
                {
                    if (!file.Eligible)
                    {
                        errors.Add(group.GroupId + " 的关联文件未通过校验，不能清理：" + file.Path);
                        continue;
                    }
                    var root = ResolveRoot(plan, file.Path, file.Kind, errors);
                    if (root == null) continue;
                    var validation = ValidateSnapshot(file.Path, file.Size, file.ModifiedUtc, file.Sha256, true);
                    if (validation != null)
                    {
                        errors.Add(group.GroupId + " 的关联文件已变化，请重新扫描：" + Path.GetFileName(file.Path) + "（" + validation + "）");
                        continue;
                    }
                    var hash = SafeHash(file.Path);
                    if (string.IsNullOrEmpty(hash)
                        || (!string.IsNullOrWhiteSpace(file.Sha256) && !hash.Equals(file.Sha256, StringComparison.OrdinalIgnoreCase)))
                    {
                        errors.Add("关联文件指纹不一致：" + file.Path);
                        continue;
                    }
                    items.Add(new DedupeOperationItem
                    {
                        ItemId = "I" + (items.Count + 1).ToString("D4", CultureInfo.InvariantCulture),
                        GroupId = group.GroupId,
                        SourcePath = DedupeFileSystem.Normalize(file.SourcePath),
                        OriginalPath = DedupeFileSystem.Normalize(file.Path),
                        Root = root,
                        Kind = file.Kind,
                        KindLabel = DedupePlanner.KindLabel(file.Kind),
                        Size = file.Size,
                        OriginalModifiedUtc = file.ModifiedUtc,
                        Sha256 = hash,
                        Status = DedupeStatuses.Pending,
                        Error = ""
                    });
                }
            }
            return operation;
        }

        private static string ResolveRoot(DedupePlan plan, string path, DedupeFileKind kind, List<string> errors)
        {
            string root = null;
            if (kind == DedupeFileKind.Ncm)
            {
                root = plan.Options.Root;
                if (!DedupeFileSystem.IsUnder(root, path))
                {
                    errors.Add("NCM 源文件不在扫描根目录内：" + path);
                    return null;
                }
            }
            else
            {
                if (!string.IsNullOrWhiteSpace(plan.Options.OutputRoot) && DedupeFileSystem.IsUnder(plan.Options.OutputRoot, path))
                    root = plan.Options.OutputRoot;
                else if (DedupeFileSystem.IsUnder(plan.Options.Root, path))
                    root = plan.Options.Root;
                else
                {
                    errors.Add("关联输出不在受管理的输入或输出目录内：" + path);
                    return null;
                }
            }
            root = DedupeFileSystem.Normalize(root);
            if (!DedupeFileSystem.IsLocalManagedRoot(root))
            {
                errors.Add("受管理目录不是可用的本机磁盘目录：" + root);
                return null;
            }
            if (DedupeFileSystem.HasReparsePointInPath(path, root))
            {
                errors.Add("文件路径包含重解析点，已拒绝处理：" + path);
                return null;
            }
            return root;
        }

        private static string ValidateSnapshot(string path, long size, string modifiedUtc, string sha256, bool mustExist)
        {
            if (string.IsNullOrWhiteSpace(path)) return "路径为空";
            var normalized = DedupeFileSystem.Normalize(path);
            if (DedupeFileSystem.IsInsideTrash(normalized)) return "文件位于回收站内";
            if (!File.Exists(normalized))
                return mustExist ? "文件不存在" : null;
            var info = new FileInfo(normalized);
            if (info.Length != size) return "大小已变化";
            if (!DedupeFileSystem.TimestampMatches(info.LastWriteTimeUtc.ToString("o", CultureInfo.InvariantCulture), modifiedUtc, 2))
                return "修改时间已变化";
            if (!string.IsNullOrWhiteSpace(sha256))
            {
                var actual = SafeHash(normalized);
                if (string.IsNullOrEmpty(actual) || !actual.Equals(sha256, StringComparison.OrdinalIgnoreCase))
                    return "内容指纹已变化";
            }
            return null;
        }

        private static string EnsureTrashTaskDirectory(string root, string operationId)
        {
            var trashRoot = TrashRootFor(root);
            if (!Directory.Exists(trashRoot))
            {
                Directory.CreateDirectory(trashRoot);
                try { File.SetAttributes(trashRoot, File.GetAttributes(trashRoot) | FileAttributes.Hidden); } catch { }
            }
            if (DedupeFileSystem.HasReparsePointInPath(trashRoot, root))
                throw new InvalidOperationException("回收站目录包含重解析点，已拒绝处理。");
            var taskRoot = Path.Combine(trashRoot, operationId);
            if (!DedupeFileSystem.IsUnder(trashRoot, taskRoot)) throw new InvalidOperationException("回收站任务目录越界。");
            Directory.CreateDirectory(taskRoot);
            Directory.CreateDirectory(Path.Combine(taskRoot, "files"));
            if (DedupeFileSystem.HasReparsePointInPath(taskRoot, trashRoot))
                throw new InvalidOperationException("回收站任务目录包含重解析点，已拒绝处理。");
            return DedupeFileSystem.Normalize(taskRoot);
        }

        private static string SafeHash(string path)
        {
            try { return DedupeFileSystem.Sha256(path); }
            catch { return ""; }
        }

        private static void MoveFile(string source, string target)
        {
            if (File.Exists(target)) throw new IOException("目标文件已存在，未覆盖：" + target);
            Directory.CreateDirectory(Path.GetDirectoryName(target));
            try
            {
                File.Move(source, target);
                return;
            }
            catch (IOException)
            {
                var copied = false;
                try
                {
                    File.Copy(source, target, false);
                    copied = true;
                    var sourceHash = DedupeFileSystem.Sha256(source);
                    var targetHash = DedupeFileSystem.Sha256(target);
                    if (!sourceHash.Equals(targetHash, StringComparison.OrdinalIgnoreCase))
                        throw new IOException("跨卷复制后的指纹不一致。");
                    File.Delete(source);
                }
                catch
                {
                    if (copied)
                    {
                        try { if (File.Exists(target)) File.Delete(target); } catch { }
                    }
                    throw;
                }
            }
        }

        public static void SaveOperation(DedupeOperation operation)
        {
            if (operation == null || string.IsNullOrWhiteSpace(operation.OperationId))
                throw new ArgumentException("操作记录为空。");
            AtomicWrite(OperationPath(operation.OperationId), operation);
        }

        private static void TrySave(DedupeOperation operation)
        {
            try { SaveOperation(operation); } catch { }
        }

        private static void AtomicWrite<T>(string path, T value)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path));
            var serializer = new JavaScriptSerializer { MaxJsonLength = int.MaxValue };
            var json = serializer.Serialize(value);
            var temporary = path + ".tmp-" + Guid.NewGuid().ToString("N");
            try
            {
                File.WriteAllText(temporary, json, new UTF8Encoding(false));
                if (File.Exists(path))
                {
                    var backup = path + ".bak";
                    try
                    {
                        File.Replace(temporary, path, backup);
                        try { if (File.Exists(backup)) File.Delete(backup); } catch { }
                    }
                    catch
                    {
                        File.Copy(temporary, path, true);
                        File.Delete(temporary);
                    }
                }
                else
                {
                    File.Move(temporary, path);
                }
            }
            finally
            {
                try { if (File.Exists(temporary)) File.Delete(temporary); } catch { }
            }
        }

        public static List<DedupeOperation> LoadOperations(out List<string> warnings)
        {
            warnings = new List<string>();
            var operations = new List<DedupeOperation>();
            if (!Directory.Exists(OperationDirectory)) return operations;
            var serializer = new JavaScriptSerializer { MaxJsonLength = int.MaxValue };
            foreach (var path in Directory.EnumerateFiles(OperationDirectory, "*.json", SearchOption.TopDirectoryOnly))
            {
                try
                {
                    var operation = serializer.Deserialize<DedupeOperation>(File.ReadAllText(path, Encoding.UTF8));
                    if (operation == null) continue;
                    if (operation.Items == null) operation.Items = new List<DedupeOperationItem>();
                    foreach (var item in operation.Items)
                        if (item != null && string.IsNullOrWhiteSpace(item.Status)) item.Status = DedupeStatuses.Pending;
                    operations.Add(operation);
                }
                catch (Exception ex)
                {
                    warnings.Add("回收站操作记录无法读取，已跳过：" + Path.GetFileName(path) + "（" + ex.Message + "）");
                }
            }
            operations.Sort(delegate(DedupeOperation left, DedupeOperation right)
            {
                return string.Compare(right.CreatedUtc, left.CreatedUtc, StringComparison.Ordinal);
            });
            return operations;
        }

        public static DedupeOperation LoadOperation(string operationId)
        {
            if (!IsSafeOperationId(operationId)) return null;
            var path = OperationPath(operationId);
            if (!File.Exists(path)) return null;
            var serializer = new JavaScriptSerializer { MaxJsonLength = int.MaxValue };
            var operation = serializer.Deserialize<DedupeOperation>(File.ReadAllText(path, Encoding.UTF8));
            if (operation == null) return null;
            if (operation.Items == null) operation.Items = new List<DedupeOperationItem>();
            return operation;
        }

        public static string MarkInterruptedOperations()
        {
            List<string> warnings;
            var operations = LoadOperations(out warnings);
            var interrupted = 0;
            var details = new List<string>();
            foreach (var operation in operations)
            {
                if (operation.Status != DedupeStatuses.OperationPending
                    && operation.Status != DedupeStatuses.OperationInProgress) continue;
                var moved = operation.Items.Count(item => item.Status == DedupeStatuses.Moved);
                operation.Status = DedupeStatuses.OperationInterrupted;
                operation.Error = "程序在操作完成前退出；已移动的文件保留恢复记录。";
                TrySave(operation);
                interrupted++;
                details.Add(operation.OperationId + "（已移动 " + moved + " 个文件）");
            }
            var notice = "";
            if (interrupted > 0)
                notice = "发现 " + interrupted + " 个未完成的回收站操作，已标记为执行中断：" + string.Join("、", details.Take(5).ToArray());
            if (warnings.Count > 0)
                notice = (notice.Length == 0 ? "" : notice + "；") + string.Join("；", warnings.Take(3).ToArray());
            return notice;
        }

        public static DedupeRestoreResult RestoreItem(string operationId, string itemId, string conflictPolicy, string customDirectory, Action<string> progress)
        {
            var result = new DedupeRestoreResult();
            var operation = LoadOperation(operationId);
            if (operation == null) { result.Error = "找不到回收站操作记录。"; return result; }
            var item = operation.Items.FirstOrDefault(entry => entry.ItemId == itemId);
            if (item == null) { result.Error = "找不到待恢复文件记录。"; return result; }
            if (item.Status != DedupeStatuses.Moved && item.Status != DedupeStatuses.RestoreFailed)
            {
                result.Error = "该文件当前状态不能恢复：" + DedupeStatuses.Label(item.Status);
                return result;
            }
            var root = DedupeFileSystem.Normalize(item.Root);
            if (!DedupeFileSystem.IsLocalManagedRoot(root)) { result.Error = "原始目录不是可用的本机磁盘目录。"; return result; }
            var quarantine = DedupeFileSystem.Normalize(item.QuarantinePath);
            var expectedRoot = Path.Combine(TrashRootFor(root), operation.OperationId);
            if (!DedupeFileSystem.IsUnder(expectedRoot, quarantine))
            {
                result.Error = "隔离文件路径不在该操作的回收站目录内，已拒绝恢复。";
                return result;
            }
            if (DedupeFileSystem.HasReparsePointInPath(quarantine, root))
            {
                result.Error = "隔离文件路径包含重解析点，已拒绝恢复。";
                return result;
            }
            if (!File.Exists(quarantine)) { result.Error = "隔离文件已不存在，无法恢复。"; return result; }
            var quarantineInfo = new FileInfo(quarantine);
            if (quarantineInfo.Length != item.Size)
            {
                result.Error = "隔离文件大小与记录不一致，已拒绝恢复。";
                return result;
            }
            if (!string.IsNullOrWhiteSpace(item.Sha256))
            {
                var actual = SafeHash(quarantine);
                if (string.IsNullOrEmpty(actual) || !actual.Equals(item.Sha256, StringComparison.OrdinalIgnoreCase))
                {
                    result.Error = "隔离文件指纹与记录不一致，已拒绝恢复。";
                    return result;
                }
            }
            var original = DedupeFileSystem.Normalize(item.OriginalPath);
            if (!DedupeFileSystem.IsUnder(root, original))
            {
                result.Error = "原始路径不在记录的受管理目录内，已拒绝恢复。";
                return result;
            }
            string target = original;
            var policy = conflictPolicy ?? ConflictCancel;
            if (policy == ConflictCustomDirectory)
            {
                if (string.IsNullOrWhiteSpace(customDirectory) || !Directory.Exists(customDirectory)
                    || !DedupeFileSystem.IsLocalManagedRoot(customDirectory))
                {
                    result.Error = "恢复目标目录不是可用的本机磁盘目录。";
                    return result;
                }
                var customRoot = DedupeFileSystem.Normalize(customDirectory);
                if (DedupeFileSystem.HasReparsePointInPath(customRoot, customRoot))
                {
                    result.Error = "恢复目标目录包含重解析点，已拒绝恢复。";
                    return result;
                }
                target = Path.Combine(customRoot, Path.GetFileName(original));
                if (File.Exists(target)) target = AvailableRestorePath(target);
            }
            else if (File.Exists(target))
            {
                if (policy != ConflictRename)
                {
                    result.Conflict = true;
                    result.Error = "原位置已存在同名文件，未覆盖：" + target;
                    return result;
                }
                target = AvailableRestorePath(target);
            }
            var targetDirectory = Path.GetDirectoryName(target);
            var targetRoot = policy == ConflictCustomDirectory ? DedupeFileSystem.Normalize(customDirectory) : root;
            if (DedupeFileSystem.HasReparsePointInPath(targetDirectory, targetRoot))
            {
                result.Error = "恢复目标目录包含重解析点，已拒绝恢复。";
                return result;
            }
            if (!Directory.Exists(targetDirectory)) Directory.CreateDirectory(targetDirectory);
            try
            {
                if (progress != null) progress("正在恢复：" + Path.GetFileName(item.OriginalPath));
                MoveFile(quarantine, target);
                var restoredHash = SafeHash(target);
                if (!string.IsNullOrWhiteSpace(item.Sha256)
                    && (string.IsNullOrEmpty(restoredHash) || !restoredHash.Equals(item.Sha256, StringComparison.OrdinalIgnoreCase)))
                {
                    try { MoveFile(target, quarantine); } catch { }
                    result.Error = "恢复后的文件指纹不一致，已尝试放回回收站。";
                    item.Status = DedupeStatuses.RestoreFailed;
                    item.Error = result.Error;
                    TrySave(operation);
                    return result;
                }
                item.Status = DedupeStatuses.Restored;
                item.RestoredPath = DedupeFileSystem.Normalize(target);
                item.RestoredUtc = DateTime.UtcNow.ToString("o", CultureInfo.InvariantCulture);
                item.Error = "";
                result.Success = true;
                result.RestoredPath = item.RestoredPath;
                try { SaveOperation(operation); }
                catch (Exception journalError)
                {
                    result.Error = "文件已恢复，但回收记录更新失败：" + journalError.Message;
                }
                return result;
            }
            catch (Exception ex)
            {
                item.Status = DedupeStatuses.RestoreFailed;
                item.Error = ex.Message;
                TrySave(operation);
                result.Error = ex.Message;
                return result;
            }
        }

        public static DedupeQuarantineResult RestoreOperation(string operationId, string conflictPolicy, Action<string> progress)
        {
            var result = new DedupeQuarantineResult { OperationId = operationId, Status = DedupeStatuses.OperationFailed };
            var operation = LoadOperation(operationId);
            if (operation == null) { result.Error = "找不到回收站操作记录。"; return result; }
            foreach (var item in operation.Items.Where(entry => entry.Status == DedupeStatuses.Moved || entry.Status == DedupeStatuses.RestoreFailed).ToList())
            {
                var restored = RestoreItem(operationId, item.ItemId, conflictPolicy, null, progress);
                if (restored.Success) result.MovedCount++;
                else
                {
                    result.FailedCount++;
                    result.Failures.Add(Path.GetFileName(item.OriginalPath) + "：" + restored.Error);
                }
            }
            result.Status = result.FailedCount == 0 ? DedupeStatuses.OperationCompleted : DedupeStatuses.OperationPartialFailure;
            if (result.FailedCount > 0)
                result.Error = "已恢复 " + result.MovedCount + " 个，失败 " + result.FailedCount + " 个。";
            return result;
        }

        public static DedupeQuarantineResult PurgeItems(string operationId, IList<string> itemIds, Action<string> progress)
        {
            var result = new DedupeQuarantineResult { OperationId = operationId, Status = DedupeStatuses.OperationFailed };
            var operation = LoadOperation(operationId);
            if (operation == null) { result.Error = "找不到回收站操作记录。"; return result; }
            if (itemIds == null || itemIds.Count == 0) { result.Error = "没有选择要永久清理的文件。"; return result; }
            foreach (var itemId in itemIds.Distinct(StringComparer.OrdinalIgnoreCase))
            {
                var item = operation.Items.FirstOrDefault(entry => entry.ItemId == itemId);
                if (item == null) { result.FailedCount++; result.Failures.Add(itemId + "：记录不存在"); continue; }
                if (item.Status != DedupeStatuses.Moved)
                {
                    result.FailedCount++;
                    result.Failures.Add(Path.GetFileName(item.OriginalPath) + "：当前状态不能永久清理（" + DedupeStatuses.Label(item.Status) + "）");
                    continue;
                }
                var root = DedupeFileSystem.Normalize(item.Root);
                var quarantine = DedupeFileSystem.Normalize(item.QuarantinePath);
                var expectedRoot = Path.Combine(TrashRootFor(root), operation.OperationId);
                if (!DedupeFileSystem.IsUnder(expectedRoot, quarantine))
                {
                    result.FailedCount++;
                    result.Failures.Add(Path.GetFileName(item.OriginalPath) + "：隔离路径不在本操作的回收站内");
                    continue;
                }
                if (DedupeFileSystem.HasReparsePointInPath(quarantine, root))
                {
                    result.FailedCount++;
                    result.Failures.Add(Path.GetFileName(item.OriginalPath) + "：隔离路径包含重解析点");
                    continue;
                }
                if (!File.Exists(quarantine))
                {
                    item.Status = DedupeStatuses.PurgeFailed;
                    item.Error = "隔离文件已不存在。";
                    TrySave(operation);
                    result.FailedCount++;
                    result.Failures.Add(Path.GetFileName(item.OriginalPath) + "：隔离文件已不存在");
                    continue;
                }
                if (!string.IsNullOrWhiteSpace(item.Sha256))
                {
                    var actual = SafeHash(quarantine);
                    if (string.IsNullOrEmpty(actual) || !actual.Equals(item.Sha256, StringComparison.OrdinalIgnoreCase))
                    {
                        item.Status = DedupeStatuses.PurgeFailed;
                        item.Error = "隔离文件指纹与记录不一致，已拒绝永久清理。";
                        TrySave(operation);
                        result.FailedCount++;
                        result.Failures.Add(Path.GetFileName(item.OriginalPath) + "：隔离文件指纹不一致");
                        continue;
                    }
                }
                try
                {
                    if (progress != null) progress("正在永久清理：" + Path.GetFileName(item.OriginalPath));
                    File.Delete(quarantine);
                    item.Status = DedupeStatuses.Purged;
                    item.Error = "";
                    try { SaveOperation(operation); }
                    catch (Exception journalError)
                    {
                        item.Error = "文件已永久清理，但回收记录更新失败：" + journalError.Message;
                        TrySave(operation);
                    }
                    result.MovedCount++;
                    result.MovedBytes += item.Size;
                }
                catch (Exception ex)
                {
                    item.Status = DedupeStatuses.PurgeFailed;
                    item.Error = ex.Message;
                    TrySave(operation);
                    result.FailedCount++;
                    result.Failures.Add(Path.GetFileName(item.OriginalPath) + "：" + ex.Message);
                }
            }
            result.Status = result.FailedCount == 0 ? DedupeStatuses.OperationCompleted : DedupeStatuses.OperationPartialFailure;
            if (result.FailedCount > 0)
                result.Error = "已永久清理 " + result.MovedCount + " 个，失败 " + result.FailedCount + " 个。";
            return result;
        }

        private static string AvailableRestorePath(string path)
        {
            var directory = Path.GetDirectoryName(path);
            var name = Path.GetFileNameWithoutExtension(path);
            var extension = Path.GetExtension(path);
            for (int index = 1; index < 10000; index++)
            {
                var candidate = Path.Combine(directory, name + " (恢复 " + index.ToString(CultureInfo.InvariantCulture) + ")" + extension);
                if (!File.Exists(candidate)) return candidate;
            }
            throw new IOException("无法生成可用的恢复文件名。");
        }

        public static long RemainingBytes(DedupeOperation operation)
        {
            if (operation == null || operation.Items == null) return 0;
            long total = 0;
            foreach (var item in operation.Items)
                if (item.Status == DedupeStatuses.Moved || item.Status == DedupeStatuses.RestoreFailed || item.Status == DedupeStatuses.PurgeFailed)
                    total += item.Size;
            return total;
        }

        public static List<DedupeOperationItem> RestorableItems(DedupeOperation operation)
        {
            if (operation == null || operation.Items == null) return new List<DedupeOperationItem>();
            return operation.Items.Where(item => item.Status == DedupeStatuses.Moved || item.Status == DedupeStatuses.RestoreFailed).ToList();
        }
    }
}
