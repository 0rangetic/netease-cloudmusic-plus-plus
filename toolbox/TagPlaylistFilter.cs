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
            var selected = SelectedTags(moods, genres);
            var excluded = TagSet(excludedTags);
            var matches = new List<TagPlaylistSong>();
            if (selected.Count == 0 || minimumHits <= 0) return matches;
            minimumHits = Math.Min(minimumHits, selected.Count);
            foreach (var song in songs ?? Enumerable.Empty<TagPlaylistSong>())
            {
                if (IsExcluded(song, excluded)) continue;
                if (HitCount(song, selected) >= minimumHits) matches.Add(song);
            }
            return matches;
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
