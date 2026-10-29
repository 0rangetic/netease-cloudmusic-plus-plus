using System;
using System.Collections;
using System.Collections.Generic;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Windows.Forms;

internal static class PlaylistEditorLayoutValidation
{
    private const BindingFlags Private = BindingFlags.Instance | BindingFlags.NonPublic;
    private static Exception failure;
    private static int state;
    private static int ticks;
    private static int openAttempts;
    private static bool ticking;
    private static Form mainForm;
    private static Form dialog;
    private static ListBox moodList;
    private static ListBox genreList;
    private static Label finalCounts;
    private static Button create;
    private static Button previewButton;
    private static Button presetSave;
    private static Button presetLoad;
    private static ComboBox presetCombo;
    private static TextBox nameBox;

    private static void Check(bool value, string message)
    {
        if (!value) throw new Exception(message);
        Console.WriteLine("PASS " + message);
    }

    private static object Call(Form form, string name, params object[] args)
    {
        return form.GetType().GetMethod(name, Private).Invoke(form, args);
    }

    private static Control Find(Control root, string name)
    {
        var found = root.Controls.Find(name, true);
        return found.Length == 0 ? null : found[0];
    }

    private static string FindText(Control root, string name)
    {
        var control = Find(root, name);
        return control == null ? null : control.Text;
    }

    private static Form FindOpenForm(string title)
    {
        foreach (Form open in Application.OpenForms)
            if (!open.IsDisposed && open.Text == title) return open;
        return null;
    }

    private static Rectangle ScreenRect(Control control)
    {
        return new Rectangle(control.PointToScreen(Point.Empty), control.Size);
    }

    private static bool Inside(Rectangle outer, Rectangle inner)
    {
        return inner.Left >= outer.Left && inner.Top >= outer.Top
            && inner.Right <= outer.Right && inner.Bottom <= outer.Bottom;
    }

    private static void Pump()
    {
        for (int tick = 0; tick < 8; tick++) { Application.DoEvents(); System.Threading.Thread.Sleep(20); }
    }

    private static void Wait(int milliseconds)
    {
        int until = Environment.TickCount + milliseconds;
        while (Environment.TickCount < until)
        {
            Application.DoEvents();
            System.Threading.Thread.Sleep(20);
        }
    }

    private static object NewSong(Type songType, string id, string title, string artist, string moods, string genres)
    {
        var song = Activator.CreateInstance(songType);
        songType.GetProperty("SongId").SetValue(song, id, null);
        songType.GetProperty("Title").SetValue(song, title, null);
        songType.GetProperty("Artist").SetValue(song, artist, null);
        songType.GetProperty("Moods").SetValue(song, string.IsNullOrEmpty(moods) ? new string[0] : moods.Split(','), null);
        songType.GetProperty("Genres").SetValue(song, string.IsNullOrEmpty(genres) ? new string[0] : genres.Split(','), null);
        return song;
    }

    private static string TagName(object item)
    {
        string text = Convert.ToString(item);
        int marker = text.IndexOf("  (", StringComparison.Ordinal);
        return marker < 0 ? text : text.Substring(0, marker);
    }

    private static int FindTag(ListBox list, string name)
    {
        for (int index = 0; index < list.Items.Count; index++)
            if (TagName(list.Items[index]) == name) return index;
        return -1;
    }

    private static void CallList(ListBox list, string method, params object[] args)
    {
        list.GetType().GetMethod(method, Private).Invoke(list, args);
    }

    private static void SetIncluded(ListBox list, int index, bool value)
    {
        CallList(list, "SetIncluded", index, value);
    }

    private static List<string> IncludedTags(ListBox list)
    {
        return (List<string>)list.GetType().GetProperty("IncludedTags", Private).GetValue(list, null);
    }

    private static object BuildSnapshot(Type songType, Type snapshotType)
    {
        var songs = (IList)Activator.CreateInstance(typeof(List<>).MakeGenericType(songType));
        songs.Add(NewSong(songType, "1", "Song One", "Artist A", "治愈,放松", "流行"));
        songs.Add(NewSong(songType, "2", "Song Two", "Artist B", "悲伤", "摇滚"));
        songs.Add(NewSong(songType, "3", "Song Three", "Artist C", "治愈", "摇滚"));
        songs.Add(NewSong(songType, "4", "Song Four", "Artist D", "平静", "轻音乐"));
        songs.Add(NewSong(songType, "5", "Song Five", "Artist E", "治愈,治愈", "流行,流行"));
        songs.Add(NewSong(songType, "6", "Song Six", "Artist F", "", ""));
        var snapshot = Activator.CreateInstance(snapshotType);
        snapshotType.GetField("Songs", Private).SetValue(snapshot, songs);
        snapshotType.GetField("Liked", Private).SetValue(snapshot, 6);
        snapshotType.GetField("Tagged", Private).SetValue(snapshot, 5);
        return snapshot;
    }

    private static void CloseOpenForms()
    {
        foreach (Form open in Application.OpenForms.Cast<Form>().ToList())
        {
            if (open == mainForm) continue;
            try { open.Close(); } catch { }
        }
    }

    private static void Tick()
    {
        try
        {
            switch (state)
            {
                case 0: OpenEditor(); break;
                case 1: CheckPreview(); break;
                case 2: ClickSavePreset(); break;
                case 3: ClickPresetOk(); break;
                case 4: VerifySavedPreset(); break;
                case 5: LoadPresetAndCreate(); break;
                case 6: RemoveAfterLoad(); break;
                case 7: CheckConfirmation(); break;
                case 8: ReturnToEditor(); break;
                case 9: ConfirmCreation(); break;
            }
        }
        catch (Exception ex)
        {
            failure = ex;
            CloseOpenForms();
        }
    }

    private static void OpenEditor()
    {
        dialog = FindOpenForm("按标签生成红心歌单");
        if (dialog == null) return;
        moodList = Find(dialog, "MoodTagList") as ListBox;
        genreList = Find(dialog, "GenreTagList") as ListBox;
        finalCounts = Find(dialog, "TagPlaylistFinalCounts") as Label;
        create = Find(dialog, "TagPlaylistCreate") as Button;
        previewButton = Find(dialog, "TagPlaylistPreview") as Button;
        presetSave = Find(dialog, "TagPlaylistPresetSave") as Button;
        presetLoad = Find(dialog, "TagPlaylistPresetLoad") as Button;
        presetCombo = Find(dialog, "TagPlaylistPresetCombo") as ComboBox;
        nameBox = Find(dialog, "TagPlaylistName") as TextBox;
        Check(moodList != null && genreList != null && finalCounts != null && create != null
            && previewButton != null && presetSave != null && presetLoad != null
            && presetCombo != null && nameBox != null,
            "editor exposes preview, final counts and preset controls");
        if (!presetSave.Enabled && openAttempts++ < 25) return;
        Check(presetSave.Enabled, "preset saving is available when the local store opens");
        Check(presetCombo.Items.Count == 0, "new account starts without presets");
        openAttempts = 0;

        dialog.Size = dialog.MinimumSize;
        dialog.PerformLayout();
        Pump();
        var client = dialog.RectangleToScreen(dialog.ClientRectangle);
        foreach (var control in new Control[] { presetCombo, presetSave, presetLoad, finalCounts, previewButton, create })
            Check(Inside(client, ScreenRect(control)), "editor control stays visible at minimum size: " + control.Name);

        int healing = FindTag(moodList, "治愈");
        int relaxing = FindTag(moodList, "放松");
        Check(healing >= 0 && relaxing >= 0, "test tags are available");
        SetIncluded(moodList, healing, true);
        SetIncluded(moodList, relaxing, true);
        Pump();
        Check(finalCounts.Text.Contains("符合规则：3 首"), "final counts show three rule matches");
        Check(finalCounts.Text.Contains("手动排除：0 首") && finalCounts.Text.Contains("最终创建：3 首"),
            "final counts start with no manual exclusions");
        Check(create.Enabled, "create is enabled with three matches");
        previewButton.PerformClick();
        Pump();
        state = 1;
    }

    private static void CheckPreview()
    {
        var preview = FindOpenForm("匹配歌曲预览");
        if (preview == null) return;
        var grid = Find(preview, "PlaylistPreviewGrid") as DataGridView;
        var counts = Find(preview, "PlaylistPreviewCounts") as Label;
        var search = Find(preview, "PlaylistPreviewSearch") as TextBox;
        var status = Find(preview, "PlaylistPreviewStatus") as ComboBox;
        var sort = Find(preview, "PlaylistPreviewSort") as ComboBox;
        Check(grid != null && counts != null && search != null && status != null && sort != null,
            "preview exposes grid, search, status and sort controls");
        Check(grid.Rows.Count == 3, "preview lists the same three matches");
        Check(counts.Text.Contains("符合规则 3 首") && counts.Text.Contains("最终创建 3 首"),
            "preview summary matches the main filter");
        Check(Convert.ToString(grid.Rows[0].Cells["Title"].Value) == "Song One",
            "preview keeps the source order");
        Check(Convert.ToString(grid.Rows[0].Cells["HitTags"].Value).Contains("治愈、放松"),
            "preview shows the matched filter tags");
        var details = Find(preview, "PlaylistPreviewDetails") as TextBox;
        Check(details != null, "preview exposes the match explanation pane");
        grid.CurrentCell = grid.Rows[0].Cells["Title"];
        Pump();
        Check(details.Text.Contains("匹配依据") && details.Text.Contains("治愈、放松"),
            "preview explains the selected match");

        int sad = FindTag(moodList, "悲伤");
        SetIncluded(moodList, sad, true);
        Pump();
        Check(grid.Rows.Count == 1 && finalCounts.Text.Contains("符合规则：1 首"),
            "tag changes refresh the open preview");
        SetIncluded(moodList, sad, false);
        Pump();
        Check(grid.Rows.Count == 3 && finalCounts.Text.Contains("符合规则：3 首"),
            "removing a tag refreshes the open preview");

        grid.Rows[0].Cells["Include"].Value = false;
        Pump();
        Check(finalCounts.Text.Contains("手动排除：1 首") && finalCounts.Text.Contains("最终创建：2 首"),
            "manual removal updates the main counts");
        Check(counts.Text.Contains("手动排除 1 首") && counts.Text.Contains("最终创建 2 首"),
            "manual removal updates the preview counts");
        grid.Rows[0].Cells["Include"].Value = true;
        Pump();
        Check(finalCounts.Text.Contains("最终创建：3 首"), "restoring a song updates the final count");

        var excludeAll = Find(preview, "PlaylistPreviewExcludeAll") as Button;
        var includeAll = Find(preview, "PlaylistPreviewIncludeAll") as Button;
        Check(excludeAll != null && includeAll != null, "preview exposes bulk selection buttons");
        excludeAll.PerformClick();
        Pump();
        Check(finalCounts.Text.Contains("最终创建：0 首") && !create.Enabled,
            "removing every song disables creation");
        includeAll.PerformClick();
        Pump();
        Check(create.Enabled && finalCounts.Text.Contains("最终创建：3 首"),
            "restoring every song re-enables creation");

        search.Text = "Song One";
        Wait(500);
        Check(grid.Rows.Count == 1, "search narrows the preview list");
        search.Text = "";
        Wait(500);
        Check(grid.Rows.Count == 3, "clearing search restores the preview list");

        status.SelectedIndex = 6;
        Pump();
        Check(grid.Rows.Count == 1 && Convert.ToString(grid.Rows[0].Cells["State"].Value) == "尚未分析",
            "status filter exposes untagged songs");
        status.SelectedIndex = 0;
        Pump();
        sort.SelectedIndex = 2;
        Pump();
        Check(Convert.ToInt32(grid.Rows[0].Cells["HitCount"].Value) == 1,
            "sorting by hit count ascending works");
        sort.SelectedIndex = 0;
        Pump();

        grid.Rows[0].Cells["Include"].Value = false;
        Pump();
        Check(finalCounts.Text.Contains("最终创建：2 首"), "manual exclusion is kept before saving");
        (Find(preview, "PlaylistPreviewClose") as Button).PerformClick();
        Pump();
        Check(FindOpenForm("匹配歌曲预览") == null, "preview closes without touching the filter");
        Check(IncludedTags(moodList).Count == 2, "closing the preview keeps the tag selection");
        nameBox.Text = "夜晚放松";
        Pump();
        state = 2;
    }

    private static void ClickSavePreset()
    {
        dialog.BeginInvoke((Action)delegate { presetSave.PerformClick(); });
        state = 3;
    }

    private static void ClickPresetOk()
    {
        var prompt = FindOpenForm("保存规则预设");
        if (prompt == null) return;
        var input = Find(prompt, "TagPlaylistPresetNameInput") as TextBox;
        var ok = Find(prompt, "TagPlaylistPresetNameOk") as Button;
        Check(input != null && ok != null, "preset name prompt exposes its controls");
        input.Text = "夜晚放松";
        ok.PerformClick();
        state = 4;
    }

    private static void VerifySavedPreset()
    {
        if (FindOpenForm("保存规则预设") != null) return;
        if (presetCombo.Items.Count == 0 && openAttempts++ < 25) return;
        Check(presetCombo.Items.Count == 1, "saving a preset adds it to the rule list");
        previewButton.PerformClick();
        Pump();
        state = 5;
    }

    private static void LoadPresetAndCreate()
    {
        var preview = FindOpenForm("匹配歌曲预览");
        if (preview == null) return;
        var grid = Find(preview, "PlaylistPreviewGrid") as DataGridView;
        Check(grid != null && grid.Rows.Count == 3, "preview reopens with every rule match");
        Check(!Convert.ToBoolean(grid.Rows[0].Cells["Include"].Value),
            "manual exclusion is remembered by song id");
        Check(finalCounts.Text.Contains("最终创建：2 首"),
            "main keeps the manual exclusion after reopening the preview");
        (Find(preview, "PlaylistPreviewClose") as Button).PerformClick();
        Pump();

        CallList(moodList, "ClearAll");
        CallList(genreList, "ClearAll");
        Pump();
        Check(finalCounts.Text.Contains("符合规则：0 首") && !create.Enabled,
            "clearing tags disables creation");

        presetCombo.SelectedIndex = 0;
        Pump();
        presetLoad.PerformClick();
        Pump();
        Check(IncludedTags(moodList).Count == 2, "loading a preset restores the selected tags");
        Check(finalCounts.Text.Contains("符合规则：3 首；手动排除：0 首；最终创建：3 首。"),
            "loading a preset resets manual exclusions and restores counts");
        Check(nameBox.Text == "夜晚放松", "loading a preset restores the default playlist name");
        Check(create.Enabled, "loading a preset re-enables creation");
        previewButton.PerformClick();
        Pump();
        state = 6;
    }

    private static void RemoveAfterLoad()
    {
        var preview = FindOpenForm("匹配歌曲预览");
        if (preview == null) return;
        var grid = Find(preview, "PlaylistPreviewGrid") as DataGridView;
        Check(grid != null && grid.Rows.Count == 3, "preview shows every match after loading the preset");
        grid.Rows[0].Cells["Include"].Value = false;
        Pump();
        Check(finalCounts.Text.Contains("符合规则：3 首；手动排除：1 首；最终创建：2 首。"),
            "a manual exclusion is removed from the final selection");
        (Find(preview, "PlaylistPreviewClose") as Button).PerformClick();
        Pump();
        dialog.BeginInvoke((Action)delegate { create.PerformClick(); });
        state = 7;
    }

    private static void CheckConfirmation()
    {
        var confirm = FindOpenForm("确认创建歌单");
        if (confirm == null) return;
        Check(FindText(confirm, "TagPlaylistConfirmName") == "夜晚放松",
            "confirmation shows the playlist name");
        Check((FindText(confirm, "TagPlaylistConfirmPrivacy") ?? "").Contains("私密"),
            "confirmation shows the privacy setting");
        Check(FindText(confirm, "TagPlaylistConfirmMatched") == "3 首",
            "confirmation shows the rule match count");
        Check(FindText(confirm, "TagPlaylistConfirmRemoved") == "1 首",
            "confirmation shows the manual removal count");
        Check(FindText(confirm, "TagPlaylistConfirmFinal") == "2 首",
            "confirmation shows the final count");
        (Find(confirm, "TagPlaylistConfirmBack") as Button).PerformClick();
        state = 8;
    }

    private static void ReturnToEditor()
    {
        if (FindOpenForm("确认创建歌单") != null) return;
        Check(FindOpenForm("按标签生成红心歌单") != null, "returning to edit keeps the filter dialog open");
        dialog.BeginInvoke((Action)delegate { create.PerformClick(); });
        state = 9;
    }

    private static void ConfirmCreation()
    {
        var confirm = FindOpenForm("确认创建歌单");
        if (confirm == null) return;
        (Find(confirm, "TagPlaylistConfirmCreate") as Button).PerformClick();
        state = 10;
    }

    [STAThread]
    private static int Main(string[] args)
    {
        string testLocalAppData = Path.Combine(Path.GetTempPath(), "ncm-playlist-editor-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(testLocalAppData);
        string originalLocalAppData = Environment.GetEnvironmentVariable("LOCALAPPDATA");
        Environment.SetEnvironmentVariable("LOCALAPPDATA", testLocalAppData);
        try
        {
            Application.EnableVisualStyles();
            var assembly = Assembly.Load(File.ReadAllBytes(Path.GetFullPath(args[0])));
            var formType = assembly.GetType("NeteaseToolbox.ToolboxForm", true);
            var songType = assembly.GetType("NeteaseToolbox.TagPlaylistSong", true);
            var snapshotType = formType.GetNestedType("TagPlaylistSnapshot", BindingFlags.NonPublic);
            using (mainForm = (Form)Activator.CreateInstance(formType, new object[] { false }))
            {
                var events = (System.ComponentModel.EventHandlerList)typeof(Control).GetProperty("Events", Private).GetValue(mainForm, null);
                foreach (var field in typeof(Form).GetFields(BindingFlags.Static | BindingFlags.NonPublic))
                {
                    if (field.Name.IndexOf("shown", StringComparison.OrdinalIgnoreCase) < 0) continue;
                    var key = field.GetValue(null);
                    if (events[key] != null) events.RemoveHandler(key, events[key]);
                }
                mainForm.StartPosition = FormStartPosition.Manual;
                mainForm.Location = new Point(-10000, -10000);
                mainForm.Show();
                Call(mainForm, "NavigateTo", 1);
                mainForm.Size = mainForm.MinimumSize;
                mainForm.PerformLayout();
                Pump();

                var snapshot = BuildSnapshot(songType, snapshotType);
                var timer = new Timer { Interval = 120 };
                timer.Tick += delegate
                {
                    if (failure != null || ticking) return;
                    ticking = true;
                    try
                    {
                        if (++ticks > 450)
                        {
                            failure = new Exception("editor validation timed out at state " + state);
                            CloseOpenForms();
                            return;
                        }
                        Tick();
                    }
                    finally
                    {
                        ticking = false;
                    }
                };
                timer.Start();
                object result = null;
                try
                {
                    result = formType.GetMethod("ShowTagPlaylistDialog", Private).Invoke(mainForm, new[] { snapshot });
                }
                catch (TargetInvocationException ex)
                {
                    failure = ex.InnerException ?? ex;
                }
                finally
                {
                    timer.Stop();
                    timer.Dispose();
                }
                Check(failure == null, failure == null ? "editor validation completed" : failure.Message);
                Check(result != null, "confirming creation returns a playlist request");
                var resultType = result.GetType();
                var nameField = resultType.GetField("Name", BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public);
                var privateField = resultType.GetField("IsPrivate", BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public);
                var idsField = resultType.GetField("SongIds", BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public);
                Check(nameField != null && privateField != null && idsField != null, "playlist request exposes its fields");
                Check(Convert.ToString(nameField.GetValue(result)) == "夜晚放松", "playlist request keeps the confirmed name");
                Check(Convert.ToBoolean(privateField.GetValue(result)), "playlist request keeps the private setting");
                var ids = (List<string>)idsField.GetValue(result);
                Check(ids.Count == 2 && ids.SequenceEqual(new[] { "3", "5" }),
                    "playlist request omits manually removed songs");
                Check(FindOpenForm("匹配歌曲预览") == null, "closing the filter dialog closes the preview");

                string ruleFolder = Path.Combine(testLocalAppData, "NeteaseToolbox", "PlaylistRules");
                var presetFiles = Directory.Exists(ruleFolder)
                    ? Directory.GetFiles(ruleFolder, "presets.json", SearchOption.AllDirectories) : new string[0];
                Check(presetFiles.Length == 1, "the preset file is stored under the account rule folder");
                string presetText = presetFiles.Length == 0 ? "" : File.ReadAllText(presetFiles[0]);
                Check(presetText.Contains("夜晚放松") && presetText.Contains("\"Version\":1"),
                    "the preset file keeps the rule and its format version");
                Check(!presetText.Contains("MUSIC_U") && !presetText.Contains("songIds"),
                    "the preset file stores no credentials or song ids");
                mainForm.Close();
            }
            return 0;
        }
        finally
        {
            Environment.SetEnvironmentVariable("LOCALAPPDATA", originalLocalAppData);
            try { Directory.Delete(testLocalAppData, true); } catch { }
        }
    }
}
