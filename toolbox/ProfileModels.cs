using System;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Windows.Media;

namespace NeteaseToolbox.Wpf
{
    public class ObservableObject : INotifyPropertyChanged
    {
        public event PropertyChangedEventHandler PropertyChanged;
        protected void OnPropertyChanged(string propertyName)
        {
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
        }
    }

    /// <summary>
    /// 单个分类项（如“流行-日本流行”、“快乐”、“日语”、“周杰伦”等）
    /// </summary>
    public class TagShareItem : ObservableObject
    {
        private string label;
        private int trackCount;
        private double share;
        private Brush accentBrush;

        public string Label
        {
            get => label;
            set { label = value; OnPropertyChanged(nameof(Label)); }
        }

        public int TrackCount
        {
            get => trackCount;
            set { trackCount = value; OnPropertyChanged(nameof(TrackCount)); OnPropertyChanged(nameof(TrackCountText)); }
        }

        public double Share
        {
            get => share;
            set { share = value; OnPropertyChanged(nameof(Share)); OnPropertyChanged(nameof(ShareText)); }
        }

        public string ShareText => $"{Share * 100.0:0.0}%";
        public string TrackCountText => $"{TrackCount} 首";
        public string ToolTipText => $"{Label}: {TrackCount} 首 ({ShareText})";

        public Brush AccentBrush
        {
            get => accentBrush;
            set { accentBrush = value; OnPropertyChanged(nameof(AccentBrush)); }
        }
    }

    /// <summary>
    /// 标签分类组（原生支持曲风、情绪，并完全可扩展语种、歌手、时代等任意后续新维度）
    /// </summary>
    public class TagCategoryGroup : ObservableObject
    {
        private string key;
        private string title;
        private string definition;
        private int coveredTracks;
        private int totalTracks;
        private ObservableCollection<TagShareItem> items = new ObservableCollection<TagShareItem>();

        public string Key
        {
            get => key;
            set { key = value; OnPropertyChanged(nameof(Key)); }
        }

        public string Title
        {
            get => title;
            set { title = value; OnPropertyChanged(nameof(Title)); }
        }

        public string Definition
        {
            get => definition;
            set { definition = value; OnPropertyChanged(nameof(Definition)); }
        }

        public int CoveredTracks
        {
            get => coveredTracks;
            set
            {
                coveredTracks = value;
                OnPropertyChanged(nameof(CoveredTracks));
                OnPropertyChanged(nameof(CoverageBadge));
            }
        }

        public int TotalTracks
        {
            get => totalTracks;
            set
            {
                totalTracks = value;
                OnPropertyChanged(nameof(TotalTracks));
                OnPropertyChanged(nameof(CoverageBadge));
            }
        }

        public string CoverageBadge
        {
            get
            {
                if (TotalTracks <= 0) return $"{CoveredTracks} 首";
                double pct = (double)CoveredTracks * 100.0 / TotalTracks;
                return $"覆盖 {CoveredTracks}/{TotalTracks} 首 ({pct:0.0}%)";
            }
        }

        public ObservableCollection<TagShareItem> Items
        {
            get => items;
            set { items = value; OnPropertyChanged(nameof(Items)); }
        }
    }

    /// <summary>
    /// 核心统计指标卡
    /// </summary>
    public class ProfileMetrics : ObservableObject
    {
        private int totalTracks;
        private int genreCovered;
        private int moodCovered;

        public int TotalTracks
        {
            get => totalTracks;
            set { totalTracks = value; OnPropertyChanged(nameof(TotalTracks)); OnPropertyChanged(nameof(TotalTracksText)); }
        }

        public int GenreCovered
        {
            get => genreCovered;
            set { genreCovered = value; OnPropertyChanged(nameof(GenreCovered)); OnPropertyChanged(nameof(GenreCoveredText)); }
        }

        public int MoodCovered
        {
            get => moodCovered;
            set { moodCovered = value; OnPropertyChanged(nameof(MoodCovered)); OnPropertyChanged(nameof(MoodCoveredText)); }
        }

        public string TotalTracksText => TotalTracks > 0 ? $"{TotalTracks:N0}" : "--";
        public string GenreCoveredText => GenreCovered > 0 ? $"{GenreCovered:N0}" : "--";
        public string MoodCoveredText => MoodCovered > 0 ? $"{MoodCovered:N0}" : "--";
    }

    /// <summary>
    /// 曲风近期与全局对比行
    /// </summary>
    public class GenreComparisonItem : ObservableObject
    {
        public string Genre { get; set; }
        public int RecentCount { get; set; }
        public string RecentPct { get; set; }
        public int GlobalCount { get; set; }
        public string GlobalPct { get; set; }
        public string Delta { get; set; }
        public bool IsPositiveDelta => Delta != null && Delta.StartsWith("+");
    }

    /// <summary>
    /// 歌曲明细行
    /// </summary>
    public class SongDetailItem : ObservableObject
    {
        public string SongId { get; set; }
        public string Title { get; set; }
        public string Artist { get; set; }
        public string Album { get; set; }
        public long PlayCount { get; set; }
        public string FirstListen { get; set; }
        public bool IsLiked { get; set; }
        public string LikedAt { get; set; }
    }

    /// <summary>
    /// AI 画像解读
    /// </summary>
    public class AiInsightModel : ObservableObject
    {
        private string statusText = "尚未生成 AI 解读";
        private string model = "";
        private string content = "启用并配置 AI 后，刷新画像可生成解读。";
        private string badgeColor = "#888888";

        public string StatusText
        {
            get => statusText;
            set { statusText = value; OnPropertyChanged(nameof(StatusText)); }
        }

        public string Model
        {
            get => model;
            set { model = value; OnPropertyChanged(nameof(Model)); }
        }

        public string Content
        {
            get => content;
            set { content = value; OnPropertyChanged(nameof(Content)); }
        }

        public string BadgeColor
        {
            get => badgeColor;
            set { badgeColor = value; OnPropertyChanged(nameof(BadgeColor)); }
        }
    }
}
