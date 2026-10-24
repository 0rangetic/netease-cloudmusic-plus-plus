using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Net;
using System.Numerics;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Web.Script.Serialization;

namespace NeteaseToolbox
{
    internal sealed class SigninFailure : InvalidOperationException { internal SigninFailure(string message) : base(message) { } }
    internal sealed class DailySigninSettings
    {
        public bool Enabled = true;
        public int Hour = 8;
        public int Minute = 30;
    }
    internal sealed class SigninEvent
    {
        public string Id = Guid.NewGuid().ToString("N");
        public string Time, Task, Phase, Detail, AccountId;
        public override string ToString() { return Time + "  " + Task + " · " + Phase + "：" + Detail; }
    }
    internal sealed class SigninDayState
    {
        public string Day = "";
        public bool YunbeiDone, VipDone;
        public string LastAttemptUtc = "";
    }
    internal sealed class SigninRunResult
    {
        public bool Skipped, YunbeiDone, VipDone;
        public string Phase, Detail;
    }
    internal static class DailySigninService
    {
        internal static string Root { get { return Path.Combine(Environment.GetEnvironmentVariable("LOCALAPPDATA") ?? Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "NeteaseToolbox"); } }
        internal static string SettingsPath { get { return Path.Combine(Root, "daily-signin-settings.json"); } }
        internal static readonly TimeSpan ChinaOffset = TimeSpan.FromHours(8);
        internal static DailySigninSettings LoadSettings()
        {
            var settings = File.Exists(SettingsPath) ? new JavaScriptSerializer().Deserialize<DailySigninSettings>(File.ReadAllText(SettingsPath, Encoding.UTF8)) : new DailySigninSettings();
            if (settings == null || settings.Hour < 0 || settings.Hour > 23 || settings.Minute < 0 || settings.Minute > 59) throw new SigninFailure("签到时间配置无效，请重新保存。");
            return settings;
        }
        internal static void SaveSettings(DailySigninSettings settings) { SaveJson(SettingsPath, settings); }
        internal static string AccountDirectory(string uid)
        {
            long parsed;
            if (!long.TryParse(uid, out parsed) || parsed <= 0) throw new SigninFailure("登录账号无效，请重新扫码登录。");
            return Path.Combine(Root, "DailySignin", uid);
        }
        internal static void SaveJson(string path, object value)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path));
            string temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
            try {
                File.WriteAllText(temporary, new JavaScriptSerializer().Serialize(value), new UTF8Encoding(false));
                if (File.Exists(path)) File.Replace(temporary, path, null); else File.Move(temporary, path);
            } finally { if (File.Exists(temporary)) File.Delete(temporary); }
        }
        internal static List<SigninEvent> ReadHistory(string folder)
        {
            string path = Path.Combine(folder, "history.json");
            return File.Exists(path) ? new JavaScriptSerializer().Deserialize<List<SigninEvent>>(File.ReadAllText(path, Encoding.UTF8)) ?? new List<SigninEvent>() : new List<SigninEvent>();
        }
        internal static bool IsDue(DailySigninSettings settings, SigninDayState state, DateTimeOffset now)
        {
            var china = now.ToOffset(ChinaOffset);
            if (!settings.Enabled || china.TimeOfDay < new TimeSpan(settings.Hour, settings.Minute, 0)) return false;
            if (state.Day != china.ToString("yyyy-MM-dd")) return true;
            if (state.YunbeiDone && state.VipDone) return false;
            DateTimeOffset last;
            return !DateTimeOffset.TryParse(state.LastAttemptUtc, out last) || now - last >= TimeSpan.FromMinutes(30);
        }
        internal static SigninDayState ReadState(string folder)
        {
            string path = Path.Combine(folder, "state.json");
            return File.Exists(path) ? new JavaScriptSerializer().Deserialize<SigninDayState>(File.ReadAllText(path, Encoding.UTF8)) ?? new SigninDayState() : new SigninDayState();
        }
        internal static SigninRunResult Run(bool automatic, Action<SigninEvent> notify)
        {
            var settings = LoadSettings();
            if (automatic && !settings.Enabled) return new SigninRunResult { Skipped = true, Phase = "已关闭", Detail = "每日自动签到已关闭。" };
            var session = NeteaseAuth.Load();
            string uid = NeteaseAuth.Text(session, "userId"), folder = AccountDirectory(uid);
            var api = new SigninApi(NeteaseAuth.Text(session, "cookie"));
            using (var mutex = new Mutex(false, @"Local\NeteaseToolboxDailySignin-" + uid)) {
                bool locked;
                try { locked = mutex.WaitOne(0); } catch (AbandonedMutexException) { locked = true; }
                if (!locked) return new SigninRunResult { Skipped = true, Phase = "运行中", Detail = "该账号的签到任务正在运行。" };
                try { return RunForAccount(uid, folder, settings, DateTimeOffset.Now, automatic, (path, payload, encrypted) => {
                    if (NeteaseAuth.Text(NeteaseAuth.Load(), "userId") != uid) throw new SigninFailure("登录账号已变化，本次任务停止；请用当前账号重新签到。");
                    return api.Send(path, payload, encrypted);
                }, notify); }
                finally { mutex.ReleaseMutex(); }
            }
        }
        internal static SigninRunResult RunForAccount(string uid, string folder, DailySigninSettings settings, DateTimeOffset now, bool automatic,
            Func<string, Dictionary<string, object>, bool, Dictionary<string, object>> send, Action<SigninEvent> notify)
        {
            var state = ReadState(folder);
            string day = now.ToOffset(ChinaOffset).ToString("yyyy-MM-dd");
            if (automatic && !IsDue(settings, state, now)) return new SigninRunResult { Skipped = true, Phase = "等待", Detail = "未到执行时间、正在等待重试，或今天已完成。" };
            if (state.Day != day) state = new SigninDayState { Day = day };
            var history = ReadHistory(folder);
            Action<string, string, string> log = (task, phase, detail) => {
                var item = new SigninEvent { Time = DateTimeOffset.Now.ToOffset(ChinaOffset).ToString("yyyy-MM-dd HH:mm:ss"), Task = task, Phase = phase, Detail = detail, AccountId = uid };
                history.Add(item);
                if (history.Count > 200) history.RemoveRange(0, history.Count - 200);
                SaveJson(Path.Combine(folder, "history.json"), history);
                if (notify != null) notify(item);
            };
            if (state.YunbeiDone && state.VipDone) {
                log("每日签到", "完成", "今天的云贝签到和黑胶乐签均已完成。");
                return new SigninRunResult { Skipped = true, YunbeiDone = true, VipDone = true, Phase = "完成", Detail = "今天两项签到均已完成。" };
            }
            state.LastAttemptUtc = now.ToUniversalTime().ToString("o");
            SaveJson(Path.Combine(folder, "state.json"), state);
            log("每日签到", "开始", automatic ? "自动执行云贝签到与黑胶乐签。" : "手动执行；已成功的项目会跳过。");
            try {
                var auth = send("/api/user/level", new Dictionary<string, object>(), false);
                Check(auth);
                if (NeteaseAuth.Text(NeteaseAuth.Object(auth, "data"), "userId") != uid) throw new SigninFailure("登录账号已变化，请重新登录后重试。");
            } catch (Exception ex) {
                string detail = SafeError(ex);
                log("每日签到", "失败", detail);
                return new SigninRunResult { Phase = "失败", Detail = detail, YunbeiDone = state.YunbeiDone, VipDone = state.VipDone };
            }
            if (!state.YunbeiDone) {
                log("云贝签到", "开始", "正在请求云贝签到。");
                try {
                    var response = send("/api/pointmall/user/sign", new Dictionary<string, object>(), false);
                    Check(response);
                    var data = NeteaseAuth.Object(response, "data");
                    object signValue;
                    // chaunsin/api/weapi/yunbei.go: sign=true means new sign-in; false means already signed.
                    // Missing/non-boolean data is not a success, even if code=200.
                    if (data == null || !data.TryGetValue("sign", out signValue) || !(signValue is bool)) throw new SigninFailure("服务器未确认云贝签到，请稍后重试。");
                    state.YunbeiDone = true;
                    SaveJson(Path.Combine(folder, "state.json"), state);
                    log("云贝签到", "完成", (bool)signValue ? "服务器已确认云贝签到。" : "服务器确认今天已签到，跳过重复签到。");
                } catch (Exception ex) { log("云贝签到", "失败", SafeError(ex)); }
            } else log("云贝签到", "完成", "今天已成功，跳过重复请求。");
            if (!state.VipDone) {
                log("黑胶乐签", "开始", "正在查询并补齐今天的乐签记录。");
                try {
                    var before = send("/api/vipnewcenter/app/user/sign/info", new Dictionary<string, object>(), false);
                    Check(before);
                    if (!HasTodayRecord(before, day)) {
                        var sign = send("/api/vip-center-bff/task/sign", new Dictionary<string, object>(), false);
                        Check(sign);
                        if (NeteaseAuth.Text(sign, "data") != "True") throw new SigninFailure("服务器未确认黑胶签到，请稍后重试。");
                        var detail = send("/api/vipnewcenter/app/level/user/checkin/history/detail", new Dictionary<string, object> {
                            { "signDayTime", (long)(now.ToUniversalTime() - new DateTimeOffset(1970, 1, 1, 0, 0, 0, TimeSpan.Zero)).TotalMilliseconds }, { "type", 1 } }, true);
                        Check(detail);
                        bool verified = false;
                        for (int attempt = 0; attempt < 3; attempt++) {
                            var after = send("/api/vipnewcenter/app/user/sign/info", new Dictionary<string, object>(), false);
                            Check(after);
                            if (HasTodayRecord(after, day)) { verified = true; break; }
                            if (attempt < 2) Thread.Sleep(1500);
                        }
                        if (!verified) throw new SigninFailure("请求已提交，但尚未查到有效的今日乐签记录；稍后会重试。");
                    }
                    state.VipDone = true;
                    SaveJson(Path.Combine(folder, "state.json"), state);
                    log("黑胶乐签", "完成", "已核实今天的乐签记录和签到时间。");
                } catch (Exception ex) { log("黑胶乐签", "失败", SafeError(ex)); }
            } else log("黑胶乐签", "完成", "今天已成功，跳过重复请求。");
            var result = new SigninRunResult { YunbeiDone = state.YunbeiDone, VipDone = state.VipDone,
                Phase = state.YunbeiDone && state.VipDone ? "完成" : state.YunbeiDone || state.VipDone ? "部分失败" : "失败" };
            result.Detail = "云贝签到：" + (state.YunbeiDone ? "完成" : "失败") + "；黑胶乐签：" + (state.VipDone ? "完成" : "失败") + (result.Phase == "完成" ? "。" : "。自动模式下 30 分钟后重试失败项，也可点击立即签到。");
            log("每日签到", result.Phase, result.Detail);
            return result;
        }
        internal static bool HasTodayRecord(Dictionary<string, object> response, string day)
        {
            object raw;
            if (!response.TryGetValue("data", out raw) || !(raw is System.Collections.IEnumerable)) return false;
            foreach (object value in (System.Collections.IEnumerable)raw) {
                var item = value as Dictionary<string, object>;
                long record, time;
                if (NeteaseAuth.Text(item, "timeStr") == day && NeteaseAuth.Text(item, "today") == "True"
                    && long.TryParse(NeteaseAuth.Text(item, "recordId"), out record) && record > 0
                    && long.TryParse(NeteaseAuth.Text(item, "time"), out time) && time > 0) return true;
            }
            return false;
        }
        private static void Check(Dictionary<string, object> response)
        {
            string code = NeteaseAuth.Text(response, "code");
            if (code == "200") return;
            if (code == "301" || code == "302") throw new SigninFailure("网易云登录已失效，请点击左侧账号重新扫码登录。");
            throw new SigninFailure("网易云接口返回错误（" + (code.Length == 0 ? "未知" : code) + "），请稍后重试。");
        }
        internal static string SafeError(Exception ex)
        {
            return ex is SigninFailure || (ex is InvalidOperationException && new[] { "请点击左侧“登录网易云”，用手机扫码登录。", "无法读取本机登录状态，请重新扫码登录。" }.Contains(ex.Message)) ? ex.Message
                : ex is WebException ? "网络请求失败，请检查网络；自动模式稍后会重试。" : "签到任务失败（" + ex.GetType().Name + "），请稍后重试。";
        }
    }
    internal sealed class SigninApi
    {
        private readonly string cookie;
        internal SigninApi(string cookie) { this.cookie = cookie; }
        internal Dictionary<string, object> Send(string path, Dictionary<string, object> payload, bool eapi)
        {
            try { return SendOnce(path, payload, eapi, false); }
            catch (WebException ex) {
                if (ex.Status == WebExceptionStatus.ProtocolError || !SystemProxyAvailable()) throw;
                try { return SendOnce(path, payload, eapi, true); }
                catch (WebException proxyError) {
                    if (proxyError.Status == WebExceptionStatus.ProtocolError) throw;
                    throw new SigninFailure("网易云直连（" + ex.Status + "）和系统代理（" + proxyError.Status + "）均不可用，请检查网络或代理设置。");
                }
            }
        }
        private static bool SystemProxyAvailable()
        {
            var proxy = WebRequest.DefaultWebProxy;
            var uri = new Uri("https://music.163.com/");
            return proxy != null && !proxy.IsBypassed(uri) && proxy.GetProxy(uri) != uri;
        }
        private Dictionary<string, object> SendOnce(string path, Dictionary<string, object> payload, bool eapi, bool useProxy)
        {
            ServicePointManager.SecurityProtocol |= SecurityProtocolType.Tls12;
            string host = eapi ? "interfacepc.music.163.com" : "music.163.com";
            var request = (HttpWebRequest)WebRequest.Create("https://" + host + (eapi ? "/eapi/" : "/weapi/") + path.Substring(5));
            request.Proxy = useProxy ? WebRequest.DefaultWebProxy : null;
            request.Method = "POST"; request.Timeout = 15000; request.ReadWriteTimeout = 15000;
            request.AllowAutoRedirect = false;
            request.UserAgent = "Mozilla/5.0 (Windows NT 10.0; Win64; x64) NeteaseMusic/3.1.41";
            request.Referer = "https://music.163.com/";
            request.ContentType = "application/x-www-form-urlencoded";
            var header = new Dictionary<string, object> { { "os", "pc" }, { "appver", "3.1.41" }, { "osver", "Microsoft-Windows-11" }, { "requestId", Guid.NewGuid().ToString("N") } };
            request.CookieContainer = new CookieContainer();
            string csrf = "";
            foreach (string part in cookie.Split(';')) {
                int equals = part.IndexOf('=');
                if (equals <= 0) continue;
                string name = part.Substring(0, equals).Trim(), value = part.Substring(equals + 1).Trim();
                if (!new[] { "MUSIC_U", "MUSIC_A", "__csrf", "_csrf", "NMTID", "deviceId" }.Contains(name)) continue;
                request.CookieContainer.Add(new Cookie(name, value, "/", host));
                header[name] = value;
                if (name == "__csrf") csrf = value;
            }
            var data = new Dictionary<string, object>(payload);
            data["csrf_token"] = csrf;
            string body;
            if (eapi) {
                data["header"] = header; data["e_r"] = false;
                body = "params=" + EncryptEapi(path, new JavaScriptSerializer().Serialize(data));
            } else {
                byte[] random = new byte[8]; using (var rng = RandomNumberGenerator.Create()) rng.GetBytes(random);
                string secret = BitConverter.ToString(random).Replace("-", "").ToLowerInvariant();
                body = EncryptWeapi(new JavaScriptSerializer().Serialize(data), secret);
            }
            byte[] bytes = Encoding.UTF8.GetBytes(body); request.ContentLength = bytes.Length;
            using (var stream = request.GetRequestStream()) stream.Write(bytes, 0, bytes.Length);
            using (var response = (HttpWebResponse)request.GetResponse())
            using (var reader = new StreamReader(response.GetResponseStream(), Encoding.UTF8))
                return new JavaScriptSerializer().Deserialize<Dictionary<string, object>>(reader.ReadToEnd());
        }
        private static byte[] AesBytes(byte[] input, string key, bool cbc)
        {
            using (var aes = Aes.Create()) {
                aes.Mode = cbc ? CipherMode.CBC : CipherMode.ECB; aes.Padding = PaddingMode.PKCS7; aes.Key = Encoding.UTF8.GetBytes(key);
                if (cbc) aes.IV = Encoding.UTF8.GetBytes("0102030405060708");
                using (var encryptor = aes.CreateEncryptor()) return encryptor.TransformFinalBlock(input, 0, input.Length);
            }
        }
        internal static string EncryptWeapi(string json, string secret)
        {
            string first = Convert.ToBase64String(AesBytes(Encoding.UTF8.GetBytes(json), "0CoJUm6Qyw8W8jud", true));
            string second = Convert.ToBase64String(AesBytes(Encoding.UTF8.GetBytes(first), secret, true));
            string hex = BitConverter.ToString(Encoding.UTF8.GetBytes(new string(secret.Reverse().ToArray()))).Replace("-", "");
            const string modulus = "00e0b509f6259df8642dbc35662901477df22677ec152b5ff68ace615bb7b725152b3ab17a876aea8a5aa76d2e417629ec4ee341f56135fccf695280104e0312ecbda92557c93870114af6c9d05c4f7f0c3685b7a46bee255932575cce10b424d813cfe4875d3e82047b97ddef52741d546b8e289dc6935b3ece0462db0a22b8e7";
            string rsa = BigInteger.ModPow(BigInteger.Parse("0" + hex, NumberStyles.AllowHexSpecifier), new BigInteger(65537), BigInteger.Parse(modulus, NumberStyles.AllowHexSpecifier)).ToString("x").TrimStart('0').PadLeft(256, '0');
            return "params=" + WebUtility.UrlEncode(second) + "&encSecKey=" + rsa;
        }
        internal static string EncryptEapi(string path, string json)
        {
            string digest;
            using (var md5 = MD5.Create()) digest = BitConverter.ToString(md5.ComputeHash(Encoding.UTF8.GetBytes("nobody" + path + "use" + json + "md5forencrypt"))).Replace("-", "").ToLowerInvariant();
            return BitConverter.ToString(AesBytes(Encoding.UTF8.GetBytes(path + "-36cd479b6b5-" + json + "-36cd479b6b5-" + digest), "e82ckenh8dichen8", false)).Replace("-", "");
        }
    }
}
