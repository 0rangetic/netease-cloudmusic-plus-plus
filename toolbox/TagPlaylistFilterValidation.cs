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

        var evaluationSongs = new List<TagPlaylistSong> {
            Song("10", "治愈,放松", "流行"),
            Song("11", "悲伤", "摇滚"),
            Song("12", null, null),
            Song("13", "治愈", "治愈"),
            Song("14", "治愈", "摇滚")
        };
        var evaluation = TagPlaylistFilter.Evaluate(evaluationSongs, new[] { "治愈", "放松" }, null, null, 2);
        Check(evaluation.Count == 5, "evaluation keeps every song");
        Check(evaluation[0].IsMatched && evaluation[0].HitCount == 2
            && evaluation[0].HitTags.SequenceEqual(new[] { "治愈", "放松" }),
            "evaluation lists matching tags in song order");
        Check(evaluation[1].State == TagPlaylistMatchState.BelowMinimum && evaluation[1].HitCount == 0,
            "evaluation reports songs below the minimum");
        Check(evaluation[2].State == TagPlaylistMatchState.NoTags,
            "evaluation separates untagged songs");
        Check(evaluation[3].State == TagPlaylistMatchState.BelowMinimum && evaluation[3].HitCount == 1,
            "duplicate tags across moods and genres count once");
        Check(evaluation[0].StateText == "符合规则" && evaluation[1].StateText == "未达最低命中"
            && evaluation[2].StateText == "尚未分析",
            "evaluation states have distinct labels");

        var excludedEvaluation = TagPlaylistFilter.Evaluate(evaluationSongs,
            new[] { "治愈", "放松" }, null, new[] { "摇滚" }, 2);
        Check(excludedEvaluation[1].State == TagPlaylistMatchState.HardExcluded
            && excludedEvaluation[1].ExcludedTags.SequenceEqual(new[] { "摇滚" }),
            "hard exclusion is reported before hit counting");
        Check(excludedEvaluation[4].State == TagPlaylistMatchState.HardExcluded,
            "hard exclusion wins over a matching included tag");

        var noSelectionEvaluation = TagPlaylistFilter.Evaluate(evaluationSongs,
            new string[0], new string[0], null, 1);
        Check(noSelectionEvaluation.All(item => item.State == TagPlaylistMatchState.NoSelection),
            "evaluation reports a missing selection");
        var clampedEvaluation = TagPlaylistFilter.Evaluate(evaluationSongs, new[] { "治愈" }, null, null, 5);
        Check(clampedEvaluation.All(item => item.MinimumHits == 1),
            "evaluation clamps the minimum to the selected tag count");

        var matchedByEvaluation = TagPlaylistFilter.Match(evaluationSongs, new[] { "治愈", "放松" }, null, null, 2);
        Check(matchedByEvaluation.Select(song => song.SongId)
            .SequenceEqual(evaluation.Where(item => item.IsMatched).Select(item => item.Song.SongId)),
            "Match reuses the same evaluation used by the preview");

        Check(TagPlaylistFilter.Explain(evaluation[0]).Contains("命中标签：治愈、放松"),
            "explanation lists matched filter tags");
        Check(TagPlaylistFilter.Explain(evaluation[1]).Contains("最低命中：2 个"),
            "explanation shows the effective minimum");
        Check(TagPlaylistFilter.Explain(excludedEvaluation[1]).Contains("硬性排除命中：摇滚"),
            "explanation explains a hard exclusion");
        Check(TagPlaylistFilter.Explain(noSelectionEvaluation[0]).Contains("尚未勾选"),
            "explanation handles an empty selection");

        var selectionMatches = TagPlaylistFilter.Match(songs, new[] { "治愈", "放松" }, null, null, 1);
        var selection = new TagPlaylistSelectionState();
        Check(selection.RemovedCount(selectionMatches) == 0 && selection.FinalSongs(selectionMatches).Count == 3,
            "every matching song is selected by default");
        selection.SetRemoved("1", true);
        selection.SetRemoved("3", true);
        Check(selection.RemovedCount(selectionMatches) == 2 && selection.FinalSongs(selectionMatches).Count == 1
            && selection.FinalSongs(selectionMatches)[0].SongId == "5",
            "manual removals reduce the final selection");
        var noHealingMatches = TagPlaylistFilter.Match(songs, new[] { "治愈", "放松" }, null, new[] { "治愈" }, 1);
        Check(noHealingMatches.Count == 0 && selection.RemovedCount(noHealingMatches) == 0,
            "manual removals only count while the song still matches");
        Check(selection.RemovedCount(selectionMatches) == 2,
            "manual removals are remembered by song id after the filter changes");
        selection.SetRemoved("1", false);
        Check(selection.RemovedCount(selectionMatches) == 1, "restoring a song removes its manual exclusion");
        selection.RestoreAll();
        Check(selection.RemovedCount(selectionMatches) == 0, "restore all clears manual exclusions");
        selection.RemoveAll(selectionMatches);
        Check(selection.RemovedCount(selectionMatches) == 3 && selection.FinalSongs(selectionMatches).Count == 0,
            "remove all clears the final selection");
        selection.Clear();
        Check(selection.FinalSongs(selectionMatches).Count == 3, "clearing the session restores the default selection");

        var manySongs = Enumerable.Range(1, 80)
            .Select(index => Song("s" + index, index % 2 == 0 ? "治愈" : "放松", "流行")).ToList();
        var manyMatches = TagPlaylistFilter.Match(manySongs, new[] { "治愈", "放松" }, null, null, 1);
        var manySelection = new TagPlaylistSelectionState();
        foreach (var song in manyMatches.Take(12)) manySelection.SetRemoved(song.SongId, true);
        int removed = manySelection.RemovedCount(manyMatches);
        var finalSongs = manySelection.FinalSongs(manyMatches);
        Check(manyMatches.Count == 80 && removed == 12 && finalSongs.Count == 68,
            "80 matching songs minus 12 manual removals leaves 68");
        Check(manyMatches.Take(12).All(song => finalSongs.All(final => final.SongId != song.SongId)),
            "manually removed songs are not submitted");
        Check(manyMatches.Skip(12).All(song => finalSongs.Any(final => final.SongId == song.SongId)),
            "every remaining matching song stays selected");
        return 0;
    }
}
