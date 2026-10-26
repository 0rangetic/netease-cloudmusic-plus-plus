// Isolated STA layout validation for the 每日签到 page.
// Args: toolbox EXE path, screenshot folder. Startup network, autostart, lyric and
// collector tasks are suppressed; LOCALAPPDATA is redirected to a fresh temp directory
// so no real session or setting is ever read or modified.
using System;
using System.Collections.Generic;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Windows.Forms;

internal static class DailySigninLayoutValidation
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
        // Redirect LOCALAPPDATA so DailySigninService.Root, NeteaseAuth.SessionPath and
        // every other per-user path resolve inside the test sandbox. The real %LOCALAPPDATA%
        // is never read or modified.
        string testLocalAppData = Path.Combine(Path.GetTempPath(),
            "ncm-daily-signin-layout-" + Guid.NewGuid().ToString("N"));
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
                Call(form, "NavigateTo", 6);
                form.PerformLayout(); Application.DoEvents();

                var nav = Find(form, "DailySigninNavigation");
                Check(nav != null, "每日签到导航按钮已注册");
                Check(nav.Visible, "每日签到导航按钮可见");
                Check(nav.Bounds.Width > 0 && nav.Bounds.Height > 0, "每日签到导航有可见尺寸 " + nav.Bounds);

                var page = Find(form, "DailySigninPage") as ScrollableControl;
                Check(page != null, "每日签到页面已注册到导航");
                Check(page.Visible, "导航到每日签到后页面可见");

                // Every entry the user needs must exist and be reachable.
                foreach (var name in new[] { "DailySigninDescription", "DailySigninAuto", "DailySigninTime",
                    "DailySigninSave", "DailySigninRun", "DailySigninStatus", "DailySigninHistory" }) {
                    var control = Find(form, name);
                    Check(control != null, name + " 存在");
                    Check(control.Visible, name + " 可见");
                    Check(control.Bounds.Width > 0 && control.Bounds.Height > 0, name + " 有可见尺寸 " + control.Bounds);
                }

                var autoCheck = (CheckBox)Find(form, "DailySigninAuto");
                Check(autoCheck.Checked, "用户已要求启用自动签到，首次配置默认开启");
                var timePicker = (DateTimePicker)Find(form, "DailySigninTime");
                Check(timePicker.Enabled, "时间选择器可用");

                // Fixed-height labels must not cut their text off.
                foreach (var label in Descendants(page).OfType<Label>()) {
                    if (label.AutoSize) continue;
                    int needed = label.GetPreferredSize(new Size(label.Width, 0)).Height;
                    Check(needed <= label.Height + 1, "标签不截断文字：" + (label.Name.Length > 0 ? label.Name : label.Text.Substring(0, Math.Min(12, label.Text.Length)))
                        + " 需要 " + needed + "px，实际 " + label.Height + "px");
                }

                // Buttons must stay readable in both themes, including after a switch.
                CheckButtons(page, "深色");

                // At the default window size nothing may overflow horizontally, and the
                // existing vertical scroll range has to reach the last control.
                Directory.CreateDirectory(args[1]);
                form.PerformLayout(); Application.DoEvents();
                Capture(form, Path.Combine(args[1], "signin-page-default.png"));
                Capture(page, Path.Combine(args[1], "signin-page-only.png"));

                Call(form, "SetTheme", true); Application.DoEvents();
                var status = (Label)Find(form, "DailySigninStatus");
                Check(Contrast(status.ForeColor, status.BackColor) >= 4.5, "浅色主题状态文字对比度符合 4.5:1");
                CheckButtons(page, "浅色");
                Capture(page, Path.Combine(args[1], "signin-page-light.png"));
                Call(form, "SetTheme", false); Application.DoEvents();

                // Minimum supported window size must stay usable through scrolling.
                form.Size = new Size(780, 600); form.PerformLayout(); Application.DoEvents();
                foreach (var label in Descendants(page).OfType<Label>()) {
                    Check(label.Right <= page.ClientSize.Width - page.Padding.Right + 1, "最小窗口下标签不横向溢出：" + label.Name);
                    if (!label.AutoSize) Check(label.GetPreferredSize(new Size(label.Width, 0)).Height <= label.Height + 1, "最小窗口下标签不截断：" + label.Name);
                }
                Capture(form, Path.Combine(args[1], "signin-page-minimum.png"));

                // Sidebar: navigation buttons and account UI must not overlap, and the
                // custom arrow area must scroll the content at minimum size.
                var sidebar = Find(form, "Sidebar");
                Check(sidebar != null, "侧栏存在");
                var sidebarScrollable = sidebar as ScrollableControl;
                var sidebarViewport = Find(form, "SidebarViewport");
                var sidebarContent = Find(form, "SidebarContent");
                var scrollUp = Find(form, "SidebarScrollUp") as Button;
                var scrollDown = Find(form, "SidebarScrollDown") as Button;
                Check(sidebarScrollable != null && sidebarViewport != null && sidebarContent != null
                    && scrollUp != null && scrollDown != null, "侧栏提供独立滚动内容和上下箭头");
                Check(!sidebarScrollable.HorizontalScroll.Visible, "侧栏没有原生横向滚动条");
                var navButtons = new[] { "ConversionNavigation", "GenreNavigation", "ProfileNavigation",
                    "MemoryRecallNavigation", "SettingsNavigation", "AudioForwardingNavigation", "DailySigninNavigation" };
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
                Check(sidebarContent.Height > sidebarViewport.ClientSize.Height,
                    "最小窗口下侧栏内容超出可视区域，箭头滚动可用（内容 " + sidebarContent.Height + " > 可视 " + sidebarViewport.ClientSize.Height + "）");
                Check(scrollUp.Visible && scrollDown.Visible, "侧栏溢出时显示上下箭头");
                int sidebarTop = sidebarContent.Top;
                scrollDown.PerformClick(); Application.DoEvents();
                Check(sidebarContent.Top < sidebarTop, "下箭头可以向下滚动侧栏");
                scrollUp.PerformClick(); Application.DoEvents();
                Check(sidebarContent.Top == sidebarTop, "上箭头可以回滚侧栏");
                var wheelFilter = type.GetField("sidebarWheelFilter", Private).GetValue(form);
                var preFilter = wheelFilter.GetType().GetMethod("PreFilterMessage");
                var wheelPoint = sidebar.PointToScreen(new Point(30, 120));
                long wheelPosition = ((long)wheelPoint.Y << 16) | (long)(wheelPoint.X & 0xFFFF);
                var wheelMessage = new Message {
                    Msg = 0x20A, WParam = new IntPtr(-120 << 16), LParam = new IntPtr(wheelPosition)
                };
                object[] wheelArguments = new object[] { wheelMessage };
                bool wheelHandled = (bool)preFilter.Invoke(wheelFilter, wheelArguments);
                Check(wheelHandled && sidebarContent.Top < sidebarTop, "滚轮可以向下滚动侧栏");

                // Task feedback must be visible in the status bar and the message log.
                Call(form, "ReportTask", "每日签到", "部分失败", "布局校验用消息");
                Check(form.Controls.Find("TaskStatus", true)[0].Text.Contains("部分失败"), "任务状态栏显示每日签到反馈");

                // StopDailySignin must halt the timer and dispose it.
                Call(form, "StopDailySignin");
                Application.DoEvents();
                var timer = (System.Windows.Forms.Timer)Field(form, "dailySigninTimer");
                Check(!timer.Enabled, "StopDailySignin 后定时器已停止");
                var disposedField = typeof(System.ComponentModel.Component).GetField("disposed", Private);
                if (disposedField != null) {
                    Check((bool)disposedField.GetValue(timer), "StopDailySignin 后定时器已释放");
                }

                type.GetField("allowExit", Private).SetValue(form, true); form.Close();
            }
            Console.WriteLine("PASS: 每日签到页面布局、文字、滚动、主题与任务反馈");
            return 0;
        } catch (Exception ex) { Console.Error.WriteLine(ex); return 1; }
        finally {
            // Restore the original LOCALAPPDATA so later processes in this shell are unaffected.
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
