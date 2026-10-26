using System;
using System.Collections;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Globalization;
using System.IO;
using System.Text;
using System.Threading.Tasks;
using System.Web.Script.Serialization;
using System.Windows.Forms;

namespace NeteaseToolbox
{
    internal sealed partial class ToolboxForm
    {
        private readonly System.Windows.Forms.Timer localPlaybackTimer = new System.Windows.Forms.Timer();
        private readonly ComboBox localPlaybackAccounts = new ComboBox { Name = "LocalPlaybackAccounts", DropDownStyle = ComboBoxStyle.DropDownList, Width = 190 };
        private readonly Button localPlaybackCollect = new Button { Name = "LocalPlaybackCollect", Text = "立即采集", Width = 110, Height = 32 };
        private readonly Label localPlaybackStatus = new Label { AutoSize = false, Dock = DockStyle.Fill };
        private readonly ListView localPlaybackSongs = new WallpaperListView();
        private readonly ListView localPlaybackSessions = new WallpaperListView();
        private readonly RichTextBox localPlaybackEvidence = new WallpaperRichTextBox { ReadOnly = true, DetectUrls = false, Dock = DockStyle.Fill, BorderStyle = BorderStyle.None };
        private bool localPlaybackBusy, localPlaybackSelecting, localPlaybackStopped;
        private Process localPlaybackWorker;
        private string localPlaybackLoginUid;
        private string localPlaybackRoot { get { return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), @"NeteaseToolbox\LocalPlayback"); } }

        private TabPage BuildLocalPlaybackTab()
        {
            var page = new TabPage("本机播放统计") { BackColor = panel };
            var surface = new WallpaperPanel { Dock = DockStyle.Fill, BackColor = panel };
            var layout = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 5, BackColor = Color.Transparent };
            layout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 42));
            layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 60));
            layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 42));
            layout.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
            layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 110));
            var buttons = new FlowLayoutPanel { Dock = DockStyle.Fill, WrapContents = false, Padding = new Padding(3), BackColor = Color.Transparent };
            var accountLabel = Label("账号", 9, false, ink); accountLabel.Margin = new Padding(3, 7, 3, 3);
            buttons.Controls.Add(accountLabel); buttons.Controls.Add(localPlaybackAccounts);
            StyleButton(localPlaybackCollect, ButtonRole.Secondary);
            buttons.Controls.Add(localPlaybackCollect);
            var folder = new Button { Text = "记录文件夹", Width = 115, Height = 32 };
            StyleButton(folder, ButtonRole.Secondary); buttons.Controls.Add(folder);
            folder.Click += delegate {
                try { Directory.CreateDirectory(localPlaybackRoot); Process.Start(new ProcessStartInfo(localPlaybackRoot) { UseShellExecute = true }); }
                catch (Exception ex) { ReportTask("本机播放统计", "失败", ex.Message); }
            };
            var phoneSync = new Button { Name = "LocalPlaybackPhoneSync", Text = "手机同步", Width = 105, Height = 32 };
            StyleButton(phoneSync, ButtonRole.Secondary); buttons.Controls.Add(phoneSync);
            phoneSync.Click += delegate { ShowPhoneSyncDialog(); };
            tips.SetToolTip(phoneSync, "与手机配对后，自动接收网易云重播并并入本页“连续重播记录”。");
            localPlaybackCollect.Click += async delegate { await UpdateLocalPlayback(true); };
            localPlaybackAccounts.SelectedIndexChanged += async delegate { if (!localPlaybackSelecting) await UpdateLocalPlayback(false); };
            localPlaybackStatus.Text = "打开工具箱时采集一次，运行期间每 3 小时采集；退出后停止。";
            localPlaybackStatus.ForeColor = muted;
            localPlaybackStatus.BackColor = Color.Transparent;
            localPlaybackStatus.Padding = new Padding(6, 2, 6, 0);
            var views = new TabControl { Dock = DockStyle.Fill };
            var totals = new TabPage("累计播放次数") { BackColor = panel };
            var sessions = new TabPage("连续重播记录") { BackColor = panel };
            ConfigureLocalPlaybackList(localPlaybackSongs, new[] { "歌曲", "歌手", "播放次数", "其中手机", "首次记录", "最近记录", "歌曲 ID" }, new[] { 165, 105, 70, 65, 140, 140, 70 });
            ConfigureLocalPlaybackList(localPlaybackSessions, new[] { "歌曲", "歌手", "来源", "连续次数", "开始记录", "结束记录", "歌曲 ID" }, new[] { 165, 105, 50, 70, 140, 140, 70 });
            totals.Controls.Add(localPlaybackSongs); sessions.Controls.Add(localPlaybackSessions);
            localPlaybackSessions.SelectedIndexChanged += delegate {
                if (localPlaybackSessions.SelectedItems.Count > 0) localPlaybackEvidence.Text = Convert.ToString(localPlaybackSessions.SelectedItems[0].Tag);
            };
            localPlaybackSongs.SelectedIndexChanged += delegate {
                if (localPlaybackSongs.SelectedItems.Count > 0) ShowLocalPlaybackLimits();
            };
            localPlaybackEvidence.BackColor = panel; localPlaybackEvidence.ForeColor = ink;
            ShowLocalPlaybackLimits();
            views.TabPages.Add(totals); views.TabPages.Add(sessions);
            layout.Controls.Add(buttons, 0, 0); layout.Controls.Add(localPlaybackStatus, 0, 1);
            layout.Controls.Add(BuildTabBar(views, "LocalPlaybackTabBar"), 0, 2);
            layout.Controls.Add(views, 0, 3); layout.Controls.Add(localPlaybackEvidence, 0, 4);
            surface.Controls.Add(layout); page.Controls.Add(surface);
            localPlaybackTimer.Interval = 3 * 60 * 60 * 1000;
            localPlaybackTimer.Tick += async delegate { await UpdateLocalPlayback(true); };
            return page;
        }

        private void ConfigureLocalPlaybackList(ListView list, string[] headings, int[] widths)
        {
            list.Dock = DockStyle.Fill; list.View = View.Details; list.FullRowSelect = true;
            list.HideSelection = false; list.BorderStyle = BorderStyle.None;
            list.BackColor = panel; list.ForeColor = ink;
            for (int i = 0; i < headings.Length; i++) list.Columns.Add(headings[i], widths[i]);
        }

        private void ShowLocalPlaybackLimits()
        {
            localPlaybackEvidence.Text = "电脑次数来自本机客户端已写入并被采集到的记录；手机次数只统计网易云音乐重播，并按“重播次数 + 1”推算首遍后合并。\r\n"
                + "电脑和手机不会同时播放，因此两边不互相去重；同一手机记录因网络失败重传时，电脑按事件 ID 幂等，避免重复计数。\r\n"
                + "手机仅记录单曲循环和回拖重播，首次播放不单独记录；其他媒体的播放不会进入手机重播。手机数据需在“手机同步”配对后、同一网络时自动传入。\r\n"
                + "连续重播按歌曲、记录顺序及时间间隔推断，不能确认是否开启单曲循环；长暂停会分段。未知账号单独保存。\r\n"
                + "客户端已清理的旧记录无法补回，正在播放的记录可能尚未写入。关闭窗口到托盘仍会采集，退出工具箱后停止。选择连续重播记录可查看逐条证据。";
        }

        private async Task StartLocalPlayback()
        {
            // Show already archived data even when the desktop client is unavailable.
            await UpdateLocalPlayback(false);
            await UpdateLocalPlayback(true);
            if (!localPlaybackStopped) localPlaybackTimer.Start();
        }

        private async Task UpdateLocalPlayback(bool collect)
        {
            if (localPlaybackBusy || localPlaybackStopped) return;
            localPlaybackBusy = true;
            localPlaybackCollect.Enabled = localPlaybackAccounts.Enabled = false;
            var uid = SelectedLocalPlaybackUid();
            if (collect) {
                localPlaybackStatus.Text = "正在采集本机播放记录…";
                ReportTask("本机播放采集", "开始", "正在复制并验证客户端数据库，按账号保存新记录。");
            }
            try {
                var data = await Task.Run(() => RunLocalPlaybackWorker(collect, uid));
                if (localPlaybackStopped || IsDisposed) return;
                ApplyLocalPlayback(data);
                if (collect) {
                    var errors = LocalArray(data, "errors"); var warnings = LocalArray(data, "warnings");
                    string detail = "检查 " + RecallValue(data, "checked") + " 条，新增 " + RecallValue(data, "added") + " 条；运行期间每 3 小时采集。";
                    if (errors.Count > 0 || warnings.Count > 0) {
                        var problems = new List<string>(); foreach (var value in errors) problems.Add(Convert.ToString(value)); foreach (var value in warnings) problems.Add(Convert.ToString(value));
                        detail += " " + string.Join("；", problems.ToArray());
                        ReportTask("本机播放采集", "部分失败", detail);
                        localPlaybackStatus.Text += "\r\n部分失败：" + string.Join("；", problems.ToArray());
                        tips.SetToolTip(localPlaybackStatus, localPlaybackStatus.Text);
                    } else ReportTask("本机播放采集", "完成", detail);
                }
            } catch (Exception ex) {
                if (!localPlaybackStopped && !IsDisposed) {
                    localPlaybackStatus.Text = "" + (collect ? "采集" : "读取") + "失败：" + ex.Message + "；已保存记录保留，3 小时后重试。";
                    ReportTask(collect ? "本机播放采集" : "本机播放统计", "失败", ex.Message);
                }
            } finally {
                localPlaybackBusy = false;
                if (!localPlaybackStopped && !IsDisposed) localPlaybackCollect.Enabled = localPlaybackAccounts.Enabled = true;
            }
        }

        private string SelectedLocalPlaybackUid()
        {
            string loggedIn = "";
            try { loggedIn = NeteaseAuth.Text(NeteaseAuth.Load(), "userId"); } catch { }
            if (loggedIn != localPlaybackLoginUid) { localPlaybackLoginUid = loggedIn; return loggedIn; }
            var item = localPlaybackAccounts.SelectedItem as LocalPlaybackAccount;
            if (item != null) return item.Uid;
            return loggedIn;
        }

        private Dictionary<string, object> RunLocalPlaybackWorker(bool collect, string uid)
        {
            string script = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "local_playback.mjs");
            if (!File.Exists(script)) throw new FileNotFoundException("缺少本机播放采集组件 local_playback.mjs。");
            string node = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, @"runtime\node\node.exe");
            if (!File.Exists(node)) node = "node.exe";
            var info = new ProcessStartInfo(node) {
                Arguments = "--disable-warning=ExperimentalWarning " + Quoted(script) + (collect ? " collect" : " report") + " --root " + Quoted(localPlaybackRoot) + " --uid " + Quoted(uid),
                UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true,
                StandardOutputEncoding = Encoding.UTF8, StandardErrorEncoding = Encoding.UTF8,
                WorkingDirectory = AppDomain.CurrentDomain.BaseDirectory
            };
            using (var process = new Process { StartInfo = info }) {
                lock (localPlaybackTimer) {
                    if (localPlaybackStopped) throw new OperationCanceledException();
                    localPlaybackWorker = process; process.Start();
                }
                try {
                    var output = process.StandardOutput.ReadToEndAsync(); var error = process.StandardError.ReadToEndAsync();
                    if (!process.WaitForExit(120000)) { process.Kill(); process.WaitForExit(); throw new TimeoutException("采集超过 2 分钟，已停止本次采集"); }
                    Task.WaitAll(output, error);
                    if (process.ExitCode != 0) throw new InvalidOperationException(error.Result.Trim());
                    return new JavaScriptSerializer { MaxJsonLength = 64 * 1024 * 1024 }.Deserialize<Dictionary<string, object>>(output.Result);
                } finally { lock (localPlaybackTimer) localPlaybackWorker = null; }
            }
        }

        private sealed class LocalPlaybackAccount
        {
            internal string Uid;
            public override string ToString() { return Uid == "unknown" ? "未知账号（独立归档）" : "账号 " + Uid; }
        }
        private static ArrayList LocalArray(Dictionary<string, object> data, string key)
        {
            object value; return data != null && data.TryGetValue(key, out value) && value is ArrayList ? (ArrayList)value : new ArrayList();
        }

        private void ApplyLocalPlayback(Dictionary<string, object> data)
        {
            localPlaybackSelecting = true;
            try {
                localPlaybackAccounts.Items.Clear();
                string uid = RecallValue(data, "uid");
                foreach (var id in LocalArray(data, "accounts")) {
                    var item = new LocalPlaybackAccount { Uid = Convert.ToString(id) }; localPlaybackAccounts.Items.Add(item);
                    if (item.Uid == uid) localPlaybackAccounts.SelectedItem = item;
                }
            } finally { localPlaybackSelecting = false; }
            localPlaybackSongs.BeginUpdate(); localPlaybackSessions.BeginUpdate();
            try {
                localPlaybackSongs.Items.Clear(); localPlaybackSessions.Items.Clear();
                foreach (Dictionary<string, object> song in LocalArray(data, "songs")) localPlaybackSongs.Items.Add(new ListViewItem(new[] {
                    RecallValue(song, "name"), RecallValue(song, "artists"), RecallValue(song, "count"), RecallValue(song, "phone_count"), RecallValue(song, "first"), RecallValue(song, "last"), RecallValue(song, "song_id")
                }));
                foreach (Dictionary<string, object> session in LocalArray(data, "sessions")) {
                    var evidence = new StringBuilder();
                    string source = RecallValue(session, "source");
                    if (source == "phone") {
                        evidence.AppendLine("手机网易云重播 " + RecallValue(session, "repeat_count") + " 次；按 +1 推算首遍，连续播放 " + RecallValue(session, "count") + " 次。");
                        evidence.AppendLine("设备：" + RecallValue(session, "device") + "。首次播放未在手机端记录，+1 为推算值。");
                    } else {
                        evidence.AppendLine("连续播放 " + RecallValue(session, "count") + " 次（包含首遍）；不能确认自动循环或手动重播。");
                    }
                    if (RecallValue(session, "estimated_span") == "True") evidence.AppendLine("部分歌曲缺少时长信息，分段使用 240 秒默认时长，结果需结合记录核对。");
                    var gaps = LocalArray(session, "gaps_sec"); var gapText = new List<string>(); foreach (var gap in gaps) gapText.Add(Convert.ToString(gap, CultureInfo.InvariantCulture));
                    evidence.AppendLine("相邻记录间隔（秒）：" + string.Join(", ", gapText.ToArray()));
                    foreach (Dictionary<string, object> entry in LocalArray(session, "evidence")) evidence.AppendLine("ID " + RecallValue(entry, "row_id") + "  " + RecallValue(entry, "time") + "  已听 " + RecallValue(entry, "seconds") + " 秒  来源 " + RecallValue(entry, "source"));
                    var item = new ListViewItem(new[] { RecallValue(session, "name"), RecallValue(session, "artists"), source == "phone" ? "手机" : "电脑", RecallValue(session, "count"), RecallValue(session, "started_at"), RecallValue(session, "ended_at"), RecallValue(session, "song_id") });
                    item.Tag = evidence.ToString(); localPlaybackSessions.Items.Add(item);
                }
            } finally { localPlaybackSongs.EndUpdate(); localPlaybackSessions.EndUpdate(); }
            ShowLocalPlaybackLimits();
            string last = RecallValue(data, "last_collected_at");
            string phoneSync = RecallValue(data, "last_phone_sync_at");
            string phoneTotal = RecallValue(data, "phone_total");
            string phoneSessions = RecallValue(data, "phone_sessions");
            if (phoneTotal.Length == 0) phoneTotal = "0";
            if (phoneSessions.Length == 0) phoneSessions = "0";
            localPlaybackStatus.Text = "已归档 " + RecallValue(data, "total") + " 条电脑记录 · " + localPlaybackSongs.Items.Count + " 首歌 · " + localPlaybackSessions.Items.Count + " 段连续重播（手机 " + phoneSessions + " 段 / " + phoneTotal + " 条重播）\r\n"
                + (last.Length > 0 ? "所选账号最近采集：" + last + "（北京时间）；每 3 小时采集。" : "暂无电脑记录；打开时及运行期间每 3 小时采集，退出后停止。")
                + (phoneSync.Length > 0 ? " 手机上次同步：" + phoneSync + "。" : " 手机尚未同步。");
            tips.SetToolTip(localPlaybackStatus, localPlaybackStatus.Text);
        }

        private void StopLocalPlayback()
        {
            lock (localPlaybackTimer) {
                localPlaybackStopped = true; localPlaybackTimer.Stop();
                if (localPlaybackWorker != null) try { if (!localPlaybackWorker.HasExited) localPlaybackWorker.Kill(); } catch { }
            }
            localPlaybackTimer.Dispose();
        }
    }
}
