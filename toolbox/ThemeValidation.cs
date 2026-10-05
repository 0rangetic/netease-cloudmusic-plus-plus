// Compile as a separate console program referencing System.Windows.Forms and
// System.Drawing. Pass the built toolbox EXE and an optional screenshot folder.
// Creates forms without showing the main window or starting account/lyric tasks.
using System;
using System.Drawing;
using System.IO;
using System.Reflection;
using System.Windows.Forms;

internal static class ThemeValidation
{
    private static readonly BindingFlags Private = BindingFlags.Instance | BindingFlags.NonPublic;
    private static void Check(bool condition, string message)
    {
        if (!condition) throw new Exception(message);
    }
    private static object Call(Form form, string name, params object[] args)
    {
        return form.GetType().GetMethod(name, Private).Invoke(form, args);
    }
    private static Control Find(Control parent, string name)
    {
        return parent.Controls.Find(name, true)[0];
    }
    [STAThread]
    private static int Main(string[] args)
    {
        string temporary = Path.Combine(Path.GetTempPath(), "ncm-theme-test-" + Guid.NewGuid());
        Directory.CreateDirectory(temporary);
        try {
            Application.EnableVisualStyles();
            Application.SetCompatibleTextRenderingDefault(false);
            var type = Assembly.LoadFrom(Path.GetFullPath(args[0])).GetType("NeteaseToolbox.ToolboxForm", true);
            using (var form = (Form)Activator.CreateInstance(type, new object[] { false })) {
                var handle = form.Handle;
                type.GetField("themeSettingPath", Private).SetValue(form, Path.Combine(temporary, "appearance.txt"));
                type.GetField("customAppearancePath", Private).SetValue(form, Path.Combine(temporary, "appearance-custom.json"));
                Call(form, "ResetCustomAppearance");
                Call(form, "LoadTheme");
                var lyricToggle = Find(form, "LyricToggleButton");
                var playbackHistory = Find(form, "PlaybackHistoryButton");
                var logButton = Find(form, "LogButton");
                Check(playbackHistory.Top == lyricToggle.Top && playbackHistory.Top == logButton.Top,
                    "Topbar action buttons stay vertically aligned");
                var selector = (ComboBox)Find(form, "ThemeSelector");
                Check(selector.SelectedIndex == 0, "Missing preference should default to dark");
                var darkBackground = form.BackColor;
                var lightBackground = Color.FromArgb(244, 246, 250);
                for (int repeat = 0; repeat < 3; repeat++) {
                    selector.SelectedIndex = 1;
                    Check(form.BackColor == lightBackground, "Light background");
                    Check(File.ReadAllText(Path.Combine(temporary, "appearance.txt")).Trim() == "light", "Persist light preference");
                    for (int page = 0; page < 5; page++) {
                        Call(form, "NavigateTo", page);
                        Check(Find(form, "PageHost").BackColor == lightBackground, "Light page host");
                    }
                    Check(Find(form, "MemoryRecallQuery").BackColor.ToArgb() == Color.White.ToArgb(), "Light memory input");
                    Check(Find(form, "ConvertButton").ForeColor.ToArgb() == Color.White.ToArgb(), "Primary text remains white");
                    Call(form, "ReportTask", "验证", "部分失败", "测试状态");
                    Check(Find(form, "TaskStatus").ForeColor == Color.FromArgb(178, 53, 43), "Readable light error");
                    selector.SelectedIndex = 0;
                    Check(form.BackColor == darkBackground, "Restore dark background");
                    Check(File.ReadAllText(Path.Combine(temporary, "appearance.txt")).Trim() == "dark", "Persist dark preference");
                }
                File.WriteAllText(Path.Combine(temporary, "appearance.txt"), "light");
                Call(form, "LoadTheme");
                Check(selector.SelectedIndex == 1 && form.BackColor == lightBackground, "Restore saved light preference");
                using (var dialog = new Form()) {
                    var input = new TextBox();
                    dialog.Controls.Add(input);
                    Call(form, "PrepareThemedDialog", dialog);
                    Check(input.BackColor.ToArgb() == Color.White.ToArgb(), "Light dialog input");
                    Check(input.ForeColor == Color.FromArgb(31, 41, 55), "Light dialog text");
                }
                // Save failure must be visible while the theme still takes effect.
                type.GetField("themeSettingPath", Private).SetValue(form, temporary);
                selector.SelectedIndex = 0;
                Check(Find(form, "TaskStatus").Text.Contains("部分失败"), "Preference failure is visible");
                type.GetField("themeSettingPath", Private).SetValue(form, Path.Combine(temporary, "appearance.txt"));
                if (args.Length > 1) {
                    Directory.CreateDirectory(args[1]);
                    selector.SelectedIndex = 1;
                    // Suppress startup work before showing the isolated test form.
                    var events = (System.ComponentModel.EventHandlerList)typeof(Control)
                        .GetProperty("Events", Private).GetValue(form, null);
                    foreach (var field in typeof(Form).GetFields(BindingFlags.Static | BindingFlags.NonPublic)) {
                        if (field.Name.IndexOf("shown", StringComparison.OrdinalIgnoreCase) < 0) continue;
                        var eventKey = field.GetValue(null);
                        if (events[eventKey] != null) events.RemoveHandler(eventKey, events[eventKey]);
                    }
                    form.Show();
                    Call(form, "NavigateTo", 4);
                    form.PerformLayout();
                    using (var bitmap = new Bitmap(form.Width, form.Height)) {
                        form.DrawToBitmap(bitmap, new Rectangle(Point.Empty, form.Size));
                        bitmap.Save(Path.Combine(args[1], "theme-light.png"));
                    }
                    selector.SelectedIndex = 0;
                    using (var bitmap = new Bitmap(form.Width, form.Height)) {
                        form.DrawToBitmap(bitmap, new Rectangle(Point.Empty, form.Size));
                        bitmap.Save(Path.Combine(args[1], "theme-dark.png"));
                    }
                }
            }
            Console.WriteLine("PASS: theme switching, all pages, status contrast, persistence, dialogs and save failure.");
            return 0;
        }
        catch (Exception ex) { Console.Error.WriteLine(ex); return 1; }
        finally { Directory.Delete(temporary, true); }
    }
}
