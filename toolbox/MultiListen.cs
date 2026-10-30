using System;
using System.Collections;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Management;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using System.Web.Script.Serialization;
using System.Windows.Forms;

namespace NeteaseToolbox
{
    internal sealed partial class ToolboxForm
    {
        // 多人一起听页面：把独立的官方多人一起听补丁（listen-together）接到工具箱。
        // 补丁运行时位于工具箱目录下的 multi-listen\，源码位于仓库 listen-together\，两处保持逐字节一致。
        private const string MultiListenPatchVersion = "0.4.2";
        // Client builds verified for the in-process transport. 3.1.42.205594 ships
        // the identical libcef.dll (same CEF 91 / branch 4472 layouts), so the CEF
        // layer and native-agent offsets are unchanged; native_bridge.py still
        // re-checks the exact libcef.dll SHA-256 before attaching.
        private static readonly string[] MultiListenClientVersions = { "3.1.41.205529", "3.1.42.205594" };
        private readonly System.Windows.Forms.Timer multiListenTimer = new System.Windows.Forms.Timer { Interval = 5000 };
        private readonly Label multiListenStatus = new Label { Name = "MultiListenStatus", AutoSize = true, MaximumSize = new Size(500, 0) };
        private readonly Label multiListenDetail = new Label { Name = "MultiListenDetail", AutoSize = true, MaximumSize = new Size(500, 0) };
        private readonly RichTextBox multiListenMessages = new RichTextBox {
            Name = "MultiListenMessages", ReadOnly = true, Dock = DockStyle.Fill, BorderStyle = BorderStyle.None,
            BackColor = Color.FromArgb(31, 37, 49), ForeColor = Color.FromArgb(236, 240, 247),
            WordWrap = true, DetectUrls = false, ScrollBars = RichTextBoxScrollBars.Vertical };
        private Button multiListenStart, multiListenRefresh, multiListenStop, multiListenOpenLog;
        private Control multiListenPage;
        private bool multiListenBusy, multiListenInstalled;
        private string multiListenClientExecutable = "";
        private int multiListenClientPid;
        private long multiListenBridgeLogStart;

        // 原生 WPF 页面读取的状态缓存。业务逻辑仍全部留在本文件，MultiListenView 只通过
        // Toolbox.MultiListenBridge.cs 中带 MultiListen 前缀的快照/动作方法访问，避免复制逻辑。
        private string multiListenStatusCache = "正在检查补丁状态…";
        private string multiListenDetailCache = "";
        private string multiListenErrorCache = "";
        private readonly List<MultiListenMessageEntry> multiListenMessageCache = new List<MultiListenMessageEntry>();

        private string multiListenRoot {
            get { return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), @"NeteaseToolbox\MultiListen"); }
        }

        private Control BuildMultiListenPage()
        {
            var page = new TableLayoutPanel { Name = "MultiListenPage", Dock = DockStyle.Fill, Padding = new Padding(24, 18, 24, 18), ColumnCount = 1, RowCount = 5 };
            page.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            page.RowStyles.Add(new RowStyle(SizeType.Absolute, 42));
            page.RowStyles.Add(new RowStyle(SizeType.Absolute, 112));
            page.RowStyles.Add(new RowStyle(SizeType.Absolute, 56));
            page.RowStyles.Add(new RowStyle(SizeType.Absolute, 82));
            page.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
            page.Controls.Add(Label("多人一起听", 15, true, ink), 0, 0);
            var description = new Label {
                Name = "MultiListenDescription", Dock = DockStyle.Fill, ForeColor = muted,
                Text = "让电脑加入手机端创建的官方多人房间，并跟随房间里的歌曲与进度。\n"
                    + "可以先开网易云，再点“启动并连接”；无需退出客户端。网易云未运行时，工具箱会自动启动；首次连接约需 30 秒。\n"
                    + "朋友用官方 App 建房并通过私信发来多人邀请后，点击邀请卡片，或在网易云右上角补丁面板粘贴邀请链接。\n"
                    + "支持恢复已加入的房间、跟随换歌与进度、本机暂停和退出；不支持建房、推歌、共同队列与聊天。"
            };
            page.Controls.Add(description, 0, 1);
            var buttons = new FlowLayoutPanel { Name = "MultiListenButtons", Dock = DockStyle.Fill, WrapContents = false };
            multiListenStart = Button("启动并连接", true);
            multiListenStart.Name = "MultiListenStart"; multiListenStart.Width = 118;
            multiListenStart.Click += async delegate { await StartMultiListen(); };
            buttons.Controls.Add(multiListenStart);
            multiListenRefresh = Button("刷新状态", false);
            multiListenRefresh.Name = "MultiListenRefresh"; multiListenRefresh.Width = 96;
            multiListenRefresh.Click += async delegate { await RefreshMultiListenStatus(true); };
            buttons.Controls.Add(multiListenRefresh);
            multiListenStop = Button("退出并停用", false);
            multiListenStop.Name = "MultiListenStop"; multiListenStop.Width = 110;
            StyleButton(multiListenStop, ButtonRole.Danger);
            multiListenStop.Click += async delegate { await StopMultiListen(); };
            buttons.Controls.Add(multiListenStop);
            multiListenOpenLog = Button("日志文件夹", false);
            multiListenOpenLog.Name = "MultiListenOpenLog"; multiListenOpenLog.Width = 110;
            multiListenOpenLog.Click += delegate { OpenMultiListenLogFolder(); };
            buttons.Controls.Add(multiListenOpenLog);
            page.Controls.Add(buttons, 0, 2);
            var state = new FlowLayoutPanel {
                Name = "MultiListenState", Dock = DockStyle.Fill, FlowDirection = FlowDirection.TopDown, WrapContents = false
            };
            multiListenStatus.ForeColor = muted; SetMultiListenStatusText(multiListenStatusCache);
            multiListenDetail.ForeColor = muted; multiListenDetail.Text = "";
            state.Controls.Add(multiListenStatus); state.Controls.Add(multiListenDetail);
            page.Controls.Add(state, 0, 3);
            multiListenMessages.BackColor = panel; multiListenMessages.ForeColor = ink; multiListenMessages.Font = UiFont(10F);
            page.Controls.Add(multiListenMessages, 0, 4);
            multiListenTimer.Tick += async delegate { await MultiListenTick(); };
            UpdateMultiListenButtons();
            return page;
        }

        private async void OnMultiListenPageOpened()
        {
            if (!multiListenTimer.Enabled) multiListenTimer.Start();
            if (!multiListenBusy) await RefreshMultiListenStatus(false);
        }

        private async Task MultiListenTick()
        {
            try {
                if (IsDisposed || multiListenBusy) return;
                // 原生 WPF 页面显示时也要继续后台探测；WinForms 页面仍按可见性判断。
                if (!multiListenWpfActive && (multiListenPage == null || !multiListenPage.Visible)) return;
                await RefreshMultiListenStatus(false);
            } catch (Exception ex) { ReportTask("多人一起听", "失败", "状态刷新异常：" + ex.Message); }
        }

        private async Task RefreshMultiListenStatus(bool announce)
        {
            if (multiListenBusy || IsDisposed) return;
            multiListenBusy = true; UpdateMultiListenButtons();
            if (announce) {
                SetMultiListenStatusText("正在检查补丁状态…");
                ReportTask("多人一起听", "开始", "正在读取当前客户端的补丁状态。");
            }
            try {
                var probe = await Task.Run(() => TryReadMultiListenStatus());
                if (IsDisposed) return;
                if (probe.Item1) {
                    RenderMultiListen(probe.Item2, null);
                    if (announce) ReportTask("多人一起听", "完成", "状态已刷新：" + multiListenStatusCache);
                } else {
                    RenderMultiListen(null, probe.Item3);
                    if (announce) ReportTask("多人一起听", "失败", "状态刷新失败：" + probe.Item3);
                }
            } finally {
                multiListenBusy = false; UpdateMultiListenButtons();
            }
        }

        private async Task StartMultiListen()
        {
            if (multiListenBusy) return;
            multiListenBusy = true; UpdateMultiListenButtons();
            multiListenErrorCache = "";
            SetMultiListenStatusText("正在检查客户端并连接补丁…");
            SetMultiListenDetailText("无需退出网易云；首次建立进程内通道约需 30 秒，请稍候。");
            ReportTask("多人一起听", "开始", "正在检查补丁组件及客户端版本；网易云已运行时直接接入，首次连接约需 30 秒。");
            string startupProblem = null;
            Process bridgeProcess = null;
            try {
                await Task.Run((Action)delegate { EnsureMultiListenDebugChannel(); });
                var probe = await Task.Run(() => TryReadMultiListenStatus());
                Dictionary<string, object> status = null;
                bool alreadyInstalled = probe.Item1 && MultiListenFlag(probe.Item2, "installed")
                    && !MultiListenFlag(probe.Item2, "bridgeDisconnected");
                if (alreadyInstalled && MultiListenConnectionReady(probe.Item2)) status = probe.Item2;
                if (status == null) {
                    SetMultiListenStatusText("正在连接网易云并加载补丁…");
                    if (!alreadyInstalled) bridgeProcess = StartMultiListenBridge();
                    var deadline = DateTime.UtcNow.AddSeconds(65);
                    while (DateTime.UtcNow < deadline) {
                        await Task.Delay(1000);
                        if (bridgeProcess != null && bridgeProcess.HasExited)
                            throw new InvalidOperationException("补丁连接失败：" + ReadMultiListenBridgeFailure(bridgeProcess.ExitCode));
                        var poll = await Task.Run(() => TryReadMultiListenStatus());
                        if (poll.Item1 && MultiListenFlag(poll.Item2, "installed") && MultiListenConnectionReady(poll.Item2)) {
                            status = poll.Item2; break;
                        }
                    }
                    if (status == null) throw new InvalidOperationException("等待补丁连接完成超时；请在日志文件夹查看 bridge.log 中的具体错误后重试。");
                }
                RenderMultiListen(status, null);
                if (MultiListenFlag(status, "clickHook"))
                    ReportTask("多人一起听", "完成", "补丁已就绪：点击私信里的多人邀请卡片，或在网易云右上角面板粘贴邀请链接。");
                else
                    ReportTask("多人一起听", "部分失败", "补丁已安装，但邀请点击入口未连接；请在网易云面板里用“读取私信邀请”或直接粘贴邀请链接。");
            } catch (Exception ex) {
                startupProblem = ex.Message;
                multiListenErrorCache = startupProblem;
                ReportTask("多人一起听", "失败", ex.Message);
            } finally {
                if (bridgeProcess != null) bridgeProcess.Dispose();
            }
            Tuple<bool, Dictionary<string, object>, string> final = null;
            try { final = await Task.Run(() => TryReadMultiListenStatus()); } catch { }
            multiListenBusy = false;
            if (IsDisposed) return;
            if (final != null) {
                if (final.Item1) {
                    RenderMultiListen(final.Item2, null);
                    if (startupProblem != null) { SetMultiListenDetailText(startupProblem); multiListenErrorCache = startupProblem; }
                } else RenderMultiListen(null, startupProblem ?? final.Item3);
            } else if (startupProblem != null) {
                SetMultiListenStatusText("未连接：" + startupProblem);
                SetMultiListenDetailText("请打开日志文件夹查看 bridge.log 中的具体错误后重试。");
            }
            UpdateMultiListenButtons();
        }

        private async Task StopMultiListen()
        {
            if (multiListenBusy) return;
            if (MessageBox.Show(DialogOwner, "确定退出多人房间并停用补丁吗？\n网易云客户端会保持运行；之后可以在本页或网易云面板重新启动。",
                "多人一起听", MessageBoxButtons.YesNo, MessageBoxIcon.Warning) != DialogResult.Yes) return;
            await StopMultiListenConfirmed();
        }

        // 原生 WPF 页面自行确认后再调用；这里只执行业务动作。
        private async Task StopMultiListenConfirmed()
        {
            if (multiListenBusy) return;
            multiListenBusy = true; UpdateMultiListenButtons();
            multiListenErrorCache = "";
            ReportTask("多人一起听", "开始", "正在退出房间并停用补丁。");
            string stopProblem = null;
            try {
                var result = await Task.Run(() => RunMultiListenControl("shutdown", 30000));
                if (result.Item1 != 0) throw new InvalidOperationException(result.Item3.Length > 0
                    ? TrimMultiListenText(result.Item3) : "停用脚本返回退出码 " + result.Item1 + "。");
                ReportTask("多人一起听", "完成", "已退出房间并停用补丁。");
            } catch (Exception ex) {
                stopProblem = ex.Message;
                multiListenErrorCache = stopProblem;
                ReportTask("多人一起听", "失败", ex.Message);
            }
            Tuple<bool, Dictionary<string, object>, string> final = null;
            try { final = await Task.Run(() => TryReadMultiListenStatus()); } catch { }
            multiListenBusy = false;
            if (IsDisposed) return;
            if (final != null) {
                if (final.Item1) {
                    RenderMultiListen(final.Item2, null);
                    if (stopProblem != null) { SetMultiListenDetailText(stopProblem); multiListenErrorCache = stopProblem; }
                } else RenderMultiListen(null, stopProblem ?? final.Item3);
            }
            UpdateMultiListenButtons();
        }

        private void OpenMultiListenLogFolder()
        {
            try {
                Directory.CreateDirectory(multiListenRoot);
                Process.Start(new ProcessStartInfo(multiListenRoot) { UseShellExecute = true });
            } catch (Exception ex) { ReportTask("多人一起听", "失败", "无法打开日志文件夹：" + ex.Message); }
        }

        private void EnsureMultiListenDebugChannel()
        {
            // Both transports need the same verified main process. A CDP endpoint alone
            // does not establish which installation is running, and is never enough here.
            foreach (var relative in new[] { @"runtime\node\node.exe", @"multi-listen\control.mjs",
                @"multi-listen\runtime.js", @"multi-listen\protocol.mjs", @"multi-listen\version.mjs",
                @"multi-listen\native-client.mjs", @"multi-listen\native-host.mjs",
                @"multi-listen\native_bridge.py", @"multi-listen\native-agent.js" }) {
                if (!File.Exists(Path.Combine(AppDomain.CurrentDomain.BaseDirectory, relative)))
                    throw new FileNotFoundException("缺少随包组件 " + relative + "；请重新部署工具箱目录。");
            }
            var folder = ClientFolder();
            if (string.IsNullOrEmpty(folder))
                throw new InvalidOperationException("没有找到网易云安装目录；请先在“设置与维护”里选择网易云安装目录，或打开一次网易云。");
            var executable = Path.GetFullPath(Path.Combine(folder, "cloudmusic.exe"));
            if (!File.Exists(executable)) throw new FileNotFoundException("安装目录里没有 cloudmusic.exe：" + folder);
            var processId = FindRunningMultiListenClient(executable);
            if (processId > 0) {
                multiListenClientExecutable = executable;
                multiListenClientPid = processId;
                return;
            }
            ValidateMultiListenClient(executable);
            using (Process.Start(new ProcessStartInfo(executable) {
                WorkingDirectory = folder, UseShellExecute = false
            })) { }
            for (int attempt = 0; attempt < 30; attempt++) {
                System.Threading.Thread.Sleep(500);
                processId = FindRunningMultiListenClient(executable, true);
                if (processId > 0) {
                    multiListenClientExecutable = executable;
                    multiListenClientPid = processId;
                    return;
                }
            }
            throw new InvalidOperationException("等待网易云主进程启动超时；请确认客户端已正常打开，再点“启动并连接”。");
        }

        private static int FindRunningMultiListenClient(string expectedExecutable, bool starting = false)
        {
            try {
                if (Process.GetProcessesByName("cloudmusic").Length == 0) return 0;
            } catch { }
            var mainProcesses = new List<Tuple<int, string>>();
            int childProcesses = 0;
            using (var query = new ManagementObjectSearcher(
                "SELECT ProcessId, ExecutablePath, CommandLine FROM Win32_Process WHERE Name='cloudmusic.exe'"))
            using (var processes = query.Get()) {
                foreach (ManagementObject process in processes) using (process) {
                    var commandLine = Convert.ToString(process["CommandLine"]);
                    // CEF renderers, GPU and utility processes use the same executable.
                    // Never attach to one merely because its image name matches.
                    if (Regex.IsMatch(commandLine, @"(?:^|\s)""?--type(?:=|\s|""|$)", RegexOptions.IgnoreCase)) {
                        childProcesses++; continue;
                    }
                    var executable = Convert.ToString(process["ExecutablePath"]);
                    if (commandLine.Length == 0 || executable.Length == 0)
                        throw new InvalidOperationException("无法核实正在运行的网易云主进程；请以与客户端相同的 Windows 用户和权限运行工具箱。");
                    mainProcesses.Add(new Tuple<int, string>(Convert.ToInt32(process["ProcessId"]), Path.GetFullPath(executable)));
                }
            }
            if (mainProcesses.Count == 0) {
                if (!starting && childProcesses > 0)
                    throw new InvalidOperationException("仅发现网易云辅助进程，尚未找到可连接的主进程；请确认客户端已正常打开后重试。");
                return 0;
            }
            if (mainProcesses.Count > 1)
                throw new InvalidOperationException("发现多个网易云主进程，无法安全确定连接目标；请只保留要连接的客户端实例后重试。");
            var runningExecutable = mainProcesses[0].Item2;
            if (!string.Equals(runningExecutable, expectedExecutable, StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException("正在运行的网易云来自另一个安装目录：" + Path.GetDirectoryName(runningExecutable)
                    + "；请在“设置与维护”中选择该目录后重试。");
            ValidateMultiListenClient(runningExecutable);
            return mainProcesses[0].Item1;
        }

        private static void ValidateMultiListenClient(string executable)
        {
            var version = FileVersionInfo.GetVersionInfo(executable).FileVersion;
            if (Array.IndexOf(MultiListenClientVersions, version) < 0)
                throw new InvalidOperationException("当前网易云版本为 " + version + "；多人一起听仅适配 "
                    + string.Join(" / ", MultiListenClientVersions) + " x64。");
            using (var stream = File.OpenRead(executable))
            using (var reader = new BinaryReader(stream)) {
                stream.Position = 0x3C; int peOffset = reader.ReadInt32();
                stream.Position = peOffset;
                if (reader.ReadUInt32() != 0x4550 || reader.ReadUInt16() != 0x8664)
                    throw new InvalidOperationException("需要 64 位网易云客户端；请选择 x64 安装目录。");
            }
        }

        private Tuple<bool, Dictionary<string, object>, string> TryReadMultiListenStatus()
        {
            var node = MultiListenNode();
            var script = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, @"multi-listen\control.mjs");
            if (!File.Exists(node)) return new Tuple<bool, Dictionary<string, object>, string>(false, null, "缺少随包 Node 运行环境 runtime\\node\\node.exe。");
            if (!File.Exists(script)) return new Tuple<bool, Dictionary<string, object>, string>(false, null, "缺少补丁组件 multi-listen\\control.mjs；请重新部署工具箱目录。");
            Tuple<int, string, string> result;
            try { result = RunMultiListenControl("status", 12000); }
            catch (Exception ex) { return new Tuple<bool, Dictionary<string, object>, string>(false, null, "控制脚本执行失败：" + ex.Message); }
            if (result.Item1 != 0) {
                var message = result.Item3.Length > 0 ? result.Item3 : result.Item2;
                message = TrimMultiListenText(message);
                return new Tuple<bool, Dictionary<string, object>, string>(false, null,
                    "尚未连接：" + message + "；可直接点“启动并连接”。");
            }
            try {
                var data = new JavaScriptSerializer { MaxJsonLength = 4 * 1024 * 1024 }.Deserialize<Dictionary<string, object>>(result.Item2);
                if (data == null) throw new InvalidDataException("状态输出为空。");
                return new Tuple<bool, Dictionary<string, object>, string>(true, data, "");
            } catch (Exception ex) {
                return new Tuple<bool, Dictionary<string, object>, string>(false, null, "状态输出无法解析：" + ex.Message);
            }
        }

        private Tuple<int, string, string> RunMultiListenControl(string mode, int timeoutMilliseconds)
        {
            var node = MultiListenNode();
            var script = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, @"multi-listen\control.mjs");
            var info = new ProcessStartInfo(node) {
                Arguments = Quoted(script) + " " + mode,
                UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true,
                StandardOutputEncoding = Encoding.UTF8, StandardErrorEncoding = Encoding.UTF8,
                WorkingDirectory = AppDomain.CurrentDomain.BaseDirectory
            };
            SetMultiListenEnvironment(info);
            using (var process = new Process { StartInfo = info }) {
                process.Start();
                var output = process.StandardOutput.ReadToEndAsync();
                var error = process.StandardError.ReadToEndAsync();
                if (!process.WaitForExit(timeoutMilliseconds)) {
                    try { process.Kill(); } catch { }
                    throw new TimeoutException("控制脚本 " + mode + " 超时。");
                }
                Task.WaitAll(output, error);
                return new Tuple<int, string, string>(process.ExitCode, output.Result, error.Result);
            }
        }

        private Process StartMultiListenBridge()
        {
            Directory.CreateDirectory(multiListenRoot);
            var log = Path.Combine(multiListenRoot, "bridge.log");
            try {
                var info = new FileInfo(log);
                if (info.Exists && info.Length > 2 * 1024 * 1024) File.Delete(log);
            } catch { }
            try { multiListenBridgeLogStart = File.Exists(log) ? new FileInfo(log).Length : 0; }
            catch { multiListenBridgeLogStart = 0; }
            var command = Quoted(MultiListenNode()) + " " + Quoted(Path.Combine(AppDomain.CurrentDomain.BaseDirectory, @"multi-listen\control.mjs"))
                + " watch >> " + Quoted(log) + " 2>&1";
            var startInfo = new ProcessStartInfo("cmd.exe", "/s /c \"" + command + "\"") {
                UseShellExecute = false, CreateNoWindow = true, WorkingDirectory = AppDomain.CurrentDomain.BaseDirectory
            };
            SetMultiListenEnvironment(startInfo);
            return Process.Start(startInfo);
        }

        private string ReadMultiListenBridgeFailure(int exitCode)
        {
            try {
                var log = Path.Combine(multiListenRoot, "bridge.log");
                using (var stream = new FileStream(log, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
                using (var reader = new StreamReader(stream, Encoding.UTF8)) {
                    stream.Position = Math.Min(stream.Length, Math.Max(multiListenBridgeLogStart, stream.Length - 8192));
                    var lines = reader.ReadToEnd().Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries);
                    for (int index = lines.Length - 1; index >= 0; index--) {
                        var line = lines[index].Trim();
                        if (line.IndexOf("Error:", StringComparison.OrdinalIgnoreCase) >= 0
                            || line.IndexOf("失败", StringComparison.Ordinal) >= 0
                            || line.IndexOf("错误", StringComparison.Ordinal) >= 0)
                            return TrimMultiListenText(line);
                    }
                }
            } catch { }
            return "后台桥已退出（退出码 " + exitCode + "）；详情见日志文件夹中的 bridge.log。";
        }

        private void SetMultiListenEnvironment(ProcessStartInfo info)
        {
            var executable = multiListenClientExecutable;
            var processId = multiListenClientPid;
            if (executable.Length == 0) {
                var folder = ClientFolder();
                if (!string.IsNullOrEmpty(folder)) executable = Path.GetFullPath(Path.Combine(folder, "cloudmusic.exe"));
            }
            if (executable.Length > 0) info.EnvironmentVariables["NCM_LISTEN_CLIENT_EXE"] = executable;
            if (processId <= 0 && executable.Length > 0) processId = FindRunningMultiListenClient(executable);
            if (processId > 0) info.EnvironmentVariables["NCM_LISTEN_CLIENT_PID"] = processId.ToString(System.Globalization.CultureInfo.InvariantCulture);
            else info.EnvironmentVariables.Remove("NCM_LISTEN_CLIENT_PID");
        }

        private void RenderMultiListen(Dictionary<string, object> data, string problem)
        {
            multiListenInstalled = false;
            multiListenMessages.BackColor = panel;
            multiListenMessages.ForeColor = ink;
            multiListenMessages.Text = "";
            multiListenMessageCache.Clear();
            if (problem != null) {
                multiListenErrorCache = problem;
                SetMultiListenStatusText("未连接：" + problem);
                SetMultiListenDetailText("点击“启动并连接”后，这里会显示补丁版本、房间人数与同步状态。");
                return;
            }
            multiListenErrorCache = "";
            if (!MultiListenFlag(data, "installed")) {
                SetMultiListenStatusText("网易云调试通道已连接，补丁尚未运行。");
                SetMultiListenDetailText("点击“启动并连接”安装补丁。");
                return;
            }
            multiListenInstalled = true;
            var version = RecallValue(data, "version");
            var roomId = RecallValue(data, "roomId");
            var state = roomId.Length > 0
                ? "房间 " + roomId + " · 在线 " + RecallValue(data, "members") + " 人 · "
                    + (MultiListenFlag(data, "stale") ? "同步中断" : MultiListenFlag(data, "syncing") ? "跟随房间" : "本机暂停")
                : "尚未连接房间";
            SetMultiListenStatusText("补丁 " + version + " · " + state + " · 邀请点击入口"
                + (MultiListenFlag(data, "clickHook") ? "已连接" : "未连接") + "。");
            var detail = new StringBuilder();
            var notice = RecallValue(data, "notice");
            if (notice.Length > 0) detail.Append(notice);
            var songId = RecallValue(data, "songId");
            if (songId.Length > 0) { if (detail.Length > 0) detail.Append("；"); detail.Append("房间歌曲 " + songId); }
            SetMultiListenDetailText(detail.ToString());
            var messages = data.ContainsKey("messages") ? data["messages"] as ArrayList : null;
            if (messages != null) {
                var lines = new List<string>();
                foreach (var entry in messages) {
                    var item = entry as Dictionary<string, object>;
                    if (item == null) continue;
                    var time = RecallValue(item, "time");
                    var message = RecallValue(item, "message");
                    var isError = MultiListenFlag(item, "error");
                    multiListenMessageCache.Add(new MultiListenMessageEntry(time, message, isError));
                    lines.Add(time + "  " + (isError ? "！" : "") + message);
                }
                if (multiListenMessageCache.Count > 10)
                    multiListenMessageCache.RemoveRange(0, multiListenMessageCache.Count - 10);
                if (lines.Count > 10) lines.RemoveRange(0, lines.Count - 10);
                multiListenMessages.Lines = lines.ToArray();
            }
        }

        private void SetMultiListenStatusText(string text)
        {
            multiListenStatusCache = text ?? "";
            multiListenStatus.Text = multiListenStatusCache;
        }

        private void SetMultiListenDetailText(string text)
        {
            multiListenDetailCache = text ?? "";
            multiListenDetail.Text = multiListenDetailCache;
        }

        private void UpdateMultiListenButtons()
        {
            if (multiListenStart == null) return;
            multiListenStart.Enabled = !multiListenBusy;
            multiListenRefresh.Enabled = !multiListenBusy;
            multiListenStop.Enabled = !multiListenBusy && multiListenInstalled;
        }

        private static bool MultiListenFlag(Dictionary<string, object> data, string key)
        {
            object value;
            return data != null && data.TryGetValue(key, out value) && value is bool && (bool)value;
        }

        private static bool MultiListenConnectionReady(Dictionary<string, object> data)
        {
            return data != null && !MultiListenFlag(data, "bridgeDisconnected") && (data.ContainsKey("clickHookReady")
                ? MultiListenFlag(data, "clickHookReady") : MultiListenFlag(data, "clickHook"));
        }

        private static string MultiListenNode()
        {
            var bundled = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, @"runtime\node\node.exe");
            if (File.Exists(bundled)) return bundled;
            return "node.exe";
        }

        private static string TrimMultiListenText(string value)
        {
            value = (value ?? "").Replace("\r", " ").Replace("\n", " ").Trim();
            return value.Length > 220 ? value.Substring(0, 220) + "…" : value;
        }
    }
}
