using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Threading.Tasks;

namespace NeteaseToolbox
{
    // Read-only view model for the native WPF audio forwarding page. The WinForms controls
    // remain the single source of truth, so the WPF page never duplicates forwarding logic.
    internal sealed class AudioForwardSnapshot
    {
        public bool Busy;
        public bool Running;
        public bool AutoStart;
        public bool ProcessOnly = true;
        public bool MixMicrophone;
        public bool Limiting;
        public int MusicVolume = 75;
        public int MicrophoneVolume = 100;
        public int ProcessId;
        public float MusicPeak;
        public float MicrophonePeak;
        public float OutputPeak;
        public string OutputId = "";
        public string OutputName = "";
        public string MicrophoneId = "";
        public string MicrophoneName = "";
        public string ProcessState = "";
        public string Status = "";
        public string MixState = "";
        public List<AudioEndpoint> Outputs = new List<AudioEndpoint>();
        public List<AudioEndpoint> Microphones = new List<AudioEndpoint>();
        public List<MusicProcess> Processes = new List<MusicProcess>();
    }

    // Cheap per-frame reading for the level meters; the full snapshot is polled far less often.
    internal sealed class AudioForwardLevels
    {
        public float MusicPeak;
        public float MicrophonePeak;
        public float OutputPeak;
        public bool Limiting;
        public string MixState = "";
    }

    // Bridge used by AudioForwardView. Every member runs on the UI thread and only reads or
    // drives the existing WinForms fields of BuildAudioForwardingPage.
    internal sealed partial class ToolboxForm
    {
        internal AudioForwardLevels ReadAudioForwardLevels()
        {
            var levels = new AudioForwardLevels();
            if (IsDisposed) return levels;
            levels.MusicPeak = audioForwarder.Peak * 100f;
            levels.MicrophonePeak = audioForwarder.MicrophonePeak * 100f;
            levels.OutputPeak = audioForwarder.OutputPeak * 100f;
            levels.Limiting = audioForwarder.Limiting;
            levels.MixState = audioMixState.Text ?? "";
            return levels;
        }

        internal AudioForwardSnapshot ReadAudioForwardSnapshot()
        {
            var snapshot = new AudioForwardSnapshot();
            if (IsDisposed) return snapshot;

            snapshot.Busy = audioBusy;
            snapshot.Running = audioForwarder.Running;
            snapshot.AutoStart = audioAutoStart.Checked;
            snapshot.ProcessOnly = audioMode.SelectedIndex <= 0;
            snapshot.MixMicrophone = audioMixMicrophone.Checked;
            snapshot.MusicVolume = audioMusicVolume.Value;
            snapshot.MicrophoneVolume = audioMicrophoneVolume.Value;
            snapshot.MusicPeak = audioForwarder.Peak * 100f;
            snapshot.MicrophonePeak = audioForwarder.MicrophonePeak * 100f;
            snapshot.OutputPeak = audioForwarder.OutputPeak * 100f;
            snapshot.Limiting = audioForwarder.Limiting;
            snapshot.Status = audioState.Text ?? "";
            snapshot.MixState = audioMixState.Text ?? "";
            snapshot.ProcessState = audioProcessState.Text ?? "";

            var output = audioOutputs.SelectedItem as AudioEndpoint;
            if (output != null) { snapshot.OutputId = output.Id; snapshot.OutputName = output.Name; }
            var microphone = audioMicrophones.SelectedItem as AudioEndpoint;
            if (microphone != null) { snapshot.MicrophoneId = microphone.Id; snapshot.MicrophoneName = microphone.Name; }
            var process = audioProcesses.SelectedItem as MusicProcess;
            if (process != null) snapshot.ProcessId = process.Id;

            snapshot.Outputs.AddRange(audioOutputs.Items.OfType<AudioEndpoint>());
            snapshot.Microphones.AddRange(audioMicrophones.Items.OfType<AudioEndpoint>());
            snapshot.Processes.AddRange(audioProcesses.Items.OfType<MusicProcess>());
            return snapshot;
        }

        internal void AudioForwardSelectOutput(string id)
        {
            if (audioBusy || audioForwarder.Running || string.IsNullOrEmpty(id)) return;
            var option = audioOutputs.Items.OfType<AudioEndpoint>().FirstOrDefault(d => d.Id == id);
            if (option != null && !ReferenceEquals(audioOutputs.SelectedItem, option)) audioOutputs.SelectedItem = option;
        }

        internal void AudioForwardSelectMicrophone(string id)
        {
            if (audioBusy || audioForwarder.Running || string.IsNullOrEmpty(id)) return;
            var option = audioMicrophones.Items.OfType<AudioEndpoint>().FirstOrDefault(d => d.Id == id);
            if (option != null && !ReferenceEquals(audioMicrophones.SelectedItem, option)) audioMicrophones.SelectedItem = option;
        }

        internal void AudioForwardSelectProcess(int id)
        {
            if (audioBusy || audioForwarder.Running) return;
            var option = audioProcesses.Items.OfType<MusicProcess>().FirstOrDefault(p => p.Id == id);
            if (option != null && !ReferenceEquals(audioProcesses.SelectedItem, option)) audioProcesses.SelectedItem = option;
        }

        internal void AudioForwardSetProcessOnly(bool processOnly)
        {
            if (audioBusy || audioForwarder.Running) return;
            int index = processOnly ? 0 : 1;
            if (audioMode.SelectedIndex != index) audioMode.SelectedIndex = index;
        }

        internal void AudioForwardSetMixMicrophone(bool enabled)
        {
            if (audioBusy || audioForwarder.Running) return;
            if (audioMixMicrophone.Checked != enabled) audioMixMicrophone.Checked = enabled;
        }

        internal void AudioForwardSetAutoStart(bool enabled)
        {
            if (audioBusy || audioForwarder.Running) return;
            // Changing the box persists the preference and may start forwarding immediately.
            if (audioAutoStart.Checked != enabled) audioAutoStart.Checked = enabled;
        }

        internal void AudioForwardSetVolumes(int music, int microphone)
        {
            music = Math.Max(0, Math.Min(100, music));
            microphone = Math.Max(0, Math.Min(100, microphone));
            if (audioMusicVolume.Value != music) audioMusicVolume.Value = music;
            if (audioMicrophoneVolume.Value != microphone) audioMicrophoneVolume.Value = microphone;
        }

        internal void AudioForwardRefreshChoices(bool userInitiated)
        {
            if (audioBusy || audioForwarder.Running) return;
            RefreshAudioChoices(userInitiated);
        }

        // Returns null on success, otherwise the message that should stay visible on the page.
        internal async Task<string> AudioForwardStartAsync()
        {
            if (audioBusy || audioForwarder.Running) return null;
            audioUserStopped = false;
            audioAutoLastMessage = null;
            try { await StartAudioForwarding(false); }
            catch (Exception ex) { return "启动失败：" + ex.Message; }
            if (audioForwarder.Running) return null;
            string status = audioState.Text;
            return string.IsNullOrWhiteSpace(status) ? "转发未能启动，请查看任务消息。" : status;
        }

        internal async Task<string> AudioForwardStopAsync()
        {
            audioUserStopped = true;
            try { await StopAudioForwarding(); }
            catch (Exception ex) { return "停止失败：" + ex.Message; }
            if (!audioForwarder.Running) return null;
            return string.IsNullOrWhiteSpace(audioState.Text) ? "转发仍在运行，请稍后重试。" : audioState.Text;
        }

        internal string AudioForwardOpenCableDownload()
        {
            try
            {
                Process.Start(new ProcessStartInfo("https://vb-audio.com/Cable/") { UseShellExecute = true });
                ReportTask("音频转麦克风", "完成", "已打开 VB-CABLE 官方网站。");
                return null;
            }
            catch (Exception ex)
            {
                string message = "打开下载页面失败：" + ex.Message;
                audioState.Text = message;
                ReportTask("音频转麦克风", "失败", message);
                return message;
            }
        }
    }
}
