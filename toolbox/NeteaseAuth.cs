using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Web.Script.Serialization;

namespace NeteaseToolbox
{
    internal static class NeteaseAuth
    {
        static NeteaseAuth() { }
        internal static readonly string SessionPath = Path.Combine((Environment.GetEnvironmentVariable("LOCALAPPDATA") ?? Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData)), @"NeteaseToolbox\Auth\session.dat");
        internal static Dictionary<string, object> Load()
        {
            if (!File.Exists(SessionPath)) throw new InvalidOperationException("请点击左侧“登录网易云”，用手机扫码登录。");
            try { return new JavaScriptSerializer().Deserialize<Dictionary<string, object>>(Encoding.UTF8.GetString(ProtectedData.Unprotect(File.ReadAllBytes(SessionPath), null, DataProtectionScope.CurrentUser))); }
            catch { throw new InvalidOperationException("无法读取本机登录状态，请重新扫码登录。"); }
        }
        internal static string Cookie() { return Convert.ToString(Load()["cookie"]); }
        internal static string AccountFolder(string category)
        {
            string uid = "unlogged";
            try { uid = Text(Load(), "userId"); } catch { }
            long id;
            if (!long.TryParse(uid, out id)) uid = "unlogged";
            return Path.Combine((Environment.GetEnvironmentVariable("LOCALAPPDATA") ?? Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData)), "NeteaseToolbox", category, uid);
        }
        internal static string Text(Dictionary<string, object> data, string key) { object value; return data != null && data.TryGetValue(key, out value) ? Convert.ToString(value) : ""; }
        internal static Dictionary<string, object> Object(Dictionary<string, object> data, string key) { object value; return data != null && data.TryGetValue(key, out value) ? value as Dictionary<string, object> : null; }
        internal static Dictionary<string, object> Validate(string cookie)
        {
            if (string.IsNullOrWhiteSpace(cookie) || cookie.IndexOf("MUSIC_U=", StringComparison.Ordinal) < 0 || cookie.Contains("\r") || cookie.Contains("\n"))
                throw new InvalidOperationException("没有有效的网易云登录凭证，请重新扫码登录。");
            string ignored;
            var data = Request("/api/w/nuser/account/get", new Dictionary<string, object>(), cookie, out ignored);
            var profile = Object(data, "profile");
            if (Text(data, "code") != "200" || profile == null || Text(profile, "userId") == "")
                throw new InvalidOperationException("网易云登录已失效，请重新扫码登录。");
            return new Dictionary<string, object> { { "cookie", cookie }, { "userId", Text(profile, "userId") }, { "nickname", Text(profile, "nickname") }, { "avatarUrl", Text(profile, "avatarUrl") } };
        }
        internal static void Save(Dictionary<string, object> session)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(SessionPath));
            byte[] data = ProtectedData.Protect(Encoding.UTF8.GetBytes(new JavaScriptSerializer().Serialize(session)), null, DataProtectionScope.CurrentUser);
            string temporary = SessionPath + ".tmp";
            File.WriteAllBytes(temporary, data);
            if (File.Exists(SessionPath)) File.Replace(temporary, SessionPath, null); else File.Move(temporary, SessionPath);
        }
        internal static void MigratePersonalCache(Dictionary<string, object> session)
        {
            string root = Path.Combine((Environment.GetEnvironmentVariable("LOCALAPPDATA") ?? Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData)), "NeteaseToolbox");
            string oldProfile = Path.Combine(root, "Profile", "netease-profile.json");
            if (!File.Exists(oldProfile)) return;
            var previous = new JavaScriptSerializer().Deserialize<Dictionary<string, object>>(File.ReadAllText(oldProfile, Encoding.UTF8));
            if (Text(previous, "userId") != Text(session, "userId")) return;
            foreach (string file in new[] { "netease-profile.json", "first-listen-cache.json", "portrait-ai-results.json" }) {
                string source = Path.Combine(root, "Profile", file), target = Path.Combine(AccountFolder("Profile"), file);
                if (File.Exists(source) && !File.Exists(target)) { Directory.CreateDirectory(Path.GetDirectoryName(target)); File.Copy(source, target); }
            }
            string history = Path.Combine(root, "Playback", "play-history.csv"), destination = Path.Combine(AccountFolder("Playback"), "play-history.csv");
            if (File.Exists(history) && !File.Exists(destination)) { Directory.CreateDirectory(Path.GetDirectoryName(destination)); File.Copy(history, destination); }
        }
        internal static Dictionary<string, object> Request(string path, Dictionary<string, object> payload, string cookie, out string responseCookie)
        {
            try { return Send(path, payload, cookie, false, out responseCookie); }
            catch (WebException ex) {
                if (ex.Status == WebExceptionStatus.ProtocolError) throw new InvalidOperationException("网易云接口暂不可用，请稍后重试。");
                return Send(path, payload, cookie, true, out responseCookie);
            }
        }
        internal static Dictionary<string, object> RequestQr(string path, Dictionary<string, object> payload, CookieContainer session, out string responseCookie)
        {
            try { return Send(path, payload, null, false, out responseCookie, session, true); }
            catch (WebException ex) {
                if (ex.Status == WebExceptionStatus.ProtocolError) throw new InvalidOperationException("网易云扫码接口暂不可用，请稍后重试。");
                return Send(path, payload, null, true, out responseCookie, session, true);
            }
        }
        private static string EncryptQr(string path, Dictionary<string, object> payload)
        {
            var data = new Dictionary<string, object>(payload);
            data["header"] = new Dictionary<string, object> { { "os", "pc" }, { "appver", "3.1.41" }, { "osver", "Microsoft-Windows-10" }, { "requestId", DateTime.UtcNow.Ticks.ToString() } };
            string json = new JavaScriptSerializer().Serialize(data);
            string digest;
            using (var md5 = MD5.Create()) digest = BitConverter.ToString(md5.ComputeHash(Encoding.UTF8.GetBytes("nobody" + path + "use" + json + "md5forencrypt"))).Replace("-", "").ToLowerInvariant();
            byte[] input = Encoding.UTF8.GetBytes(path + "-36cd479b6b5-" + json + "-36cd479b6b5-" + digest);
            using (var aes = Aes.Create()) {
                aes.Mode = CipherMode.ECB; aes.Padding = PaddingMode.PKCS7; aes.Key = Encoding.UTF8.GetBytes("e82ckenh8dichen8");
                using (var transform = aes.CreateEncryptor()) return BitConverter.ToString(transform.TransformFinalBlock(input, 0, input.Length)).Replace("-", "");
            }
        }
        private static Dictionary<string, object> Send(string path, Dictionary<string, object> payload, string cookie, bool proxy, out string responseCookie, CookieContainer session = null, bool encryptedQr = false)
        {
            ServicePointManager.SecurityProtocol |= SecurityProtocolType.Tls12;
            var request = (HttpWebRequest)WebRequest.Create(encryptedQr ? "https://interface.music.163.com/eapi/" + path.Substring(5) : "https://music.163.com" + path);
            request.Proxy = proxy ? WebRequest.DefaultWebProxy : null;
            request.Method = "POST"; request.Timeout = 15000; request.ReadWriteTimeout = 15000;
            request.UserAgent = "Mozilla/5.0"; request.Referer = "https://music.163.com/";
            request.ContentType = "application/x-www-form-urlencoded";
            request.CookieContainer = session ?? new CookieContainer();
            if (!string.IsNullOrWhiteSpace(cookie)) foreach (string part in cookie.Split(';')) {
                int equals = part.IndexOf('=');
                if (equals > 0) {
                    string name = part.Substring(0, equals).Trim();
                    if (name != "MUSIC_U" && name != "MUSIC_A" && name != "__csrf" && name != "_csrf") continue;
                    try { request.CookieContainer.Add(new Cookie(name, part.Substring(equals + 1).Trim(), "/", "music.163.com")); }
                    catch (CookieException) { throw new InvalidOperationException("登录凭证格式无效，请重新扫码登录。"); }
                }
            }
            var pairs = new List<string>();
            if (encryptedQr) pairs.Add("params=" + EncryptQr(path, payload));
            else foreach (var item in payload) pairs.Add(WebUtility.UrlEncode(item.Key) + "=" + WebUtility.UrlEncode(Convert.ToString(item.Value)));
            byte[] body = Encoding.UTF8.GetBytes(string.Join("&", pairs)); request.ContentLength = body.Length;
            using (var stream = request.GetRequestStream()) stream.Write(body, 0, body.Length);
            using (var response = (HttpWebResponse)request.GetResponse())
            using (var reader = new StreamReader(response.GetResponseStream(), Encoding.UTF8)) {
                var cookies = new List<string>();
                foreach (Cookie item in request.CookieContainer.GetCookies(response.ResponseUri)) cookies.Add(item.Name + "=" + item.Value);
                responseCookie = string.Join("; ", cookies);
                return new JavaScriptSerializer().Deserialize<Dictionary<string, object>>(reader.ReadToEnd());
            }
        }
        internal static Bitmap QrImage(string url, string python)
        {
            var info = new ProcessStartInfo(python) { Arguments = "\"" + Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "netease_auth.py") + "\"", UseShellExecute = false, CreateNoWindow = true, RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true };
            using (var process = Process.Start(info)) {
                process.StandardInput.Write(url); process.StandardInput.Close();
                string json = process.StandardOutput.ReadToEnd(); process.WaitForExit();
                if (process.ExitCode != 0) throw new InvalidOperationException("二维码绘制组件不可用，请检查安装文件。");
                var matrix = new JavaScriptSerializer().Deserialize<bool[][]>(json);
                int scale = Math.Max(1, 300 / matrix.Length);
                var image = new Bitmap(matrix.Length * scale, matrix.Length * scale);
                using (var g = Graphics.FromImage(image)) {
                    g.Clear(Color.White);
                    for (int y = 0; y < matrix.Length; y++) for (int x = 0; x < matrix.Length; x++) if (matrix[y][x]) g.FillRectangle(Brushes.Black, x * scale, y * scale, scale, scale);
                }
                return image;
            }
        }
    }
}
