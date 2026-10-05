using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Text;
using System.Web.Script.Serialization;

namespace NeteaseToolbox
{
    // Optional live checks use the legacy account only in an isolated temporary directory.
    internal static class NeteaseAuthValidation
    {
        private static int Main(string[] args)
        {
            string originalLocal = Environment.GetEnvironmentVariable("LOCALAPPDATA");
            string sandbox = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "NcmAuthValidation-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(sandbox);
            Environment.SetEnvironmentVariable("LOCALAPPDATA", sandbox);
            try {
                bool missing = false;
                try { NeteaseAuth.Cookie(); } catch (InvalidOperationException) { missing = true; }
                if (!missing) throw new Exception("Missing session was accepted");
                var sample = new Dictionary<string, object> { { "cookie", "MUSIC_U=validation-only" }, { "userId", "12345" }, { "nickname", "验证账号" } };
                NeteaseAuth.Save(sample); NeteaseAuth.Save(sample);
                if (NeteaseAuth.Cookie() != "MUSIC_U=validation-only" || Encoding.UTF8.GetString(File.ReadAllBytes(NeteaseAuth.SessionPath)).Contains("validation-only")) throw new Exception("Credential protection failed");
                if (!NeteaseAuth.AccountFolder("Profile").EndsWith("12345")) throw new Exception("Account path failed");
                string python = @"D:\CloudMusic\Lyric3\runtime\python.exe";
                var info = new ProcessStartInfo(python) { Arguments = "-c \"import sys,os; sys.path.insert(0,os.getcwd()); import netease_auth; assert netease_auth.load_cookie() == 'MUSIC_U=validation-only'; print('DPAPI_INTEROP_OK')\"", WorkingDirectory = AppDomain.CurrentDomain.BaseDirectory, UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true };
                using (var process = Process.Start(info)) { Console.Write(process.StandardOutput.ReadToEnd()); process.WaitForExit(); if (process.ExitCode != 0) throw new Exception("Python credential reader failed: " + process.StandardError.ReadToEnd()); }
                File.WriteAllBytes(NeteaseAuth.SessionPath, new byte[] { 1, 2, 3 });
                bool corrupt = false; try { NeteaseAuth.Load(); } catch (InvalidOperationException) { corrupt = true; }
                if (!corrupt) throw new Exception("Corrupt credential accepted");
                File.Delete(NeteaseAuth.SessionPath);
                Console.WriteLine("LOCAL_AUTH_OK");
                string ignored;
                var qrSession = new System.Net.CookieContainer();
                var keyResponse = NeteaseAuth.RequestQr("/api/login/qrcode/unikey", new Dictionary<string, object> { { "type", 3 } }, qrSession, out ignored);
                string key = NeteaseAuth.Text(keyResponse, "unikey");
                if (key.Length == 0) throw new Exception("QR key missing");
                using (var bitmap = NeteaseAuth.QrImage("https://music.163.com/login?codekey=" + Uri.EscapeDataString(key), python)) { bitmap.Save(Path.Combine(sandbox, "qr.png")); if (bitmap.Width < 200) throw new Exception("QR too small"); }
                var waiting = NeteaseAuth.RequestQr("/api/login/qrcode/client/login", new Dictionary<string, object> { { "key", key }, { "type", 3 } }, qrSession, out ignored);
                if (NeteaseAuth.Text(waiting, "code") != "801") throw new Exception("Unexpected pending QR status");
                var expired = NeteaseAuth.RequestQr("/api/login/qrcode/client/login", new Dictionary<string, object> { { "key", "invalid-validation-key" }, { "type", 3 } }, qrSession, out ignored);
                if (NeteaseAuth.Text(expired, "code") != "800") throw new Exception("Unexpected invalid QR status");
                Console.WriteLine("LIVE_QR_PENDING_AND_EXPIRED_OK");
                if (Array.IndexOf(args, "--legacy-live") >= 0) {
                    string settings = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "MusicLyricApp", "MusicLyricAppSetting.json");
                    var root = new JavaScriptSerializer().Deserialize<Dictionary<string, object>>(File.ReadAllText(settings, Encoding.UTF8));
                    string cookie = NeteaseAuth.Text(NeteaseAuth.Object(root, "Config"), "NetEaseCookie");
                    NeteaseAuth.Save(NeteaseAuth.Validate(cookie));
                    bool failed = false;
                    using (var recorder = new PlaybackRecorder(Path.Combine(sandbox, "history.csv"), message => { if (message.StartsWith("失败：")) failed = true; })) recorder.StartAsync().Wait();
                    if (failed) throw new Exception("Playback endpoint failed");
                    Console.WriteLine("LIVE_ACCOUNT_AND_PLAYBACK_OK");
                }
                Console.WriteLine("AUTH_VALIDATION_OK"); return 0;
            } catch (Exception ex) { Console.WriteLine("AUTH_VALIDATION_FAILED: " + ex.GetBaseException().Message); return 1; }
            finally {
                Environment.SetEnvironmentVariable("LOCALAPPDATA", originalLocal);
                if (Directory.Exists(sandbox) && Path.GetFullPath(sandbox).StartsWith(Path.GetFullPath(AppDomain.CurrentDomain.BaseDirectory), StringComparison.OrdinalIgnoreCase)) Directory.Delete(sandbox, true);
            }
        }
    }
}
