using System;
using System.Collections.Generic;

namespace NeteaseToolbox
{
    internal enum DedupeConfidence
    {
        Unconfirmed = 0,
        Medium = 1,
        High = 2
    }

    internal enum DedupeFileKind
    {
        Ncm = 0,
        Audio = 1,
        Lyric = 2,
        Cover = 3
    }

    internal static class DedupeStatuses
    {
        public const string Pending = "Pending";
        public const string Moved = "Moved";
        public const string Failed = "Failed";
        public const string Restored = "Restored";
        public const string RestoreFailed = "RestoreFailed";
        public const string Purged = "Purged";
        public const string PurgeFailed = "PurgeFailed";

        public const string OperationPending = "OperationPending";
        public const string OperationInProgress = "OperationInProgress";
        public const string OperationCompleted = "OperationCompleted";
        public const string OperationPartialFailure = "OperationPartialFailure";
        public const string OperationFailed = "OperationFailed";
        public const string OperationInterrupted = "OperationInterrupted";

        public static string Label(string status)
        {
            switch (status)
            {
                case Pending: return "等待处理";
                case Moved: return "可恢复";
                case Failed: return "移动失败";
                case Restored: return "已恢复";
                case RestoreFailed: return "恢复失败";
                case Purged: return "已永久清理";
                case PurgeFailed: return "清理失败";
                case OperationPending: return "等待执行";
                case OperationInProgress: return "执行中";
                case OperationCompleted: return "已完成";
                case OperationPartialFailure: return "部分失败";
                case OperationFailed: return "执行失败";
                case OperationInterrupted: return "执行中断";
                default: return status;
            }
        }
    }

    internal sealed class DedupeScanFile
    {
        public string Path { get; set; }
        public string Key { get; set; }
        public string SongId { get; set; }
        public string Title { get; set; }
        public string Artists { get; set; }
        public long Bitrate { get; set; }
        public int FormatRank { get; set; }
        public string Format { get; set; }
        public long Size { get; set; }
        public string ModifiedUtc { get; set; }
        public bool MetadataOk { get; set; }
        public string Error { get; set; }
    }

    internal sealed class DedupeScanResult
    {
        public int Version { get; set; }
        public string Root { get; set; }
        public string CreatedUtc { get; set; }
        public bool Recurse { get; set; }
        public List<DedupeScanFile> Files { get; set; }
        public List<string> Errors { get; set; }

        public DedupeScanResult()
        {
            Files = new List<DedupeScanFile>();
            Errors = new List<string>();
        }
    }

    internal sealed class DedupePlanOptions
    {
        public string Root { get; set; }
        public string OutputRoot { get; set; }
        public bool Recurse { get; set; }
        public bool ShowAssociated { get; set; }
        public bool IncludeSuspected { get; set; }
        public bool VerifyAssociatedHashes { get; set; }
        public string CreatedUtc { get; set; }

        public DedupePlanOptions()
        {
            VerifyAssociatedHashes = true;
        }
    }

    internal sealed class DedupeQualityInfo
    {
        public int Tier { get; set; }
        public string Category { get; set; }
        public string Label { get; set; }
        public string Reason { get; set; }
        public long Bitrate { get; set; }
        public string Format { get; set; }
        public bool Known { get { return Tier > 0; } }
    }

    internal sealed class DedupeGroupFile
    {
        public string Path { get; set; }
        public string Title { get; set; }
        public string Artists { get; set; }
        public string SongId { get; set; }
        public string Format { get; set; }
        public long Bitrate { get; set; }
        public long Size { get; set; }
        public string ModifiedUtc { get; set; }
        public bool MetadataOk { get; set; }
        public DedupeQualityInfo Quality { get; set; }
        public bool IsKeep { get; set; }
        public bool IsSelected { get; set; }
        public bool CanSelect { get; set; }
        public string Recommendation { get; set; }
        public string Warning { get; set; }
    }

    internal sealed class DedupeAssociatedFile
    {
        public string Path { get; set; }
        public string SourcePath { get; set; }
        public string GroupId { get; set; }
        public DedupeFileKind Kind { get; set; }
        public string KindLabel { get; set; }
        public DedupeConfidence Confidence { get; set; }
        public bool FromLedger { get; set; }
        public bool Verified { get { return Confidence == DedupeConfidence.High; } }
        public bool Eligible { get; set; }
        public bool CanSelect { get; set; }
        public bool IsSelected { get; set; }
        public string Basis { get; set; }
        public string Warning { get; set; }
        public long Size { get; set; }
        public string ModifiedUtc { get; set; }
        public string Sha256 { get; set; }
        public bool IsOnlyPlayableCopy { get; set; }
    }

    internal sealed class DedupeGroup
    {
        public string GroupId { get; set; }
        public DedupeConfidence Confidence { get; set; }
        public string MatchBasis { get; set; }
        public string Title { get; set; }
        public string Artists { get; set; }
        public string SongId { get; set; }
        public string KeepPath { get; set; }
        public string Recommendation { get; set; }
        public string Warning { get; set; }
        public bool SkipGroup { get; set; }
        public List<DedupeGroupFile> Files { get; set; }
        public List<DedupeAssociatedFile> Associated { get; set; }

        public DedupeGroup()
        {
            Files = new List<DedupeGroupFile>();
            Associated = new List<DedupeAssociatedFile>();
        }
    }

    internal sealed class DedupePlan
    {
        public int Version { get; set; }
        public string CreatedUtc { get; set; }
        public string ScanCreatedUtc { get; set; }
        public DedupePlanOptions Options { get; set; }
        public List<DedupeGroup> Groups { get; set; }
        public List<DedupeScanFile> ScannedFiles { get; set; }
        public List<string> Warnings { get; set; }
        public int ScannedFileCount { get; set; }
        public int ConfirmedGroupCount { get; set; }
        public int SuspectedGroupCount { get; set; }
        public int ReviewGroupCount { get; set; }
        public int KeepFileCount { get; set; }
        public int SelectedSourceCount { get; set; }
        public int SelectedAssociatedCount { get; set; }
        public long SelectedBytes { get; set; }
        public int AssociatedCount { get; set; }
        public long AssociatedBytes { get; set; }

        public DedupePlan()
        {
            Groups = new List<DedupeGroup>();
            ScannedFiles = new List<DedupeScanFile>();
            Warnings = new List<string>();
            Options = new DedupePlanOptions();
        }

        public bool HasPendingSelection { get { return SelectedSourceCount + SelectedAssociatedCount > 0; } }
    }

    internal sealed class ConversionOutput
    {
        public string Path { get; set; }
        public string Kind { get; set; }
        public long Size { get; set; }
        public string ModifiedUtc { get; set; }
        public string Sha256 { get; set; }
        public string GeneratedUtc { get; set; }
    }

    internal sealed class ConversionRecord
    {
        public int Version { get; set; }
        public string TaskId { get; set; }
        public string SourcePath { get; set; }
        public string SourceSongId { get; set; }
        public long SourceSize { get; set; }
        public string SourceModifiedUtc { get; set; }
        public string SourceSha256 { get; set; }
        public bool Success { get; set; }
        public string Error { get; set; }
        public string ConvertedUtc { get; set; }
        public List<ConversionOutput> Outputs { get; set; }

        public ConversionRecord()
        {
            Outputs = new List<ConversionOutput>();
        }
    }

    internal sealed class ConversionLedger
    {
        public int Version { get; set; }
        public string TaskId { get; set; }
        public string CreatedUtc { get; set; }
        public List<ConversionRecord> Records { get; set; }

        public ConversionLedger()
        {
            Records = new List<ConversionRecord>();
        }
    }

    internal sealed class DedupeOperationItem
    {
        public string ItemId { get; set; }
        public string GroupId { get; set; }
        public string SourcePath { get; set; }
        public string OriginalPath { get; set; }
        public string QuarantinePath { get; set; }
        public string Root { get; set; }
        public DedupeFileKind Kind { get; set; }
        public string KindLabel { get; set; }
        public long Size { get; set; }
        public string OriginalModifiedUtc { get; set; }
        public string Sha256 { get; set; }
        public string Status { get; set; }
        public string Error { get; set; }
        public string MovedUtc { get; set; }
        public string RestoredUtc { get; set; }
        public string RestoredPath { get; set; }
    }

    internal sealed class DedupeOperation
    {
        public int Version { get; set; }
        public string OperationId { get; set; }
        public string CreatedUtc { get; set; }
        public string CompletedUtc { get; set; }
        public string InputRoot { get; set; }
        public string OutputRoot { get; set; }
        public string Status { get; set; }
        public string Error { get; set; }
        public List<DedupeOperationItem> Items { get; set; }

        public DedupeOperation()
        {
            Items = new List<DedupeOperationItem>();
        }
    }

    internal sealed class DedupeQuarantineResult
    {
        public string OperationId { get; set; }
        public string Status { get; set; }
        public int MovedCount { get; set; }
        public int FailedCount { get; set; }
        public long MovedBytes { get; set; }
        public string Error { get; set; }
        public List<string> Failures { get; set; }

        public DedupeQuarantineResult()
        {
            Failures = new List<string>();
        }
    }

    internal sealed class DedupeRestoreResult
    {
        public bool Success { get; set; }
        public bool Conflict { get; set; }
        public string RestoredPath { get; set; }
        public string Error { get; set; }
    }
}
