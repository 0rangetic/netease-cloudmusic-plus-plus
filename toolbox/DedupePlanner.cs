using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Web.Script.Serialization;

namespace NeteaseToolbox
{
    internal static class DedupeFileSystem
    {
        public const string TrashFolderName = ".NeteaseToolboxTrash";
        private static readonly char[] Separators = new[] { Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar };

        public static string Normalize(string path)
        {
            if (string.IsNullOrWhiteSpace(path)) return "";
            var full = Path.GetFullPath(path);
            var root = Path.GetPathRoot(full);
            if (!string.IsNullOrEmpty(root) && full.Length > root.Length)
                full = full.TrimEnd(Separators);
            return full;
        }

        public static bool IsUnder(string root, string path)
        {
            if (string.IsNullOrWhiteSpace(root) || string.IsNullOrWhiteSpace(path)) return false;
            var normalizedRoot = Normalize(root);
            var normalizedPath = Normalize(path);
            if (normalizedPath.Equals(normalizedRoot, StringComparison.OrdinalIgnoreCase)) return true;
            var prefix = normalizedRoot.EndsWith(Path.DirectorySeparatorChar.ToString(), StringComparison.Ordinal)
                ? normalizedRoot : normalizedRoot + Path.DirectorySeparatorChar;
            return normalizedPath.StartsWith(prefix, StringComparison.OrdinalIgnoreCase);
        }

        public static bool IsInsideTrash(string path)
        {
            if (string.IsNullOrWhiteSpace(path)) return false;
            var parts = Normalize(path).Split(Separators, StringSplitOptions.RemoveEmptyEntries);
            return parts.Any(part => part.Equals(TrashFolderName, StringComparison.OrdinalIgnoreCase));
        }

        public static bool IsLocalManagedRoot(string path)
        {
            if (string.IsNullOrWhiteSpace(path) || !Path.IsPathRooted(path)) return false;
            var full = Normalize(path);
            if (full.StartsWith(@"\\", StringComparison.Ordinal)) return false;
            if (!Directory.Exists(full)) return false;
            try
            {
                var root = Path.GetPathRoot(full);
                if (string.IsNullOrEmpty(root)) return false;
                var drive = new DriveInfo(root);
                return drive.DriveType == DriveType.Fixed || drive.DriveType == DriveType.Removable;
            }
            catch { return false; }
        }

        public static bool HasReparsePointInPath(string path, string stopRoot)
        {
            if (string.IsNullOrWhiteSpace(path) || string.IsNullOrWhiteSpace(stopRoot)) return true;
            var root = Normalize(stopRoot);
            var current = Normalize(path);
            if (!IsUnder(root, current)) return true;
            if (!File.Exists(current) && !Directory.Exists(current))
                current = Path.GetDirectoryName(current);
            while (!string.IsNullOrEmpty(current) && IsUnder(root, current))
            {
                try
                {
                    var attributes = File.GetAttributes(current);
                    if ((attributes & FileAttributes.ReparsePoint) != 0) return true;
                }
                catch { return true; }
                if (current.Equals(root, StringComparison.OrdinalIgnoreCase)) break;
                current = Path.GetDirectoryName(current);
            }
            return false;
        }

        public static string Sha256(string path)
        {
            using (var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
            using (var sha = SHA256.Create())
            {
                var hash = sha.ComputeHash(stream);
                var builder = new StringBuilder(hash.Length * 2);
                foreach (var value in hash) builder.Append(value.ToString("X2", CultureInfo.InvariantCulture));
                return builder.ToString();
            }
        }

        public static List<FileInfo> EnumerateNcmFiles(string root, bool recurse, List<string> errors)
        {
            var result = new List<FileInfo>();
            if (string.IsNullOrWhiteSpace(root) || !Directory.Exists(root)) return result;
            var rootFull = Normalize(root);
            if (Path.GetFileName(rootFull).Equals(TrashFolderName, StringComparison.OrdinalIgnoreCase)) return result;
            var pending = new Stack<string>();
            pending.Push(rootFull);
            while (pending.Count > 0)
            {
                var directory = pending.Pop();
                try
                {
                    var attributes = File.GetAttributes(directory);
                    if ((attributes & FileAttributes.ReparsePoint) != 0)
                    {
                        if (errors != null) errors.Add("已跳过重解析点目录：" + directory);
                        continue;
                    }
                }
                catch (Exception ex)
                {
                    if (errors != null) errors.Add("无法读取目录：" + directory + "（" + ex.Message + "）");
                    continue;
                }
                try
                {
                    foreach (var file in Directory.EnumerateFiles(directory, "*.ncm", SearchOption.TopDirectoryOnly))
                    {
                        try
                        {
                            var attributes = File.GetAttributes(file);
                            if ((attributes & FileAttributes.ReparsePoint) != 0)
                            {
                                if (errors != null) errors.Add("已跳过重解析点文件：" + file);
                                continue;
                            }
                            result.Add(new FileInfo(file));
                        }
                        catch (Exception ex)
                        {
                            if (errors != null) errors.Add("无法读取文件：" + file + "（" + ex.Message + "）");
                        }
                    }
                }
                catch (Exception ex)
                {
                    if (errors != null) errors.Add("无法读取目录：" + directory + "（" + ex.Message + "）");
                }
                if (!recurse) continue;
                try
                {
                    foreach (var child in Directory.EnumerateDirectories(directory))
                    {
                        if (Path.GetFileName(child).Equals(TrashFolderName, StringComparison.OrdinalIgnoreCase)) continue;
                        pending.Push(Normalize(child));
                    }
                }
                catch (Exception ex)
                {
                    if (errors != null) errors.Add("无法读取子目录：" + directory + "（" + ex.Message + "）");
                }
            }
            result.Sort((left, right) => string.Compare(left.FullName, right.FullName, StringComparison.OrdinalIgnoreCase));
            return result;
        }

        public static bool TryParseUtc(string value, out DateTime parsed)
        {
            parsed = DateTime.MinValue;
            if (string.IsNullOrWhiteSpace(value)) return false;
            DateTimeOffset offset;
            if (!DateTimeOffset.TryParse(value, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out offset)) return false;
            parsed = offset.UtcDateTime;
            return true;
        }

        public static bool TimestampMatches(string left, string right, double toleranceSeconds)
        {
            DateTime leftTime;
            DateTime rightTime;
            if (!TryParseUtc(left, out leftTime) || !TryParseUtc(right, out rightTime)) return false;
            return Math.Abs((leftTime - rightTime).TotalSeconds) <= toleranceSeconds;
        }

        public static string ModifiedUtc(FileSystemInfo file)
        {
            return file.LastWriteTimeUtc.ToString("o", CultureInfo.InvariantCulture);
        }

        public static string Quote(string value)
        {
            return "\"" + value.Replace("\"", "\\\"") + "\"";
        }
    }

    internal static class DedupeConversionStore
    {
        internal static string RootOverride;

        public static string Root
        {
            get
            {
                if (!string.IsNullOrWhiteSpace(RootOverride)) return RootOverride;
                return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                    @"NeteaseToolbox\Dedupe");
            }
        }

        public static string ConversionDirectory { get { return Path.Combine(Root, "conversions"); } }

        public static List<ConversionRecord> LoadRecords(out List<string> warnings)
        {
            warnings = new List<string>();
            var records = new List<ConversionRecord>();
            if (!Directory.Exists(ConversionDirectory)) return records;
            var serializer = new JavaScriptSerializer { MaxJsonLength = 64 * 1024 * 1024 };
            foreach (var path in Directory.EnumerateFiles(ConversionDirectory, "*.json", SearchOption.TopDirectoryOnly))
            {
                try
                {
                    var info = new FileInfo(path);
                    if (info.Length > 64L * 1024 * 1024)
                    {
                        warnings.Add("转换来源记录过大，已跳过：" + path);
                        continue;
                    }
                    var ledger = serializer.Deserialize<ConversionLedger>(File.ReadAllText(path, Encoding.UTF8));
                    if (ledger == null || ledger.Records == null) continue;
                    foreach (var record in ledger.Records)
                    {
                        if (record == null) continue;
                        if (record.Outputs == null) record.Outputs = new List<ConversionOutput>();
                        records.Add(record);
                    }
                }
                catch (Exception ex)
                {
                    warnings.Add("转换来源记录无法读取，已跳过：" + Path.GetFileName(path) + "（" + ex.Message + "）");
                }
            }
            return records;
        }
    }

    internal static class DedupeScanner
    {
        public static DedupeScanResult Scan(string scriptPath, string root, bool recurse, CancellationToken cancellationToken)
        {
            if (!File.Exists(scriptPath)) throw new FileNotFoundException("转换脚本不存在。", scriptPath);
            var rootFull = DedupeFileSystem.Normalize(root);
            if (!Directory.Exists(rootFull)) throw new DirectoryNotFoundException("扫描目录不存在：" + rootFull);
            if (!DedupeFileSystem.IsLocalManagedRoot(rootFull)) throw new InvalidOperationException("扫描目录必须位于本机固定磁盘或可移动磁盘，且不能是网络路径。");
            var tempDirectory = ResolveScanTempDirectory();
            Directory.CreateDirectory(tempDirectory);
            var jsonPath = Path.Combine(tempDirectory, "scan-" + Guid.NewGuid().ToString("N") + ".json");
            try
            {
                var arguments = "-NoLogo -NoProfile -ExecutionPolicy Bypass -File " + DedupeFileSystem.Quote(scriptPath)
                    + " -ScanOnly -ScanJsonPath " + DedupeFileSystem.Quote(jsonPath)
                    + " -SourceRoot " + DedupeFileSystem.Quote(rootFull)
                    + (recurse ? " -Recurse" : "");
                var info = new ProcessStartInfo("powershell.exe")
                {
                    Arguments = arguments,
                    WorkingDirectory = Path.GetDirectoryName(scriptPath),
                    UseShellExecute = false,
                    CreateNoWindow = true,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    StandardOutputEncoding = Encoding.UTF8,
                    StandardErrorEncoding = Encoding.UTF8
                };
                var stdout = new StringBuilder();
                var stderr = new StringBuilder();
                var gate = new object();
                using (var process = new Process { StartInfo = info })
                {
                    process.OutputDataReceived += delegate(object sender, DataReceivedEventArgs e)
                    {
                        if (e.Data == null) return;
                        lock (gate) stdout.AppendLine(e.Data);
                    };
                    process.ErrorDataReceived += delegate(object sender, DataReceivedEventArgs e)
                    {
                        if (e.Data == null) return;
                        lock (gate) stderr.AppendLine(e.Data);
                    };
                    process.Start();
                    using (cancellationToken.Register(delegate
                    {
                        try { if (!process.HasExited) process.Kill(); } catch { }
                    }))
                    {
                        process.BeginOutputReadLine();
                        process.BeginErrorReadLine();
                        process.WaitForExit();
                    }
                    if (cancellationToken.IsCancellationRequested) throw new OperationCanceledException(cancellationToken);
                    if (process.ExitCode != 0)
                    {
                        string detail;
                        lock (gate) detail = stderr.ToString().Trim();
                        if (string.IsNullOrWhiteSpace(detail))
                        {
                            lock (gate) detail = stdout.ToString().Trim();
                        }
                        throw new InvalidOperationException("只读扫描失败（退出码 " + process.ExitCode + "）"
                            + (string.IsNullOrWhiteSpace(detail) ? "。" : "：" + detail));
                    }
                }
                if (!File.Exists(jsonPath)) throw new InvalidOperationException("只读扫描没有生成结果文件。");
                var serializer = new JavaScriptSerializer { MaxJsonLength = int.MaxValue };
                var result = serializer.Deserialize<DedupeScanResult>(File.ReadAllText(jsonPath, Encoding.UTF8));
                if (result == null) throw new InvalidDataException("只读扫描结果为空。");
                if (result.Files == null) result.Files = new List<DedupeScanFile>();
                if (result.Errors == null) result.Errors = new List<string>();
                foreach (var file in result.Files)
                {
                    if (file == null) continue;
                    file.Path = DedupeFileSystem.Normalize(file.Path);
                    file.SongId = (file.SongId ?? "").Trim();
                    file.Title = (file.Title ?? "").Trim();
                    file.Artists = (file.Artists ?? "").Trim();
                    file.Format = (file.Format ?? "").Trim().TrimStart('.').ToLowerInvariant();
                    file.Error = file.Error ?? "";
                }
                return result;
            }
            finally
            {
                try { if (File.Exists(jsonPath)) File.Delete(jsonPath); } catch { }
            }
        }

        // 低完整性（沙箱构建或受限令牌）启动时无法写入 %TEMP%，因此优先把扫描结果放在程序自身目录下；
        // 目录不可写（例如安装在只读位置）时退回系统临时目录。
        private static string ResolveScanTempDirectory()
        {
            try
            {
                var localDirectory = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, ".dedupe-tmp");
                Directory.CreateDirectory(localDirectory);
                var probePath = Path.Combine(localDirectory, "probe-" + Guid.NewGuid().ToString("N") + ".tmp");
                using (new FileStream(probePath, FileMode.CreateNew, FileAccess.Write, FileShare.None)) { }
                try { File.Delete(probePath); } catch { }
                return localDirectory;
            }
            catch
            {
                return Path.Combine(Path.GetTempPath(), @"NeteaseToolbox\Dedupe");
            }
        }
    }

    internal static class DedupePlanner
    {
        private static readonly string[] SidecarExtensions = new[]
        {
            ".flac", ".mp3", ".m4a", ".aac", ".wav", ".ape", ".ogg",
            ".lrc", ".cover.jpg", ".cover.png"
        };
        private static readonly HashSet<string> LosslessFormats = new HashSet<string>(
            new[] { "flac", "wav", "ape", "alac", "aiff", "aif", "dff", "dsf" }, StringComparer.OrdinalIgnoreCase);
        private static readonly HashSet<string> LossyFormats = new HashSet<string>(
            new[] { "mp3", "m4a", "aac", "ogg", "opus", "wma" }, StringComparer.OrdinalIgnoreCase);

        private sealed class ConversionIndex
        {
            public readonly Dictionary<string, List<ConversionRecord>> BySource =
                new Dictionary<string, List<ConversionRecord>>(StringComparer.OrdinalIgnoreCase);
            public readonly Dictionary<string, int> OutputReferences =
                new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);

            public ConversionIndex(List<ConversionRecord> records)
            {
                if (records == null) return;
                var latestBySource = new Dictionary<string, ConversionRecord>(StringComparer.OrdinalIgnoreCase);
                foreach (var record in records)
                {
                    if (record == null || string.IsNullOrWhiteSpace(record.SourcePath)) continue;
                    var source = DedupeFileSystem.Normalize(record.SourcePath);
                    ConversionRecord existing;
                    if (!latestBySource.TryGetValue(source, out existing) || IsNewer(record, existing))
                        latestBySource[source] = record;
                }
                foreach (var entry in latestBySource)
                {
                    var record = entry.Value;
                    if (!record.Success) continue;
                    List<ConversionRecord> list;
                    if (!BySource.TryGetValue(entry.Key, out list))
                    {
                        list = new List<ConversionRecord>();
                        BySource.Add(entry.Key, list);
                    }
                    list.Add(record);
                    if (record.Outputs == null) continue;
                    var outputPaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                    foreach (var output in record.Outputs)
                    {
                        if (output == null || string.IsNullOrWhiteSpace(output.Path)) continue;
                        var path = DedupeFileSystem.Normalize(output.Path);
                        if (!outputPaths.Add(path)) continue;
                        int count;
                        OutputReferences.TryGetValue(path, out count);
                        OutputReferences[path] = count + 1;
                    }
                }
            }

            private static bool IsNewer(ConversionRecord candidate, ConversionRecord existing)
            {
                DateTime candidateTime;
                DateTime existingTime;
                var candidateValid = DedupeFileSystem.TryParseUtc(candidate.ConvertedUtc, out candidateTime);
                var existingValid = DedupeFileSystem.TryParseUtc(existing.ConvertedUtc, out existingTime);
                if (candidateValid && existingValid)
                {
                    var comparison = candidateTime.CompareTo(existingTime);
                    if (comparison != 0) return comparison > 0;
                }
                var textual = string.Compare(candidate.ConvertedUtc ?? "", existing.ConvertedUtc ?? "", StringComparison.Ordinal);
                if (textual != 0) return textual > 0;
                return string.Compare(candidate.TaskId ?? "", existing.TaskId ?? "", StringComparison.Ordinal) > 0;
            }
        }

        public static DedupePlan Build(DedupeScanResult scan, DedupePlanOptions options, List<ConversionRecord> conversions, List<string> conversionWarnings)
        {
            if (scan == null) throw new ArgumentNullException("scan");
            if (options == null) throw new ArgumentNullException("options");
            scan.Files = scan.Files ?? new List<DedupeScanFile>();
            scan.Errors = scan.Errors ?? new List<string>();
            options.Root = DedupeFileSystem.Normalize(string.IsNullOrWhiteSpace(options.Root) ? scan.Root : options.Root);
            if (!string.IsNullOrWhiteSpace(options.OutputRoot))
                options.OutputRoot = DedupeFileSystem.Normalize(options.OutputRoot);
            options.CreatedUtc = string.IsNullOrWhiteSpace(options.CreatedUtc)
                ? DateTime.UtcNow.ToString("o", CultureInfo.InvariantCulture) : options.CreatedUtc;

            var plan = new DedupePlan
            {
                Version = 1,
                CreatedUtc = options.CreatedUtc,
                ScanCreatedUtc = scan.CreatedUtc,
                Options = options,
                ScannedFiles = scan.Files.Where(item => item != null).ToList(),
                Warnings = new List<string>()
            };
            plan.ScannedFileCount = plan.ScannedFiles.Count;
            foreach (var error in scan.Errors)
                if (!string.IsNullOrWhiteSpace(error)) plan.Warnings.Add(error);
            if (conversionWarnings != null)
                foreach (var warning in conversionWarnings)
                    if (!string.IsNullOrWhiteSpace(warning)) plan.Warnings.Add(warning);

            var groups = BuildGroups(plan.ScannedFiles, options);
            AssignGroupIds(groups);
            var index = new ConversionIndex(conversions);
            var sourceHashes = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            var sidecarReferences = options.ShowAssociated
                ? BuildSidecarReferenceCounts(plan.ScannedFiles, options) : null;
            foreach (var group in groups)
            {
                ApplyRecommendation(group);
                if (options.ShowAssociated)
                {
                    AttachAssociations(group, index, options, sidecarReferences, sourceHashes);
                    MarkOnlyPlayableCopies(group);
                }
            }
            plan.Groups = groups;
            if (!options.IncludeSuspected)
            {
                var hidden = plan.Groups.Count(group => group.Confidence == DedupeConfidence.Medium);
                if (hidden > 0) plan.Warnings.Add("已按设置隐藏 " + hidden + " 个仅凭歌名和歌手判断的疑似重复组。");
                plan.Groups = plan.Groups.Where(group => group.Confidence != DedupeConfidence.Medium).ToList();
            }
            Recalculate(plan);
            return plan;
        }

        private static List<DedupeGroup> BuildGroups(List<DedupeScanFile> files, DedupePlanOptions options)
        {
            var groups = new List<DedupeGroup>();
            var used = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            var idGroups = files.Where(file => file.MetadataOk && !string.IsNullOrWhiteSpace(file.SongId))
                .GroupBy(file => file.SongId.Trim(), StringComparer.OrdinalIgnoreCase)
                .Where(group => group.Count() > 1)
                .ToList();
            foreach (var idGroup in idGroups)
            {
                var items = idGroup.ToList();
                var titles = DistinctNormalized(items.Select(item => item.Title));
                var artists = DistinctNormalized(items.Select(item => item.Artists));
                var complete = items.All(item => !string.IsNullOrWhiteSpace(item.Title) && !string.IsNullOrWhiteSpace(item.Artists));
                var conflict = !complete || titles.Count > 1 || artists.Count > 1;
                var group = CreateGroup(items, conflict ? DedupeConfidence.Unconfirmed : DedupeConfidence.High,
                    conflict ? "网易云歌曲 ID 相同，但标题、歌手缺失或存在冲突" : "网易云歌曲 ID 相同");
                groups.Add(group);
                foreach (var item in items) used.Add(item.Path);
            }

            var tagGroups = files.Where(file => !used.Contains(file.Path)
                    && !string.IsNullOrWhiteSpace(file.Title) && !string.IsNullOrWhiteSpace(file.Artists))
                .GroupBy(file => NormalizeText(file.Title) + "|" + NormalizeText(file.Artists))
                .Where(group => group.Count() > 1)
                .ToList();
            foreach (var tagGroup in tagGroups)
            {
                var items = tagGroup.ToList();
                var ids = DistinctNormalized(items.Select(item => item.SongId));
                DedupeConfidence confidence;
                string basis;
                if (ids.Count > 1)
                {
                    confidence = DedupeConfidence.Unconfirmed;
                    basis = "歌名和歌手相同，但歌曲 ID 冲突";
                }
                else if (ids.Count == 1)
                {
                    confidence = DedupeConfidence.Unconfirmed;
                    basis = "歌名和歌手相同，但部分文件缺少歌曲 ID";
                }
                else
                {
                    confidence = DedupeConfidence.Medium;
                    basis = "歌名和歌手相同，缺少歌曲 ID";
                }
                groups.Add(CreateGroup(items, confidence, basis));
                foreach (var item in items) used.Add(item.Path);
            }

            var leftover = files.Where(file => !used.Contains(file.Path)).ToList();
            var stemGroups = leftover
                .GroupBy(file => NormalizeText(Path.GetFileNameWithoutExtension(file.Path)))
                .Where(group => group.Count() > 1)
                .ToList();
            foreach (var stemGroup in stemGroups)
            {
                var items = stemGroup.ToList();
                groups.Add(CreateGroup(items, DedupeConfidence.Unconfirmed,
                    "元数据不完整或不可读，仅按文件名显示，需人工检查"));
                foreach (var item in items) used.Add(item.Path);
            }

            groups.Sort(delegate(DedupeGroup left, DedupeGroup right)
            {
                var confidence = right.Confidence.CompareTo(left.Confidence);
                if (confidence != 0) return confidence;
                var title = string.Compare(left.Title, right.Title, StringComparison.CurrentCulture);
                if (title != 0) return title;
                var leftPath = left.Files.Count == 0 ? "" : left.Files[0].Path;
                var rightPath = right.Files.Count == 0 ? "" : right.Files[0].Path;
                return string.Compare(leftPath, rightPath, StringComparison.OrdinalIgnoreCase);
            });
            return groups;
        }

        private static DedupeGroup CreateGroup(List<DedupeScanFile> files, DedupeConfidence confidence, string basis)
        {
            var group = new DedupeGroup { Confidence = confidence, MatchBasis = basis };
            foreach (var file in files.OrderBy(item => item.Path, StringComparer.OrdinalIgnoreCase))
            {
                group.Files.Add(new DedupeGroupFile
                {
                    Path = file.Path,
                    Title = file.Title,
                    Artists = file.Artists,
                    SongId = file.SongId,
                    Format = file.Format,
                    Bitrate = file.Bitrate,
                    Size = file.Size,
                    ModifiedUtc = file.ModifiedUtc,
                    MetadataOk = file.MetadataOk,
                    Quality = AssessQuality(file)
                });
            }
            var best = group.Files.OrderBy(item => item, Comparer<DedupeGroupFile>.Create(CompareQuality)).FirstOrDefault();
            if (best != null)
            {
                group.KeepPath = best.Path;
                group.Title = best.Title;
                group.Artists = best.Artists;
                group.SongId = best.SongId;
            }
            return group;
        }

        private static void AssignGroupIds(List<DedupeGroup> groups)
        {
            for (int index = 0; index < groups.Count; index++)
                groups[index].GroupId = "D" + (index + 1).ToString("D3", CultureInfo.InvariantCulture);
        }

        private static List<string> DistinctNormalized(IEnumerable<string> values)
        {
            var result = new List<string>();
            foreach (var value in values)
            {
                var normalized = NormalizeText(value);
                if (!string.IsNullOrEmpty(normalized) && !result.Contains(normalized)) result.Add(normalized);
            }
            return result;
        }

        public static string NormalizeText(string value)
        {
            if (string.IsNullOrWhiteSpace(value)) return "";
            return Regex.Replace(value.Trim().ToLowerInvariant(), @"[\s\p{P}\p{S}]+", "");
        }

        public static DedupeQualityInfo AssessQuality(DedupeScanFile file)
        {
            if (file == null || !file.MetadataOk)
                return new DedupeQualityInfo { Tier = 0, Category = "unknown", Label = "音质未知", Reason = "元数据不可读，无法比较音质", Format = file == null ? "" : file.Format, Bitrate = file == null ? 0 : file.Bitrate };
            var format = (file.Format ?? "").Trim().TrimStart('.').ToLowerInvariant();
            if (LosslessFormats.Contains(format))
            {
                var label = format.ToUpperInvariant() + " 无损";
                return new DedupeQualityInfo
                {
                    Tier = 2,
                    Category = "lossless",
                    Format = format,
                    Bitrate = file.Bitrate,
                    Label = label,
                    Reason = label
                };
            }
            if (LossyFormats.Contains(format))
            {
                var label = format.ToUpperInvariant() + (file.Bitrate > 0 ? " " + file.Bitrate + " kbps" : "") + " 有损";
                return new DedupeQualityInfo
                {
                    Tier = 1,
                    Category = "lossy",
                    Format = format,
                    Bitrate = file.Bitrate,
                    Label = label,
                    Reason = label
                };
            }
            return new DedupeQualityInfo
            {
                Tier = 0,
                Category = "unknown",
                Format = format,
                Bitrate = file.Bitrate,
                Label = string.IsNullOrEmpty(format) ? "格式未知" : format.ToUpperInvariant() + " 格式未确认",
                Reason = "格式未确认，不能据此推荐删除"
            };
        }

        private static int CompareQuality(DedupeGroupFile left, DedupeGroupFile right)
        {
            var leftQuality = left.Quality ?? new DedupeQualityInfo();
            var rightQuality = right.Quality ?? new DedupeQualityInfo();
            var tier = rightQuality.Tier.CompareTo(leftQuality.Tier);
            if (tier != 0) return tier;
            if (leftQuality.Tier == 1 || leftQuality.Tier == 2)
            {
                var bitrate = rightQuality.Bitrate.CompareTo(leftQuality.Bitrate);
                if (bitrate != 0) return bitrate;
                var rank = right.FormatRankForCompare().CompareTo(left.FormatRankForCompare());
                if (rank != 0) return rank;
            }
            var size = right.Size.CompareTo(left.Size);
            if (size != 0) return size;
            DateTime leftTime, rightTime;
            if (DedupeFileSystem.TryParseUtc(left.ModifiedUtc, out leftTime)
                && DedupeFileSystem.TryParseUtc(right.ModifiedUtc, out rightTime))
            {
                var modified = rightTime.CompareTo(leftTime);
                if (modified != 0) return modified;
            }
            return string.Compare(left.Path, right.Path, StringComparison.OrdinalIgnoreCase);
        }

        public static bool IsClearlyLower(DedupeGroupFile candidate, DedupeGroupFile keep)
        {
            if (candidate == null || keep == null || candidate.Quality == null || keep.Quality == null) return false;
            if (!candidate.Quality.Known || !keep.Quality.Known) return false;
            if (candidate.Quality.Tier < keep.Quality.Tier) return true;
            if (candidate.Quality.Tier == keep.Quality.Tier && keep.Quality.Tier == 1
                && candidate.Quality.Bitrate > 0 && keep.Quality.Bitrate > 0
                && candidate.Quality.Bitrate < keep.Quality.Bitrate * 0.85) return true;
            return false;
        }

        private static void ApplyRecommendation(DedupeGroup group)
        {
            var keep = group.Files.FirstOrDefault(file => file.Path.Equals(group.KeepPath, StringComparison.OrdinalIgnoreCase));
            if (keep == null) keep = group.Files.OrderBy(file => file, Comparer<DedupeGroupFile>.Create(CompareQuality)).FirstOrDefault();
            if (keep == null) return;
            group.KeepPath = keep.Path;
            foreach (var file in group.Files)
            {
                file.IsKeep = ReferenceEquals(file, keep);
                if (group.Confidence == DedupeConfidence.High)
                {
                    file.CanSelect = !file.IsKeep && IsClearlyLower(file, keep);
                    file.IsSelected = file.CanSelect;
                    if (file.IsKeep)
                        file.Recommendation = "建议保留：" + keep.Quality.Label;
                    else if (file.CanSelect)
                        file.Recommendation = "建议清理：音质低于保留项（" + file.Quality.Label + "）";
                    else
                        file.Recommendation = "建议保留：与保留项质量相同或无法确认更低";
                }
                else
                {
                    file.CanSelect = false;
                    file.IsSelected = false;
                    file.Recommendation = group.Confidence == DedupeConfidence.Medium
                        ? "仅疑似重复，默认保留" : "需要人工检查，默认保留";
                }
            }
            if (group.Confidence != DedupeConfidence.High)
            {
                group.Recommendation = group.Confidence == DedupeConfidence.Medium
                    ? "仅凭歌名和歌手判断为疑似重复，默认不清理任何文件。"
                    : "元数据不完整或冲突，默认不清理任何文件。";
                group.Warning = group.Recommendation;
                return;
            }
            var selected = group.Files.Count(file => file.IsSelected);
            if (selected > 0)
            {
                group.Recommendation = "保留 " + keep.Quality.Label + "，建议清理 " + selected + " 个明显较低音质的版本。";
                group.Warning = "";
            }
            else
            {
                group.Recommendation = "无法可靠确认更低音质，建议全部保留。";
                group.Warning = "同组文件质量相同或无法比较，未自动选择任何清理项。";
            }
        }

        public static void SetKeep(DedupeGroup group, string path)
        {
            var keep = group.Files.FirstOrDefault(file => file.Path.Equals(path, StringComparison.OrdinalIgnoreCase));
            if (keep == null) return;
            group.KeepPath = keep.Path;
            foreach (var file in group.Files)
                file.IsKeep = ReferenceEquals(file, keep);
            ApplyRecommendation(group);
            group.SkipGroup = false;
        }

        public static void ResetDefaults(DedupePlan plan)
        {
            foreach (var group in plan.Groups)
            {
                foreach (var file in group.Files)
                {
                    if (file.IsKeep) { file.IsSelected = false; continue; }
                    file.CanSelect = group.Confidence == DedupeConfidence.High
                        && IsClearlyLower(file, group.Files.FirstOrDefault(item => item.IsKeep));
                    file.IsSelected = file.CanSelect;
                }
                foreach (var associated in group.Associated)
                    associated.IsSelected = false;
            }
            Recalculate(plan);
        }

        public static void ClearSelections(DedupePlan plan)
        {
            foreach (var group in plan.Groups)
            {
                foreach (var file in group.Files) file.IsSelected = false;
                foreach (var associated in group.Associated) associated.IsSelected = false;
            }
            Recalculate(plan);
        }

        public static void Recalculate(DedupePlan plan)
        {
            plan.ConfirmedGroupCount = 0;
            plan.SuspectedGroupCount = 0;
            plan.ReviewGroupCount = 0;
            plan.KeepFileCount = 0;
            plan.SelectedSourceCount = 0;
            plan.SelectedAssociatedCount = 0;
            plan.SelectedBytes = 0;
            plan.AssociatedCount = 0;
            plan.AssociatedBytes = 0;
            foreach (var group in plan.Groups)
            {
                if (group.Confidence == DedupeConfidence.High) plan.ConfirmedGroupCount++;
                else if (group.Confidence == DedupeConfidence.Medium) plan.SuspectedGroupCount++;
                else plan.ReviewGroupCount++;

                var keep = group.Files.FirstOrDefault(file => file.IsKeep)
                    ?? group.Files.FirstOrDefault(file => file.Path.Equals(group.KeepPath, StringComparison.OrdinalIgnoreCase));
                if (keep == null && group.Files.Count > 0)
                    keep = group.Files.OrderBy(file => file, Comparer<DedupeGroupFile>.Create(CompareQuality)).First();
                if (keep != null)
                {
                    group.KeepPath = keep.Path;
                    foreach (var file in group.Files) file.IsKeep = ReferenceEquals(file, keep);
                }
                var selectedSources = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                foreach (var file in group.Files)
                {
                    if (file.IsKeep) file.IsSelected = false;
                    if (file.IsSelected) selectedSources.Add(DedupeFileSystem.Normalize(file.Path));
                    if (!file.IsSelected) plan.KeepFileCount++;
                    if (!file.IsSelected) continue;
                    plan.SelectedSourceCount++;
                    plan.SelectedBytes += file.Size;
                }
                foreach (var associated in group.Associated)
                {
                    plan.AssociatedCount++;
                    plan.AssociatedBytes += associated.Size;
                    associated.Eligible = associated.Eligible && associated.Verified;
                    associated.CanSelect = associated.Eligible && selectedSources.Contains(DedupeFileSystem.Normalize(associated.SourcePath));
                    if (!associated.CanSelect) associated.IsSelected = false;
                    if (!associated.IsSelected) continue;
                    plan.SelectedAssociatedCount++;
                    plan.SelectedBytes += associated.Size;
                }
            }
        }

        public static List<string> ValidateSelection(DedupePlan plan)
        {
            var errors = new List<string>();
            var selectedPaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            if (!plan.HasPendingSelection) errors.Add("没有选择任何待处理文件。");
            foreach (var group in plan.Groups)
            {
                var selectedFiles = group.Files.Where(file => file.IsSelected).ToList();
                var selectedAssociated = group.Associated.Where(file => file.IsSelected).ToList();
                if (selectedFiles.Count == 0 && selectedAssociated.Count == 0) continue;
                if (group.SkipGroup) errors.Add(group.GroupId + " 已整组跳过，但仍包含待处理文件。");
                var keep = group.Files.FirstOrDefault(file => file.IsKeep);
                if (keep == null) errors.Add(group.GroupId + " 没有保留任何原始候选文件。");
                else if (keep.IsSelected) errors.Add(group.GroupId + " 的保留文件同时被标记为清理。");
                foreach (var file in selectedFiles)
                {
                    if (!file.CanSelect) errors.Add(group.GroupId + " 中的文件不允许清理：" + Path.GetFileName(file.Path));
                    var normalized = DedupeFileSystem.Normalize(file.Path);
                    if (!selectedPaths.Add(normalized)) errors.Add("同一文件被重复选择：" + file.Path);
                }
                foreach (var file in selectedAssociated)
                {
                    if (!file.CanSelect || !file.Eligible) errors.Add(group.GroupId + " 中的关联文件未通过校验，不能清理：" + file.Path);
                    if (!selectedFiles.Any(source => DedupeFileSystem.Normalize(source.Path)
                        .Equals(DedupeFileSystem.Normalize(file.SourcePath), StringComparison.OrdinalIgnoreCase)))
                        errors.Add(group.GroupId + " 的关联文件没有同时选择其源 NCM：" + Path.GetFileName(file.Path));
                    var normalized = DedupeFileSystem.Normalize(file.Path);
                    if (!selectedPaths.Add(normalized)) errors.Add("同一文件被重复选择：" + file.Path);
                }
            }
            return errors;
        }

        public static string ConfidenceLabel(DedupeConfidence confidence)
        {
            switch (confidence)
            {
                case DedupeConfidence.High: return "高可信";
                case DedupeConfidence.Medium: return "疑似";
                default: return "需人工检查";
            }
        }

        public static string KindLabel(DedupeFileKind kind)
        {
            switch (kind)
            {
                case DedupeFileKind.Audio: return "转换音频";
                case DedupeFileKind.Lyric: return "歌词";
                case DedupeFileKind.Cover: return "封面";
                default: return "NCM 源文件";
            }
        }

        private static DedupeFileKind ParseKind(string value)
        {
            if (string.Equals(value, "Audio", StringComparison.OrdinalIgnoreCase)) return DedupeFileKind.Audio;
            if (string.Equals(value, "Lyric", StringComparison.OrdinalIgnoreCase)) return DedupeFileKind.Lyric;
            if (string.Equals(value, "Cover", StringComparison.OrdinalIgnoreCase)) return DedupeFileKind.Cover;
            return DedupeFileKind.Audio;
        }

        private static Dictionary<string, int> BuildSidecarReferenceCounts(List<DedupeScanFile> files, DedupePlanOptions options)
        {
            var counts = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
            foreach (var file in files)
            {
                var paths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                foreach (var directory in ExpectedOutputDirectories(file.Path, options))
                {
                    var stem = Path.GetFileNameWithoutExtension(file.Path);
                    foreach (var extension in SidecarExtensions)
                    {
                        var candidate = Path.Combine(directory, stem + extension);
                        if (File.Exists(candidate)) paths.Add(DedupeFileSystem.Normalize(candidate));
                    }
                }
                foreach (var path in paths)
                {
                    int count;
                    counts.TryGetValue(path, out count);
                    counts[path] = count + 1;
                }
            }
            return counts;
        }

        private static IEnumerable<string> ExpectedOutputDirectories(string sourcePath, DedupePlanOptions options)
        {
            var sourceDirectory = Path.GetDirectoryName(DedupeFileSystem.Normalize(sourcePath));
            if (string.IsNullOrEmpty(sourceDirectory)) yield break;
            yield return Path.Combine(sourceDirectory, "unlock");
            if (string.IsNullOrWhiteSpace(options.OutputRoot)) yield break;
            var root = DedupeFileSystem.Normalize(options.Root);
            var outputRoot = DedupeFileSystem.Normalize(options.OutputRoot);
            var normalizedSource = DedupeFileSystem.Normalize(sourceDirectory);
            if (normalizedSource.Equals(root, StringComparison.OrdinalIgnoreCase))
                yield return outputRoot;
            else if (DedupeFileSystem.IsUnder(root, normalizedSource))
                yield return Path.Combine(outputRoot, normalizedSource.Substring(root.Length).TrimStart(Path.DirectorySeparatorChar));
            else
                yield return outputRoot;
        }

        private static void AttachAssociations(DedupeGroup group, ConversionIndex index, DedupePlanOptions options,
            Dictionary<string, int> sidecarReferences, Dictionary<string, string> sourceHashes)
        {
            foreach (var candidate in group.Files)
            {
                var added = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                List<ConversionRecord> records;
                if (index.BySource.TryGetValue(DedupeFileSystem.Normalize(candidate.Path), out records))
                {
                    foreach (var record in records)
                    {
                        var sourceTrusted = VerifySource(candidate, record, options, sourceHashes);
                        foreach (var output in record.Outputs)
                        {
                            if (output == null || string.IsNullOrWhiteSpace(output.Path)) continue;
                            var outputPath = DedupeFileSystem.Normalize(output.Path);
                            if (!added.Add(outputPath)) continue;
                            var fileInfo = new FileInfo(outputPath);
                            var exists = fileInfo.Exists && (fileInfo.Attributes & FileAttributes.ReparsePoint) == 0;
                            var outputMatches = exists && fileInfo.Length == output.Size
                                && DedupeFileSystem.TimestampMatches(fileInfo.LastWriteTimeUtc.ToString("o", CultureInfo.InvariantCulture), output.ModifiedUtc, 2);
                            if (outputMatches && options.VerifyAssociatedHashes && !string.IsNullOrWhiteSpace(output.Sha256))
                            {
                                try { outputMatches = DedupeFileSystem.Sha256(outputPath).Equals(output.Sha256, StringComparison.OrdinalIgnoreCase); }
                                catch { outputMatches = false; }
                            }
                            int references;
                            index.OutputReferences.TryGetValue(outputPath, out references);
                            var managed = IsManagedOutput(outputPath, options);
                            var verified = sourceTrusted && outputMatches && references == 1 && managed && exists;
                            var associated = new DedupeAssociatedFile
                            {
                                Path = outputPath,
                                SourcePath = candidate.Path,
                                GroupId = group.GroupId,
                                Kind = ParseKind(output.Kind),
                                KindLabel = KindLabel(ParseKind(output.Kind)),
                                Confidence = verified ? DedupeConfidence.High : DedupeConfidence.Medium,
                                FromLedger = true,
                                Eligible = verified,
                                CanSelect = false,
                                IsSelected = false,
                                Basis = verified ? "转换来源记录和输出指纹一致" : "转换来源记录校验未通过，默认保留",
                                Size = exists ? fileInfo.Length : output.Size,
                                ModifiedUtc = exists ? fileInfo.LastWriteTimeUtc.ToString("o", CultureInfo.InvariantCulture) : output.ModifiedUtc,
                                Sha256 = output.Sha256
                            };
                            if (!sourceTrusted) associated.Warning = "源 NCM 的大小、修改时间或指纹与转换记录不一致。";
                            else if (!exists) associated.Warning = "来源记录中的输出文件已不存在。";
                            else if (!outputMatches) associated.Warning = "输出文件已被修改或替换，默认保留。";
                            else if (references != 1) associated.Warning = "该输出被多个转换记录引用，无法确认唯一归属。";
                            else if (!managed) associated.Warning = "输出不在受管理的输入或输出目录内，不能移入回收站。";
                            AddAssociated(group, associated);
                        }
                    }
                }
                foreach (var directory in ExpectedOutputDirectories(candidate.Path, options))
                {
                    var stem = Path.GetFileNameWithoutExtension(candidate.Path);
                    foreach (var extension in SidecarExtensions)
                    {
                        var path = Path.Combine(directory, stem + extension);
                        if (!File.Exists(path)) continue;
                        var normalized = DedupeFileSystem.Normalize(path);
                        if (!added.Add(normalized)) continue;
                        var fileInfo = new FileInfo(normalized);
                        if ((fileInfo.Attributes & FileAttributes.ReparsePoint) != 0) continue;
                        int references;
                        sidecarReferences.TryGetValue(normalized, out references);
                        int ledgerReferences;
                        index.OutputReferences.TryGetValue(normalized, out ledgerReferences);
                        var confidence = DedupeConfidence.Medium;
                        var warning = "";
                        if (references > 1)
                        {
                            confidence = DedupeConfidence.Unconfirmed;
                            warning = "多个 NCM 可能对应这个同名输出，未自动纳入清理。";
                        }
                        if (ledgerReferences > 0)
                        {
                            confidence = DedupeConfidence.Unconfirmed;
                            warning = "该文件出现在其他转换来源记录中，不能仅按同名推断归属。";
                        }
                        var kind = extension.EndsWith(".lrc", StringComparison.OrdinalIgnoreCase) ? DedupeFileKind.Lyric
                            : extension.StartsWith(".cover.", StringComparison.OrdinalIgnoreCase) ? DedupeFileKind.Cover
                            : DedupeFileKind.Audio;
                        AddAssociated(group, new DedupeAssociatedFile
                        {
                            Path = normalized,
                            SourcePath = candidate.Path,
                            GroupId = group.GroupId,
                            Kind = kind,
                            KindLabel = KindLabel(kind),
                            Confidence = confidence,
                            FromLedger = false,
                            Eligible = false,
                            CanSelect = false,
                            IsSelected = false,
                            Basis = "仅按同名和预期目录推测，缺少转换来源记录",
                            Warning = warning,
                            Size = fileInfo.Length,
                            ModifiedUtc = fileInfo.LastWriteTimeUtc.ToString("o", CultureInfo.InvariantCulture),
                            Sha256 = ""
                        });
                    }
                }
            }
        }

        private static void AddAssociated(DedupeGroup group, DedupeAssociatedFile file)
        {
            var existing = group.Associated.FirstOrDefault(item =>
                DedupeFileSystem.Normalize(item.Path).Equals(DedupeFileSystem.Normalize(file.Path), StringComparison.OrdinalIgnoreCase));
            if (existing == null)
            {
                group.Associated.Add(file);
                return;
            }
            existing.Confidence = DedupeConfidence.Unconfirmed;
            existing.Eligible = false;
            existing.CanSelect = false;
            existing.IsSelected = false;
            existing.Warning = "同一输出被同组多个候选歌曲引用，归属不明确，默认保留。";
        }

        private static bool VerifySource(DedupeGroupFile candidate, ConversionRecord record, DedupePlanOptions options,
            Dictionary<string, string> sourceHashes)
        {
            if (record == null || !record.Success) return false;
            if (string.IsNullOrWhiteSpace(record.SourceSha256)) return false;
            if (record.SourceSize != candidate.Size) return false;
            if (!DedupeFileSystem.TimestampMatches(record.SourceModifiedUtc, candidate.ModifiedUtc, 2)) return false;
            if (!string.IsNullOrWhiteSpace(record.SourceSongId) && !string.IsNullOrWhiteSpace(candidate.SongId)
                && !record.SourceSongId.Trim().Equals(candidate.SongId.Trim(), StringComparison.OrdinalIgnoreCase)) return false;
            if (!options.VerifyAssociatedHashes) return true;
            string actual;
            var key = DedupeFileSystem.Normalize(candidate.Path);
            if (!sourceHashes.TryGetValue(key, out actual))
            {
                try { actual = DedupeFileSystem.Sha256(candidate.Path); }
                catch { actual = ""; }
                sourceHashes[key] = actual;
            }
            return !string.IsNullOrEmpty(actual) && actual.Equals(record.SourceSha256, StringComparison.OrdinalIgnoreCase);
        }

        private static bool IsManagedOutput(string path, DedupePlanOptions options)
        {
            if (DedupeFileSystem.IsInsideTrash(path)) return false;
            if (DedupeFileSystem.IsUnder(options.Root, path)) return true;
            return !string.IsNullOrWhiteSpace(options.OutputRoot) && DedupeFileSystem.IsUnder(options.OutputRoot, path);
        }

        private static void MarkOnlyPlayableCopies(DedupeGroup group)
        {
            var audioOutputs = group.Associated.Where(file => file.Kind == DedupeFileKind.Audio).ToList();
            if (audioOutputs.Count != 1) return;
            var audio = audioOutputs[0];
            audio.IsOnlyPlayableCopy = true;
            var warning = "这可能是该歌曲唯一的可播放副本，默认保留。";
            audio.Warning = string.IsNullOrWhiteSpace(audio.Warning) ? warning : audio.Warning + " " + warning;
        }

        public static void ExportCsv(DedupePlan plan, string path)
        {
            var builder = new StringBuilder();
            builder.AppendLine("组,置信度,匹配依据,歌曲,歌手,歌曲ID,文件,类型,音质,大小,清理选择,保留,建议,路径");
            foreach (var group in plan.Groups)
            {
                foreach (var file in group.Files)
                {
                    builder.AppendLine(string.Join(",", new[]
                    {
                        Csv(group.GroupId), Csv(ConfidenceLabel(group.Confidence)), Csv(group.MatchBasis),
                        Csv(file.Title), Csv(file.Artists), Csv(file.SongId), Csv(Path.GetFileName(file.Path)),
                        Csv("NCM 源文件"), Csv(file.Quality.Label), Csv(file.Size.ToString(CultureInfo.InvariantCulture)),
                        Csv(file.IsSelected ? "是" : "否"), Csv(file.IsKeep ? "是" : "否"),
                        Csv(file.Recommendation), Csv(file.Path)
                    }));
                }
                foreach (var file in group.Associated)
                {
                    builder.AppendLine(string.Join(",", new[]
                    {
                        Csv(group.GroupId), Csv(ConfidenceLabel(file.Confidence)), Csv(file.Basis),
                        Csv(group.Title), Csv(group.Artists), Csv(group.SongId), Csv(Path.GetFileName(file.Path)),
                        Csv(file.KindLabel), Csv(""), Csv(file.Size.ToString(CultureInfo.InvariantCulture)),
                        Csv(file.IsSelected ? "是" : "否"), Csv(""), Csv(file.Warning),
                        Csv(file.Path)
                    }));
                }
            }
            File.WriteAllText(path, builder.ToString(), new UTF8Encoding(true));
        }

        private static string Csv(string value)
        {
            value = value ?? "";
            return "\"" + value.Replace("\"", "\"\"") + "\"";
        }
    }

    internal static class DedupeGroupFileExtensions
    {
        public static int FormatRankForCompare(this DedupeGroupFile file)
        {
            var quality = file == null ? null : file.Quality;
            if (quality == null) return 0;
            switch (quality.Format)
            {
                case "flac": return 5;
                case "wav": return 4;
                case "ape": return 4;
                case "alac": return 4;
                case "m4a": return 3;
                case "aac": return 3;
                case "ogg": return 2;
                case "mp3": return 1;
                default: return 0;
            }
        }
    }
}
