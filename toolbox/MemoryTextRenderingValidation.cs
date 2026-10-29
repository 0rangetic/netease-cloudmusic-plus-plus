// Visible-window regression: capture the live RichEdit surface, never WM_PRINT.
// Uses temporary appearance files and disables all startup work.
using System;
using System.ComponentModel;
using System.Drawing;
using System.IO;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using System.Windows.Forms;

internal static class MemoryTextRenderingValidation
{
    private const BindingFlags Private = BindingFlags.Instance | BindingFlags.NonPublic;
    [DllImport("user32.dll")] private static extern IntPtr SendMessage(IntPtr window, int message, IntPtr wParam, IntPtr lParam);
    [DllImport("dwmapi.dll")] private static extern int DwmFlush();
    private static object Call(object value, string method, params object[] args) { return value.GetType().GetMethod(method, Private | BindingFlags.Public).Invoke(value, args); }
    private static Control Find(Form form, string name) { return form.Controls.Find(name, true)[0]; }
    private static void Check(bool result, string message) { if (!result) throw new Exception(message); }
    private static void Settle()
    {
        // RichEdit completes smooth wheel scrolling on its own timers.
        for (int tick = 0; tick < 10; tick++) { Application.DoEvents(); Thread.Sleep(40); }
        Application.DoEvents(); DwmFlush();
    }
    private static Bitmap Capture(Control text)
    {
        Settle();
        text.Update(); // Finish a native paint already pending from smooth scroll.
        Settle();
        var image = new Bitmap(text.ClientSize.Width, text.ClientSize.Height);
        using (var graphics = Graphics.FromImage(image)) graphics.CopyFromScreen(text.PointToScreen(Point.Empty), Point.Empty, image.Size);
        return image;
    }
    private static void CheckClean(Form form, RichTextBox text, string output, string name)
    {
        using (var actual = Capture(text)) {
            int firstBefore = SendMessage(text.Handle, 0xCE, IntPtr.Zero, IntPtr.Zero).ToInt32();
            using (var background = new Bitmap(actual.Width, actual.Height)) {
                using (var graphics = Graphics.FromImage(background)) Call(form, "PaintWallpaperRegion", graphics, text, text.ClientRectangle);
                int ink = 0;
                for (int y = 3; y < actual.Height - 3; y++)
                    for (int x = 4; x < actual.Width - 24; x++)
                        if (actual.GetPixel(x, y) != background.GetPixel(x, y)) ink++;
                Check(text.TextLength == 0 ? ink == 0 : ink > 20, name + " must render visible native text (or exactly wallpaper when empty), ink=" + ink);
            }
            // Independent reference: explicitly erase the entire live surface,
            // then let RichEdit render the same content, font and scroll position.
            using (var graphics = Graphics.FromHwnd(text.Handle)) Call(form, "PaintWallpaperRegion", graphics, text, text.ClientRectangle);
            text.Invalidate(); text.Update();
            using (var clean = Capture(text)) {
                int different = 0;
                // Exclude native scrollbar, border and focused caret.
                for (int y = 3; y < actual.Height - 3; y++)
                    for (int x = 4; x < actual.Width - 24; x++)
                        if (actual.GetPixel(x, y) != clean.GetPixel(x, y)) different++;
                actual.Save(Path.Combine(output, name + ".png"));
                clean.Save(Path.Combine(output, name + "-clean.png"));
                int firstAfter = SendMessage(text.Handle, 0xCE, IntPtr.Zero, IntPtr.Zero).ToInt32();
                Check(different < 20, name + " has " + different + " stale/different pixels before explicit background erase; first visible line " + firstBefore + " -> " + firstAfter);
                Console.WriteLine("PASS: " + name + " (" + different + " changed pixels)");
            }
        }
    }
    [STAThread]
    private static int Main(string[] args)
    {
        string temporary = Path.Combine(Path.GetTempPath(), "ncm-memory-render-" + Guid.NewGuid());
        Directory.CreateDirectory(temporary);
        string output = Path.GetFullPath(args[1]); Directory.CreateDirectory(output);
        try {
            Application.EnableVisualStyles();
            Application.SetUnhandledExceptionMode(UnhandledExceptionMode.ThrowException);
            var type = Assembly.Load(File.ReadAllBytes(Path.GetFullPath(args[0]))).GetType("NeteaseToolbox.ToolboxForm", true);
            using (var form = (Form)Activator.CreateInstance(type, new object[] { false })) {
                var startupEvidence = (RichTextBox)Find(form, "MemoryRecallEvidence");
                startupEvidence.Select(0, 4);
                Console.WriteLine("Startup detail managed font: " + startupEvidence.Font + "; native font: " + startupEvidence.SelectionFont);
                type.GetField("customAppearancePath", Private).SetValue(form, Path.Combine(temporary, "custom.json"));
                type.GetField("themeSettingPath", Private).SetValue(form, Path.Combine(temporary, "theme.txt"));
                var events = (EventHandlerList)typeof(Control).GetProperty("Events", Private).GetValue(form, null);
                foreach (var field in typeof(Form).GetFields(BindingFlags.Static | BindingFlags.NonPublic)) {
                    if (field.Name.IndexOf("shown", StringComparison.OrdinalIgnoreCase) < 0) continue;
                    object key = field.GetValue(null);
                    if (events[key] != null) events.RemoveHandler(key, events[key]);
                }
                Call(form, "ResetCustomAppearance");
                string wallpaper = Path.Combine(temporary, "background.png");
                using (var image = new Bitmap(1200, 800)) {
                    using (var graphics = Graphics.FromImage(image)) {
                        graphics.Clear(Color.SteelBlue);
                        using (var brush = new SolidBrush(Color.Coral)) graphics.FillEllipse(brush, 100, 80, 600, 650);
                    }
                    image.Save(wallpaper);
                }
                Call(form, "InstallWallpaper", wallpaper);
                form.StartPosition = FormStartPosition.Manual; form.Location = new Point(40, 40);
                form.Size = new Size(1150, 850); form.TopMost = true;
                form.Show(); Call(form, "NavigateTo", 3); Settle();
                var evidence = (RichTextBox)Find(form, "MemoryRecallEvidence");
                var query = (RichTextBox)Find(form, "MemoryRecallQuery");
                Console.WriteLine("Initial detail font: " + evidence.Font);
                Check(evidence.Font.SizeInPoints == 10, "Detail uses the 10pt UI body size");
                object preferences = type.GetField("appearance", Private).GetValue(form);
                string family;
                try { using (var custom = new FontFamily("猫啃什锦黑")) family = custom.Name; }
                catch (ArgumentException) { using (var fallback = new FontFamily("SimSun")) family = fallback.Name; }
                preferences.GetType().GetProperty("FontFamily").SetValue(preferences, family, null);
                Call(form, "ApplyUiFonts", form);
                evidence.Text = "別の人の彼女になったよ — 秋葵AKIAOI\n专辑：别の人の彼女になったよ\n播放时间：2026-10-13 23:44:56\n设备：pc / 未知\n\n本机检索依据\n• 播放时间符合日期/时段线索";
                evidence.Select(evidence.Text.IndexOf("播放时间"), 4);
                Console.WriteLine("Configured font: " + evidence.Font + "; native Chinese font: " + evidence.SelectionFont);
                Check(evidence.SelectionFont != null && evidence.SelectionFont.FontFamily.Name == family, "Native Chinese detail font follows the UI font");
                evidence.Select(0, 0); query.Focus();
                CheckClean(form, evidence, output, "initial");
                var lines = new StringBuilder();
                for (int index = 0; index < 70; index++) lines.AppendLine(index + " 歌曲证据 • 別の人の彼女になったよ / 播放时间与歌词匹配");
                evidence.Text = lines.ToString();
                for (int scroll = 0; scroll < 8; scroll++) { SendMessage(evidence.Handle, 0x20A, new IntPtr(-120 << 16), IntPtr.Zero); Settle(); }
                CheckClean(form, evidence, output, "wheel-down");
                for (int scroll = 0; scroll < 5; scroll++) { SendMessage(evidence.Handle, 0x20A, new IntPtr(120 << 16), IntPtr.Zero); Settle(); }
                CheckClean(form, evidence, output, "wheel-up");
                evidence.ZoomFactor = 1.7f; CheckClean(form, evidence, output, "zoom-large");
                evidence.ZoomFactor = 0.8f; CheckClean(form, evidence, output, "zoom-small");
                evidence.ZoomFactor = 1;
                using (var font = new Font("SimSun", 14)) { evidence.Font = font; CheckClean(form, evidence, output, "font-large"); }
                evidence.Font = query.Font; CheckClean(form, evidence, output, "font-restored");
                evidence.Text = "仅剩这一行，旧的长文应当全部清除。";
                CheckClean(form, evidence, output, "replace-short");
                evidence.Select(0, 3);
                using (var wrongFont = new Font("SimSun", 20)) evidence.SelectionFont = wrongFont;
                evidence.Text = "换歌后正文应当使用当前 UI 字体。";
                evidence.Select(0, 3);
                Check(evidence.SelectionFont != null && evidence.SelectionFont.FontFamily.Name == evidence.Font.FontFamily.Name &&
                    Math.Abs(evidence.SelectionFont.SizeInPoints - evidence.Font.SizeInPoints) < 0.1f, "Replacing evidence resets stale character font and size");
                evidence.Select(0, 0); CheckClean(form, evidence, output, "replace-font");
                evidence.Clear(); CheckClean(form, evidence, output, "empty");
                evidence.Text = "缩放窗口后的检索证据";
                form.Size = new Size(980, 740); Settle(); CheckClean(form, evidence, output, "resize");
                Call(form, "SetTheme", true); CheckClean(form, evidence, output, "light");
                Call(form, "SetTheme", false); CheckClean(form, evidence, output, "dark");
                query.Text = "编辑检索文字\n第二行"; query.Select(0, 2);
                Check(query.SelectedText == "编辑", "Native selection works");
                query.SelectedText = "替换"; Check(query.Text.StartsWith("替换检索"), "Native editing works");
                evidence.Text = "只读检索证据"; SendMessage(evidence.Handle, 0x102, new IntPtr('X'), IntPtr.Zero);
                Check(evidence.Text == "只读检索证据", "Evidence stays read-only");
                type.GetField("allowExit", Private).SetValue(form, true); form.Close();
            }
            Console.WriteLine("PASS: live text rendering, native font, wheel, zoom, font size, replacement, resize, themes, editing and read-only behavior.");
            return 0;
        }
        catch (Exception ex) { Console.Error.WriteLine(ex); return 1; }
        finally { Directory.Delete(temporary, true); }
    }
}
