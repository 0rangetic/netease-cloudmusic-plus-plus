using System;
using System.Collections.Generic;
using System.Linq;

namespace NeteaseToolbox
{
    internal sealed class TagPlaylistSong
    {
        public string SongId { get; set; }
        public string Title { get; set; }
        public string Artist { get; set; }
        public string[] Genres { get; set; }
        public string[] Moods { get; set; }

        internal string DisplayName
        {
            get
            {
                if (string.IsNullOrWhiteSpace(Title)) return Artist ?? "";
                return string.IsNullOrWhiteSpace(Artist) ? Title : Title + " - " + Artist;
            }
        }

        internal bool HasTags
        {
            get { return (Moods != null && Moods.Length > 0) || (Genres != null && Genres.Length > 0); }
        }
    }

    internal sealed class TagPlaylistCount
    {
        internal string Name;
        internal int Count;

        public override string ToString()
        {
            return Name + "  (" + Count + ")";
        }
    }

    internal enum TagPlaylistMatchState
    {
        NoSelection,
        Matched,
        BelowMinimum,
        HardExcluded,
        NoTags
    }

    internal sealed class TagPlaylistEvaluation
    {
        public TagPlaylistSong Song { get; set; }
        public TagPlaylistMatchState State { get; set; }
        public int SelectedTagCount { get; set; }
        public int MinimumHits { get; set; }
        public int HitCount { get; set; }
        public string[] HitTags { get; set; }
        public string[] ExcludedTags { get; set; }

        internal bool IsMatched { get { return State == TagPlaylistMatchState.Matched; } }

        internal string StateText
        {
            get
            {
                switch (State)
                {
                    case TagPlaylistMatchState.Matched: return "符合规则";
                    case TagPlaylistMatchState.BelowMinimum: return "未达最低命中";
                    case TagPlaylistMatchState.HardExcluded: return "硬性排除";
                    case TagPlaylistMatchState.NoTags: return "尚未分析";
                    default: return "未选择标签";
                }
            }
        }
    }

    internal sealed class TagPlaylistSelectionState
    {
        private readonly HashSet<string> removedSongIds = new HashSet<string>(StringComparer.Ordinal);

        internal bool IsRemoved(string songId)
        {
            return !string.IsNullOrEmpty(songId) && removedSongIds.Contains(songId);
        }

        internal void SetRemoved(string songId, bool removed)
        {
            if (string.IsNullOrEmpty(songId)) return;
            if (removed) removedSongIds.Add(songId);
            else removedSongIds.Remove(songId);
        }

        internal void RemoveAll(IEnumerable<TagPlaylistSong> songs)
        {
            if (songs == null) return;
            foreach (var song in songs)
                if (song != null && !string.IsNullOrEmpty(song.SongId)) removedSongIds.Add(song.SongId);
        }

        internal void RestoreAll()
        {
            removedSongIds.Clear();
        }

        internal void Clear()
        {
            removedSongIds.Clear();
        }

        internal int RemovedCount(IEnumerable<TagPlaylistSong> matchedSongs)
        {
            if (matchedSongs == null) return 0;
            int count = 0;
            var seen = new HashSet<string>(StringComparer.Ordinal);
            foreach (var song in matchedSongs)
            {
                if (song == null || string.IsNullOrEmpty(song.SongId) || !seen.Add(song.SongId)) continue;
                if (removedSongIds.Contains(song.SongId)) count++;
            }
            return count;
        }

        internal List<TagPlaylistSong> FinalSongs(IEnumerable<TagPlaylistSong> matchedSongs)
        {
            var result = new List<TagPlaylistSong>();
            if (matchedSongs == null) return result;
            foreach (var song in matchedSongs)
            {
                if (song == null || string.IsNullOrEmpty(song.SongId) || removedSongIds.Contains(song.SongId)) continue;
                result.Add(song);
            }
            return result;
        }
    }

    internal static class TagPlaylistFilter
    {
        internal static List<TagPlaylistCount> MoodCounts(IEnumerable<TagPlaylistSong> songs)
        {
            return Counts(songs, delegate(TagPlaylistSong song) { return song.Moods; });
        }

        internal static List<TagPlaylistCount> GenreCounts(IEnumerable<TagPlaylistSong> songs)
        {
            return Counts(songs, delegate(TagPlaylistSong song) { return song.Genres; });
        }

        // WPF 曲风页用：把每首歌的标签数组（主曲风、融合元素、情绪）汇总成按出现次数排序的统计。
        // 与 Counts 一致：同一首歌里重复出现的同名标签只计一次，空标签忽略。
        internal static List<TagPlaylistCount> CountDistinct(IEnumerable<IEnumerable<string>> songTags)
        {
            var counts = new Dictionary<string, int>(StringComparer.Ordinal);
            foreach (var tags in songTags ?? Enumerable.Empty<IEnumerable<string>>())
            {
                if (tags == null) continue;
                var distinct = new HashSet<string>(StringComparer.Ordinal);
                foreach (var value in tags)
                {
                    if (string.IsNullOrWhiteSpace(value)) continue;
                    distinct.Add(value.Trim());
                }
                foreach (var value in distinct)
                {
                    int count;
                    counts[value] = counts.TryGetValue(value, out count) ? count + 1 : 1;
                }
            }
            return counts.Select(item => new TagPlaylistCount { Name = item.Key, Count = item.Value })
                .OrderByDescending(item => item.Count)
                .ThenBy(item => item.Name, StringComparer.CurrentCulture)
                .ToList();
        }

        // 曲风分析结果里的“其他曲风/情绪”用 " / " 拼接（见 genre_analysis.py），这里按同一分隔符拆成独立标签。
        // 主曲风本身是一个整体标签，不做拆分；“暂无标签/未分析/无”不是标签，直接丢弃。
        internal static string[] SplitTags(string value)
        {
            if (string.IsNullOrWhiteSpace(value)) return new string[0];
            var result = new List<string>();
            var seen = new HashSet<string>(StringComparer.Ordinal);
            foreach (var part in value.Split(new[] { " / " }, StringSplitOptions.RemoveEmptyEntries))
            {
                var tag = part.Trim();
                if (tag.Length == 0 || tag == "暂无标签" || tag == "未分析" || tag == "无") continue;
                if (seen.Add(tag)) result.Add(tag);
            }
            return result.ToArray();
        }

        private static List<TagPlaylistCount> Counts(IEnumerable<TagPlaylistSong> songs, Func<TagPlaylistSong, string[]> selector)
        {
            var counts = new Dictionary<string, int>(StringComparer.Ordinal);
            foreach (var song in songs ?? Enumerable.Empty<TagPlaylistSong>())
            {
                var values = selector(song);
                if (values == null) continue;
                foreach (var value in new HashSet<string>(values.Where(value => !string.IsNullOrWhiteSpace(value)), StringComparer.Ordinal))
                {
                    int count;
                    counts[value] = counts.TryGetValue(value, out count) ? count + 1 : 1;
                }
            }
            return counts.Select(item => new TagPlaylistCount { Name = item.Key, Count = item.Value })
                .OrderByDescending(item => item.Count)
                .ThenBy(item => item.Name, StringComparer.CurrentCulture)
                .ToList();
        }

        internal static int SelectedCount(IEnumerable<string> moods, IEnumerable<string> genres)
        {
            return SelectedTags(moods, genres).Count;
        }

        internal static List<TagPlaylistSong> Match(IEnumerable<TagPlaylistSong> songs,
            IEnumerable<string> moods, IEnumerable<string> genres, bool requireAll)
        {
            int count = SelectedCount(moods, genres);
            return Match(songs, moods, genres, null, requireAll ? count : 1);
        }

        internal static List<TagPlaylistSong> Match(IEnumerable<TagPlaylistSong> songs,
            IEnumerable<string> moods, IEnumerable<string> genres, int minimumHits)
        {
            return Match(songs, moods, genres, null, minimumHits);
        }

        internal static List<TagPlaylistSong> Match(IEnumerable<TagPlaylistSong> songs,
            IEnumerable<string> moods, IEnumerable<string> genres, IEnumerable<string> excludedTags, int minimumHits)
        {
            return Evaluate(songs, moods, genres, excludedTags, minimumHits)
                .Where(item => item.IsMatched)
                .Select(item => item.Song)
                .ToList();
        }

        internal static List<TagPlaylistEvaluation> Evaluate(IEnumerable<TagPlaylistSong> songs,
            IEnumerable<string> moods, IEnumerable<string> genres, IEnumerable<string> excludedTags, int minimumHits)
        {
            var selected = SelectedTags(moods, genres);
            var excluded = TagSet(excludedTags);
            int effectiveMinimum = selected.Count == 0 ? 0 : Math.Min(Math.Max(1, minimumHits), selected.Count);
            var evaluations = new List<TagPlaylistEvaluation>();
            foreach (var song in songs ?? Enumerable.Empty<TagPlaylistSong>())
            {
                var hitTags = MatchingTags(song, selected);
                var excludedHits = MatchingTags(song, excluded);
                var evaluation = new TagPlaylistEvaluation {
                    Song = song,
                    SelectedTagCount = selected.Count,
                    MinimumHits = effectiveMinimum,
                    HitCount = hitTags.Length,
                    HitTags = hitTags,
                    ExcludedTags = excludedHits
                };
                if (selected.Count == 0 || minimumHits <= 0) evaluation.State = TagPlaylistMatchState.NoSelection;
                else if (excludedHits.Length > 0) evaluation.State = TagPlaylistMatchState.HardExcluded;
                else if (!song.HasTags) evaluation.State = TagPlaylistMatchState.NoTags;
                else if (hitTags.Length < effectiveMinimum) evaluation.State = TagPlaylistMatchState.BelowMinimum;
                else evaluation.State = TagPlaylistMatchState.Matched;
                evaluations.Add(evaluation);
            }
            return evaluations;
        }

        internal static string Explain(TagPlaylistEvaluation evaluation)
        {
            if (evaluation == null) return "";
            var song = evaluation.Song;
            var lines = new List<string>();
            lines.Add("歌曲：" + (song == null ? "" : song.DisplayName));
            lines.Add("曲风：" + JoinTags(song == null ? null : song.Genres));
            lines.Add("情绪：" + JoinTags(song == null ? null : song.Moods));
            lines.Add("命中标签：" + (evaluation.HitTags.Length == 0 ? "无"
                : string.Join("、", evaluation.HitTags) + "（" + evaluation.HitCount + " 个）"));
            if (evaluation.ExcludedTags.Length > 0)
                lines.Add("硬性排除命中：" + string.Join("、", evaluation.ExcludedTags));
            lines.Add("状态：" + evaluation.StateText);
            lines.Add("已勾选标签：" + evaluation.SelectedTagCount + " 个；最低命中：" + evaluation.MinimumHits + " 个");
            switch (evaluation.State)
            {
                case TagPlaylistMatchState.Matched:
                    lines.Add("匹配依据：命中 " + evaluation.HitCount + " 个已勾选标签，达到最低命中数。");
                    break;
                case TagPlaylistMatchState.BelowMinimum:
                    lines.Add(evaluation.HitCount == 0
                        ? "匹配依据：没有命中任何已勾选标签，低于最低命中数。"
                        : "匹配依据：只命中 " + evaluation.HitCount + " 个已勾选标签，低于最低命中数。");
                    break;
                case TagPlaylistMatchState.HardExcluded:
                    lines.Add("匹配依据：歌曲包含硬性排除标签，优先排除，不再计入匹配。");
                    break;
                case TagPlaylistMatchState.NoTags:
                    lines.Add("匹配依据：这首歌尚未分析出曲风或情绪标签，未参与筛选。");
                    break;
                default:
                    lines.Add("匹配依据：尚未勾选筛选标签。");
                    break;
            }
            return string.Join(Environment.NewLine, lines);
        }

        internal static int[] CountAtLeast(IEnumerable<TagPlaylistSong> songs,
            IEnumerable<string> moods, IEnumerable<string> genres)
        {
            return CountAtLeast(songs, moods, genres, null);
        }

        internal static int[] CountAtLeast(IEnumerable<TagPlaylistSong> songs,
            IEnumerable<string> moods, IEnumerable<string> genres, IEnumerable<string> excludedTags)
        {
            var selected = SelectedTags(moods, genres);
            if (selected.Count == 0) return new int[0];
            var excluded = TagSet(excludedTags);
            var exact = new int[selected.Count + 1];
            foreach (var song in songs ?? Enumerable.Empty<TagPlaylistSong>())
            {
                if (IsExcluded(song, excluded)) continue;
                int hits = HitCount(song, selected);
                if (hits > 0) exact[hits]++;
            }
            var counts = new int[selected.Count];
            int running = 0;
            for (int hits = selected.Count; hits >= 1; hits--)
            {
                running += exact[hits];
                counts[hits - 1] = running;
            }
            return counts;
        }

        private static HashSet<string> SelectedTags(IEnumerable<string> moods, IEnumerable<string> genres)
        {
            var selected = new HashSet<string>(StringComparer.Ordinal);
            if (moods != null) selected.UnionWith(moods.Where(value => !string.IsNullOrWhiteSpace(value)));
            if (genres != null) selected.UnionWith(genres.Where(value => !string.IsNullOrWhiteSpace(value)));
            return selected;
        }

        private static HashSet<string> TagSet(IEnumerable<string> tags)
        {
            var result = new HashSet<string>(StringComparer.Ordinal);
            if (tags != null) result.UnionWith(tags.Where(value => !string.IsNullOrWhiteSpace(value)));
            return result;
        }

        private static bool IsExcluded(TagPlaylistSong song, HashSet<string> excluded)
        {
            if (excluded.Count == 0) return false;
            if (song.Moods != null && song.Moods.Any(value => !string.IsNullOrWhiteSpace(value) && excluded.Contains(value))) return true;
            return song.Genres != null && song.Genres.Any(value => !string.IsNullOrWhiteSpace(value) && excluded.Contains(value));
        }

        private static int HitCount(TagPlaylistSong song, HashSet<string> selected)
        {
            var tags = new HashSet<string>(StringComparer.Ordinal);
            if (song.Moods != null) tags.UnionWith(song.Moods.Where(value => !string.IsNullOrWhiteSpace(value)));
            if (song.Genres != null) tags.UnionWith(song.Genres.Where(value => !string.IsNullOrWhiteSpace(value)));
            return tags.Count(selected.Contains);
        }

        private static string[] MatchingTags(TagPlaylistSong song, HashSet<string> wanted)
        {
            if (song == null || wanted.Count == 0) return new string[0];
            var result = new List<string>();
            var seen = new HashSet<string>(StringComparer.Ordinal);
            AppendMatchingTags(song.Moods, wanted, seen, result);
            AppendMatchingTags(song.Genres, wanted, seen, result);
            return result.ToArray();
        }

        private static void AppendMatchingTags(string[] tags, HashSet<string> wanted, HashSet<string> seen, List<string> result)
        {
            if (tags == null) return;
            foreach (var tag in tags)
            {
                if (string.IsNullOrWhiteSpace(tag) || !wanted.Contains(tag) || !seen.Add(tag)) continue;
                result.Add(tag);
            }
        }

        private static string JoinTags(string[] tags)
        {
            if (tags == null) return "无";
            var values = new List<string>();
            var seen = new HashSet<string>(StringComparer.Ordinal);
            foreach (var tag in tags)
                if (!string.IsNullOrWhiteSpace(tag) && seen.Add(tag)) values.Add(tag);
            return values.Count == 0 ? "无" : string.Join("、", values);
        }

        internal static string DefaultName(IEnumerable<string> moods, IEnumerable<string> genres)
        {
            var names = new List<string>();
            if (moods != null) names.AddRange(moods.Where(value => !string.IsNullOrWhiteSpace(value)));
            if (genres != null) names.AddRange(genres.Where(value => !string.IsNullOrWhiteSpace(value)));
            if (names.Count == 0) return "红心 · 标签歌单";
            var name = "红心 · " + string.Join(" · ", names.Take(3));
            return name.Length > 40 ? name.Substring(0, 40) : name;
        }
    }
}
