using System;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using System.Web.Script.Serialization;
using System.Windows.Forms;

namespace NeteaseToolbox
{
    // Persisted forwarding preferences: the toolbox autostarts with Windows, so restoring the
    // last session is what keeps a voice app that can only use the default microphone working.
    internal sealed class AudioForwardingSettings
    {
        public bool AutoStart;
        public string OutputId = "";
        public string OutputName = "";
        public string MicrophoneId = "";
        public string MicrophoneName = "";
        public bool ProcessOnly = true;
        public bool MixMicrophone;
        public int MusicVolume = 75;
        public int MicrophoneVolume = 100;

        public string ToJson() { return new JavaScriptSerializer().Serialize(this); }
        public static AudioForwardingSettings FromJson(string json)
        {
            try { return new JavaScriptSerializer().Deserialize<AudioForwardingSettings>(json); }
            catch { return null; }
        }
    }

    internal sealed partial class ToolboxForm
    {
        private readonly AudioForwarder audioForwarder = new AudioForwarder();
        private readonly ComboBox audioMode = new ComboBox { DropDownStyle = ComboBoxStyle.DropDownList, Width = 260 };
        private readonly ComboBox audioProcesses = new ComboBox { DropDownStyle = ComboBoxStyle.DropDownList, Width = 330 };
        private readonly ComboBox audioOutputs = new ComboBox { DropDownStyle = ComboBoxStyle.DropDownList, Width = 470 };
        private readonly ComboBox audioMicrophones = new ComboBox { Name = "AudioMicrophoneSelector", DropDownStyle = ComboBoxStyle.DropDownList, Width = 470 };
        private readonly CheckBox audioMixMicrophone = new CheckBox { Name = "AudioMixMicrophone", Text = "混入真实麦克风", AutoSize = true };
        private readonly CheckBox audioAutoStart = new CheckBox { Name = "AudioAutoStart", Text = "打开工具箱后自动开始转发（记住本次设备与音量）", AutoSize = true };
        private readonly TrackBar audioMusicVolume = new TrackBar { Name = "AudioMusicVolume", Minimum = 0, Maximum = 100, Value = 75, TickFrequency = 10, Width = 360 };
        private readonly TrackBar audioMicrophoneVolume = new TrackBar { Name = "AudioMicrophoneVolume", Minimum = 0, Maximum = 100, Value = 100, TickFrequency = 10, Width = 360 };
        private readonly ProgressBar audioMicrophoneLevel = new ProgressBar { Name = "AudioMicrophoneLevel", Width = 360, Height = 20, Maximum = 100 };
        private readonly Label audioMixState = new Label { Name = "AudioMixState", AutoSize = false, Width = 570, Height = 40 };
        private readonly Label audioState = new Label { AutoSize = false, Height = 90, Dock = DockStyle.Top };
        private readonly Label audioProcessState = new Label { AutoSize = true };
        private readonly ProgressBar audioMeter = new ProgressBar { Width = 360, Height = 20, Maximum = 100 };
        private readonly System.Windows.Forms.Timer audioUiTimer = new System.Windows.Forms.Timer { Interval = 100 };
        private Button audioStart, audioStop, audioRefresh;
        private AudioForwardingSettings audioSettings = new AudioForwardingSettings();
        private bool audioBusy;
        private bool audioSettingsDirty;
        private bool audioUserStopped;
        private string audioAutoLastMessage;
        private int audioTick;

        private static string AudioForwardSettingsPath()
        {
            return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "NeteaseToolbox", "audio-forward.json");
        }
        private void LoadAudioForwardSettings()
        {
            try {
                var path = AudioForwardSettingsPath();
                if (!File.Exists(path)) return;
                var loaded = AudioForwardingSettings.FromJson(File.ReadAllText(path, Encoding.UTF8));
                if (loaded != null) audioSettings = loaded;
            } catch { }
        }
        private void SaveAudioForwardSettings()
        {
            try {
                var path = AudioForwardSettingsPath();
                var folder = Path.GetDirectoryName(path);
                if (!string.IsNullOrEmpty(folder)) Directory.CreateDirectory(folder);
                File.WriteAllText(path, audioSettings.ToJson(), new UTF8Encoding(false));
                audioSettingsDirty = false;
            } catch { }
        }
        private void CaptureAudioSettings()
        {
            audioSettings.AutoStart = audioAutoStart.Checked;
            audioSettings.ProcessOnly = audioMode.SelectedIndex == 0;
            audioSettings.MixMicrophone = audioMixMicrophone.Checked;
            audioSettings.MusicVolume = audioMusicVolume.Value;
            audioSettings.MicrophoneVolume = audioMicrophoneVolume.Value;
            var output = audioOutputs.SelectedItem as AudioEndpoint;
            if (output != null) { audioSettings.OutputId = output.Id; audioSettings.OutputName = output.Name; }
            var microphone = audioMicrophones.SelectedItem as AudioEndpoint;
            if (microphone != null) { audioSettings.MicrophoneId = microphone.Id; audioSettings.MicrophoneName = microphone.Name; }
        }
        private void AudioSettingChanged()
        {
            CaptureAudioSettings();
            audioSettingsDirty = true;
        }

        private Control BuildAudioForwardingPage()
        {
            LoadAudioForwardSettings();
            var page = new WallpaperPanel { Name = "AudioForwardingPage", Dock = DockStyle.Fill, Padding = new Padding(28), AutoScroll = true, BackColor = background };
            var layout = new FlowLayoutPanel { Dock = DockStyle.Top, AutoSize = true, FlowDirection = FlowDirection.TopDown, WrapContents = false };
            page.Controls.Add(layout);
            layout.Controls.Add(Label("音频转麦克风", 20, true, ink));
            layout.Controls.Add(Label("网易云 → 虚拟声卡 → QQ / Discord / 游戏语音", 10, false, muted));
            audioProcessState.Margin = new Padding(3, 20, 3, 8); layout.Controls.Add(audioProcessState);
            audioProcesses.Name = "AudioProcessSelector"; layout.Controls.Add(audioProcesses);
            layout.Controls.Add(Label("捕获模式", 10, true, ink));
            audioMode.Name = "AudioCaptureMode"; audioMode.Items.AddRange(new object[] { "仅网易云（含子进程）", "系统声音（所有应用）" });
            audioMode.SelectedIndex = audioSettings.ProcessOnly ? 0 : 1; layout.Controls.Add(audioMode);
            layout.Controls.Add(Label("输出设备 · 选择 CABLE Input 或其他虚拟声卡播放端", 10, true, ink));
            audioOutputs.Name = "AudioOutputSelector"; layout.Controls.Add(audioOutputs);
            var buttons = new FlowLayoutPanel { AutoSize = true, WrapContents = false, Margin = new Padding(0, 12, 0, 12) };
            audioStart = Button("开始转发", true); audioStart.Name = "AudioForwardStart"; audioStart.Width = 104;
            audioStop = Button("停止", false); audioStop.Name = "AudioForwardStop"; audioStop.Width = 84; audioStop.Enabled = false;
            audioRefresh = Button("刷新设备 / 进程", false); audioRefresh.Width = 160;
            buttons.Controls.AddRange(new Control[] { audioStart, audioStop, audioRefresh }); layout.Controls.Add(buttons);
            audioAutoStart.ForeColor = ink; audioAutoStart.Margin = new Padding(3, 0, 3, 6);
            audioAutoStart.Checked = audioSettings.AutoStart;
            audioAutoStart.CheckedChanged += delegate {
                audioUserStopped = false;
                AudioSettingChanged();
                SaveAudioForwardSettings();
                if (audioAutoStart.Checked) StartAudioForwarding(true);
            };
            layout.Controls.Add(audioAutoStart);
            // Clamp restored values: a hand-edited or older settings file must not break the page.
            if (audioSettings.MusicVolume >= 0 && audioSettings.MusicVolume <= 100) audioMusicVolume.Value = audioSettings.MusicVolume;
            if (audioSettings.MicrophoneVolume >= 0 && audioSettings.MicrophoneVolume <= 100) audioMicrophoneVolume.Value = audioSettings.MicrophoneVolume;
            var volumeLabel = Label("音乐音量：" + audioMusicVolume.Value + "%", 10, true, ink); layout.Controls.Add(volumeLabel);
            audioMusicVolume.ValueChanged += delegate {
                audioForwarder.Volume = audioMusicVolume.Value / 100f;
                volumeLabel.Text = "音乐音量：" + audioMusicVolume.Value + "%";
                AudioSettingChanged();
            };
            layout.Controls.Add(audioMusicVolume);
            layout.Controls.Add(Label("转发电平（音量控制后）", 10, false, muted));
            audioMeter.Name = "AudioForwardLevel"; layout.Controls.Add(audioMeter);

            audioMixMicrophone.ForeColor = ink;
            audioMixMicrophone.Margin = new Padding(3, 18, 3, 4);
            audioMixMicrophone.Checked = audioSettings.MixMicrophone;
            layout.Controls.Add(audioMixMicrophone);
            layout.Controls.Add(Label("麦克风设备 · 选择你说话用的真实麦克风，不要选虚拟声卡的录音端", 10, true, ink));
            layout.Controls.Add(audioMicrophones);
            var microphoneVolumeLabel = Label("麦克风音量：" + audioMicrophoneVolume.Value + "%", 10, true, ink); layout.Controls.Add(microphoneVolumeLabel);
            audioMicrophoneVolume.ValueChanged += delegate {
                audioForwarder.MicrophoneVolume = audioMicrophoneVolume.Value / 100f;
                microphoneVolumeLabel.Text = "麦克风音量：" + audioMicrophoneVolume.Value + "%";
                AudioSettingChanged();
            };
            layout.Controls.Add(audioMicrophoneVolume);
            layout.Controls.Add(Label("麦克风电平（音量控制后）", 10, false, muted));
            layout.Controls.Add(audioMicrophoneLevel);
            audioMixState.ForeColor = muted; audioMixState.Margin = new Padding(3, 10, 3, 0);
            layout.Controls.Add(audioMixState);

            audioState.Name = "AudioForwardStatus"; audioState.Width = 540; audioState.Text = "就绪 · 选择虚拟输出设备后开始转发。";
            audioState.Margin = new Padding(3, 14, 3, 0); layout.Controls.Add(audioState);
            layout.Controls.Add(Label("使用方法（按顺序）", 11, true, ink));
            layout.Controls.Add(AudioNote("1. 准备虚拟声卡：点下方按钮从官网安装 VB-CABLE；也可以直接用 Steam 自带的“Steam Streaming Microphone”设备对，不必安装任何驱动。"));
            layout.Controls.Add(AudioNote("2. 装完 VB-CABLE 后，去 Windows“设置 → 系统 → 声音”把默认播放改回扬声器 / 耳机、默认录音改回真实麦克风。"
                + "否则其他软件的声音会被送进虚拟线，你听不到声音，通话里还会出现自己的回声。"));
            layout.Controls.Add(AudioNote("3. 本页“输出设备”选虚拟声卡的播放端：CABLE Input (VB-Audio Virtual Cable)；用 Steam 设备对则选“扬声器 (Steam Streaming Microphone)”。"));
            layout.Controls.Add(AudioNote("4. 语音软件里把麦克风选成虚拟声卡的录音端：CABLE Output (VB-Audio Virtual Cable)；Steam 设备对则选“麦克风 (Steam Streaming Microphone)”。"));
            layout.Controls.Add(AudioHighlight("★ 打开 QQ 后把麦克风改成 CABLE Output (VB-Audio Virtual Cable)。"
                + "QQ NT 没有麦克风选择，只能去 Windows“声音 → 更多声音设置 → 录制”把 CABLE Output 设为“默认通信设备”"
                + "（多媒体的默认录音仍保持真实麦克风）。"));
            layout.Controls.Add(AudioNote("5. 想把自己的说话声一起送过去，勾选“混入真实麦克风”并选择真实麦克风（例如“麦克风阵列 (Realtek(R) Audio)”），再分别调好音乐音量和麦克风音量。"));
            layout.Controls.Add(AudioNote("6. 点“开始转发”。运行状态、限幅和错误都显示在下方，并记录到“任务消息”。"));
            layout.Controls.Add(AudioNote("7. 勾选“打开工具箱后自动开始转发”，工具箱随开机自启时会自动恢复本次的设备、模式和音量；"
                + "网易云还没打开就每 5 秒重试一次，不用再手动点。"
                + "注意：只要转发停止，虚拟线里就没有声音——使用默认麦克风的语音软件会连你的说话声一起听不到。"));
            layout.Controls.Add(Label("注意", 11, true, ink));
            layout.Controls.Add(AudioNote("· 请戴耳机使用：外放时麦克风会把歌再收一遍，产生回声或啸叫。"));
            layout.Controls.Add(AudioNote("· 不要把与输出同一块虚拟声卡的录音端选作混入麦克风，工具箱会拒绝启动，避免啸叫。"));
            layout.Controls.Add(AudioNote("· “系统声音”模式捕获默认播放设备上的所有声音，因此不能输出到同一个设备，否则会循环回送。"));
            layout.Controls.Add(AudioNote("· 只做实时转发，不保存音频；网易云退出、麦克风或输出设备断开时会停止转发并释放音频设备。"));
            layout.Controls.Add(Label("安装 VB-CABLE", 11, true, ink));
            layout.Controls.Add(AudioNote("在官网下载 Windows 版压缩包，解压后以管理员身份运行 VBCABLE_Setup_x64.exe，点 Install Driver，按提示重启电脑，再回到此页点“刷新设备 / 进程”。工具箱不代装驱动。"));
            var download = Button("下载 VB-CABLE（官方网站）", false); download.Width = 260;
            download.Click += delegate {
                try { System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo("https://vb-audio.com/Cable/") { UseShellExecute = true }); }
                catch (Exception ex) { audioState.Text = "打开下载页面失败：" + ex.Message; ReportTask("音频转麦克风", "失败", audioState.Text); }
            };
            layout.Controls.Add(download);
            audioStart.Click += async delegate { audioUserStopped = false; audioAutoLastMessage = null; await StartAudioForwarding(false); };
            audioStop.Click += async delegate { audioUserStopped = true; await StopAudioForwarding(); };
            audioRefresh.Click += delegate { RefreshAudioChoices(true); };
            audioMode.SelectedIndexChanged += delegate {
                audioProcesses.Enabled = !audioBusy && !audioForwarder.Running && audioMode.SelectedIndex == 0;
                AudioSettingChanged();
            };
            audioMixMicrophone.CheckedChanged += delegate { SetAudioControls(); AudioSettingChanged(); };
            audioOutputs.SelectedIndexChanged += delegate { AudioSettingChanged(); };
            audioMicrophones.SelectedIndexChanged += delegate { AudioSettingChanged(); };
            audioForwarder.Status += delegate(string phase, string message) {
                InvokeUiAsync(() => {
                    if (IsDisposed) return;
                    audioState.Text = message; ReportTask("音频转麦克风", phase, message); SetAudioControls();
                });
            };
            audioUiTimer.Tick += delegate {
                if (!audioForwarder.Running && audioTick % 10 != 0) {
                    audioTick++;
                    return;
                }
                audioMeter.Value = Math.Max(0, Math.Min(100, (int)(audioForwarder.Peak * 100)));
                audioMicrophoneLevel.Value = Math.Max(0, Math.Min(100, (int)(audioForwarder.MicrophonePeak * 100)));
                audioMixState.Text = MixStateText();
                audioTick++;
                if (audioTick % 20 == 0 && !audioBusy && !audioForwarder.Running && page.Visible && !audioOutputs.DroppedDown
                    && !audioProcesses.DroppedDown && !audioMicrophones.DroppedDown) RefreshAudioChoices(false);
                if (audioTick % 50 == 0) {
                    if (audioSettingsDirty) { CaptureAudioSettings(); SaveAudioForwardSettings(); }
                    // Keep trying while the app runs so a late NetEase start still gets forwarded.
                    if (audioAutoStart.Checked && !audioUserStopped && !audioBusy && !audioForwarder.Running) StartAudioForwarding(true);
                }
                SetAudioControls();
            };
            audioForwarder.Volume = audioMusicVolume.Value / 100f;
            audioForwarder.MicrophoneVolume = audioMicrophoneVolume.Value / 100f;
            audioUiTimer.Start(); RefreshAudioChoices(false);
            audioState.Text = audioAutoStart.Checked
                ? "已开启自动转发 · 正在等待设备与网易云音乐…"
                : "就绪 · 选择虚拟输出设备后开始转发。";
            return page;
        }
        private string MixStateText()
        {
            if (!audioMixMicrophone.Checked) return "混音输出：未启用麦克风，只转发音乐。";
            if (audioForwarder.Limiting) return "混音输出：" + Math.Round(audioForwarder.OutputPeak * 100) + "% · 已限幅，请降低音乐或麦克风音量。";
            return "混音输出：" + Math.Round(audioForwarder.OutputPeak * 100) + "% · 音乐与麦克风相加后输出，带限幅保护。";
        }
        // Wrapped instruction line: AutoSize plus MaximumSize keeps the text readable after the
        // user changes the UI font, instead of a fixed height that could clip it.
        private Label AudioNote(string text)
        {
            return new Label { AutoSize = true, MaximumSize = new Size(600, 0), ForeColor = muted, Text = text,
                Margin = new Padding(3, 2, 3, 2) };
        }
        // Highlighted instruction line for the steps that break the whole setup when missed.
        private Label AudioHighlight(string text)
        {
            return new Label { AutoSize = true, MaximumSize = new Size(600, 0), ForeColor = accent, Text = text,
                Font = UiFont(10F, FontStyle.Bold), Margin = new Padding(3, 4, 3, 4) };
        }
        private void RefreshAudioChoices(bool userInitiated)
        {
            if (audioBusy || audioForwarder.Running) return;
            try {
                var previousDevice = audioOutputs.SelectedItem as AudioEndpoint;
                var previousProcess = audioProcesses.SelectedItem as MusicProcess;
                var previousMicrophone = audioMicrophones.SelectedItem as AudioEndpoint;
                var outputs = AudioForwarder.Outputs(); var processes = AudioForwarder.Processes(); var microphones = AudioForwarder.Microphones();
                audioOutputs.BeginUpdate(); audioProcesses.BeginUpdate(); audioMicrophones.BeginUpdate();
                try {
                    audioOutputs.Items.Clear(); audioOutputs.Items.AddRange(outputs.Cast<object>().ToArray());
                    audioProcesses.Items.Clear(); audioProcesses.Items.AddRange(processes.Cast<object>().ToArray());
                    audioMicrophones.Items.Clear(); audioMicrophones.Items.AddRange(microphones.Cast<object>().ToArray());
                    var selected = outputs.FirstOrDefault(d => previousDevice != null && d.Id == previousDevice.Id)
                        ?? outputs.FirstOrDefault(d => !string.IsNullOrEmpty(audioSettings.OutputId) && d.Id == audioSettings.OutputId)
                        ?? outputs.FirstOrDefault(d => d.Name.IndexOf("CABLE Input", StringComparison.OrdinalIgnoreCase) >= 0);
                    // Never silently route audio into a physical speaker when no virtual device is found.
                    if (selected != null) audioOutputs.SelectedItem = selected;
                    var process = processes.FirstOrDefault(p => previousProcess != null && p.Id == previousProcess.Id) ?? processes.FirstOrDefault();
                    if (process != null) audioProcesses.SelectedItem = process;
                    var microphone = microphones.FirstOrDefault(d => previousMicrophone != null && d.Id == previousMicrophone.Id)
                        ?? microphones.FirstOrDefault(d => !string.IsNullOrEmpty(audioSettings.MicrophoneId) && d.Id == audioSettings.MicrophoneId);
                    if (microphone == null) {
                        string preferred = null;
                        try { preferred = AudioForwarder.DefaultMicrophoneId(); } catch { }
                        var preferredDevice = microphones.FirstOrDefault(d => preferred != null && d.Id == preferred);
                        // Installing a virtual cable makes Windows point its default recording
                        // device at the cable's own capture side; prefer a real microphone so the
                        // first run does not silently mix the cable back into itself.
                        microphone = (preferredDevice != null && !AudioForwarder.IsVirtualDevice(preferredDevice.Name) ? preferredDevice : null)
                            ?? microphones.FirstOrDefault(d => !AudioForwarder.IsVirtualDevice(d.Name))
                            ?? preferredDevice ?? microphones.FirstOrDefault();
                    }
                    // The recording side of the output cable would feed the forwarded music back in.
                    var outputName = audioOutputs.SelectedItem == null ? null : ((AudioEndpoint)audioOutputs.SelectedItem).Name;
                    if (microphone != null && AudioForwarder.LoopbackConflict(outputName, microphone.Name) != null) microphone = null;
                    if (microphone != null) audioMicrophones.SelectedItem = microphone;
                } finally { audioOutputs.EndUpdate(); audioProcesses.EndUpdate(); audioMicrophones.EndUpdate(); }
                audioProcessState.Text = processes.Count > 0 ? "网易云进程：运行中 · " + processes.Count + " 个进程树" : "网易云进程：未运行";
                if (userInitiated) { audioState.Text = "设备和进程已刷新。"; ReportTask("音频转麦克风", "完成", audioState.Text); }
                if (outputs.Count == 0) audioState.Text = "未找到可用的播放设备，请安装 / 连接虚拟声卡后刷新。";
                else if (audioOutputs.SelectedItem == null && audioState.Text.StartsWith("就绪"))
                    audioState.Text = "未检测到 CABLE Input。可从下方官方网站安装 VB-CABLE；其他虚拟声卡请手动选择其播放端。";
                if (microphones.Count == 0) audioMixState.Text = "未找到可用的麦克风，无法混入真实麦克风。";
            } catch (Exception ex) {
                audioProcessState.Text = "设备 / 进程读取失败";
                audioState.Text = ex.Message;
                if (userInitiated) ReportTask("音频转麦克风", "失败", ex.Message);
            }
        }
        private void SetAudioControls()
        {
            bool idle = !audioBusy && !audioForwarder.Running;
            bool mixing = audioMixMicrophone.Checked;
            audioStart.Enabled = idle; audioStop.Enabled = !audioBusy && audioForwarder.Running;
            audioRefresh.Enabled = idle; audioMode.Enabled = idle; audioOutputs.Enabled = idle;
            audioProcesses.Enabled = idle && audioMode.SelectedIndex == 0;
            audioMixMicrophone.Enabled = idle; audioMicrophones.Enabled = idle && mixing;
            audioMicrophoneVolume.Enabled = idle && mixing; audioAutoStart.Enabled = idle;
        }
        // Automatic attempts stay quiet: they update the status line and only report a new
        // problem once, so a long wait for NetEase does not flood the task log.
        private void ReportAudioAutomatic(string phase, string message)
        {
            if (message == audioAutoLastMessage) return;
            audioAutoLastMessage = message;
            ReportTask("音频转麦克风", phase, message);
        }
        private async Task StartAudioForwarding(bool automatic)
        {
            if (audioBusy || audioForwarder.Running) return;
            var output = audioOutputs.SelectedItem as AudioEndpoint;
            var process = audioProcesses.SelectedItem as MusicProcess;
            var microphone = audioMixMicrophone.Checked ? audioMicrophones.SelectedItem as AudioEndpoint : null;
            if (output == null || (audioMode.SelectedIndex == 0 && process == null)) {
                audioState.Text = output == null ? "请选择虚拟声卡的播放端；未安装时请先安装 VB-CABLE。" : "等待网易云音乐启动后自动转发…";
                if (automatic) ReportAudioAutomatic("等待", audioState.Text);
                else ReportTask("音频转麦克风", "失败", audioState.Text);
                return;
            }
            if (audioMixMicrophone.Checked && microphone == null) {
                audioState.Text = "请选择要混入的麦克风，或取消勾选“混入真实麦克风”。";
                if (automatic) ReportAudioAutomatic("失败", audioState.Text); else ReportTask("音频转麦克风", "失败", audioState.Text);
                return;
            }
            if (microphone != null) {
                var conflict = AudioForwarder.LoopbackConflict(output.Name, microphone.Name);
                if (conflict != null) {
                    audioState.Text = "所选麦克风与输出虚拟声卡属于同一设备（" + conflict + "），会把歌再收回来造成啸叫；请改选真实麦克风。";
                    if (automatic) ReportAudioAutomatic("失败", audioState.Text); else ReportTask("音频转麦克风", "失败", audioState.Text);
                    return;
                }
            }
            audioBusy = true; SetAudioControls();
            CaptureAudioSettings(); SaveAudioForwardSettings();
            audioState.Text = microphone == null ? "正在连接捕获和输出设备…" : "正在连接网易云、麦克风与输出设备…";
            if (!automatic) ReportTask("音频转麦克风", "开始", audioState.Text);
            try {
                await audioForwarder.StartAsync(output.Id, audioMode.SelectedIndex == 0, process == null ? 0 : process.Id,
                    microphone == null ? null : microphone.Id);
                audioState.Text = "正在转发：" + audioMode.Text + " → " + output.Name
                    + (microphone == null ? "" : "（已混入 " + microphone.Name + "）")
                    + (automatic ? "（自动开始）" : "");
                ReportTask("音频转麦克风", "运行中", audioState.Text);
                audioAutoLastMessage = null;
            } catch (Exception ex) {
                audioState.Text = "启动失败：" + ex.Message + (audioMode.SelectedIndex == 0 ? "\n可手动选择系统声音后重试。" : "");
                if (automatic) ReportAudioAutomatic("失败", audioState.Text); else ReportTask("音频转麦克风", "失败", audioState.Text);
            } finally { audioBusy = false; if (!IsDisposed) SetAudioControls(); }
        }
        private async Task StopAudioForwarding()
        {
            if (audioBusy) return;
            audioBusy = true; SetAudioControls(); audioState.Text = "正在停止并释放音频设备…";
            ReportTask("音频转麦克风", "停止中", audioState.Text);
            try { await audioForwarder.StopAsync(); }
            catch (Exception ex) { audioState.Text = ex.Message; ReportTask("音频转麦克风", "失败", ex.Message); }
            finally { audioBusy = false; if (!IsDisposed) SetAudioControls(); }
        }
    }
}