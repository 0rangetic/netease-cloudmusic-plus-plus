using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using NeteaseToolbox;

internal static class PlaylistRuleStoreValidation
{
    private static void Check(bool value, string message)
    {
        if (!value) throw new Exception(message);
        Console.WriteLine("PASS " + message);
    }

    private static void ExpectRuleFailure(Action action, string message)
    {
        try { action(); }
        catch (PlaylistRuleException) { Console.WriteLine("PASS " + message); return; }
        throw new Exception(message);
    }

    private static PlaylistRulePreset Preset(string name)
    {
        return new PlaylistRulePreset {
            Name = name,
            Moods = new[] { "轻快", "治愈", "流行" },
            Genres = new string[0],
            ExcludedTags = new[] { "伤感" },
            MinimumMode = "manual",
            MinimumHits = 2,
            PlaylistName = name
        };
    }

    private static void Write(string folder, string text)
    {
        Directory.CreateDirectory(folder);
        File.WriteAllText(Path.Combine(folder, "presets.json"), text, new UTF8Encoding(false));
    }

    private static int Main()
    {
        string root = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "rule-test-" + Guid.NewGuid().ToString("N"));
        try
        {
            var store = new PlaylistRuleStore(Path.Combine(root, "A"));
            Check(store.FilePath == Path.Combine(store.Folder, "presets.json"), "preset file lives at presets.json");
            Check(store.Load().Count == 0, "missing preset file loads as empty");

            store.Save(new[] { Preset("夜晚放松"), Preset("清晨通勤") });
            string json = File.ReadAllText(store.FilePath, Encoding.UTF8);
            Check(json.Contains("\"Version\":1"), "preset file records its format version");
            Check(json.Contains("夜晚放松") && json.Contains("伤感"), "preset file keeps rules in UTF-8");
            Check(Directory.GetFiles(store.Folder, "*.tmp").Length == 0, "atomic save leaves no temporary file");
            string lowered = json.ToLowerInvariant();
            Check(!lowered.Contains("cookie") && !lowered.Contains("music_u") && !lowered.Contains("csrf")
                && !lowered.Contains("apikey") && !lowered.Contains("songids") && !lowered.Contains("token"),
                "preset file stores no credentials or song ids");

            var loaded = store.Load();
            Check(loaded.Count == 2, "saved presets load back");
            Check(loaded[0].Name == "夜晚放松" && loaded[0].Moods.SequenceEqual(new[] { "轻快", "治愈", "流行" })
                && loaded[0].ExcludedTags.SequenceEqual(new[] { "伤感" }),
                "saved tags and exclusions round-trip");
            Check(loaded[0].MinimumMode == "manual" && loaded[0].MinimumHits == 2,
                "manual threshold round-trips");
            Check(!string.IsNullOrEmpty(loaded[0].Id) && !string.IsNullOrEmpty(loaded[0].UpdatedAt),
                "saving assigns an id and timestamp");
            string firstId = loaded[0].Id;

            loaded[0].Name = "深夜放松";
            store.Save(loaded);
            var renamed = store.Load();
            Check(renamed[0].Name == "深夜放松" && renamed[0].Id == firstId,
                "renaming a preset keeps its identity");
            Check(renamed[1].Name == "清晨通勤", "renaming leaves other presets untouched");

            renamed.RemoveAt(1);
            store.Save(renamed);
            Check(store.Load().Count == 1, "deleting a preset persists");

            var other = new PlaylistRuleStore(Path.Combine(root, "B"));
            other.Save(new[] { Preset("另一个账号") });
            Check(store.Load().Count == 1 && other.Load().Count == 1
                && store.Load()[0].Name == "深夜放松" && other.Load()[0].Name == "另一个账号",
                "different account folders keep presets isolated");

            string future = Path.Combine(root, "future");
            Write(future, "{\"Version\":99,\"Presets\":[]}");
            ExpectRuleFailure(delegate { new PlaylistRuleStore(future).Load(); },
                "future format version is rejected");
            Check(File.ReadAllText(Path.Combine(future, "presets.json")).Contains("99"),
                "rejected future file is left untouched");

            string corrupt = Path.Combine(root, "corrupt");
            Write(corrupt, "{broken");
            ExpectRuleFailure(delegate { new PlaylistRuleStore(corrupt).Load(); },
                "corrupt JSON is reported as a rule failure");
            Check(File.ReadAllText(Path.Combine(corrupt, "presets.json")) == "{broken",
                "corrupt file is left untouched");

            string missingVersion = Path.Combine(root, "missing-version");
            Write(missingVersion, "{\"Presets\":[]}");
            ExpectRuleFailure(delegate { new PlaylistRuleStore(missingVersion).Load(); },
                "preset file without a format version is rejected");

            string sanitize = Path.Combine(root, "sanitize");
            Write(sanitize, "{\"Version\":1,\"Presets\":["
                + "{\"Id\":\"\",\"Name\":\"  夜晚放松  \",\"Moods\":[\" 治愈 \",\"治愈\",\"\"],\"Genres\":[\"流行\"],"
                + "\"ExcludedTags\":null,\"MinimumMode\":\"weird\",\"MinimumHits\":0,\"PlaylistName\":\"   \",\"UpdatedAt\":\"\"},"
                + "{\"Id\":\"same\",\"Name\":\"保留规则\"},"
                + "{\"Id\":\"same\",\"Name\":\"重复 ID\"},"
                + "{\"Id\":\"\",\"Name\":\"夜晚放松\"},"
                + "{\"Id\":\"\",\"Name\":\"\"}]}");
            var sanitized = new PlaylistRuleStore(sanitize).Load();
            Check(sanitized.Count == 3, "invalid names and duplicate names are dropped on load");
            Check(sanitized[0].Name == "夜晚放松" && sanitized[0].Moods.SequenceEqual(new[] { "治愈" })
                && sanitized[0].PlaylistName == "夜晚放松",
                "loaded rules are trimmed and empty values fall back");
            Check(sanitized[0].MinimumMode == "auto" && sanitized[0].MinimumHits == 1,
                "invalid threshold settings fall back to safe defaults");
            Check(!string.IsNullOrEmpty(sanitized[0].Id), "missing ids are repaired on load");
            Check(!string.Equals(sanitized[1].Id, sanitized[2].Id, StringComparison.Ordinal),
                "duplicate ids are made unique on load");

            var guarded = new PlaylistRuleStore(Path.Combine(root, "guarded"));
            guarded.Save(new[] { Preset("有效规则") });
            string guardedText = File.ReadAllText(guarded.FilePath, Encoding.UTF8);
            ExpectRuleFailure(delegate { guarded.Save(new[] { Preset("") }); },
                "empty preset name is rejected");
            ExpectRuleFailure(delegate { guarded.Save(new[] { Preset("同名"), Preset("同名") }); },
                "duplicate preset names are rejected");
            Check(File.ReadAllText(guarded.FilePath, Encoding.UTF8) == guardedText,
                "rejected saves leave the previous file untouched");

            return 0;
        }
        finally
        {
            try { if (Directory.Exists(root)) Directory.Delete(root, true); } catch { }
        }
    }
}
