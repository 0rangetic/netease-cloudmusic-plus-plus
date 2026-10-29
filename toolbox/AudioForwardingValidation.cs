using System;
using System.Diagnostics;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading;
using NAudio.CoreAudioApi;
using NAudio.Wave;

namespace NeteaseToolbox
{
    // Compile separately as a console program, never part of the shipping EXE.
    internal static class AudioForwardingValidation
    {
        private static void Check(bool condition, string message)
        {
            if (!condition) throw new Exception(message);
            Console.WriteLine("PASS " + message);
        }
        private static void Capture(int pid)
        {
            using (var client = ProcessAudioCapture.Activate(pid))
            using (var ready = new EventWaitHandle(false, EventResetMode.AutoReset)) {
                var format = new WaveFormat(44100, 16, 2);
                client.Initialize(AudioClientShareMode.Shared, AudioClientStreamFlags.Loopback | AudioClientStreamFlags.EventCallback | AudioClientStreamFlags.AutoConvertPcm,
                    0, 0, format, Guid.Empty);
                client.SetEventHandle(ready.SafeWaitHandle.DangerousGetHandle());
                var capture = client.AudioCaptureClient;
                client.Start();
                long total = 0; int peak = 0;
                try {
                    var clock = Stopwatch.StartNew();
                    while (clock.ElapsedMilliseconds < 1500) {
                        ready.WaitOne(20);
                        while (capture.GetNextPacketSize() > 0) {
                            int frames; AudioClientBufferFlags flags;
                            var ptr = capture.GetBuffer(out frames, out flags);
                            try {
                                var data = new byte[frames * format.BlockAlign];
                                if ((flags & AudioClientBufferFlags.Silent) == 0) Marshal.Copy(ptr, data, 0, data.Length);
                                for (int i = 0; i < data.Length; i += 2) peak = Math.Max(peak, Math.Abs((int)BitConverter.ToInt16(data, i)));
                                total += frames;
                            } finally { capture.ReleaseBuffer(frames); }
                        }
                    }
                } finally { client.Stop(); }
                Console.WriteLine("Capture PID=" + pid + " frames=" + total + " peak=" + peak);
                Check(true, "process activation/start/stop/dispose");
            }
        }
        private static float MeasureCable(string deviceName, int milliseconds, Action during)
        {
            using (var enumerator = new MMDeviceEnumerator()) {
                var device = enumerator.EnumerateAudioEndPoints(DataFlow.Capture, DeviceState.Active)
                    .FirstOrDefault(d => d.FriendlyName.IndexOf(deviceName, StringComparison.OrdinalIgnoreCase) >= 0);
                if (device == null) throw new InvalidOperationException("找不到采集设备：" + deviceName);
                using (device) using (var capture = new WasapiCapture(device)) {
                    bool isFloat = capture.WaveFormat.Encoding == WaveFormatEncoding.IeeeFloat;
                    float peak = 0;
                    capture.DataAvailable += delegate(object sender, WaveInEventArgs args) {
                        float local = 0;
                        if (isFloat) {
                            for (int i = 0; i + 4 <= args.BytesRecorded; i += 4) local = Math.Max(local, Math.Abs(BitConverter.ToSingle(args.Buffer, i)));
                        } else if (capture.WaveFormat.BitsPerSample == 16) {
                            for (int i = 0; i + 2 <= args.BytesRecorded; i += 2) local = Math.Max(local, Math.Abs(BitConverter.ToInt16(args.Buffer, i) / 32768f));
                        }
                        if (local > peak) peak = local;
                    };
                    capture.StartRecording();
                    Thread.Sleep(400);
                    peak = 0;
                    var clock = Stopwatch.StartNew();
                    while (clock.ElapsedMilliseconds < milliseconds) { Thread.Sleep(100); if (during != null) during(); }
                    capture.StopRecording();
                    return peak;
                }
            }
        }
        private static int Main()
        {
            return System.Threading.Tasks.Task.Run(() => Validate()).GetAwaiter().GetResult();
        }
        private static int Validate()
        {
            try {
                Console.WriteLine("Windows build=" + ProcessAudioCapture.WindowsBuild + " apartment=" + Thread.CurrentThread.GetApartmentState());
                var outputs = AudioForwarder.Outputs();
                foreach (var endpoint in outputs) Console.WriteLine("OUTPUT " + endpoint.Name + " | " + endpoint.Id);
                var processes = AudioForwarder.Processes();
                foreach (var process in processes) Console.WriteLine("PROCESS " + process);
                var microphones = AudioForwarder.Microphones();
                foreach (var endpoint in microphones) Console.WriteLine("MICROPHONE " + endpoint.Name + " | " + endpoint.Id);
                Check(outputs.Count > 0, "enumerate playback endpoints");
                for (int i = 0; i < 3; i++) Capture(Process.GetCurrentProcess().Id);
                foreach (var process in processes) Capture(process.Id);
                using (var forwarder = new AudioForwarder()) {
                    for (int i = 0; i < 3; i++) {
                        try { forwarder.StartAsync("invalid-endpoint", false, 0, null).GetAwaiter().GetResult(); throw new Exception("Expected failure"); }
                        catch (InvalidOperationException) { }
                        forwarder.StopAsync().GetAwaiter().GetResult();
                        Check(!forwarder.Running, "failed startup releases worker and permits restart");
                    }
                    using (var enumerator = new MMDeviceEnumerator())
                    using (var device = enumerator.GetDefaultAudioEndpoint(DataFlow.Render, Role.Multimedia)) {
                        bool blocked = false;
                        try { forwarder.StartAsync(device.ID, false, 0, null).GetAwaiter().GetResult(); }
                        catch (InvalidOperationException ex) { blocked = ex.Message.Contains("循环回送"); }
                        forwarder.StopAsync().GetAwaiter().GetResult();
                        Check(blocked, "system loopback blocks output into its own source");
                    }
                    // Mixing the recording side of the output cable would feed the forwarded music back in.
                    var cableOutput = outputs.FirstOrDefault(d => d.Name.IndexOf("CABLE Input", StringComparison.OrdinalIgnoreCase) >= 0);
                    var cableInput = microphones.FirstOrDefault(d => d.Name.IndexOf("CABLE Output", StringComparison.OrdinalIgnoreCase) >= 0);
                    if (cableOutput != null && cableInput != null) {
                        string conflict = null;
                        try { forwarder.StartAsync(cableOutput.Id, false, 0, cableInput.Id).GetAwaiter().GetResult(); }
                        catch (InvalidOperationException ex) { conflict = ex.Message; }
                        forwarder.StopAsync().GetAwaiter().GetResult();
                        Check(conflict != null && conflict.Contains("啸叫"), "VB-CABLE recording side is refused as a mix source");
                    } else Console.WriteLine("SKIP VB-CABLE loopback guard: virtual cable unavailable");
                    var steamOutput = outputs.FirstOrDefault(d => d.Name.IndexOf("Steam Streaming Microphone", StringComparison.OrdinalIgnoreCase) >= 0);
                    var steamMicrophone = microphones.FirstOrDefault(d => d.Name.IndexOf("Steam Streaming Microphone", StringComparison.OrdinalIgnoreCase) >= 0);
                    if (steamOutput != null && steamMicrophone != null) {
                        Check(AudioForwarder.LoopbackConflict(steamOutput.Name, steamMicrophone.Name) != null,
                            "Steam virtual pair is detected as a loopback risk");
                        string conflict = null;
                        try { forwarder.StartAsync(steamOutput.Id, false, 0, steamMicrophone.Id).GetAwaiter().GetResult(); }
                        catch (InvalidOperationException ex) { conflict = ex.Message; }
                        forwarder.StopAsync().GetAwaiter().GetResult();
                        Check(conflict != null && conflict.Contains("啸叫"), "Steam virtual pair is refused as a mix source");
                    } else Console.WriteLine("SKIP Steam loopback guard: Steam audio devices unavailable");
                    if (microphones.Count > 0) {
                        using (var enumerator = new MMDeviceEnumerator())
                        using (var device = enumerator.GetDefaultAudioEndpoint(DataFlow.Render, Role.Multimedia)) {
                            var target = outputs.First(d => d.Id != device.ID);
                            // Physical microphones only: a virtual cable's capture side would be
                            // silent here and must never be picked as the mix source by default.
                            var realMicrophone = microphones.FirstOrDefault(d => !AudioForwarder.IsVirtualDevice(d.Name)
                                && AudioForwarder.LoopbackConflict(target.Name, d.Name) == null);
                            Check(realMicrophone != null, "a real microphone is accepted next to the selected output");
                            Check(!AudioForwarder.IsVirtualDevice(realMicrophone.Name), "virtual capture endpoints are told apart from real microphones");
                            if (cableInput != null) Check(AudioForwarder.IsVirtualDevice(cableInput.Name), "VB-CABLE recording side is classified as virtual");
                            forwarder.Volume = 0; forwarder.MicrophoneVolume = 0;
                            for (int i = 0; i < 3; i++) {
                                forwarder.StartAsync(target.Id, false, 0, realMicrophone.Id).GetAwaiter().GetResult();
                                Thread.Sleep(400);
                                forwarder.StopAsync().GetAwaiter().GetResult();
                                Check(!forwarder.Running, "muted system sound plus microphone start/stop/restart");
                            }
                            string microphoneFailure = null;
                            try { forwarder.StartAsync(target.Id, false, 0, "invalid-microphone").GetAwaiter().GetResult(); }
                            catch (InvalidOperationException ex) { microphoneFailure = ex.Message; }
                            forwarder.StopAsync().GetAwaiter().GetResult();
                            Check(microphoneFailure != null && !forwarder.Running, "missing microphone fails cleanly and releases the worker");
                            // With the music muted, the output must still carry the live microphone.
                            forwarder.Volume = 0; forwarder.MicrophoneVolume = 1;
                            forwarder.StartAsync(target.Id, false, 0, realMicrophone.Id).GetAwaiter().GetResult();
                            Thread.Sleep(1500);
                            float microphonePeak = forwarder.MicrophonePeak, outputPeak = forwarder.OutputPeak;
                            Console.WriteLine("MIX meters microphone=" + microphonePeak + " output=" + outputPeak + " limiting=" + forwarder.Limiting);
                            forwarder.StopAsync().GetAwaiter().GetResult();
                            Check(microphonePeak > 0, "live microphone samples reach the mix meter");
                            Check(outputPeak >= microphonePeak * 0.5f, "the muted-music output still carries the microphone");
                            Check(forwarder.MicrophonePeak == 0 && forwarder.OutputPeak == 0, "stopping clears the mix meters");
                        }
                    } else Console.WriteLine("SKIP microphone mixing: no capture device available");
                    var helperPath = Environment.GetEnvironmentVariable("NCM_AUDIO_TEST_CHILD");
                    if (!string.IsNullOrEmpty(helperPath)) {
                        using (var child = Process.Start(new ProcessStartInfo(helperPath) { UseShellExecute = false, CreateNoWindow = true })) {
                            try {
                                string failure = null;
                                forwarder.Status += (phase, message) => { if (phase == "失败") failure = message; };
                                forwarder.Volume = 0;
                                forwarder.StartAsync(outputs[0].Id, true, child.Id, null).GetAwaiter().GetResult();
                                child.Kill(); child.WaitForExit();
                                var wait = Stopwatch.StartNew();
                                while (forwarder.Running && wait.ElapsedMilliseconds < 5000) Thread.Sleep(50);
                                forwarder.StopAsync().GetAwaiter().GetResult();
                                Check(!forwarder.Running && failure != null && failure.Contains("网易云已退出"), "target process exit reports failure and releases devices");
                            } finally { if (!child.HasExited) { child.Kill(); child.WaitForExit(); } }
                        }
                    }
                    var cable = outputs.FirstOrDefault(d => d.Name.IndexOf("CABLE Input", StringComparison.OrdinalIgnoreCase) >= 0);
                    // Mix a physical microphone, never a virtual cable's own capture side.
                    var mixSource = microphones.FirstOrDefault(d => cable != null && !AudioForwarder.IsVirtualDevice(d.Name)
                        && AudioForwarder.LoopbackConflict(cable.Name, d.Name) == null);
                    if (mixSource != null) Console.WriteLine("MIX source=" + mixSource.Name);
                    if (cable == null && processes.Count > 0) {
                        // Muted rendering exercises WasapiOut/resampling without playing captured music aloud.
                        forwarder.Volume = 0;
                        for (int i = 0; i < 3; i++) {
                            forwarder.StartAsync(outputs[0].Id, true, processes[0].Id, null).GetAwaiter().GetResult();
                            Thread.Sleep(500); forwarder.StopAsync().GetAwaiter().GetResult();
                            Check(!forwarder.Running, "muted live process-to-render start/stop/restart");
                        }
                        using (var enumerator = new MMDeviceEnumerator())
                        using (var device = enumerator.GetDefaultAudioEndpoint(DataFlow.Render, Role.Multimedia)) {
                            var target = outputs.First(d => d.Id != device.ID);
                            forwarder.StartAsync(target.Id, false, 0, null).GetAwaiter().GetResult();
                            Thread.Sleep(500); forwarder.StopAsync().GetAwaiter().GetResult();
                            Check(!forwarder.Running, "muted live system-to-render start/stop");
                        }
                    }
                    if (cable != null && processes.Count > 0) {
                        for (int i = 0; i < 3; i++) {
                            forwarder.StartAsync(cable.Id, true, processes[0].Id, null).GetAwaiter().GetResult();
                            Thread.Sleep(1200);
                            Console.WriteLine("FORWARD peak=" + forwarder.Peak);
                            forwarder.StopAsync().GetAwaiter().GetResult();
                            Check(!forwarder.Running, "process to VB-CABLE start/stop/restart");
                        }
                        using (var enumerator = new MMDeviceEnumerator())
                        using (var device = enumerator.GetDefaultAudioEndpoint(DataFlow.Render, Role.Multimedia)) {
                            if (device.ID == cable.Id) {
                                // Installing VB-CABLE makes it the default playback device; system
                                // sound mode must then refuse instead of building a feedback loop.
                                string refused = null;
                                try { forwarder.StartAsync(cable.Id, false, 0, null).GetAwaiter().GetResult(); }
                                catch (InvalidOperationException ex) { refused = ex.Message; }
                                forwarder.StopAsync().GetAwaiter().GetResult();
                                Check(refused != null && refused.Contains("循环回送"), "默认播放设备是 CABLE Input 时系统声音模式被拒绝");
                                Console.WriteLine("NOTE 默认播放设备目前是 CABLE Input：请在声音设置里改回扬声器 / 耳机");
                            } else {
                                forwarder.StartAsync(cable.Id, false, 0, null).GetAwaiter().GetResult();
                                Thread.Sleep(500); forwarder.StopAsync().GetAwaiter().GetResult();
                                Check(!forwarder.Running, "system to VB-CABLE start/stop");
                            }
                        }
                        if (mixSource != null) {
                            // Music plus a real microphone into the cable, microphones muted for the test.
                            forwarder.MicrophoneVolume = 0;
                            for (int i = 0; i < 3; i++) {
                                forwarder.StartAsync(cable.Id, true, processes[0].Id, mixSource.Id).GetAwaiter().GetResult();
                                Thread.Sleep(1200);
                                Console.WriteLine("FORWARD+MIX music=" + forwarder.Peak + " microphone=" + forwarder.MicrophonePeak + " output=" + forwarder.OutputPeak);
                                forwarder.StopAsync().GetAwaiter().GetResult();
                                Check(!forwarder.Running, "process plus microphone to VB-CABLE start/stop/restart");
                            }
                        } else Console.WriteLine("SKIP cable microphone mixing: no real microphone next to the cable");
                        // True end-to-end: record the cable's own capture side while forwarding.
                        if (cableInput != null) {
                            var cablePeak = 0f;
                            forwarder.Volume = 0.25f; forwarder.MicrophoneVolume = 0;
                            forwarder.StartAsync(cable.Id, true, processes[0].Id, null).GetAwaiter().GetResult();
                            float meter = 0;
                            for (int i = 0; i < 6 && meter <= 0.0005f; i++) { Thread.Sleep(400); meter = Math.Max(meter, forwarder.Peak); }
                            cablePeak = MeasureCable("CABLE Output", 1500, () => { meter = Math.Max(meter, forwarder.Peak); });
                            forwarder.StopAsync().GetAwaiter().GetResult();
                            Console.WriteLine("CABLE end-to-end music meter=" + meter + " captured=" + cablePeak);
                            if (meter > 0.0005f) Check(cablePeak > 0.001f, "网易云声音确实到达 CABLE Output");
                            else Console.WriteLine("SKIP cable music capture: 网易云当前没有播放声音");
                            if (mixSource != null) {
                                forwarder.Volume = 0; forwarder.MicrophoneVolume = 1;
                                forwarder.StartAsync(cable.Id, true, processes[0].Id, mixSource.Id).GetAwaiter().GetResult();
                                float microphoneMeter = 0;
                                var microphoneLog = new System.Text.StringBuilder();
                                for (int i = 0; i < 6; i++) {
                                    Thread.Sleep(400);
                                    microphoneMeter = Math.Max(microphoneMeter, forwarder.MicrophonePeak);
                                    microphoneLog.Append(forwarder.MicrophonePeak.ToString("F5")).Append(' ');
                                    if (microphoneMeter > 0.0005f) break;
                                }
                                Console.WriteLine("MIX microphone meter samples: " + microphoneLog);
                                var microphoneCable = MeasureCable("CABLE Output", 1500, () => { microphoneMeter = Math.Max(microphoneMeter, forwarder.MicrophonePeak); });
                                forwarder.StopAsync().GetAwaiter().GetResult();
                                Console.WriteLine("CABLE end-to-end microphone meter=" + microphoneMeter + " captured=" + microphoneCable);
                                Check(microphoneMeter > 0.0005f, "混音前麦克风有信号（" + mixSource.Name + "）");
                                Check(microphoneCable > 0.0005f, "麦克风声音确实到达 CABLE Output");
                            }
                        } else Console.WriteLine("SKIP cable capture check: CABLE Output unavailable");
                    } else Console.WriteLine("SKIP end-to-end VB-CABLE: virtual device or music process unavailable");
                }
                return 0;
            } catch (Exception ex) { Console.Error.WriteLine(ex); return 1; }
        }
    }
}
