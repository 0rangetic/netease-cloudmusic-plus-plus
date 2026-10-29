using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using System.Windows.Forms;

namespace NeteaseToolbox
{
    internal sealed partial class ToolboxForm
    {
        private readonly CheckBox dailySigninAuto = new CheckBox { Name = "DailySigninAuto", Text = "每天自动签到", AutoSize = true };
        private readonly DateTimePicker dailySigninTime = new DateTimePicker { Name = "DailySigninTime", Format = DateTimePickerFormat.Custom, CustomFormat = "HH:mm", ShowUpDown = true, Width = 100 };
        private readonly Label dailySigninStatus = new Label { Name = "DailySigninStatus", AutoSize = true, MaximumSize = new Size(620, 0) };
        private readonly RichTextBox dailySigninHistory = new RichTextBox { Name = "DailySigninHistory", ReadOnly = true, Dock = DockStyle.Fill, BorderStyle = BorderStyle.None, BackColor = Color.FromArgb(31, 37, 49), ForeColor = Color.FromArgb(236, 240, 247), WordWrap = true, DetectUrls = false };
        private readonly System.Windows.Forms.Timer dailySigninTimer = new System.Windows.Forms.Timer { Interval = 60000 };
        private readonly HashSet<string> dailySigninSeen = new HashSet<string>();
        private Button dailySigninRun, dailySigninSave;
        private bool dailySigninBusy, dailySigninConfigBusy, dailySigninInitialized;
        private string dailySigninLastFailure = "", dailySigninAccount = "";

        private Control BuildDailySigninPage()
        {
            var page = new TableLayoutPanel { Name = "DailySigninPage", Dock = DockStyle.Fill, Padding = new Padding(24, 18, 24, 18), ColumnCount = 1, RowCount = 5 };
            page.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            page.RowStyles.Add(new RowStyle(SizeType.Absolute, 42));
            page.RowStyles.Add(new RowStyle(SizeType.Absolute, 82));
            page.RowStyles.Add(new RowStyle(SizeType.Absolute, 56));
            page.RowStyles.Add(new RowStyle(SizeType.Absolute, 64));
            page.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
            page.Controls.Add(Label("每日签到", 15, true, ink), 0, 0);
            var description = new Label { Name = "DailySigninDescription", Dock = DockStyle.Fill, ForeColor = muted,
                Text = "使用当前网易云账号，分别完成云贝签到和黑胶乐签。\n按北京时间执行；错过时间会在登录 Windows 或打开工具箱后补签。\n关闭工具箱后，Windows 计划任务仍可执行；电脑需联网且当前用户已登录。" };
            page.Controls.Add(description, 0, 1);
            var options = new FlowLayoutPanel { Dock = DockStyle.Fill, WrapContents = false, AutoScroll = true };
            dailySigninAuto.ForeColor = ink; dailySigninAuto.Margin = new Padding(0, 12, 12, 0);
            options.Controls.Add(dailySigninAuto);
            dailySigninTime.Margin = new Padding(0, 9, 12, 0); options.Controls.Add(dailySigninTime);
            dailySigninSave = Button("保存设置", false); dailySigninSave.Name = "DailySigninSave"; dailySigninSave.Width = 102;
            dailySigninSave.Click += async delegate { await SaveDailySigninSettings(); };
            options.Controls.Add(dailySigninSave);
            dailySigninRun = Button("立即签到", true); dailySigninRun.Name = "DailySigninRun"; dailySigninRun.Width = 102;
            dailySigninRun.Click += async delegate { await RunDailySignin(false); };
            options.Controls.Add(dailySigninRun);
            page.Controls.Add(options, 0, 2);
            dailySigninStatus.ForeColor = muted; dailySigninStatus.Text = "正在读取签到配置…";
            page.Controls.Add(dailySigninStatus, 0, 3);
            dailySigninHistory.BackColor = panel; dailySigninHistory.ForeColor = ink; dailySigninHistory.Font = UiFont(10F);
            page.Controls.Add(dailySigninHistory, 0, 4);
            try {
                var settings = DailySigninService.LoadSettings();
                dailySigninAuto.Checked = settings.Enabled;
                dailySigninTime.Value = DateTime.Today.AddHours(settings.Hour).AddMinutes(settings.Minute);
                dailySigninStatus.Text = settings.Enabled ? "每日 " + dailySigninTime.Value.ToString("HH:mm") + "（北京时间）自动执行。" : "每日自动签到已关闭；仍可手动签到。";
            } catch (Exception ex) { dailySigninAuto.Checked = false; dailySigninStatus.Text = DailySigninService.SafeError(ex); }
            dailySigninTimer.Tick += async delegate { RefreshDailySigninHistory(); await RunDailySignin(true); };
            Shown += async delegate { await InitializeDailySignin(); };
            return page;
        }
        private async Task RegisterDailySigninTask(DailySigninSettings settings)
        {
            string baseDir = Path.GetDirectoryName(typeof(ToolboxForm).Assembly.Location) ?? AppDomain.CurrentDomain.BaseDirectory;
            string script = Path.Combine(baseDir, "RegisterSigninTask.ps1");
            if (!File.Exists(script)) throw new SigninFailure("缺少自动签到计划任务组件，请重新安装工具箱。");
            var start = new ProcessStartInfo("powershell.exe") {
                Arguments = "-NoProfile -NonInteractive -ExecutionPolicy Bypass -File " + Quoted(script) + " -ExecutablePath " + Quoted(Application.ExecutablePath)
                    + " -Hour " + settings.Hour + " -Minute " + settings.Minute + " -Enabled " + (settings.Enabled ? "true" : "false"),
                UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true
            };
            int code = await Task.Run(() => {
                using (var process = Process.Start(start)) {
                    var output = process.StandardOutput.ReadToEndAsync(); var error = process.StandardError.ReadToEndAsync();
                    if (!process.WaitForExit(30000)) { try { process.Kill(); } catch { } throw new SigninFailure("注册自动签到计划任务超时，请重新保存设置。"); }
                    Task.WaitAll(output, error);
                    return process.ExitCode;
                }
            });
            if (code != 0) throw new SigninFailure("Windows 未能注册签到计划任务；工具箱运行期间仍会自动执行。请重新保存设置重试。");
        }
        private async Task InitializeDailySignin()
        {
            if (dailySigninInitialized) return;
            dailySigninInitialized = true;
            RefreshDailySigninHistory();
            dailySigninTimer.Start();
            try {
                var settings = DailySigninService.LoadSettings();
                await RegisterDailySigninTask(settings);
                if (IsDisposed) return;
                DailySigninService.SaveSettings(settings);
                ReportTask("自动签到设置", "完成", settings.Enabled ? "每天 " + settings.Hour.ToString("00") + ":" + settings.Minute.ToString("00") + "（北京时间）执行；已启用错过时间补签。" : "每日自动签到已关闭。");
            } catch (Exception ex) { if (!IsDisposed) { dailySigninStatus.Text = DailySigninService.SafeError(ex); ReportTask("自动签到设置", "失败", dailySigninStatus.Text); } }
            if (!IsDisposed) await RunDailySignin(true);
        }
        private async Task SaveDailySigninSettings()
        {
            if (dailySigninConfigBusy) return;
            dailySigninConfigBusy = true; dailySigninSave.Enabled = false;
            var settings = new DailySigninSettings { Enabled = dailySigninAuto.Checked, Hour = dailySigninTime.Value.Hour, Minute = dailySigninTime.Value.Minute };
            try {
                // Save the intent first: even if registration fails an old scheduled task obeys disable.
                DailySigninService.SaveSettings(settings);
                ReportTask("自动签到设置", "开始", "正在保存时间并更新 Windows 计划任务。");
                await RegisterDailySigninTask(settings);
                if (IsDisposed) return;
                dailySigninStatus.Text = settings.Enabled ? "每天 " + dailySigninTime.Value.ToString("HH:mm") + "（北京时间）自动执行。" : "每日自动签到已关闭。";
                ReportTask("自动签到设置", "完成", dailySigninStatus.Text);
                if (settings.Enabled) await RunDailySignin(true);
            } catch (Exception ex) { if (!IsDisposed) { dailySigninStatus.Text = DailySigninService.SafeError(ex); ReportTask("自动签到设置", "失败", dailySigninStatus.Text); } }
            finally { dailySigninConfigBusy = false; if (!IsDisposed) dailySigninSave.Enabled = true; }
        }
        private async Task RunDailySignin(bool automatic)
        {
            if (dailySigninBusy || IsDisposed) return;
            dailySigninBusy = true; dailySigninRun.Enabled = false;
            try {
                var result = await Task.Run(() => DailySigninService.Run(automatic, item => {
                    InvokeUiAsync(() => {
                        if (IsDisposed) return;
                        try { if (NeteaseAuth.Text(NeteaseAuth.Load(), "userId") != item.AccountId) return; } catch { return; }
                        dailySigninSeen.Add(item.Id);
                        ReportTask(item.Task, item.Phase, item.Detail); dailySigninStatus.Text = item.Task + " · " + item.Phase + "：" + item.Detail;
                    });
                }));
                if (IsDisposed) return;
                dailySigninLastFailure = "";
                if (!automatic || !result.Skipped) dailySigninStatus.Text = result.Phase + "：" + result.Detail;
                if (!automatic && result.Skipped) ReportTask("每日签到", result.Phase, result.Detail);
                RefreshDailySigninHistory();
            } catch (Exception ex) {
                if (!IsDisposed) {
                    string message = DailySigninService.SafeError(ex);
                    dailySigninStatus.Text = message;
                    if (!automatic || dailySigninLastFailure != message) ReportTask("每日签到", "失败", message);
                    dailySigninLastFailure = message;
                }
            } finally { dailySigninBusy = false; if (!IsDisposed) dailySigninRun.Enabled = true; }
        }
        private void RefreshDailySigninHistory()
        {
            dailySigninHistory.BackColor = panel;
            dailySigninHistory.ForeColor = ink;
            try {
                string runnerError = Path.Combine(DailySigninService.Root, "daily-signin-runner-error.json");
                if (File.Exists(runnerError)) {
                    var failure = new System.Web.Script.Serialization.JavaScriptSerializer().Deserialize<SigninEvent>(File.ReadAllText(runnerError, Encoding.UTF8));
                    if (failure != null && dailySigninSeen.Add(failure.Id)) ReportTask(failure.Task, failure.Phase, failure.Time + " · " + failure.Detail);
                }
                string uid = NeteaseAuth.Text(NeteaseAuth.Load(), "userId");
                if (dailySigninAccount != uid) { dailySigninAccount = uid; dailySigninSeen.Clear(); dailySigninHistory.Clear(); }
                var history = DailySigninService.ReadHistory(DailySigninService.AccountDirectory(uid));
                dailySigninHistory.Text = history.Count == 0 ? "暂无签到记录。点击“立即签到”执行今天的两项签到。" : string.Join(Environment.NewLine + Environment.NewLine, history.AsEnumerable().Reverse().Select(item => item.ToString()));
                // Scheduled runs write persistent messages even when the main window is closed.
                foreach (var item in history.Skip(Math.Max(0, history.Count - 20))) if (dailySigninSeen.Add(item.Id)) {
                    ReportTask(item.Task, item.Phase, item.Time + " · " + item.Detail);
                    dailySigninStatus.Text = item.Time + " · " + item.Phase + "：" + item.Detail;
                }
            } catch (Exception ex) { dailySigninHistory.Text = DailySigninService.SafeError(ex); }
        }
        private void StopDailySignin() { dailySigninTimer.Stop(); dailySigninTimer.Dispose(); }
    }
}
