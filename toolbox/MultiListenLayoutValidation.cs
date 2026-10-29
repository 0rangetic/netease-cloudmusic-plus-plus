// Isolated STA layout validation for the 多人一起听 page.
// Args: toolbox EXE path, screenshot folder. Startup network, autostart, lyric and
// collector tasks are suppressed; LOCALAPPDATA is redirected to a fresh temp directory
// so no real session or setting is ever read or modified. The page's status refresh runs
// against a sandbox WITHOUT multi-listen\node, so it fails fast without spawning anything.
using System;
using System.Collections.Generic;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Windows.Forms;

internal static class MultiListenLayoutValidation
{
    private const BindingFlags Private = BindingFlags.Instance | BindingFlags.NonPublic;
    private static object Call(Form form, string name, params object[] args) { return form.GetType().GetMethod(name, Private).Invoke(form, args); }
    private static object Field(Form form, string name) { return form.GetType().GetField(name, Private).GetValue(form); }
    private static void Check(bool value, string message) { if (!value) throw new Exception(message); Console.WriteLine("PASS " + message); }
    private static Control Find(Form form, string name)
    {
        var found = form.Controls.Find(name, true);
        return found.Length == 0 ? null : found[0];
    }
    private static IEnumerable<Control> Descendants(Control root)
    {
        foreach (Control child in root.Controls) {
            yield return child;
            foreach (var nested in Descendants(child)) yield return nested;
        }
    }
    [STAThread]
    private static int Main(string[] args)
    {
        string testLocalAppData = Path.Combine(Path.GetTempPath(),
            "ncm-multi-listen-layout-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(testLocalAppData);
        string originalLocalAppData = Environment.GetEnvironmentVariable("LOCALAPPDATA");
        Environment.SetEnvironmentVariable("LOCALAPPDATA", testLocalAppData);
        try {
            Application.EnableVisualStyles();
            // Load from bytes: this workspace can be classified as a remote location,
            // which blocks Assembly.LoadFrom on the freshly built EXE.
            var type = Assembly.Load(File.ReadAllBytes(Path.GetFullPath(args[0]))).GetType("NeteaseToolbox.ToolboxForm", true);
            using (var form = (Form)Activator.CreateInstance(type, new object[] { false })) {
                // Suppress every Shown handler: network session checks, autostart registration,
                // lyric status, playback collectors and the daily-signin initialiser itself.
                var events = (System.ComponentModel.EventHandlerList)typeof(Control).GetProperty("Events", Private).GetValue(form, null);
                foreach (var field in typeof(Form).GetFields(BindingFlags.Static | BindingFlags.NonPublic)) {
                    if (field.Name.IndexOf("shown", StringComparison.OrdinalIgnoreCase) < 0) continue;
                    var key = field.GetValue(null); if (events[key] != null) events.RemoveHandler(key, events[key]);
                }
                form.StartPosition = FormStartPosition.Manual; form.Location = new Point(-10000, -10000);
                form.Show();
                Call(form, "NavigateTo", 7);
                form.PerformLayout(); Application.DoEvents();

                var nav = Find(form, "MultiListenNavigation");
                Check(nav != null, "多人一起听导航按钮已注册");
                Check(nav.Visible, "多人一起听导航按钮可见");
                Check(nav.Bounds.Width > 0 && nav.Bounds.Height > 0, "多人一起听导航有可见尺寸 " + nav.Bounds);

                var page = Find(form, "MultiListenPage");
                Check(page != null, "多人一起听页面已注册到导航");
                Check(page.Visible, "导航到多人一起听后页面可见");

                foreach (var name in new[] { "MultiListenDescription", "MultiListenStart", "MultiListenRefresh",
                    "MultiListenStop", "MultiListenOpenLog", "MultiListenStatus", "MultiListenDetail", "MultiListenMessages" }) {
                    var control = Find(form, name);
                    Check(control != null, name + " 存在");
                    Check(control.Visible, name + " 可见");
                    Check(control.Bounds.Width > 0 && control.Bounds.Height > 0, name + " 有可见尺寸 " + control.Bounds);
                }

                var startButton = (Button)Find(form, "MultiListenStart");
                var stopButton = (Button)Find(form, "MultiListenStop");
                Check(startButton.Enabled, "“启动并连接”初始可用");
                Check(stopButton.Enabled == false, "未安装补丁时“退出并停用”不可用");
                Check(stopButton.Text.Length > 0, "停用按钮文字正常：" + stopButton.Text);

                var timer = (System.Windows.Forms.Timer)Field(form, "multiListenTimer");
                Check(timer.Enabled, "进入页面后状态轮询定时器已启动");
                Check(timer.Interval == 5000, "轮询间隔为 5 秒");

                // Fixed-height labels must not cut their text off.
                foreach (var label in Descendants(page).OfType<Label>()) {
                    if (label.AutoSize) continue;
                    int needed = label.GetPreferredSize(new Size(label.Width, 0)).Height;
                    Check(needed <= label.Height + 1, "标签不截断文字：" + (label.Name.Length > 0 ? label.Name : label.Text.Substring(0, Math.Min(12, label.Text.Length)))
                        + " 需要 " + needed + "px，实际 " + label.Height + "px");
                }

                // Buttons must stay readable in both themes, including after a switch.
                CheckButtons(page, "深色");

                Directory.CreateDirectory(args[1]);
                form.PerformLayout(); Application.DoEvents();
                Capture(form, Path.Combine(args[1], "multi-listen-page-default.png"));
                Capture(page, Path.Combine(args[1], "multi-listen-page-only.png"));

                Call(form, "SetTheme", true); Application.DoEvents();
                var status = (Label)Find(form, "MultiListenStatus");
                Check(Contrast(status.ForeColor, status.BackColor) >= 4.5, "浅色主题状态文字对比度符合 4.5:1");
                CheckButtons(page, "浅色");
                Capture(page, Path.Combine(args[1], "multi-listen-page-light.png"));
                Call(form, "SetTheme", false); Application.DoEvents();

                // Minimum supported window size must stay usable.
                form.Size = new Size(780, 600); form.PerformLayout(); Application.DoEvents();
                foreach (var label in Descendants(page).OfType<Label>()) {
                    Check(label.Right <= page.ClientSize.Width - 3, "最小窗口下标签不横向溢出：" + label.Name);
                    if (!label.AutoSize) Check(label.GetPreferredSize(new Size(label.Width, 0)).Height <= label.Height + 1, "最小窗口下标签不截断：" + label.Name);
                }
                Capture(form, Path.Combine(args[1], "multi-listen-page-minimum.png"));

                // Sidebar: navigation buttons and account UI must not overlap, and the
                // sidebar must be scrollable at minimum size so every entry stays reachable.
                var sidebar = Find(form, "Sidebar");
                Check(sidebar != null, "侧栏存在");
                var sidebarScrollable = sidebar as ScrollableControl;
                Check(sidebarScrollable != null && sidebarScrollable.AutoScroll, "侧栏允许滚动");
                Check(!sidebarScrollable.HorizontalScroll.Visible, "侧栏无需横向滚动即可操作导航");
                var navButtons = new[] { "ConversionNavigation", "GenreNavigation", "ProfileNavigation",
                    "MemoryRecallNavigation", "SettingsNavigation", "AudioForwardingNavigation", "DailySigninNavigation", "MultiListenNavigation" };
                var accountControls = new[] { "NeteaseLoginButton", "NeteaseAccountStatus" };
                foreach (var navName in navButtons) {
                    var navControl = Find(form, navName);
                    if (navControl == null) continue;
                    foreach (var accountName in accountControls) {
                        var accountControl = Find(form, accountName);
                        if (accountControl == null) continue;
                        bool noOverlap = navControl.Bottom <= accountControl.Top
                            || navControl.Top >= accountControl.Bottom
                            || navControl.Right <= accountControl.Left
                            || navControl.Left >= accountControl.Right;
                        Check(noOverlap, "导航与账号不重叠：" + navName + " " + navControl.Bounds + " vs " + accountName + " " + accountControl.Bounds);
                    }
                }

                // Task feedback must be visible in the status bar and the message log.
                Call(form, "ReportTask", "多人一起听", "部分失败", "布局校验用消息");
                Check(form.Controls.Find("TaskStatus", true)[0].Text.Contains("部分失败"), "任务状态栏显示多人一起听反馈");

                // Leaving the page and returning must not throw or double-start the timer.
                Call(form, "NavigateTo", 0); Application.DoEvents();
                Call(form, "NavigateTo", 7); Application.DoEvents();
                Check(((System.Windows.Forms.Timer)Field(form, "multiListenTimer")).Enabled, "切走再切回后轮询定时器仍在运行");

                type.GetField("allowExit", Private).SetValue(form, true); form.Close();
            }
            Console.WriteLine("PASS: 多人一起听页面布局、文字、主题、侧栏与任务反馈");
            return 0;
        } catch (Exception ex) { Console.Error.WriteLine(ex); return 1; }
        finally {
            try { Environment.SetEnvironmentVariable("LOCALAPPDATA", originalLocalAppData); } catch { }
        }
    }
    private static void CheckButtons(Control root, string themeName)
    {
        foreach (var button in Descendants(root).OfType<Button>()) {
            double contrast = Math.Abs(button.ForeColor.GetBrightness() - button.BackColor.GetBrightness());
            Console.WriteLine("BUTTON[" + themeName + "] " + button.Text + " role=" + button.AccessibleDescription
                + " fore=" + button.ForeColor + " back=" + button.BackColor + " contrast=" + contrast.ToString("F2"));
            Check(contrast >= 0.25, themeName + " 主题按钮文字对比度：" + button.Text + " = " + contrast.ToString("F2"));
        }
    }
    private static double Contrast(Color first, Color second)
    {
        Func<int, double> channel = value => value / 255.0 <= 0.04045 ? value / 255.0 / 12.92 : Math.Pow((value / 255.0 + 0.055) / 1.055, 2.4);
        Func<Color, double> luminance = color => 0.2126 * channel(color.R) + 0.7152 * channel(color.G) + 0.0722 * channel(color.B);
        return (Math.Max(luminance(first), luminance(second)) + 0.05) / (Math.Min(luminance(first), luminance(second)) + 0.05);
    }
    private static void Capture(Control control, string path)
    {
        using (var bitmap = new Bitmap(Math.Max(1, control.Width), Math.Max(1, control.Height))) {
            control.DrawToBitmap(bitmap, new Rectangle(Point.Empty, control.Size)); bitmap.Save(path);
        }
        Console.WriteLine("CAPTURE " + path + " " + control.Width + "x" + control.Height);
    }
}
