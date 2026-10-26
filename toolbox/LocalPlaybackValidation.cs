// Isolated STA UI validation; startup network and lyric tasks are suppressed.
// Args: toolbox EXE, real collector JSON report, screenshot folder.
using System;
using System.Collections.Generic;
using System.Drawing;
using System.IO;
using System.Reflection;
using System.Web.Script.Serialization;
using System.Windows.Forms;

internal static class LocalPlaybackValidation
{
    private const BindingFlags Private = BindingFlags.Instance | BindingFlags.NonPublic;
    private static object Call(Form form, string name, params object[] args) { return form.GetType().GetMethod(name, Private).Invoke(form, args); }
    private static void Check(bool value, string message) { if (!value) throw new Exception(message); }
    [STAThread]
    private static int Main(string[] args)
    {
        try {
            Application.EnableVisualStyles();
            var type = Assembly.LoadFrom(Path.GetFullPath(args[0])).GetType("NeteaseToolbox.ToolboxForm", true);
            using (var form = (Form)Activator.CreateInstance(type, new object[] { false })) {
                var events = (System.ComponentModel.EventHandlerList)typeof(Control).GetProperty("Events", Private).GetValue(form, null);
                foreach (var field in typeof(Form).GetFields(BindingFlags.Static | BindingFlags.NonPublic)) {
                    if (field.Name.IndexOf("shown", StringComparison.OrdinalIgnoreCase) < 0) continue;
                    var key = field.GetValue(null); if (events[key] != null) events.RemoveHandler(key, events[key]);
                }
                form.StartPosition = FormStartPosition.Manual; form.Location = new Point(-10000,-10000);
                form.Show(); Call(form, "NavigateTo", 2);
                var tabs = (TabControl)form.Controls.Find("ProfileTabs", true)[0];
                tabs.SelectedIndex = tabs.TabPages.Count - 1;
                var data = new JavaScriptSerializer { MaxJsonLength = 64 * 1024 * 1024 }.Deserialize<Dictionary<string, object>>(File.ReadAllText(args[1]));
                Call(form, "ApplyLocalPlayback", data);
                var songs = (ListView)type.GetField("localPlaybackSongs", Private).GetValue(form);
                var sessions = (ListView)type.GetField("localPlaybackSessions", Private).GetValue(form);
                var evidence = (RichTextBox)type.GetField("localPlaybackEvidence", Private).GetValue(form);
                var timer = (Timer)type.GetField("localPlaybackTimer", Private).GetValue(form);
                Check(timer.Interval == 3 * 60 * 60 * 1000 && !timer.Enabled, "Three-hour timer is inactive before startup");
                Check(songs.Items.Count > 0 && songs.Columns[2].Text == "播放次数", "Unified counts populated");
                Check(sessions.Items.Count > 0 && sessions.Items[0].SubItems[3].Text == "10", "Partial replay counted with nine full repeats");
                Check(sessions.Items[0].SubItems[2].Text == "电脑", "Desktop source is labelled");
                var account = (ComboBox)form.Controls.Find("LocalPlaybackAccounts", true)[0];
                Check(account.SelectedItem != null, "Account selected");
                Directory.CreateDirectory(args[2]);
                form.PerformLayout(); Application.DoEvents();
                Capture(form, Path.Combine(args[2], "local-playback-totals.png"));
                ((TabControl)sessions.Parent.Parent).SelectedIndex = 1;
                sessions.Items[0].Selected = true; Application.DoEvents();
                Check(evidence.Text.Contains("25.6") && evidence.Text.Contains("ID "), "Per-play evidence visible");
                Capture(form, Path.Combine(args[2], "local-playback-sessions.png"));
                Call(form, "SetTheme", true); Application.DoEvents();
                Check(songs.ForeColor.ToArgb() == Color.FromArgb(31,41,55).ToArgb(), "Readable light table text");
                Capture(form, Path.Combine(args[2], "local-playback-light.png"));
                Call(form, "SetTheme", false);
                Call(form, "ReportTask", "本机播放采集", "部分失败", "测试一个数据源失败，另一个已保存");
                Check(form.Controls.Find("TaskStatus", true)[0].Text.Contains("部分失败"), "Partial failure visible");
                timer.Start(); Call(form, "StopLocalPlayback");
                Check((bool)type.GetField("localPlaybackStopped", Private).GetValue(form), "Exit stops future polling");
                type.GetField("allowExit", Private).SetValue(form, true); form.Close();
            }
            Console.WriteLine("PASS: real report, account selection, unified counts, repeat evidence, task feedback, timer shutdown and rendered UI");
            return 0;
        } catch (Exception ex) { Console.Error.WriteLine(ex); return 1; }
    }
    private static void Capture(Form form, string path)
    {
        using (var bitmap = new Bitmap(form.Width, form.Height)) {
            form.DrawToBitmap(bitmap, new Rectangle(Point.Empty, form.Size)); bitmap.Save(path);
        }
    }
}
