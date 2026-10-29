using System;
using System.Collections.Generic;
using System.Linq;
using NeteaseToolbox;

internal static class TagPlaylistFilterValidation
{
    private static void Check(bool value, string message)
    {
        if (!value) throw new Exception(message);
        Console.WriteLine("PASS " + message);
    }

    private static TagPlaylistSong Song(string id, string mood, string genre)
    {
        return new TagPlaylistSong {
            SongId = id,
            Title = "Song " + id,
            Artist = "Artist",
            Moods = mood == null ? new string[0] : mood.Split(','),
            Genres = genre == null ? new string[0] : genre.Split(',')
        };
    }

    private static int Main()
    {
        var songs = new List<TagPlaylistSong> {
            Song("1", "治愈,放松", "流行"),
            Song("2", "悲伤", "摇滚"),
            Song("3", "治愈", "摇滚"),
            Song("4", "平静", "轻音乐"),
            Song("5", "治愈,治愈", "流行,流行")
        };

        var moods = TagPlaylistFilter.MoodCounts(songs);
        Check(moods.First(item => item.Name == "治愈").Count == 3, "mood count is unique per song");
        Check(moods.First(item => item.Name == "放松").Count == 1, "secondary mood is counted");

        var union = TagPlaylistFilter.Match(songs, new[] { "治愈", "放松" }, null, false);
        Check(union.Count == 3, "multiple near-synonym moods match with OR");

        var all = TagPlaylistFilter.Match(songs, new[] { "治愈", "放松" }, null, true);
        Check(all.Count == 1 && all[0].SongId == "1", "AND mode requires every selected mood");

        var genres = TagPlaylistFilter.Match(songs, null, new[] { "摇滚" }, false);
        Check(genres.Count == 2, "genre-only filter matches");

        var combined = TagPlaylistFilter.Match(songs, new[] { "悲伤" }, new[] { "轻音乐" }, false);
        Check(combined.Count == 2, "moods and genres combine with OR");

        var combinedAll = TagPlaylistFilter.Match(songs, new[] { "悲伤" }, new[] { "摇滚" }, true);
        Check(combinedAll.Count == 1 && combinedAll[0].SongId == "2", "AND mode combines moods and genres");

        Check(TagPlaylistFilter.Match(songs, new string[0], new string[0], false).Count == 0, "empty selection returns no songs");
        Check(TagPlaylistFilter.SelectedCount(new[] { "治愈", "放松" }, new[] { "治愈" }) == 2, "selected tag count uses the distinct union");
        var fourTags = new[] { "治愈", "放松", "悲伤", "平静" };
        var thresholdCounts = TagPlaylistFilter.CountAtLeast(songs, fourTags, null);
        Check(thresholdCounts.Length == 4, "match distribution has one bucket per selected tag");
        Check(thresholdCounts[0] == 5 && thresholdCounts[1] == 1 && thresholdCounts[2] == 0 && thresholdCounts[3] == 0,
            "match distribution is cumulative");
        var atLeastTwo = TagPlaylistFilter.Match(songs, fourTags, null, 2);
        Check(atLeastTwo.Count == 1 && atLeastTwo[0].SongId == "1", "threshold match keeps songs hitting half of four tags");
        Check(TagPlaylistFilter.Match(songs, fourTags, null, 3).Count == 0, "threshold match drops songs below three tags");
        Check(TagPlaylistFilter.Match(songs, fourTags, null, true).Count == 0, "AND mode remains equivalent to the maximum threshold");
        var withoutRock = TagPlaylistFilter.Match(songs, fourTags, null, new[] { "摇滚" }, 1);
        Check(withoutRock.Count == 3 && withoutRock.All(song => song.SongId != "2" && song.SongId != "3"),
            "excluded genre removes every song containing it");
        var countsWithoutRock = TagPlaylistFilter.CountAtLeast(songs, fourTags, null, new[] { "摇滚" });
        Check(countsWithoutRock[0] == 3 && countsWithoutRock[1] == 1 && countsWithoutRock[2] == 0 && countsWithoutRock[3] == 0,
            "match distribution applies hard exclusions");
        Check(TagPlaylistFilter.Match(songs, new[] { "治愈" }, null, new[] { "治愈" }, 1).Count == 0,
            "exclusion wins when the same tag is also selected");
        var withoutSadRock = TagPlaylistFilter.Match(songs, null, new[] { "摇滚" }, new[] { "悲伤" }, 1);
        Check(withoutSadRock.Count == 1 && withoutSadRock[0].SongId == "3",
            "excluded mood removes songs before counting included genres");
        Check(TagPlaylistFilter.DefaultName(new[] { "治愈", "放松" }, new[] { "流行" }) == "红心 · 治愈 · 放松 · 流行", "default name lists selected tags");
        Check(TagPlaylistFilter.DefaultName(new string[0], new string[0]) == "红心 · 标签歌单", "default name fallback");
        return 0;
    }
}
