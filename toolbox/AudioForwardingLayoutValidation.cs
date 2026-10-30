// Isolated STA layout validation for the 音频转麦克风 page.
// Args: toolbox EXE, screenshot folder. Startup network and lyric tasks are suppressed.
using System;
using System.Collections.Generic;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text;
using System.Windows.Forms;

internal static class AudioForwardingLayoutValidation
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
        // The page now restores the last forwarding session from %LOCALAPPDATA%. Move the real
        // file aside so the assertions start from defaults, then put it back untouched.
        string settingsPath = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "NeteaseToolbox", "audio-forward.json");
        string savedSettings = File.Exists(settingsPath) ? File.ReadAllText(settingsPath, Encoding.UTF8) : null;
        try {
            if (savedSettings != null) File.Delete(settingsPath);
            Application.EnableVisualStyles();
            // Load from bytes: this workspace can be classified as a remote location,
            // which blocks Assembly.LoadFrom on the freshly built EXE.
            var type = Assembly.Load(File.ReadAllBytes(Path.GetFullPath(args[0]))).GetType("NeteaseToolbox.ToolboxForm", true);
            using (var form = (Form)Activator.CreateInstance(type, new object[] { false })) {
                // Suppress the startup workers (network, playback sync, lyric checks).
                var events = (System.ComponentModel.EventHandlerList)typeof(Control).GetProperty("Events", Private).GetValue(form, null);
                foreach (var field in typeof(Form).GetFields(BindingFlags.Static | BindingFlags.NonPublic)) {
                    if (field.Name.IndexOf("shown", StringComparison.OrdinalIgnoreCase) < 0) continue;
                    var key = field.GetValue(null); if (events[key] != null) events.RemoveHandler(key, events[key]);
                }
                form.StartPosition = FormStartPosition.Manual; form.Location = new Point(-10000, -10000);
                form.Show();
                Call(form, "NavigateTo", 5);
                form.PerformLayout(); Application.DoEvents();

                var page = Find(form, "AudioForwardingPage") as ScrollableControl;
                Check(page != null, "音频转麦克风页面已注册到导航");
                Check(page.Visible, "导航到音频转麦克风后页面可见");
                Check(page.AutoScroll, "页面内容超出时允许滚动");

                // Every entry the user needs must exist and be reachable.
                foreach (var name in new[] { "AudioProcessSelector", "AudioCaptureMode", "AudioOutputSelector",
                    "AudioForwardStart", "AudioForwardStop", "AudioMusicVolume", "AudioForwardLevel", "AudioForwardStatus",
                    "AudioMixMicrophone", "AudioMicrophoneSelector", "AudioMicrophoneVolume", "AudioMicrophoneLevel", "AudioMixState",
                    "AudioAutoStart" }) {
                    var control = Find(form, name);
                    Check(control != null, name + " 存在");
                    Check(control.Visible, name + " 可见");
                    Check(control.Bounds.Width > 0 && control.Bounds.Height > 0, name + " 有可见尺寸 " + control.Bounds);
                }
                // The guide starts collapsed; installing the cable must not require expanding it.
                var guideToggle = Find(form, "AudioGuideToggle") as Button;
                Check(guideToggle != null, "说明开关按钮存在");
                Check(guideToggle.Visible, "说明开关按钮可见");
                Check(guideToggle.Text == "显示", "说明默认收起，按钮文本为“显示”");
                var guidePanel = Find(form, "AudioGuidePanel");
                Check(guidePanel != null, "使用方法和注意事项容器存在");
                Check(!guidePanel.Visible, "使用方法和注意事项默认隐藏");
                var cableDownload = Find(form, "AudioCableDownload") as Button;
                Check(cableDownload != null, "VB-CABLE 下载按钮存在");
                Check(cableDownload.Visible, "说明隐藏时 VB-CABLE 下载按钮仍可见");
                guideToggle.PerformClick(); Application.DoEvents();
                Check(guidePanel.Visible, "点击“显示”后使用方法和注意事项展开");
                Check(guideToggle.Text == "隐藏", "展开后按钮文本为“隐藏”");
                guideToggle.PerformClick(); Application.DoEvents();
                Check(!guidePanel.Visible, "再次点击后使用方法和注意事项收起");
                Check(guideToggle.Text == "显示", "收起后按钮文本恢复为“显示”");
                // Expanding scrolled the page to the guide; the scroll-range checks need the top.
                page.AutoScrollPosition = Point.Empty; page.PerformLayout(); Application.DoEvents();

                var layout = page.Controls.Cast<Control>().FirstOrDefault();
                Check(layout != null, "页面内容容器存在");
                var volume = (TrackBar)Find(form, "AudioMusicVolume");
                Check(volume.Minimum == 0 && volume.Maximum == 100 && volume.Value >= 0 && volume.Value <= 100, "音乐音量范围 0-100");
                Check(volume.Value == 75, "没有保存配置时音乐音量默认 75%");
                var mode = (ComboBox)Find(form, "AudioCaptureMode");
                Check(mode.Items.Count == 2 && mode.SelectedIndex == 0, "捕获模式含仅网易云与系统声音");

                // Microphone mixing is opt-in and only its own controls enable.
                var mix = (CheckBox)Find(form, "AudioMixMicrophone");
                var microphoneSelector = (ComboBox)Find(form, "AudioMicrophoneSelector");
                var microphoneVolume = (TrackBar)Find(form, "AudioMicrophoneVolume");
                Check(!mix.Checked, "默认不混入真实麦克风");
                Check(!microphoneSelector.Enabled && !microphoneVolume.Enabled, "未勾选时麦克风控件不可用");
                Check(microphoneVolume.Minimum == 0 && microphoneVolume.Maximum == 100 && microphoneVolume.Value == 100, "麦克风音量范围 0-100 且默认 100%");
                mix.Checked = true; Application.DoEvents();
                Check(microphoneSelector.Enabled && microphoneVolume.Enabled, "勾选后麦克风控件可用");
                var mixState = (Label)Find(form, "AudioMixState");
                for (int i = 0; i < 6 && mixState.Text.Contains("未启用"); i++) { System.Threading.Thread.Sleep(120); Application.DoEvents(); }
                Check(!mixState.Text.Contains("未启用"), "勾选后混音状态改为启用描述：" + mixState.Text);
                mix.Checked = false; Application.DoEvents();
                Check(!microphoneSelector.Enabled, "取消勾选后重新禁用麦克风控件");

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
                int viewport = page.ClientSize.Width;
                foreach (Control child in layout.Controls) {
                    Check(child.Right <= viewport + 1 || page.HorizontalScroll.Visible,
                        "默认尺寸下控件不超出页面宽度：" + child.GetType().Name + " 右边距 " + child.Right + " / 页面 " + viewport);
                }
                Check(page.DisplayRectangle.Bottom + page.Padding.Bottom >= layout.Bottom,
                    "滚动范围覆盖页面底部（" + (page.DisplayRectangle.Bottom + page.Padding.Bottom) + " >= " + layout.Bottom + "）");
                Console.WriteLine("layout viewport=" + page.ClientSize + " content=" + page.DisplayRectangle + " flow=" + layout.Bounds);
                foreach (Control child in layout.Controls)
                    Console.WriteLine("  " + (child.Name.Length > 0 ? child.Name : child.GetType().Name) + " " + child.Bounds + " text=" + Truncate(child.Text));

                // Collapsed header: the toggle must be visible at the top-right of the page.
                page.AutoScrollPosition = Point.Empty; page.PerformLayout(); Application.DoEvents();
                Capture(page, Path.Combine(args[1], "audio-page-collapsed.png"));

                // Expand the guide before capturing so the review images show the complete steps.
                guideToggle.PerformClick(); Application.DoEvents();
                Check(guidePanel.Visible, "截图前使用方法和注意事项已展开");
                form.PerformLayout(); Application.DoEvents();
                Check(page.DisplayRectangle.Bottom + page.Padding.Bottom >= layout.Bottom,
                    "展开说明后滚动范围覆盖页面底部（" + (page.DisplayRectangle.Bottom + page.Padding.Bottom) + " >= " + layout.Bottom + "）");
                Capture(form, Path.Combine(args[1], "audio-page-default.png"));

                // Render the flow layout itself so the complete page is reviewable in one
                // frame, independent of how tall an offscreen test window may grow.
                Capture(layout, Path.Combine(args[1], "audio-page-full.png"));
                Capture(page, Path.Combine(args[1], "audio-page-only.png"));

                Call(form, "SetTheme", true); Application.DoEvents();
                var status = (Label)Find(form, "AudioForwardStatus");
                Check(status.ForeColor.GetBrightness() > 0.5f || status.ForeColor.GetBrightness() < 0.4f, "浅色主题下状态文字可读");
                CheckButtons(page, "浅色");
                Capture(layout, Path.Combine(args[1], "audio-page-light.png"));
                Call(form, "SetTheme", false); Application.DoEvents();

                // Minimum supported window size must stay usable through scrolling.
                form.Size = new Size(780, 600); form.PerformLayout(); Application.DoEvents();
                Check(page.HorizontalScroll.Visible || page.VerticalScroll.Visible, "最小窗口下页面可滚动查看");
                Capture(form, Path.Combine(args[1], "audio-page-minimum.png"));

                // Task feedback must be visible in the status bar and the message log.
                Call(form, "ReportTask", "音频转麦克风", "部分失败", "布局校验用消息");
                Check(form.Controls.Find("TaskStatus", true)[0].Text.Contains("部分失败"), "任务状态栏显示音频转麦克风反馈");

                // The QQ step must stand out: bold and drawn in the accent colour.
                var highlights = Descendants(page).OfType<Label>()
                    .Where(l => l.Text.Contains("打开 QQ 后把麦克风改成 CABLE Output")).ToList();
                Check(highlights.Count == 1, "使用方法含“打开 QQ 后把麦克风改成 CABLE Output”一步");
                Check(highlights[0].Font.Bold, "QQ 这一步是粗体");
                var noteLabel = Descendants(page).OfType<Label>().First(l => l.Text.StartsWith("1. 准备虚拟声卡"));
                Check(highlights[0].ForeColor.ToArgb() != noteLabel.ForeColor.ToArgb(),
                    "QQ 这一步用了高亮颜色 " + highlights[0].ForeColor + "（普通说明是 " + noteLabel.ForeColor + "）");

                // Auto-start is opt-in, persisted, and restores the whole session. Ticking it may
                // really begin forwarding, so this runs last and is stopped again below.
                var autoStart = (CheckBox)Find(form, "AudioAutoStart");
                Check(!autoStart.Checked, "默认不自动开始转发");
                autoStart.Checked = true; Application.DoEvents();
                Check(File.Exists(settingsPath), "勾选自动转发后写下配置文件");
                var written = File.ReadAllText(settingsPath, Encoding.UTF8);
                Check(written.Contains("\"AutoStart\":true"), "配置里记录自动转发：" + written);
                Check(File.ReadAllText(settingsPath, Encoding.UTF8).Contains("\"ProcessOnly\":true"), "配置里记录捕获模式");
                autoStart.Checked = false; Application.DoEvents();
                Check(File.ReadAllText(settingsPath, Encoding.UTF8).Contains("\"AutoStart\":false"), "取消勾选同样被记录");
                var stopping = Call(form, "StopAudioForwarding") as System.Threading.Tasks.Task;
                if (stopping != null) stopping.Wait(5000);
                Application.DoEvents();
                Check(!File.ReadAllText(settingsPath, Encoding.UTF8).Contains("\"AutoStart\":true"), "自动转发保持关闭，不会被校验留下开启状态");

                var timer = (Timer)Field(form, "audioUiTimer");
                timer.Stop();
                var forwarder = Field(form, "audioForwarder");
                ((IDisposable)forwarder).Dispose();
                type.GetField("allowExit", Private).SetValue(form, true); form.Close();
            }
            Console.WriteLine("PASS: 音频转麦克风页面布局、文字、滚动、主题与任务反馈");
            return 0;
        } catch (Exception ex) { Console.Error.WriteLine(ex); return 1; }
        finally {
            // Put the user's real forwarding preferences back exactly as they were.
            try {
                if (savedSettings == null) { if (File.Exists(settingsPath)) File.Delete(settingsPath); }
                else {
                    var folder = Path.GetDirectoryName(settingsPath);
                    if (!string.IsNullOrEmpty(folder)) Directory.CreateDirectory(folder);
                    File.WriteAllText(settingsPath, savedSettings, new UTF8Encoding(false));
                }
            } catch { }
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
    private static string Truncate(string text)
    {
        if (string.IsNullOrEmpty(text)) return "";
        text = text.Replace("\n", " / ");
        return text.Length <= 40 ? text : text.Substring(0, 40) + "…";
    }
    private static void Capture(Control control, string path)
    {
        using (var bitmap = new Bitmap(Math.Max(1, control.Width), Math.Max(1, control.Height))) {
            control.DrawToBitmap(bitmap, new Rectangle(Point.Empty, control.Size)); bitmap.Save(path);
        }
        Console.WriteLine("CAPTURE " + path + " " + control.Width + "x" + control.Height);
    }
}