using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using NeteaseToolbox;

internal static class DedupeValidation
{
    private static int checks;
    private static string sandbox;

    private static void Check(bool condition, string message)
    {
        if (!condition) throw new Exception("FAIL " + message);
        checks++;
        Console.WriteLine("PASS " + message);
    }

    private static string Root(string name)
    {
        var path = Path.Combine(sandbox, name);
        Directory.CreateDirectory(path);
        return path;
    }

    private static void WriteFile(string path, string content)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path));
        File.WriteAllText(path, content, new UTF8Encoding(false));
    }

    private static DedupeScanFile ScanFile(string path, string id, string title, string artist,
        string format, long bitrate, string modifiedUtc, bool metadataOk = true, string error = "")
    {
        return new DedupeScanFile
        {
            Path = path,
            SongId = id,
            Title = title,
            Artists = artist,
            Format = format,
            Bitrate = bitrate,
            Size = 1024,
            ModifiedUtc = modifiedUtc,
            MetadataOk = metadataOk,
            Error = error
        };
    }

    private static DedupeScanFile ActualFile(string path, string id, string title, string artist, string format, long bitrate)
    {
        var info = new FileInfo(path);
        return new DedupeScanFile
        {
            Path = path,
            SongId = id,
            Title = title,
            Artists = artist,
            Format = format,
            Bitrate = bitrate,
            Size = info.Length,
            ModifiedUtc = info.LastWriteTimeUtc.ToString("o", CultureInfo.InvariantCulture),
            MetadataOk = true,
            Error = ""
        };
    }

    private static DedupeScanResult Scan(string root, params DedupeScanFile[] files)
    {
        return new DedupeScanResult
        {
            Version = 1,
            Root = root,
            CreatedUtc = DateTime.UtcNow.ToString("o", CultureInfo.InvariantCulture),
            Recurse = true,
            Files = files.ToList(),
            Errors = new List<string>()
        };
    }

    private static DedupePlanOptions Options(string root, string outputRoot = null, bool showAssociated = true, bool includeSuspected = true)
    {
        return new DedupePlanOptions
        {
            Root = root,
            OutputRoot = outputRoot,
            Recurse = true,
            ShowAssociated = showAssociated,
            IncludeSuspected = includeSuspected,
            VerifyAssociatedHashes = true
        };
    }

    private static DedupePlan Build(DedupeScanResult scan, DedupePlanOptions options, List<ConversionRecord> records = null)
    {
        return DedupePlanner.Build(scan, options, records ?? new List<ConversionRecord>(), new List<string>());
    }

    private static ConversionRecord Ledger(string sourcePath, string outputPath, string kind, string songId = "1", string error = null)
    {
        var source = new FileInfo(sourcePath);
        var output = new FileInfo(outputPath);
        return new ConversionRecord
        {
            Version = 1,
            TaskId = "test",
            SourcePath = sourcePath,
            SourceSongId = songId,
            SourceSize = source.Length,
            SourceModifiedUtc = source.LastWriteTimeUtc.ToString("o", CultureInfo.InvariantCulture),
            SourceSha256 = DedupeFileSystem.Sha256(sourcePath),
            Success = string.IsNullOrEmpty(error),
            Error = error,
            ConvertedUtc = DateTime.UtcNow.ToString("o", CultureInfo.InvariantCulture),
            Outputs = new List<ConversionOutput>
            {
                new ConversionOutput
                {
                    Path = outputPath,
                    Kind = kind,
                    Size = output.Length,
                    ModifiedUtc = output.LastWriteTimeUtc.ToString("o", CultureInfo.InvariantCulture),
                    Sha256 = DedupeFileSystem.Sha256(outputPath),
                    GeneratedUtc = DateTime.UtcNow.ToString("o", CultureInfo.InvariantCulture)
                }
            }
        };
    }

    [STAThread]
    private static int Main()
    {
        sandbox = Path.Combine(Path.GetTempPath(), "ncm-dedupe-validation-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(sandbox);
        DedupeConversionStore.RootOverride = Path.Combine(sandbox, "appdata");
        try
        {
            RunPlannerTests();
            RunAssociationTests();
            RunQuarantineTests();
            Console.WriteLine("Dedupe validation passed: " + checks + " checks.");
            return 0;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine(ex.Message);
            Console.Error.WriteLine(ex.ToString());
            return 1;
        }
        finally
        {
            DedupeConversionStore.RootOverride = null;
            try { Directory.Delete(sandbox, true); } catch { }
        }
    }

    private static void RunPlannerTests()
    {
        var root = Root("planner");
        var when = "2026-10-24T09:00:00.0000000Z";

        var scan = Scan(root,
            ScanFile(Path.Combine(root, "A", "song_HQ.ncm"), "1", "Song", "Artist", "flac", 900000, when),
            ScanFile(Path.Combine(root, "A", "song_LQ.ncm"), "1", "Song", "Artist", "mp3", 320000, when));
        var plan = Build(scan, Options(root, null, false));
        Check(plan.Groups.Count == 1 && plan.Groups[0].Confidence == DedupeConfidence.High, "same song ID forms a high-confidence group");
        var keep = plan.Groups[0].Files.First(item => item.IsKeep);
        var loser = plan.Groups[0].Files.First(item => item.IsSelected);
        Check(keep.Format == "flac" && loser.Format == "mp3", "lossless file is kept and lossy file is selected");
        Check(!keep.IsSelected, "keep file is never selected for cleanup");
        Check(plan.SelectedSourceCount == 1, "only the lower-quality source is selected by default");
        Check(DedupePlanner.ValidateSelection(plan).Count == 0, "default selection passes validation");

        plan = Build(Scan(root,
            ScanFile(Path.Combine(root, "B", "one.ncm"), "1", "Same Title", "Artist", "mp3", 320000, when),
            ScanFile(Path.Combine(root, "B", "two.ncm"), "2", "Same Title", "Artist", "mp3", 320000, when)), Options(root, null, false));
        Check(plan.Groups.Count == 1 && plan.Groups[0].Confidence == DedupeConfidence.Unconfirmed, "different song IDs with the same title are not high confidence");
        Check(plan.SelectedSourceCount == 0, "different song IDs are never selected automatically");

        plan = Build(Scan(root,
            ScanFile(Path.Combine(root, "C", "one.ncm"), "", "Same Title", "Artist A", "mp3", 320000, when),
            ScanFile(Path.Combine(root, "C", "two.ncm"), "", "Same Title", "Artist B", "mp3", 320000, when)), Options(root));
        Check(plan.Groups.Count == 0, "same title with different artists is not grouped");

        plan = Build(Scan(root,
            ScanFile(Path.Combine(root, "D", "live.ncm"), "", "Song (Live)", "Artist", "mp3", 320000, when),
            ScanFile(Path.Combine(root, "D", "studio.ncm"), "", "Song", "Artist", "mp3", 320000, when)), Options(root));
        Check(plan.Groups.Count == 0, "live and studio versions are not grouped by similar names");

        plan = Build(Scan(root,
            ScanFile(Path.Combine(root, "E", "broken.ncm"), "", "broken", "", "", 0, when, false, "damaged"),
            ScanFile(Path.Combine(root, "E", "sub", "broken.ncm"), "", "broken", "", "", 0, when, false, "damaged")), Options(root));
        Check(plan.Groups.Count == 1 && plan.Groups[0].Confidence == DedupeConfidence.Unconfirmed, "unreadable metadata is a review group");
        Check(plan.SelectedSourceCount == 0, "unreadable metadata is never selected automatically");

        plan = Build(Scan(root,
            ScanFile(Path.Combine(root, "F", "one.ncm"), "7", "Equal", "Artist", "mp3", 320000, when),
            ScanFile(Path.Combine(root, "F", "two.ncm"), "7", "Equal", "Artist", "mp3", 320000, when)), Options(root, null, false));
        Check(plan.Groups.Count == 1 && plan.Groups[0].Confidence == DedupeConfidence.High, "equal quality duplicates are still grouped");
        Check(plan.SelectedSourceCount == 0 && plan.KeepFileCount == 2, "equal quality files are all kept");

        plan = Build(Scan(root,
            ScanFile(Path.Combine(root, "G", "one.ncm"), "", "No Id", "Artist", "mp3", 320000, when),
            ScanFile(Path.Combine(root, "G", "two.ncm"), "", "No Id", "Artist", "flac", 900000, when)), Options(root));
        Check(plan.Groups.Count == 1 && plan.Groups[0].Confidence == DedupeConfidence.Medium, "missing IDs form a suspected group");
        Check(plan.SelectedSourceCount == 0, "suspected groups are not selected automatically");
        var hidden = Build(Scan(root,
            ScanFile(Path.Combine(root, "G2", "one.ncm"), "", "No Id", "Artist", "mp3", 320000, when),
            ScanFile(Path.Combine(root, "G2", "two.ncm"), "", "No Id", "Artist", "flac", 900000, when)), Options(root, null, true, false));
        Check(hidden.Groups.Count == 0, "suspected groups can be hidden by scan option");

        plan = Build(Scan(root,
            ScanFile(Path.Combine(root, "H", "one.ncm"), "1", "Conflict", "Artist", "flac", 900000, when),
            ScanFile(Path.Combine(root, "H", "two.ncm"), "2", "Conflict", "Artist", "mp3", 320000, when)), Options(root));
        Check(plan.Groups.Count == 1 && plan.Groups[0].Confidence == DedupeConfidence.Unconfirmed, "conflicting IDs remain a review group");

        var original = Path.Combine(root, "I", "source.ncm");
        WriteFile(original, "unchanged");
        var before = File.ReadAllText(original);
        Build(Scan(root, ScanFile(original, "1", "Song", "Artist", "flac", 900000, when)), Options(root));
        Check(File.ReadAllText(original) == before, "building a plan does not modify source files");
    }

    private static void RunAssociationTests()
    {
        var root = Root("assoc");
        var source = Path.Combine(root, "A", "song.ncm");
        var highQuality = Path.Combine(root, "A", "song-hq.ncm");
        var output = Path.Combine(root, "A", "unlock", "song.flac");
        WriteFile(source, "ncm-content");
        WriteFile(highQuality, "hq-content");
        WriteFile(output, "flac-content");
        var scan = Scan(root,
            ActualFile(source, "1", "Song", "Artist", "mp3", 320000),
            ActualFile(highQuality, "1", "Song", "Artist", "flac", 900000));
        var plan = Build(scan, Options(root), new List<ConversionRecord> { Ledger(source, output, "Audio") });
        var group = plan.Groups.Single();
        Check(group.Associated.Count == 1, "ledger output is attached to the source group");
        var associated = group.Associated[0];
        Check(associated.Verified && associated.Eligible, "unmodified ledger output is verified");
        Check(!associated.IsSelected, "associated outputs are not selected by default");
        Check(associated.CanSelect, "verified output becomes selectable when its source is selected");
        Check(associated.IsOnlyPlayableCopy, "single audio output is marked as the only playable copy");
        associated.IsSelected = true;
        DedupePlanner.Recalculate(plan);
        Check(DedupePlanner.ValidateSelection(plan).Count == 0, "selecting a verified output with its source passes validation");

        var modifiedRecord = Ledger(source, output, "Audio");
        WriteFile(output, "modified-content");
        plan = Build(scan, Options(root), new List<ConversionRecord> { modifiedRecord });
        associated = plan.Groups.Single().Associated.Single();
        Check(!associated.Verified && !associated.Eligible, "modified ledger output is not eligible for cleanup");

        var historicalRoot = Root("historical");
        var historicalSource = Path.Combine(historicalRoot, "B", "song.ncm");
        var historicalHigh = Path.Combine(historicalRoot, "B", "song-hq.ncm");
        var historicalOutput = Path.Combine(historicalRoot, "B", "unlock", "song.mp3");
        WriteFile(historicalSource, "ncm");
        WriteFile(historicalHigh, "hq");
        WriteFile(historicalOutput, "mp3");
        var historicalScan = Scan(historicalRoot,
            ActualFile(historicalSource, "2", "Song", "Artist", "mp3", 320000),
            ActualFile(historicalHigh, "2", "Song", "Artist", "flac", 900000));
        var historicalPlan = Build(historicalScan, Options(historicalRoot));
        var suspect = historicalPlan.Groups.Single().Associated.Single();
        Check(!suspect.FromLedger && !suspect.Verified && !suspect.Eligible,
            "historical same-name output is only a suspected association");

        var sharedRoot = Root("shared");
        var sharedSource = Path.Combine(sharedRoot, "C", "song.ncm");
        var sharedHigh = Path.Combine(sharedRoot, "C", "song-hq.ncm");
        var sharedOutput = Path.Combine(sharedRoot, "C", "unlock", "song.flac");
        WriteFile(sharedSource, "ncm");
        WriteFile(sharedHigh, "hq");
        WriteFile(sharedOutput, "flac");
        var sharedScan = Scan(sharedRoot,
            ActualFile(sharedSource, "3", "Song", "Artist", "mp3", 320000),
            ActualFile(sharedHigh, "3", "Song", "Artist", "flac", 900000));
        var sharedOther = Path.Combine(sharedRoot, "D", "other.ncm");
        WriteFile(sharedOther, "other");
        var sharedRecord = Ledger(sharedSource, sharedOutput, "Audio", "3");
        var otherRecord = Ledger(sharedOther, sharedOutput, "Audio", "4");
        var sharedPlan = Build(sharedScan, Options(sharedRoot), new List<ConversionRecord> { sharedRecord, otherRecord });
        var sharedAssociated = sharedPlan.Groups.Single().Associated.Single();
        Check(!sharedAssociated.Eligible && !sharedAssociated.Verified, "output referenced by two different sources is not eligible");

        var outsideRoot = Root("outside");
        var outsideSource = Path.Combine(root, "A", "outside-source.ncm");
        var outsideOutput = Path.Combine(outsideRoot, "outside.flac");
        WriteFile(outsideSource, "ncm");
        WriteFile(outsideOutput, "flac");
        var outsideScan = Scan(root,
            ActualFile(outsideSource, "1", "Song", "Artist", "mp3", 320000),
            ActualFile(highQuality, "1", "Song", "Artist", "flac", 900000));
        var outsidePlan = Build(outsideScan, Options(root), new List<ConversionRecord> { Ledger(outsideSource, outsideOutput, "Audio") });
        Check(outsidePlan.Groups.Single().Associated.All(item => !item.Eligible), "output outside managed roots is not eligible");

        var hiddenPlan = Build(scan, Options(root, null, false), new List<ConversionRecord> { Ledger(source, output, "Audio") });
        Check(hiddenPlan.Groups.Single().Associated.Count == 0, "disabling associated files omits conversion outputs");

        var customPlanRoot = Root("custom-output-plan");
        var customOutputRoot = Root("custom-output-dir");
        var customSource = Path.Combine(customPlanRoot, "A", "song.ncm");
        var customHigh = Path.Combine(customPlanRoot, "A", "song-hq.ncm");
        var customOutput = Path.Combine(customOutputRoot, "A", "song.mp3");
        WriteFile(customSource, "ncm");
        WriteFile(customHigh, "hq");
        WriteFile(customOutput, "mp3");
        var customPlan = Build(Scan(customPlanRoot,
            ActualFile(customSource, "6", "Custom Song", "Artist", "mp3", 192000),
            ActualFile(customHigh, "6", "Custom Song", "Artist", "flac", 900000)),
            Options(customPlanRoot, customOutputRoot),
            new List<ConversionRecord> { Ledger(customSource, customOutput, "Audio", "6") });
        Check(customPlan.Groups.Single().Associated.Single().Eligible, "custom output directory association is verified");

        var reconvertRoot = Root("reconvert");
        var reconvertSource = Path.Combine(reconvertRoot, "A", "song.ncm");
        var reconvertHigh = Path.Combine(reconvertRoot, "A", "song-hq.ncm");
        var reconvertOutput = Path.Combine(reconvertRoot, "A", "unlock", "song.mp3");
        WriteFile(reconvertSource, "ncm");
        WriteFile(reconvertHigh, "hq");
        WriteFile(reconvertOutput, "mp3");
        var reconvertScan = Scan(reconvertRoot,
            ActualFile(reconvertSource, "8", "Reconverted", "Artist", "mp3", 192000),
            ActualFile(reconvertHigh, "8", "Reconverted", "Artist", "flac", 900000));
        var oldRecord = Ledger(reconvertSource, reconvertOutput, "Audio", "8");
        oldRecord.ConvertedUtc = "2025-01-01T00:00:00.0000000Z";
        var newRecord = Ledger(reconvertSource, reconvertOutput, "Audio", "8");
        newRecord.ConvertedUtc = "2026-01-01T00:00:00.0000000Z";
        var reconvertPlan = Build(reconvertScan, Options(reconvertRoot), new List<ConversionRecord> { oldRecord, newRecord });
        Check(reconvertPlan.Groups.Single().Associated.Single().Eligible,
            "latest conversion record supersedes older references to the same output");
        var failedLatest = new ConversionRecord
        {
            Version = 1,
            SourcePath = reconvertSource,
            Success = false,
            Error = "failed",
            ConvertedUtc = "2027-01-01T00:00:00.0000000Z",
            Outputs = new List<ConversionOutput>()
        };
        var failedPlan = Build(reconvertScan, Options(reconvertRoot), new List<ConversionRecord> { newRecord, failedLatest });
        Check(failedPlan.Groups.Single().Associated.All(item => !item.Eligible),
            "a newer failed conversion prevents stale output cleanup");

        var crossRoot = Root("cross-directory");
        var sourceA = Path.Combine(crossRoot, "A", "song.ncm");
        var sourceB = Path.Combine(crossRoot, "B", "song.ncm");
        var outputA = Path.Combine(crossRoot, "A", "unlock", "song.mp3");
        var outputB = Path.Combine(crossRoot, "B", "unlock", "song.mp3");
        WriteFile(sourceA, "a");
        WriteFile(sourceB, "b");
        WriteFile(outputA, "out-a");
        WriteFile(outputB, "out-b");
        var crossPlan = Build(Scan(crossRoot,
            ActualFile(sourceA, "10", "Cross Song", "Artist", "mp3", 192000),
            ActualFile(sourceB, "11", "Cross Song", "Artist", "mp3", 192000)), Options(crossRoot));
        var crossGroup = crossPlan.Groups.Single();
        var associatedA = crossGroup.Associated.Where(item => item.SourcePath == sourceA).ToList();
        var associatedB = crossGroup.Associated.Where(item => item.SourcePath == sourceB).ToList();
        Check(associatedA.Count == 1 && associatedA[0].Path == outputA
            && associatedB.Count == 1 && associatedB[0].Path == outputB,
            "same filename in different directories is only associated with its own source directory");
        Check(crossGroup.Associated.All(item => !item.Eligible),
            "historical same-name outputs remain suspected even in separate directories");
    }

    private static DedupePlan BuildPairPlan(string root, bool showAssociated, out string loser, out string high, out string output)
    {
        loser = Path.Combine(root, "A", "song.ncm");
        high = Path.Combine(root, "A", "song-hq.ncm");
        output = Path.Combine(root, "A", "unlock", "song.mp3");
        WriteFile(loser, "loser-content");
        WriteFile(high, "high-content");
        if (showAssociated) WriteFile(output, "converted-content");
        var scan = Scan(root,
            ActualFile(loser, "1", "Song", "Artist", "mp3", 192000),
            ActualFile(high, "1", "Song", "Artist", "flac", 900000));
        var records = showAssociated
            ? new List<ConversionRecord> { Ledger(loser, output, "Audio") }
            : new List<ConversionRecord>();
        return Build(scan, Options(root, null, showAssociated), records);
    }

    private static DedupePlan BuildPartialPlan(string root, out string keep, out string loserA, out string loserB)
    {
        keep = Path.Combine(root, "A", "keep.ncm");
        loserA = Path.Combine(root, "B", "loser-a.ncm");
        loserB = Path.Combine(root, "C", "loser-b.ncm");
        WriteFile(keep, "keep-content");
        WriteFile(loserA, "loser-a-content");
        WriteFile(loserB, "loser-b-content");
        var scan = Scan(root,
            ActualFile(keep, "9", "Partial Song", "Artist", "flac", 900000),
            ActualFile(loserA, "9", "Partial Song", "Artist", "mp3", 192000),
            ActualFile(loserB, "9", "Partial Song", "Artist", "mp3", 128000));
        return Build(scan, Options(root, null, false));
    }

    private static bool TryCreateJunction(string link, string target)
    {
        try
        {
            var info = new ProcessStartInfo("cmd.exe", "/c mklink /J " + DedupeFileSystem.Quote(link) + " " + DedupeFileSystem.Quote(target))
            {
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true
            };
            using (var process = new Process { StartInfo = info })
            {
                process.Start();
                process.StandardOutput.ReadToEnd();
                process.StandardError.ReadToEnd();
                process.WaitForExit();
                return process.ExitCode == 0 && Directory.Exists(link);
            }
        }
        catch { return false; }
    }

    private static void RunQuarantineTests()
    {
        var root = Root("quarantine");
        string loser, high, output;
        var plan = BuildPairPlan(root, true, out loser, out high, out output);
        var associated = plan.Groups.Single().Associated.Single();
        associated.IsSelected = true;
        DedupePlanner.Recalculate(plan);
        Check(DedupePlanner.ValidateSelection(plan).Count == 0, "quarantine plan is valid");
        var result = DedupeQuarantine.Execute(plan, null, "quarantine-test");
        Check(result.Status == DedupeStatuses.OperationCompleted && result.MovedCount == 2,
            "selected source and verified output are moved into quarantine");
        Check(!File.Exists(loser) && !File.Exists(output), "quarantined files are no longer at their original paths");
        Check(Directory.Exists(Path.Combine(root, DedupeFileSystem.TrashFolderName, result.OperationId)),
            "operation-specific trash directory exists");
        var operation = DedupeQuarantine.LoadOperation(result.OperationId);
        Check(operation != null && operation.Items.Count == 2, "operation journal survives in local storage");
        Check(operation.Items.All(item => item.Status == DedupeStatuses.Moved && File.Exists(item.QuarantinePath)),
            "each successful move stores its quarantine path");
        var scanned = DedupeFileSystem.EnumerateNcmFiles(root, true, new List<string>());
        Check(scanned.All(item => !DedupeFileSystem.IsInsideTrash(item.FullName)), "trash files are excluded from music scans");

        var sourceItem = operation.Items.First(item => item.Kind == DedupeFileKind.Ncm);
        var outputItem = operation.Items.First(item => item.Kind == DedupeFileKind.Audio);
        var restoredSource = DedupeQuarantine.RestoreItem(result.OperationId, sourceItem.ItemId, DedupeQuarantine.ConflictCancel, null, null);
        Check(restoredSource.Success && File.Exists(loser) && File.ReadAllText(loser) == "loser-content",
            "source restores to its original path with identical content");
        var restoredOutput = DedupeQuarantine.RestoreItem(result.OperationId, outputItem.ItemId, DedupeQuarantine.ConflictCancel, null, null);
        Check(restoredOutput.Success && File.Exists(output) && File.ReadAllText(output) == "converted-content",
            "associated output restores with identical content");
        operation = DedupeQuarantine.LoadOperation(result.OperationId);
        Check(operation.Items.All(item => item.Status == DedupeStatuses.Restored), "restore status is persisted");

        var conflictRoot = Root("conflict");
        plan = BuildPairPlan(conflictRoot, false, out loser, out high, out output);
        result = DedupeQuarantine.Execute(plan, null, "conflict-test");
        operation = DedupeQuarantine.LoadOperation(result.OperationId);
        sourceItem = operation.Items.Single();
        WriteFile(loser, "new-version");
        var conflict = DedupeQuarantine.RestoreItem(result.OperationId, sourceItem.ItemId, DedupeQuarantine.ConflictCancel, null, null);
        Check(conflict.Conflict && !conflict.Success && File.ReadAllText(loser) == "new-version",
            "restore conflict never overwrites the new file");
        var renamed = DedupeQuarantine.RestoreItem(result.OperationId, sourceItem.ItemId, DedupeQuarantine.ConflictRename, null, null);
        Check(renamed.Success && renamed.RestoredPath != loser && File.ReadAllText(renamed.RestoredPath) == "loser-content"
            && File.ReadAllText(loser) == "new-version", "conflict can restore with an automatically generated name");

        var customRoot = Root("custom-restore");
        var customPlanRoot = Root("custom-plan");
        plan = BuildPairPlan(customPlanRoot, false, out loser, out high, out output);
        result = DedupeQuarantine.Execute(plan, null, "custom-restore-test");
        operation = DedupeQuarantine.LoadOperation(result.OperationId);
        var custom = DedupeQuarantine.RestoreItem(result.OperationId, operation.Items.Single().ItemId,
            DedupeQuarantine.ConflictCustomDirectory, customRoot, null);
        Check(custom.Success && custom.RestoredPath.StartsWith(customRoot, StringComparison.OrdinalIgnoreCase)
            && File.ReadAllText(custom.RestoredPath) == "loser-content", "restore can use a user-selected directory");

        var purgeRoot = Root("purge");
        plan = BuildPairPlan(purgeRoot, false, out loser, out high, out output);
        result = DedupeQuarantine.Execute(plan, null, "purge-test");
        operation = DedupeQuarantine.LoadOperation(result.OperationId);
        var purgeItem = operation.Items.Single();
        var purge = DedupeQuarantine.PurgeItems(result.OperationId, new[] { purgeItem.ItemId }, null);
        Check(purge.MovedCount == 1 && purge.FailedCount == 0 && !File.Exists(purgeItem.QuarantinePath),
            "explicit permanent cleanup deletes only the quarantined file");
        operation = DedupeQuarantine.LoadOperation(result.OperationId);
        Check(operation.Items.Single().Status == DedupeStatuses.Purged, "permanent cleanup status is persisted");

        var tamperRoot = Root("tamper");
        plan = BuildPairPlan(tamperRoot, false, out loser, out high, out output);
        result = DedupeQuarantine.Execute(plan, null, "tamper-test");
        operation = DedupeQuarantine.LoadOperation(result.OperationId);
        var outsideVictim = Path.Combine(tamperRoot, "outside-victim.txt");
        WriteFile(outsideVictim, "keep-me");
        operation.Items.Single().QuarantinePath = outsideVictim;
        DedupeQuarantine.SaveOperation(operation);
        var tampered = DedupeQuarantine.PurgeItems(result.OperationId, new[] { operation.Items.Single().ItemId }, null);
        Check(!tampered.Failures.Count.Equals(0) || tampered.FailedCount > 0, "tampered quarantine path is rejected");
        Check(File.Exists(outsideVictim) && File.ReadAllText(outsideVictim) == "keep-me", "permanent cleanup never deletes outside the isolation directory");

        var interruptRoot = Root("interrupt");
        plan = BuildPairPlan(interruptRoot, false, out loser, out high, out output);
        result = DedupeQuarantine.Execute(plan, null, "interrupt-test");
        operation = DedupeQuarantine.LoadOperation(result.OperationId);
        operation.Status = DedupeStatuses.OperationInProgress;
        DedupeQuarantine.SaveOperation(operation);
        var notice = DedupeQuarantine.MarkInterruptedOperations();
        Check(notice.Contains("interrupt-test") && DedupeQuarantine.LoadOperation(result.OperationId).Status == DedupeStatuses.OperationInterrupted,
            "unfinished operation is identified after restart");
        var interruptedRestore = DedupeQuarantine.RestoreItem(result.OperationId,
            DedupeQuarantine.LoadOperation(result.OperationId).Items.Single().ItemId, DedupeQuarantine.ConflictCancel, null, null);
        Check(interruptedRestore.Success && File.Exists(loser), "interrupted operation can still be restored");

        var missingRoot = Root("missing-keep");
        plan = BuildPairPlan(missingRoot, false, out loser, out high, out output);
        File.Delete(high);
        result = DedupeQuarantine.Execute(plan, null, "missing-keep-test");
        Check(result.Status == DedupeStatuses.OperationFailed && File.Exists(loser),
            "missing keep file rejects the whole operation before moving anything");
        Check(!File.Exists(DedupeQuarantine.OperationPath("missing-keep-test")), "rejected plan does not create an operation journal");

        var staleRoot = Root("stale");
        plan = BuildPairPlan(staleRoot, false, out loser, out high, out output);
        File.AppendAllText(loser, " changed");
        result = DedupeQuarantine.Execute(plan, null, "stale-test");
        Check(result.Status == DedupeStatuses.OperationFailed && File.Exists(loser),
            "source changed after scanning is rejected before moving");

        var partialRoot = Root("partial");
        string keep, loserA, loserB;
        plan = BuildPartialPlan(partialRoot, out keep, out loserA, out loserB);
        var occupied = Path.Combine(partialRoot, DedupeFileSystem.TrashFolderName, "partial-test", "files", "B", "I0001_loser-a.ncm");
        WriteFile(occupied, "occupied");
        result = DedupeQuarantine.Execute(plan, null, "partial-test");
        Check(result.Status == DedupeStatuses.OperationPartialFailure && result.MovedCount == 1 && result.FailedCount == 1,
            "one item failure is reported as a partial failure");
        operation = DedupeQuarantine.LoadOperation(result.OperationId);
        Check(operation.Items.Any(item => item.Status == DedupeStatuses.Moved)
            && operation.Items.Any(item => item.Status == DedupeStatuses.Failed), "partial failure keeps a status for every item");
        Check(File.Exists(loserB) || File.Exists(loserA), "non-failed items remain recoverable after a partial failure");
        Check(File.Exists(keep), "the keep file is never moved during a partial failure");

        Check(!DedupeFileSystem.IsLocalManagedRoot(@"\\server\share"), "network shares are rejected as managed roots");

        var junctionRoot = Root("junction-root");
        var junctionTarget = Root("junction-target");
        WriteFile(Path.Combine(junctionTarget, "outside.ncm"), "outside");
        var errors = new List<string>();
        if (TryCreateJunction(Path.Combine(junctionRoot, "linked"), junctionTarget))
        {
            var junctionFiles = DedupeFileSystem.EnumerateNcmFiles(junctionRoot, true, errors);
            Check(junctionFiles.All(item => !item.FullName.StartsWith(junctionTarget, StringComparison.OrdinalIgnoreCase)),
                "directory junctions are not followed during scans");
        }
        else
        {
            Console.WriteLine("SKIP junction creation is unavailable in this environment.");
        }
    }
}
