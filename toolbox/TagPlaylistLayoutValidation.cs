using System;
using System.Collections;
using System.Collections.Generic;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Windows.Forms;

internal static class TagPlaylistLayoutValidation
{
    private const BindingFlags Private = BindingFlags.Instance | BindingFlags.NonPublic;
    private static Exception failure;

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
        for (int tick = 0; tick < 6; tick++) { Application.DoEvents(); System.Threading.Thread.Sleep(20); }
    }

    private static object NewSong(Type songType, string id, string title, string artist, string moods, string genres)
    {
        var song = Activator.CreateInstance(songType);
        songType.GetProperty("SongId").SetValue(song, id, null);
        songType.GetProperty("Title").SetValue(song, title, null);
        songType.GetProperty("Artist").SetValue(song, artist, null);
        songType.GetProperty("Moods").SetValue(song, moods.Split(','), null);
        songType.GetProperty("Genres").SetValue(song, genres.Split(','), null);
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

    private static object CallList(ListBox list, string method, params object[] args)
    {
        return list.GetType().GetMethod(method, Private).Invoke(list, args);
    }

    private static void SetIncluded(ListBox list, int index, bool value)
    {
        CallList(list, "SetIncluded", index, value);
    }

    private static void SetExcluded(ListBox list, int index, bool value)
    {
        CallList(list, "SetExcluded", index, value);
    }

    private static bool IsIncluded(ListBox list, int index)
    {
        return (bool)CallList(list, "IsIncluded", index);
    }

    private static bool IsExcluded(ListBox list, int index)
    {
        return (bool)CallList(list, "IsExcluded", index);
    }

    private static List<string> IncludedTags(ListBox list)
    {
        return (List<string>)list.GetType().GetProperty("IncludedTags", Private).GetValue(list, null);
    }

    private static void ClickTag(ListBox list, int index, MouseButtons button)
    {
        var bounds = list.GetItemRectangle(index);
        var args = new MouseEventArgs(button, 1, bounds.Left + 12, bounds.Top + bounds.Height / 2, 0);
        list.GetType().GetMethod("OnMouseDown", Private).Invoke(list, new object[] { args });
    }

    private static int[] ReadChartCounts(Control chart)
    {
        return (int[])chart.GetType().GetField("counts", Private).GetValue(chart);
    }

    private static int ReadChartThreshold(Control chart)
    {
        return (int)chart.GetType().GetField("threshold", Private).GetValue(chart);
    }

    [STAThread]
    private static int Main(string[] args)
    {
        string testLocalAppData = Path.Combine(Path.GetTempPath(), "ncm-tag-playlist-" + Guid.NewGuid().ToString("N"));
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
            using (var form = (Form)Activator.CreateInstance(formType, new object[] { false }))
            {
                var events = (System.ComponentModel.EventHandlerList)typeof(Control).GetProperty("Events", Private).GetValue(form, null);
                foreach (var field in typeof(Form).GetFields(BindingFlags.Static | BindingFlags.NonPublic))
                {
                    if (field.Name.IndexOf("shown", StringComparison.OrdinalIgnoreCase) < 0) continue;
                    var key = field.GetValue(null);
                    if (events[key] != null) events.RemoveHandler(key, events[key]);
                }
                form.StartPosition = FormStartPosition.Manual;
                form.Location = new Point(-10000, -10000);
                form.Show();
                Call(form, "NavigateTo", 1);
                form.Size = form.MinimumSize;
                form.PerformLayout();
                Pump();

                var header = Find(form, "GenreHeader");
                var toolbar = Find(form, "GenreToolbar");
                Check(header != null && toolbar != null, "genre header and toolbar exist");
                var tagButton = Find(form, "TagPlaylistButton") as Button;
                var analyzeButton = Find(form, "AnalyzeGenresButton") as Button;
                var exportButton = Find(form, "ExportGenresButton") as Button;
                var hint = Find(form, "TagPlaylistHint") as Label;
                Check(tagButton != null && analyzeButton != null && exportButton != null && hint != null, "all genre action controls exist");
                Check(tagButton.Enabled, "tag playlist button is enabled when the analysis script exists");
                Check(analyzeButton.Right <= exportButton.Left, "header buttons do not overlap");
                Check(exportButton.Right <= header.ClientSize.Width + 1, "header buttons stay inside the header");
                Check(tagButton.Right <= toolbar.ClientSize.Width + 1, "tag playlist button stays inside the toolbar");
                Check(hint.Right <= tagButton.Left - 4, "toolbar hint clears the tag playlist button");

                var songs = (IList)Activator.CreateInstance(typeof(List<>).MakeGenericType(songType));
                songs.Add(NewSong(songType, "1", "Song One", "Artist A", "治愈,放松", "流行"));
                songs.Add(NewSong(songType, "2", "Song Two", "Artist B", "悲伤", "摇滚"));
                songs.Add(NewSong(songType, "3", "Song Three", "Artist C", "治愈", "摇滚"));
                songs.Add(NewSong(songType, "4", "Song Four", "Artist D", "平静", "轻音乐"));
                var snapshot = Activator.CreateInstance(snapshotType);
                snapshotType.GetField("Songs", Private).SetValue(snapshot, songs);
                snapshotType.GetField("Liked", Private).SetValue(snapshot, 4);
                snapshotType.GetField("Tagged", Private).SetValue(snapshot, 4);

                var timer = new Timer { Interval = 150 };
                int attempts = 0;
                timer.Tick += delegate {
                    if (failure != null) return;
                    Form dialog = null;
                    foreach (Form open in Application.OpenForms)
                        if (open.Text == "按标签生成红心歌单") { dialog = open; break; }
                    if (dialog == null)
                    {
                        if (++attempts > 30)
                        {
                            failure = new Exception("tag playlist dialog did not open");
                            timer.Stop();
                        }
                        return;
                    }
                    timer.Stop();
                    try
                    {
                        var moodList = Find(dialog, "MoodTagList") as ListBox;
                        var genreList = Find(dialog, "GenreTagList") as ListBox;
                        var summary = Find(dialog, "TagPlaylistSummary") as Label;
                        var nameBox = Find(dialog, "TagPlaylistName") as TextBox;
                        var privacy = Find(dialog, "TagPlaylistPrivate") as CheckBox;
                        var matchAuto = Find(dialog, "TagMatchAuto") as CheckBox;
                        var matchThreshold = Find(dialog, "TagMatchThreshold") as NumericUpDown;
                        var matchHint = Find(dialog, "TagMatchHint") as Label;
                        var chart = Find(dialog, "TagMatchChart");
                        var create = Find(dialog, "TagPlaylistCreate") as Button;
                        var cancel = Find(dialog, "TagPlaylistCancel") as Button;
                        Check(moodList != null && genreList != null && summary != null && nameBox != null
                            && privacy != null && matchAuto != null && matchThreshold != null && matchHint != null
                            && chart != null && create != null && cancel != null,
                            "tag playlist dialog exposes every control");
                        Check(moodList.DrawMode == DrawMode.OwnerDrawFixed && genreList.DrawMode == DrawMode.OwnerDrawFixed,
                            "tag lists use owner-drawn tri-state markers");
                        Check(moodList.ItemHeight >= 24 && genreList.ItemHeight >= 24, "tag rows keep usable height");
                        Check(moodList.Items.Count > 1 && genreList.Items.Count > 1, "mood and genre catalogs are populated");
                        Check(TagName(moodList.Items[0]).Length > 0 && Convert.ToString(moodList.Items[0]).Contains("("), "tag counts are visible");
                        Check(matchAuto.Checked, "automatic half threshold is selected by default");
                        Check(!matchThreshold.Enabled, "threshold spinner is locked while automatic");
                        Check(privacy.Checked, "new playlists are private by default");

                        int healing = FindTag(moodList, "治愈");
                        int relaxing = FindTag(moodList, "放松");
                        int sad = FindTag(moodList, "悲伤");
                        int calm = FindTag(moodList, "平静");
                        int rock = FindTag(genreList, "摇滚");
                        int light = FindTag(genreList, "轻音乐");
                        Check(healing >= 0 && relaxing >= 0 && sad >= 0 && calm >= 0, "near-synonym moods are available");
                        Check(rock >= 0 && light >= 0, "genre exclusions are available");

                        ClickTag(genreList, light, MouseButtons.Left);
                        Pump();
                        Check(IsIncluded(genreList, light) && !IsExcluded(genreList, light), "left click marks a tag included");
                        ClickTag(genreList, light, MouseButtons.Left);
                        Pump();
                        Check(!IsIncluded(genreList, light) && !IsExcluded(genreList, light), "left click clears a tag");
                        ClickTag(genreList, light, MouseButtons.Right);
                        Pump();
                        Check(IsExcluded(genreList, light) && !IsIncluded(genreList, light), "right click marks a hard exclusion");
                        ClickTag(genreList, light, MouseButtons.Left);
                        Pump();
                        Check(IsIncluded(genreList, light) && !IsExcluded(genreList, light), "left click converts an exclusion to inclusion");
                        ClickTag(genreList, light, MouseButtons.Left);
                        Pump();
                        Check(!IsIncluded(genreList, light) && !IsExcluded(genreList, light), "left click clears the converted inclusion");
                        ClickTag(genreList, light, MouseButtons.Right);
                        Pump();
                        ClickTag(genreList, light, MouseButtons.Right);
                        Pump();
                        Check(!IsExcluded(genreList, light), "right click again clears the exclusion");

                        SetIncluded(moodList, healing, true);
                        SetIncluded(moodList, relaxing, true);
                        Pump();
                        Check(IncludedTags(moodList).Count == 2, "mood list keeps multiple selections");
                        Check(matchThreshold.Value == 1 && matchThreshold.Maximum == 2, "two tags default to at least one");
                        Check(create.Enabled, "create button enables after a matching multi-select");
                        Check(summary.Text.Contains("至少命中 1 个") && !summary.Text.Contains("匹配 0 首"), "summary previews non-zero matches");
                        Check(nameBox.Text.StartsWith("红心 · ", StringComparison.Ordinal) && nameBox.Text.Contains("治愈"),
                            "default playlist name follows the selected moods");
                        Check(ReadChartThreshold(chart) == 1 && ReadChartCounts(chart).Length == 2,
                            "chart shows cumulative counts and the automatic threshold");

                        SetIncluded(moodList, sad, true);
                        SetIncluded(moodList, calm, true);
                        Pump();
                        Check(matchThreshold.Value == 2 && matchThreshold.Maximum == 4, "four tags default to half rounded up");
                        Check(ReadChartThreshold(chart) == 2 && ReadChartCounts(chart).Length == 4,
                            "chart follows the four-tag half threshold");

                        SetExcluded(genreList, rock, true);
                        Pump();
                        Check(IsExcluded(genreList, rock), "hard exclusion state is retained");
                        Check(summary.Text.Contains("硬排除 1 个标签"), "summary reports hard exclusions");
                        var excludedCounts = ReadChartCounts(chart);
                        Check(excludedCounts.Length == 4 && excludedCounts[0] == 2 && excludedCounts[1] == 1
                            && excludedCounts[2] == 0 && excludedCounts[3] == 0,
                            "chart removes songs containing the excluded genre");
                        SetExcluded(genreList, rock, false);
                        Pump();
                        var restoredCounts = ReadChartCounts(chart);
                        Check(restoredCounts[0] == 4 && restoredCounts[1] == 1, "clearing exclusion restores chart counts");

                        SetIncluded(moodList, calm, false);
                        Pump();
                        Check(matchThreshold.Value == 2 && matchThreshold.Maximum == 3, "three tags default to two");
                        matchAuto.Checked = false;
                        Pump();
                        Check(matchThreshold.Enabled, "manual threshold unlocks the spinner");
                        matchThreshold.Value = 1;
                        Pump();
                        Check(summary.Text.Contains("至少命中 1 个"), "manual threshold updates the summary");

                        dialog.Size = dialog.MinimumSize;
                        dialog.PerformLayout();
                        Pump();
                        var client = dialog.RectangleToScreen(dialog.ClientRectangle);
                        foreach (var control in new Control[] { moodList, genreList, chart, summary, nameBox, privacy, matchAuto, matchThreshold, matchHint, create, cancel })
                            Check(Inside(client, ScreenRect(control)), "dialog control stays visible at minimum size: " + control.Name);
                        Check(ScreenRect(create).Right <= client.Right + 1 && ScreenRect(cancel).Right <= client.Right + 1, "dialog footer stays inside the window");
                        Console.WriteLine("tag list heights at minimum size: mood=" + moodList.Height + ", genre=" + genreList.Height + ", client=" + dialog.ClientSize.Height);
                        Check(moodList.Height >= 120 && genreList.Height >= 120, "tag lists keep usable height at minimum size");
                        Check(chart.Height >= 100, "match chart keeps usable height at minimum size");
                        Check(ScreenRect(matchAuto).Right <= ScreenRect(matchThreshold).Left, "automatic switch clears the threshold spinner");
                        Check(ScreenRect(matchThreshold).Right <= ScreenRect(matchHint).Left, "threshold controls do not overlap");
                        dialog.DialogResult = DialogResult.Cancel;
                        dialog.Close();
                    }
                    catch (Exception ex)
                    {
                        failure = ex;
                        try { dialog.DialogResult = DialogResult.Cancel; dialog.Close(); } catch { }
                    }
                };
                timer.Start();
                var method = formType.GetMethod("ShowTagPlaylistDialog", Private);
                var result = method.Invoke(form, new[] { snapshot });
                timer.Stop();
                timer.Dispose();
                Check(failure == null, failure == null ? "dialog validation completed" : failure.Message);
                Check(result == null, "cancelled dialog returns no playlist request");
                form.Close();
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
