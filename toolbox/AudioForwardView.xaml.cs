using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;

namespace NeteaseToolbox.Wpf
{
    // Native WPF page for audio forwarding. All state and actions stay in ToolboxForm's
    // BuildAudioForwardingPage; this view only renders a snapshot and routes user actions
    // through Toolbox.AudioForwardBridge.cs.
    public partial class AudioForwardView : UserControl
    {
        private readonly ToolboxForm facade;
        private readonly DispatcherTimer refreshTimer;
        private readonly DispatcherTimer meterTimer;
        private string outputSignature = "";
        private string microphoneSignature = "";
        private string processSignature = "";
        private bool applying, operating, loaded;

        private static readonly Brush SuccessBrush = Frozen(42, 157, 143);
        private static readonly Brush WarningBrush = Frozen(233, 150, 62);
        private static readonly Brush ErrorBrush = Frozen(231, 111, 81);

        public AudioForwardView() : this(null) { }

        internal AudioForwardView(ToolboxForm facade)
        {
            this.facade = facade ?? ToolboxForm.Instance;
            InitializeComponent();
            refreshTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1.5) };
            refreshTimer.Tick += delegate { OnRefreshTick(); };
            // Level meters are sampled much faster than the status snapshot so they do not look
            // frozen; this only reads the forwarder peaks and never touches device state.
            meterTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(120) };
            meterTimer.Tick += delegate { OnMeterTick(); };
            Loaded += OnLoaded;
            Unloaded += delegate { refreshTimer.Stop(); meterTimer.Stop(); };
            IsVisibleChanged += delegate
            {
                if (!IsLoaded) return;
                if (IsVisible) { refreshTimer.Start(); meterTimer.Start(); Refresh(); }
                else { refreshTimer.Stop(); meterTimer.Stop(); }
            };
        }

        private static Brush Frozen(byte red, byte green, byte blue)
        {
            var brush = new SolidColorBrush(Color.FromRgb(red, green, blue));
            brush.Freeze();
            return brush;
        }

        private void OnLoaded(object sender, RoutedEventArgs e)
        {
            loaded = true;
            refreshTimer.Start();
            meterTimer.Start();
            // Entering the page re-enumerates devices once so a freshly plugged cable or a
            // NetEase process started in the meantime shows up immediately.
            if (facade != null && !facade.IsDisposed)
            {
                try { facade.AudioForwardRefreshChoices(false); }
                catch { }
            }
            Refresh();
        }

        private void OnMeterTick()
        {
            if (facade == null || facade.IsDisposed) return;
            AudioForwardLevels levels;
            try { levels = facade.ReadAudioForwardLevels(); }
            catch { return; }
            double music = Math.Max(0, Math.Min(100, levels.MusicPeak));
            double microphone = Math.Max(0, Math.Min(100, levels.MicrophonePeak));
            MusicMeter.Value = music;
            MicrophoneMeter.Value = microphone;
            MusicLevelText.Text = (int)Math.Round(music) + "%";
            MicrophoneLevelText.Text = (int)Math.Round(microphone) + "%";
            if (!string.IsNullOrEmpty(levels.MixState)) MixStateText.Text = levels.MixState;
        }

        private void OnRefreshTick()
        {
            // Device enumeration is expensive and now runs in the background on page entry or
            // the explicit refresh button, so the periodic tick only re-reads the snapshot.
            Refresh();
        }

        private void Refresh()
        {
            if (facade == null || facade.IsDisposed)
            {
                ShowError("未找到音频转麦克风服务，请重启工具箱。");
                return;
            }
            AudioForwardSnapshot snapshot;
            try { snapshot = facade.ReadAudioForwardSnapshot(); }
            catch (Exception ex) { ShowError("读取转发状态失败：" + ex.Message); return; }
            ApplySnapshot(snapshot);
        }

        private void ApplySnapshot(AudioForwardSnapshot snapshot)
        {
            applying = true;
            try
            {
                bool busy = snapshot.Busy || operating;
                bool idle = !busy && !snapshot.Running;

                StatusTextBlock.Text = string.IsNullOrWhiteSpace(snapshot.Status)
                    ? "就绪 · 选择虚拟输出设备后开始转发。"
                    : snapshot.Status;
                StatusDot.Fill = StatusBrush(snapshot, busy);
                BusyBar.Visibility = busy ? Visibility.Visible : Visibility.Collapsed;
                BusyText.Visibility = busy ? Visibility.Visible : Visibility.Collapsed;

                ProcessStateText.Text = string.IsNullOrWhiteSpace(snapshot.ProcessState)
                    ? "网易云进程：正在检测…"
                    : snapshot.ProcessState;
                ProcessModeRadio.IsChecked = snapshot.ProcessOnly;
                SystemModeRadio.IsChecked = !snapshot.ProcessOnly;
                AutoStartSwitch.IsOn = snapshot.AutoStart;
                MixSwitch.IsOn = snapshot.MixMicrophone;

                UpdateOutputs(snapshot.Outputs, snapshot.OutputId);
                UpdateMicrophones(snapshot.Microphones, snapshot.MicrophoneId);
                UpdateProcesses(snapshot.Processes, snapshot.ProcessId);

                double music = Math.Max(0, Math.Min(100, snapshot.MusicVolume));
                if (Math.Abs(MusicVolumeSlider.Value - music) > 0.4) MusicVolumeSlider.Value = music;
                MusicVolumeText.Text = (int)Math.Round(music) + "%";
                double microphone = Math.Max(0, Math.Min(100, snapshot.MicrophoneVolume));
                if (Math.Abs(MicrophoneVolumeSlider.Value - microphone) > 0.4) MicrophoneVolumeSlider.Value = microphone;
                MicrophoneVolumeText.Text = (int)Math.Round(microphone) + "%";

                double musicPeak = Math.Max(0, Math.Min(100, snapshot.MusicPeak));
                double microphonePeak = Math.Max(0, Math.Min(100, snapshot.MicrophonePeak));
                MusicMeter.Value = musicPeak;
                MicrophoneMeter.Value = microphonePeak;
                MusicLevelText.Text = (int)Math.Round(musicPeak) + "%";
                MicrophoneLevelText.Text = (int)Math.Round(microphonePeak) + "%";
                MixStateText.Text = string.IsNullOrWhiteSpace(snapshot.MixState)
                    ? "混音输出：未启用麦克风，只转发音乐。"
                    : snapshot.MixState;

                StartButton.IsEnabled = idle;
                StopButton.IsEnabled = !busy && snapshot.Running;
                RefreshButton.IsEnabled = idle;
                ProcessModeRadio.IsEnabled = idle;
                SystemModeRadio.IsEnabled = idle;
                ProcessCombo.IsEnabled = idle && snapshot.ProcessOnly;
                OutputCombo.IsEnabled = idle;
                AutoStartSwitch.IsEnabled = idle;
                MixSwitch.IsEnabled = idle;
                MicrophoneCombo.IsEnabled = idle && snapshot.MixMicrophone;
                MicrophoneVolumeSlider.IsEnabled = idle && snapshot.MixMicrophone;
                // Music volume stays adjustable while forwarding; it is applied live.
                MusicVolumeSlider.IsEnabled = !busy;
            }
            finally { applying = false; }
        }

        private Brush StatusBrush(AudioForwardSnapshot snapshot, bool busy)
        {
            if (busy) return TryFindResource("SystemControlHighlightAccentBrush") as Brush ?? SuccessBrush;
            if (snapshot.Running) return SuccessBrush;
            string status = snapshot.Status ?? "";
            if (status.Contains("失败") || status.Contains("错误") || status.Contains("未找到可用的播放设备")) return ErrorBrush;
            if (snapshot.AutoStart) return WarningBrush;
            return TryFindResource("SystemControlHighlightAccentBrush") as Brush ?? SuccessBrush;
        }

        private void UpdateOutputs(List<AudioEndpoint> outputs, string selectedId)
        {
            string signature = string.Join("|", outputs.Select(d => d.Id + "=" + d.Name));
            if (signature != outputSignature && !OutputCombo.IsDropDownOpen)
            {
                outputSignature = signature;
                OutputCombo.Items.Clear();
                foreach (var device in outputs) OutputCombo.Items.Add(device);
                OutputCombo.SelectedItem = null;
            }
            SelectById(OutputCombo, selectedId);
        }

        private void UpdateMicrophones(List<AudioEndpoint> microphones, string selectedId)
        {
            string signature = string.Join("|", microphones.Select(d => d.Id + "=" + d.Name));
            if (signature != microphoneSignature && !MicrophoneCombo.IsDropDownOpen)
            {
                microphoneSignature = signature;
                MicrophoneCombo.Items.Clear();
                foreach (var device in microphones) MicrophoneCombo.Items.Add(device);
                MicrophoneCombo.SelectedItem = null;
            }
            SelectById(MicrophoneCombo, selectedId);
        }

        private void UpdateProcesses(List<MusicProcess> processes, int processId)
        {
            string signature = string.Join("|", processes.Select(p => p.Id.ToString()));
            if (signature != processSignature && !ProcessCombo.IsDropDownOpen)
            {
                processSignature = signature;
                ProcessCombo.Items.Clear();
                foreach (var process in processes) ProcessCombo.Items.Add(process);
                ProcessCombo.SelectedItem = null;
            }
            if (processId <= 0)
            {
                if (ProcessCombo.SelectedItem != null) ProcessCombo.SelectedItem = null;
                return;
            }
            foreach (object item in ProcessCombo.Items)
            {
                var process = item as MusicProcess;
                if (process != null && process.Id == processId)
                {
                    if (!ReferenceEquals(ProcessCombo.SelectedItem, item)) ProcessCombo.SelectedItem = item;
                    return;
                }
            }
            if (ProcessCombo.SelectedItem != null) ProcessCombo.SelectedItem = null;
        }

        private static void SelectById(ComboBox combo, string id)
        {
            if (string.IsNullOrEmpty(id))
            {
                if (combo.SelectedItem != null) combo.SelectedItem = null;
                return;
            }
            foreach (object item in combo.Items)
            {
                var endpoint = item as AudioEndpoint;
                if (endpoint != null && endpoint.Id == id)
                {
                    if (!ReferenceEquals(combo.SelectedItem, item)) combo.SelectedItem = item;
                    return;
                }
            }
        }

        private void OnOutputSelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (applying || facade == null) return;
            var endpoint = OutputCombo.SelectedItem as AudioEndpoint;
            if (endpoint == null) return;
            facade.AudioForwardSelectOutput(endpoint.Id);
            Refresh();
        }

        private void OnMicrophoneSelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (applying || facade == null) return;
            var endpoint = MicrophoneCombo.SelectedItem as AudioEndpoint;
            if (endpoint == null) return;
            facade.AudioForwardSelectMicrophone(endpoint.Id);
            Refresh();
        }

        private void OnProcessSelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (applying || facade == null) return;
            var process = ProcessCombo.SelectedItem as MusicProcess;
            if (process == null) return;
            facade.AudioForwardSelectProcess(process.Id);
            Refresh();
        }

        private void OnModeChecked(object sender, RoutedEventArgs e)
        {
            if (applying || facade == null) return;
            facade.AudioForwardSetProcessOnly(ProcessModeRadio.IsChecked == true);
            Refresh();
        }

        private void OnMixToggled(object sender, RoutedEventArgs e)
        {
            if (applying || facade == null) return;
            facade.AudioForwardSetMixMicrophone(MixSwitch.IsOn);
            Refresh();
        }

        private void OnAutoStartToggled(object sender, RoutedEventArgs e)
        {
            if (applying || facade == null) return;
            facade.AudioForwardSetAutoStart(AutoStartSwitch.IsOn);
            Refresh();
        }

        private void OnMusicVolumeChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
        {
            if (MusicVolumeText != null) MusicVolumeText.Text = (int)Math.Round(e.NewValue) + "%";
            if (!loaded || applying || facade == null) return;
            if (MusicVolumeSlider == null || MicrophoneVolumeSlider == null) return;
            facade.AudioForwardSetVolumes((int)Math.Round(e.NewValue), (int)Math.Round(MicrophoneVolumeSlider.Value));
        }

        private void OnMicrophoneVolumeChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
        {
            if (MicrophoneVolumeText != null) MicrophoneVolumeText.Text = (int)Math.Round(e.NewValue) + "%";
            if (!loaded || applying || facade == null) return;
            if (MusicVolumeSlider == null || MicrophoneVolumeSlider == null) return;
            facade.AudioForwardSetVolumes((int)Math.Round(MusicVolumeSlider.Value), (int)Math.Round(e.NewValue));
        }

        private async void OnStartClick(object sender, RoutedEventArgs e)
        {
            if (facade == null || operating) return;
            operating = true;
            HideError();
            Refresh();
            try
            {
                string error = await facade.AudioForwardStartAsync();
                if (!string.IsNullOrEmpty(error)) ShowError(error);
            }
            catch (Exception ex) { ShowError("启动失败：" + ex.Message); }
            finally
            {
                operating = false;
                Refresh();
            }
        }

        private async void OnStopClick(object sender, RoutedEventArgs e)
        {
            if (facade == null || operating) return;
            operating = true;
            HideError();
            Refresh();
            try
            {
                string error = await facade.AudioForwardStopAsync();
                if (!string.IsNullOrEmpty(error)) ShowError(error);
            }
            catch (Exception ex) { ShowError("停止失败：" + ex.Message); }
            finally
            {
                operating = false;
                Refresh();
            }
        }

        private void OnRefreshClick(object sender, RoutedEventArgs e)
        {
            if (facade == null) return;
            HideError();
            try { facade.AudioForwardRefreshChoices(true); }
            catch (Exception ex) { ShowError("刷新设备失败：" + ex.Message); }
            Refresh();
        }

        private void OnGuideToggleClick(object sender, RoutedEventArgs e)
        {
            bool show = GuidePanel.Visibility != Visibility.Visible;
            GuidePanel.Visibility = show ? Visibility.Visible : Visibility.Collapsed;
            GuideToggleButton.Content = show ? "隐藏指南" : "显示指南";
        }

        private void OnDownloadClick(object sender, RoutedEventArgs e)
        {
            if (facade == null) return;
            string error = facade.AudioForwardOpenCableDownload();
            if (string.IsNullOrEmpty(error)) HideError();
            else ShowError(error);
        }

        private void ShowError(string message)
        {
            if (string.IsNullOrWhiteSpace(message)) { HideError(); return; }
            ErrorTextBlock.Text = message;
            ErrorPanel.Visibility = Visibility.Visible;
        }

        private void HideError()
        {
            ErrorTextBlock.Text = "";
            ErrorPanel.Visibility = Visibility.Collapsed;
        }
    }
}
