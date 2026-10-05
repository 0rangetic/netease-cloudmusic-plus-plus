// STA offscreen integration test: toolbox EXE, optional screenshot directory.
using System;
using System.ComponentModel;
using System.Drawing;
using System.IO;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Windows.Forms;

internal static class WallpaperTextValidation
{
    private const BindingFlags Private = BindingFlags.Instance | BindingFlags.NonPublic;
    [DllImport("user32.dll")] private static extern IntPtr SendMessage(IntPtr window, int message, IntPtr wParam, IntPtr lParam);
    [DllImport("user32.dll")] private static extern bool SetWindowPos(IntPtr window, IntPtr after, int x, int y, int width, int height, uint flags);
    [DllImport("user32.dll")] private static extern int GetWindowLong(IntPtr window, int index);
    private static object Call(object value, string method, params object[] args) { return value.GetType().GetMethod(method, Private | BindingFlags.Public).Invoke(value, args); }
    private static object Field(object value, string name) { return value.GetType().GetField(name, Private).GetValue(value); }
    private static Control Find(Form form, string name) { return form.Controls.Find(name, true)[0]; }
    private static void Check(bool result, string message) { if (!result) throw new Exception(message); }
    private static Bitmap Print(Control control)
    {
        var result = new Bitmap(control.ClientSize.Width, control.ClientSize.Height);
        using (var graphics = Graphics.FromImage(result)) {
            graphics.Clear(Color.Magenta);
            IntPtr dc = graphics.GetHdc();
            try { SendMessage(control.Handle, 0x318, dc, new IntPtr(4 | 8)); }
            finally { graphics.ReleaseHdc(dc); }
        }
        return result;
    }
    private static void CheckTextBackground(Form form, RichTextBox text)
    {
        Check((GetWindowLong(text.Handle, -20) & 0x20) != 0, text.Name + " uses native transparent text style");
        using (var actual = Print(text))
        using (var expected = new Bitmap(actual.Width, actual.Height)) {
            using (var graphics = Graphics.FromImage(expected)) Call(form, "PaintWallpaperRegion", graphics, text, text.ClientRectangle);
            int x = Math.Max(1, actual.Width - 45);
            for (int y = 3; y < actual.Height - 3; y += 7)
                Check(actual.GetPixel(x, y) == expected.GetPixel(x, y), text.Name + " wallpaper alignment at y=" + y + "; actual=" + actual.GetPixel(x, y) + "; expected=" + expected.GetPixel(x, y));
            // The short fixture text is at the left. Confirm native text is still
            // rendered instead of validating only the wallpaper-only portion.
            int ink = 0;
            for (int y = 0; y < Math.Min(26, actual.Height); y++)
                for (int px = 0; px < Math.Min(180, actual.Width); px++)
                    if (actual.GetPixel(px, y) != expected.GetPixel(px, y)) ink++;
            Check(ink > 5, text.Name + " preserves native visible text");
        }
    }
    private static void Snapshot(Form form, string directory, string name)
    {
        if (directory == null) return;
        Directory.CreateDirectory(directory);
        using (var bitmap = new Bitmap(form.Width, form.Height)) {
            form.DrawToBitmap(bitmap, new Rectangle(Point.Empty, form.Size));
            bitmap.Save(Path.Combine(directory, name + ".png"));
        }
    }
    [STAThread]
    private static int Main(string[] args)
    {
        string temporary = Path.Combine(Path.GetTempPath(), "ncm-wallpaper-text-" + Guid.NewGuid());
        Directory.CreateDirectory(temporary);
        try {
            Application.EnableVisualStyles();
            Application.SetUnhandledExceptionMode(UnhandledExceptionMode.ThrowException);
            var type = Assembly.LoadFrom(Path.GetFullPath(args[0])).GetType("NeteaseToolbox.ToolboxForm", true);
            using (var form = (Form)Activator.CreateInstance(type, new object[] { false })) {
                type.GetField("customAppearancePath", Private).SetValue(form, Path.Combine(temporary, "custom.json"));
                type.GetField("themeSettingPath", Private).SetValue(form, Path.Combine(temporary, "theme.txt"));
                var events = (EventHandlerList)typeof(Control).GetProperty("Events", Private).GetValue(form, null);
                foreach (var field in typeof(Form).GetFields(BindingFlags.Static | BindingFlags.NonPublic)) {
                    if (field.Name.IndexOf("shown", StringComparison.OrdinalIgnoreCase) < 0) continue;
                    var key = field.GetValue(null);
                    if (events[key] != null) events.RemoveHandler(key, events[key]);
                }
                Call(form, "ResetCustomAppearance");
                var query = (RichTextBox)Find(form, "MemoryRecallQuery");
                var path = (RichTextBox)Find(form, "MemoryRecallLifeRecallPath");
                var evidence = (RichTextBox)Find(form, "MemoryRecallEvidence");
                query.Text = "记得的歌曲\n夜晚听过的旋律";
                path.Text = "C:\\Example\\LifeRecall";
                evidence.Text = "检索结果\n歌曲、歌词命中与时间线证据";
                query.Select(0, 5);
                Check(query.SelectedText == "记得的歌曲", "Native text selection preserved");
                string imagePath = Path.Combine(temporary, "source.png");
                using (var image = new Bitmap(1280, 800)) {
                    using (var graphics = Graphics.FromImage(image)) {
                        for (int y = 0; y < image.Height; y++)
                            using (var brush = new SolidBrush(Color.FromArgb(30 + y % 170, 50 + y % 180, 60 + y % 160))) graphics.FillRectangle(brush, 0, y, image.Width, 1);
                        using (var brush = new SolidBrush(Color.Coral)) graphics.FillEllipse(brush, 50, 50, 650, 650);
                    }
                    image.Save(imagePath);
                }
                Call(form, "InstallWallpaper", imagePath);
                Check(query.Text.Contains("夜晚") && query.SelectedText == "记得的歌曲", "Enabling wallpaper preserves text and selection");
                form.StartPosition = FormStartPosition.Manual;
                form.Location = new Point(-30000, -30000);
                form.MaximumSize = new Size(3840, 2160);
                form.Show();
                SetWindowPos(form.Handle, IntPtr.Zero, -30000, -30000, 2560, 1500, 0x414);
                Call(form, "NavigateTo", 3);
                Application.DoEvents();
                foreach (bool light in new[] { false, true }) {
                    Call(form, "SetTheme", light);
                    query.Select(0, 0);
                    CheckTextBackground(form, query);
                    CheckTextBackground(form, path);
                    CheckTextBackground(form, evidence);
                    Snapshot(form, args.Length > 1 ? args[1] : null, light ? "memory-transparent-light" : "memory-transparent-dark");
                    Call(form, "NavigateTo", 4);
                    foreach (string card in new[] { "OutputSettingsCard", "MaintenanceCard" }) {
                        var panel = Find(form, card);
                        Check(panel.BackColor == Color.Transparent, card + " is transparent with wallpaper");
                        using (var actual = new Bitmap(panel.Width, panel.Height))
                        using (var expected = new Bitmap(panel.Width, panel.Height)) {
                            panel.DrawToBitmap(actual, new Rectangle(Point.Empty, panel.Size));
                            using (var graphics = Graphics.FromImage(expected)) Call(form, "PaintWallpaperRegion", graphics, panel, panel.ClientRectangle);
                            Check(actual.GetPixel(actual.Width - 35, 35) == expected.GetPixel(expected.Width - 35, 35), card + " matches global wallpaper position");
                        }
                    }
                    Snapshot(form, args.Length > 1 ? args[1] : null, light ? "settings-transparent-light" : "settings-transparent-dark");
                    Call(form, "NavigateTo", 3);
                }
                evidence.Text = string.Join("\n", new string[80]).Replace("\n", "歌曲证据与歌词命中\n");
                for (int input = 0; input < 100; input++) SendMessage(evidence.Handle, 0x20A, new IntPtr(-120 << 16), IntPtr.Zero);
                CheckTextBackground(form, evidence);
                evidence.Select(evidence.TextLength, 0);
                evidence.ScrollToCaret();
                // Restore short fixtures for the empty right-side pixel checks.
                evidence.Text = "滚动后的检索结果\n保留可选择复制的文字";
                var originalQueryFont = query.Font;
                var originalEvidenceFont = evidence.Font;
                using (var largerFont = new Font("SimSun", 14)) {
                    query.Font = largerFont; evidence.Font = largerFont;
                    CheckTextBackground(form, query);
                    CheckTextBackground(form, evidence);
                    query.Font = originalQueryFont; evidence.Font = originalEvidenceFont;
                }
                var fitSelector = (ComboBox)Find(form, "WallpaperFitSelector");
                for (int mode = 0; mode < 5; mode++) {
                    fitSelector.SelectedIndex = mode;
                    CheckTextBackground(form, query);
                    CheckTextBackground(form, evidence);
                }
                fitSelector.SelectedIndex = 0;
                for (int cycle = 0; cycle < 4; cycle++) {
                    SetWindowPos(form.Handle, IntPtr.Zero, -30000, -30000, cycle % 2 == 0 ? 1040 : 2560, cycle % 2 == 0 ? 740 : 1500, 0x414);
                    Application.DoEvents();
                    CheckTextBackground(form, query);
                    CheckTextBackground(form, evidence);
                    query.Select(0, 5);
                    Call(form, "ClearWallpaper");
                    Check((GetWindowLong(query.Handle, -20) & 0x20) == 0 && query.SelectedText == "记得的歌曲", "Removing wallpaper restores solid native text without losing selection");
                    foreach (string card in new[] { "OutputSettingsCard", "MaintenanceCard" }) Check(Find(form, card).BackColor == query.BackColor, "Solid card theme restored: " + card);
                    Call(form, "InstallWallpaper", imagePath);
                    query.Select(0, 0);
                }
                query.Select(query.TextLength, 0);
                SendMessage(query.Handle, 0x102, new IntPtr('A'), IntPtr.Zero);
                Check(query.Text.EndsWith("A"), "Native keyboard editing works after wallpaper toggles");
                query.Select(0, 5);
                // Inspect the native selected text without replacing the user's
                // clipboard. Copy/paste remains RichEdit's built-in behavior.
                Check(query.SelectedText == "记得的歌曲", "Native selected text remains available after editing and appearance changes");
                // Read-only controls must not accept character messages.
                string originalPath = path.Text, originalEvidence = evidence.Text;
                SendMessage(path.Handle, 0x102, new IntPtr('X'), IntPtr.Zero);
                SendMessage(evidence.Handle, 0x102, new IntPtr('X'), IntPtr.Zero);
                Check(path.Text == originalPath && evidence.Text == originalEvidence, "Path and evidence remain read-only");
                var queryHandle = query.Handle;
                var evidenceHandle = evidence.Handle;
                form.Close();
                Check(!form.Visible && !form.IsDisposed && query.Handle == queryHandle && evidence.Handle == evidenceHandle, "Closing to tray retains native text windows");
                Call(form, "ShowMainWindow");
                form.Location = new Point(-30000, -30000);
                Check(query.Text.EndsWith("A") && evidence.Text == originalEvidence, "Tray restore retains input and evidence");
                Call(form, "ResetCustomAppearance");
                Check((GetWindowLong(query.Handle, -20) & 0x20) == 0, "Reset restores ordinary text surfaces");
            }
            Console.WriteLine("PASS: aligned transparent cards and native text, themes, wheel/resize, appearance toggles, editing, selection, read-only behavior and tray restore.");
            return 0;
        }
        catch (Exception exception) { Console.Error.WriteLine(exception); return 1; }
        finally { Directory.Delete(temporary, true); }
    }
}
