using System;
using System.Runtime.InteropServices;
using System.Threading;
using NAudio.CoreAudioApi;
using NAudio.CoreAudioApi.Interfaces;

namespace NeteaseToolbox
{
    // User-mode activation only. The process and its children are included by Windows.
    public static class ProcessAudioCapture
    {
        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        private struct OsVersion
        {
            public uint Size, Major, Minor, Build, Platform;
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)] public string ServicePack;
        }
        [DllImport("ntdll.dll", CharSet = CharSet.Unicode)]
        private static extern int RtlGetVersion(ref OsVersion version);
        public static uint WindowsBuild
        {
            get {
                var version = new OsVersion { Size = (uint)Marshal.SizeOf(typeof(OsVersion)) };
                if (RtlGetVersion(ref version) != 0) throw new InvalidOperationException("无法读取 Windows 版本。");
                return version.Build;
            }
        }
        [StructLayout(LayoutKind.Explicit, Size = 24)]
        private struct BlobVariant
        {
            [FieldOffset(0)] public ushort Type;
            [FieldOffset(8)] public uint Length;
            [FieldOffset(16)] public IntPtr Data;
        }

        [ComVisible(true), ComImport, Guid("72A22D78-CDE4-431D-B8CC-843A71199B6D"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        public interface IActivationOperation
        {
            [PreserveSig] int GetActivateResult(out int result, [MarshalAs(UnmanagedType.IUnknown)] out object client);
        }
        [ComVisible(true), Guid("41D949AB-9862-444A-80F6-C261334DA5EB"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        public interface ICompletion
        {
            [PreserveSig] int ActivateCompleted(IActivationOperation operation);
        }
        [ComVisible(true), Guid("94EA2B94-E9CC-49E0-C0FF-EE64CA8F5B90"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        public interface IAgile { }

        [DllImport("Mmdevapi.dll", ExactSpelling = true, CharSet = CharSet.Unicode)]
        private static extern int ActivateAudioInterfaceAsync(string path, ref Guid iid,
            IntPtr parameters, ICompletion completion, out IActivationOperation operation);

        [ComVisible(true), ClassInterface(ClassInterfaceType.None)]
        public sealed class Completion : ICompletion, IAgile
        {
            public readonly ManualResetEvent Ready = new ManualResetEvent(false);
            public object Client;
            public int Result;
            public int ActivateCompleted(IActivationOperation operation)
            {
                try {
                    int result;
                    int hr = operation.GetActivateResult(out result, out Client);
                    Result = hr < 0 ? hr : result;
                } catch (Exception ex) { Result = Marshal.GetHRForException(ex); }
                finally { Ready.Set(); }
                return 0;
            }
        }

        public static AudioClient Activate(int processId)
        {
            if (WindowsBuild < 20348)
                throw new NotSupportedException("仅网易云捕获需要 Windows Build 20348 或更高版本；可手动选择系统声音。");
            var completion = new Completion();
            var memory = Marshal.AllocHGlobal(40);
            IActivationOperation operation = null;
            try {
                var data = IntPtr.Add(memory, 24);
                Marshal.WriteInt32(data, 0, 1); // PROCESS_LOOPBACK
                Marshal.WriteInt32(data, 4, processId);
                Marshal.WriteInt32(data, 8, 0); // INCLUDE_TARGET_PROCESS_TREE
                var parameters = new BlobVariant { Type = 65, Length = 12, Data = data };
                Marshal.StructureToPtr(parameters, memory, false);
                var iid = typeof(IAudioClient).GUID;
                Marshal.ThrowExceptionForHR(ActivateAudioInterfaceAsync("VAD\\Process_Loopback", ref iid,
                    memory, completion, out operation));
                // Keep both the callback and its native parameters alive until activation completes.
                if (!completion.Ready.WaitOne(10000)) {
                    var retainedMemory = memory; memory = IntPtr.Zero;
                    var retainedOperation = operation; operation = null;
                    ThreadPool.RegisterWaitForSingleObject(completion.Ready, delegate {
                        if (completion.Client != null && Marshal.IsComObject(completion.Client)) Marshal.ReleaseComObject(completion.Client);
                        if (retainedOperation != null) Marshal.ReleaseComObject(retainedOperation);
                        Marshal.FreeHGlobal(retainedMemory); completion.Ready.Dispose();
                    }, null, -1, true);
                    throw new TimeoutException("网易云进程音频激活超时；请重试，或手动切换系统声音。");
                }
                Marshal.ThrowExceptionForHR(completion.Result);
                var client = new AudioClient((IAudioClient)completion.Client);
                completion.Client = null;
                return client;
            } finally {
                if (memory != IntPtr.Zero) {
                    Marshal.FreeHGlobal(memory);
                    if (completion.Client != null && Marshal.IsComObject(completion.Client)) Marshal.ReleaseComObject(completion.Client);
                    completion.Ready.Dispose();
                }
                if (operation != null) Marshal.ReleaseComObject(operation);
                GC.KeepAlive(completion);
            }
        }
    }
}
