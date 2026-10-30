using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using NAudio.CoreAudioApi;
using NAudio.Wave;
using NAudio.Wave.SampleProviders;

namespace NeteaseToolbox
{
    internal sealed class AudioEndpoint
    {
        public string Id, Name;
        public override string ToString() { return Name; }
    }
    internal sealed class MusicProcess
    {
        public int Id;
        public override string ToString() { return "网易云 · PID " + Id + "（含子进程）"; }
    }
    internal sealed class AudioForwarder : IDisposable
    {
        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        private struct ProcessEntry
        {
            public uint Size, Usage, Id;
            public IntPtr DefaultHeap;
            public uint Module, Threads, ParentId;
            public int Priority;
            public uint Flags;
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 260)] public string Exe;
        }
        [DllImport("kernel32.dll", SetLastError = true)] private static extern IntPtr CreateToolhelp32Snapshot(uint flags, uint pid);
        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)] private static extern bool Process32FirstW(IntPtr snapshot, ref ProcessEntry entry);
        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)] private static extern bool Process32NextW(IntPtr snapshot, ref ProcessEntry entry);
        [DllImport("kernel32.dll")] private static extern bool CloseHandle(IntPtr handle);
        // A virtual cable exposes both a render and a capture endpoint. Mixing its own
        // capture side back in while rendering to it would build a feedback loop.
        private static readonly string[] VirtualTokens = { "vb-audio", "cable", "voicemeeter", "virtual", "steam streaming", "loopback" };
        // Reading FriendlyName goes through the audio service and can take hundreds of
        // milliseconds per endpoint on some drivers, so reuse the first name seen per endpoint.
        private static readonly object FriendlyNameLock = new object();
        private static readonly Dictionary<string, string> FriendlyNameCache = new Dictionary<string, string>();
        private readonly object lifecycle = new object();
        // .NET Framework has no Environment.TickCount64; a shared stopwatch gives the same base.
        private static readonly Stopwatch uptime = Stopwatch.StartNew();
        private CancellationTokenSource cancellation;
        private Task worker;
        private volatile float volume = 0.75f;
        private volatile float microphoneVolume = 1f;
        private volatile float peak;
        private volatile float microphonePeak;
        private volatile float outputPeak;
        private long limitingUntil;
        public float Volume { get { return volume; } set { volume = Math.Max(0, Math.Min(1, value)); } }
        public float MicrophoneVolume { get { return microphoneVolume; } set { microphoneVolume = Math.Max(0, Math.Min(1, value)); } }
        public float Peak { get { return peak; } }
        public float MicrophonePeak { get { return microphonePeak; } }
        public float OutputPeak { get { return outputPeak; } }
        public bool Limiting { get { return uptime.ElapsedMilliseconds < Interlocked.Read(ref limitingUntil); } }
        public bool Running { get { lock (lifecycle) return worker != null && !worker.IsCompleted; } }
        public event Action<string, string> Status;

        public static List<AudioEndpoint> Outputs() { return Endpoints(DataFlow.Render); }
        public static List<AudioEndpoint> Microphones() { return Endpoints(DataFlow.Capture); }
        private static List<AudioEndpoint> Endpoints(DataFlow flow)
        {
            using (var enumerator = new MMDeviceEnumerator()) {
                var result = new List<AudioEndpoint>();
                foreach (var device in enumerator.EnumerateAudioEndPoints(flow, DeviceState.Active))
                    using (device) result.Add(new AudioEndpoint { Id = device.ID, Name = CachedFriendlyName(device) });
                return result;
            }
        }
        private static string CachedFriendlyName(MMDevice device)
        {
            string id = device.ID, name;
            lock (FriendlyNameLock) if (FriendlyNameCache.TryGetValue(id, out name)) return name;
            name = device.FriendlyName;
            lock (FriendlyNameLock) FriendlyNameCache[id] = name;
            return name;
        }
        public static string DefaultMicrophoneId()
        {
            using (var enumerator = new MMDeviceEnumerator()) {
                // Voice software uses the communications role; fall back to the multimedia default.
                try { using (var device = enumerator.GetDefaultAudioEndpoint(DataFlow.Capture, Role.Communications)) return device.ID; }
                catch { using (var device = enumerator.GetDefaultAudioEndpoint(DataFlow.Capture, Role.Multimedia)) return device.ID; }
            }
        }
        internal static string DeviceKey(string friendlyName)
        {
            if (string.IsNullOrEmpty(friendlyName)) return "";
            int open = friendlyName.IndexOf('('), close = friendlyName.LastIndexOf(')');
            if (open < 0 || close <= open) return friendlyName.Trim();
            return friendlyName.Substring(open + 1, close - open - 1).Trim();
        }
        public static bool IsVirtualDevice(string friendlyName)
        {
            if (string.IsNullOrEmpty(friendlyName)) return false;
            var key = DeviceKey(friendlyName);
            return VirtualTokens.Any(token => key.IndexOf(token, StringComparison.OrdinalIgnoreCase) >= 0);
        }
        // Returns the shared device name when the microphone and the output belong to the
        // same (virtual) sound card, otherwise null. Real speakers and a real microphone on
        // one adapter share a name but are separate endpoints, so they stay allowed.
        public static string LoopbackConflict(string outputName, string microphoneName)
        {
            if (string.IsNullOrEmpty(outputName) || string.IsNullOrEmpty(microphoneName)) return null;
            var outputKey = DeviceKey(outputName);
            var microphoneKey = DeviceKey(microphoneName);
            if (string.Equals(outputKey, microphoneKey, StringComparison.OrdinalIgnoreCase))
                return IsVirtualDevice(outputKey) ? outputKey : null;
            if (VirtualTokens.Any(token => outputKey.IndexOf(token, StringComparison.OrdinalIgnoreCase) >= 0
                && microphoneKey.IndexOf(token, StringComparison.OrdinalIgnoreCase) >= 0))
                return outputKey + " / " + microphoneKey;
            return null;
        }
        public static List<MusicProcess> Processes()
        {
            var ids = new Dictionary<int, int>();
            var snapshot = CreateToolhelp32Snapshot(2, 0);
            if (snapshot == new IntPtr(-1)) throw new System.ComponentModel.Win32Exception(Marshal.GetLastWin32Error());
            try {
                var entry = new ProcessEntry { Size = (uint)Marshal.SizeOf(typeof(ProcessEntry)) };
                if (!Process32FirstW(snapshot, ref entry)) throw new System.ComponentModel.Win32Exception(Marshal.GetLastWin32Error());
                do {
                    if (string.Equals(entry.Exe, "cloudmusic.exe", StringComparison.OrdinalIgnoreCase))
                        ids[(int)entry.Id] = (int)entry.ParentId;
                } while (Process32NextW(snapshot, ref entry));
            } finally { CloseHandle(snapshot); }
            return ids.Where(p => !ids.ContainsKey(p.Value)).OrderBy(p => p.Key)
                .Select(p => new MusicProcess { Id = p.Key }).ToList();
        }
        private void Notify(string phase, string message)
        {
            var handler = Status;
            if (handler != null) handler(phase, message);
        }
        public Task StartAsync(string outputId, bool processOnly, int processId, string microphoneId)
        {
            lock (lifecycle) {
                if (worker != null && !worker.IsCompleted) throw new InvalidOperationException("已有转发任务正在运行。");
                if (cancellation != null) cancellation.Dispose();
                cancellation = new CancellationTokenSource();
                var started = new TaskCompletionSource<bool>();
                var token = cancellation.Token;
                worker = Task.Factory.StartNew(() => Run(outputId, processOnly, processId, microphoneId, token, started),
                    CancellationToken.None, TaskCreationOptions.LongRunning, TaskScheduler.Default);
                return started.Task;
            }
        }
        public async Task StopAsync()
        {
            Task pending;
            lock (lifecycle) { if (cancellation != null) cancellation.Cancel(); pending = worker; }
            if (pending != null) await pending.ConfigureAwait(false);
        }
        private void Run(string outputId, bool processOnly, int processId, string microphoneId, CancellationToken token, TaskCompletionSource<bool> started)
        {
            AudioClient client = null;
            MMDevice input = null, output = null, microphone = null;
            WasapiOut player = null;
            WasapiCapture microphoneCapture = null;
            Process process = null;
            Exception playbackError = null;
            Exception microphoneError = null;
            string failure = null;
            bool began = false;
            using (var enumerator = new MMDeviceEnumerator())
            using (var ready = new EventWaitHandle(false, EventResetMode.AutoReset)) {
                try {
                    output = enumerator.GetDevice(outputId);
                    if (output.State != DeviceState.Active) throw new InvalidOperationException("输出设备已断开，请刷新设备。");
                    if (!string.IsNullOrEmpty(microphoneId)) {
                        microphone = enumerator.GetDevice(microphoneId);
                        if (microphone.State != DeviceState.Active) throw new InvalidOperationException("麦克风已断开或未启用，请刷新设备。");
                        var conflict = LoopbackConflict(output.FriendlyName, microphone.FriendlyName);
                        if (conflict != null)
                            throw new InvalidOperationException("所选麦克风与输出虚拟声卡属于同一设备（" + conflict
                                + "），会把转发出去的歌再收回来造成啸叫；请改选真实麦克风。");
                    }
                    WaveFormat format;
                    if (processOnly) {
                        process = Process.GetProcessById(processId);
                        if (!string.Equals(process.ProcessName, "cloudmusic", StringComparison.OrdinalIgnoreCase))
                            throw new InvalidOperationException("所选进程已退出，请刷新网易云进程。");
                        client = ProcessAudioCapture.Activate(processId);
                        format = new WaveFormat(44100, 16, 2);
                    } else {
                        input = enumerator.GetDefaultAudioEndpoint(DataFlow.Render, Role.Multimedia);
                        if (input.ID == output.ID) throw new InvalidOperationException("系统声音模式不能输出到当前默认播放设备，会产生循环回送。请将扬声器/耳机设为默认播放设备。");
                        client = input.AudioClient;
                        format = client.MixFormat;
                    }
                    token.ThrowIfCancellationRequested();
                    client.Initialize(AudioClientShareMode.Shared,
                        AudioClientStreamFlags.Loopback | AudioClientStreamFlags.EventCallback | AudioClientStreamFlags.AutoConvertPcm,
                        0, 0, format, Guid.Empty);
                    client.SetEventHandle(ready.SafeWaitHandle.DangerousGetHandle());
                    var capture = client.AudioCaptureClient;
                    var buffered = new BufferedWaveProvider(format.AsStandardWaveFormat()) {
                        BufferDuration = TimeSpan.FromMilliseconds(300), DiscardOnBufferOverflow = true, ReadFully = true
                    };
                    var music = new MeteredVolume(buffered.ToSampleProvider(), this, false);
                    ISampleProvider source = music;
                    if (microphone != null) {
                        // The real microphone is a second capture stream, resampled to the music
                        // format and mixed in with its own volume before the output limiter.
                        microphoneCapture = new WasapiCapture(microphone);
                        var microphoneBuffer = new BufferedWaveProvider(microphoneCapture.WaveFormat.AsStandardWaveFormat()) {
                            BufferDuration = TimeSpan.FromMilliseconds(300), DiscardOnBufferOverflow = true, ReadFully = true
                        };
                        microphoneCapture.DataAvailable += delegate(object sender, WaveInEventArgs args) {
                            if (microphoneBuffer.BufferedDuration.TotalMilliseconds > 200) microphoneBuffer.ClearBuffer();
                            microphoneBuffer.AddSamples(args.Buffer, 0, args.BytesRecorded);
                        };
                        microphoneCapture.RecordingStopped += delegate(object sender, StoppedEventArgs args) {
                            Interlocked.CompareExchange(ref microphoneError, args.Exception ?? new InvalidOperationException("麦克风采集意外停止。"), null);
                        };
                        ISampleProvider microphoneSamples = microphoneBuffer.ToSampleProvider();
                        if (microphoneSamples.WaveFormat.Channels == 1 && music.WaveFormat.Channels == 2)
                            microphoneSamples = new MonoToStereoSampleProvider(microphoneSamples);
                        else if (microphoneSamples.WaveFormat.Channels == 2 && music.WaveFormat.Channels == 1)
                            microphoneSamples = new StereoToMonoSampleProvider(microphoneSamples);
                        if (microphoneSamples.WaveFormat.SampleRate != music.WaveFormat.SampleRate)
                            microphoneSamples = new WdlResamplingSampleProvider(microphoneSamples, music.WaveFormat.SampleRate);
                        source = new MixingSampleProvider(new[] { music, new MeteredVolume(microphoneSamples, this, true) }) { ReadFully = true };
                    }
                    source = new OutputLimiter(source, this);
                    player = new WasapiOut(output, AudioClientShareMode.Shared, true, 60);
                    player.PlaybackStopped += delegate(object sender, NAudio.Wave.StoppedEventArgs args) {
                        Interlocked.CompareExchange(ref playbackError, args.Exception ?? new InvalidOperationException("输出音频流意外停止。"), null);
                    };
                    player.Init(new SampleToWaveProvider(source));
                    if (microphoneCapture != null) microphoneCapture.StartRecording();
                    client.Start(); player.Play(); began = true;
                    started.TrySetResult(true);
                    var checks = Stopwatch.StartNew();
                    byte[] bytes = new byte[Math.Max(format.BlockAlign, client.BufferSize * format.BlockAlign)];
                    while (!token.IsCancellationRequested) {
                        ready.WaitOne(20);
                        if (token.IsCancellationRequested) break;
                        var error = Interlocked.CompareExchange(ref playbackError, null, null);
                        if (error != null) throw error;
                        var microphoneFailure = Interlocked.CompareExchange(ref microphoneError, null, null);
                        if (microphoneFailure != null) throw microphoneFailure;
                        if (checks.ElapsedMilliseconds >= 500) {
                            checks.Restart();
                            if (process != null && process.HasExited) throw new InvalidOperationException("网易云已退出，转发已停止。");
                            if (output.State != DeviceState.Active || (input != null && input.State != DeviceState.Active))
                                throw new InvalidOperationException("音频设备已断开，转发已停止；重新连接后刷新设备。");
                            if (microphone != null && microphone.State != DeviceState.Active)
                                throw new InvalidOperationException("麦克风已断开，转发已停止；重新连接后刷新设备。");
                            if (input != null) using (var current = enumerator.GetDefaultAudioEndpoint(DataFlow.Render, Role.Multimedia))
                                if (current.ID != input.ID) throw new InvalidOperationException("默认播放设备已切换，请重新开始系统声音转发。");
                        }
                        int packets = 0;
                        while (!token.IsCancellationRequested && capture.GetNextPacketSize() > 0 && packets++ < 100) {
                            int frames; AudioClientBufferFlags flags;
                            var pointer = capture.GetBuffer(out frames, out flags);
                            try {
                                int count = checked(frames * format.BlockAlign);
                                if (bytes.Length < count) bytes = new byte[count];
                                if ((flags & AudioClientBufferFlags.Silent) != 0) Array.Clear(bytes, 0, count);
                                else Marshal.Copy(pointer, bytes, 0, count);
                                // Drop old audio instead of accumulating seconds of latency after a stall.
                                if (buffered.BufferedDuration.TotalMilliseconds > 200) buffered.ClearBuffer();
                                buffered.AddSamples(bytes, 0, count);
                            } finally { capture.ReleaseBuffer(frames); }
                        }
                    }
                } catch (OperationCanceledException) { started.TrySetCanceled(); }
                catch (Exception ex) { failure = ex.Message + " (0x" + ex.HResult.ToString("X8") + ")"; }
                finally {
                    // The worker owns all native resources. No UI callback disposes an active capture.
                    Action<Action> clean = action => { try { action(); } catch (Exception ex) { if (failure == null) failure = "释放音频设备失败：" + ex.Message; } };
                    if (microphoneCapture != null) { clean(() => microphoneCapture.StopRecording()); clean(() => microphoneCapture.Dispose()); }
                    if (client != null) clean(() => client.Stop());
                    if (player != null) { clean(() => player.Stop()); clean(() => player.Dispose()); }
                    if (client != null) clean(() => client.Dispose());
                    if (input != null) clean(() => input.Dispose());
                    if (microphone != null) clean(() => microphone.Dispose());
                    if (output != null) clean(() => output.Dispose());
                    if (process != null) process.Dispose();
                    peak = 0; microphonePeak = 0; outputPeak = 0;
                    Interlocked.Exchange(ref limitingUntil, 0);
                    if (failure != null) {
                        started.TrySetException(new InvalidOperationException(failure));
                        if (began) Notify("失败", failure);
                    } else {
                        started.TrySetCanceled();
                        if (began) Notify("完成", "转发已停止，音频设备已释放。");
                    }
                }
            }
        }
        private sealed class MeteredVolume : ISampleProvider
        {
            private readonly ISampleProvider source;
            private readonly AudioForwarder owner;
            private readonly bool microphone;
            public MeteredVolume(ISampleProvider source, AudioForwarder owner, bool microphone) { this.source = source; this.owner = owner; this.microphone = microphone; }
            public WaveFormat WaveFormat { get { return source.WaveFormat; } }
            public int Read(float[] buffer, int offset, int count)
            {
                int read = source.Read(buffer, offset, count);
                float gain = microphone ? owner.microphoneVolume : owner.volume, maximum = 0;
                for (int i = offset; i < offset + read; i++) {
                    buffer[i] *= gain;
                    maximum = Math.Max(maximum, Math.Abs(buffer[i]));
                }
                if (microphone) owner.microphonePeak = maximum; else owner.peak = maximum;
                return read;
            }
        }
        private sealed class OutputLimiter : ISampleProvider
        {
            private readonly ISampleProvider source;
            private readonly AudioForwarder owner;
            public OutputLimiter(ISampleProvider source, AudioForwarder owner) { this.source = source; this.owner = owner; }
            public WaveFormat WaveFormat { get { return source.WaveFormat; } }
            public int Read(float[] buffer, int offset, int count)
            {
                int read = source.Read(buffer, offset, count);
                float maximum = 0;
                bool clipped = false;
                for (int i = offset; i < offset + read; i++) {
                    float value = buffer[i];
                    if (value > 1f) { value = 1f; clipped = true; }
                    else if (value < -1f) { value = -1f; clipped = true; }
                    buffer[i] = value;
                    maximum = Math.Max(maximum, Math.Abs(value));
                }
                owner.outputPeak = maximum;
                // Keep the warning visible for a moment so the level meter can show it.
                if (clipped) Interlocked.Exchange(ref owner.limitingUntil, uptime.ElapsedMilliseconds + 600);
                return read;
            }
        }
        public void Dispose()
        {
            StopAsync().GetAwaiter().GetResult();
            lock (lifecycle) { if (cancellation != null) { cancellation.Dispose(); cancellation = null; } }
        }
    }
}