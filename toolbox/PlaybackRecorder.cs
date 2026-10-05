using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Web.Script.Serialization;

namespace NeteaseToolbox
{
    // Syncs the account-side recent-play list, so plays from phones and other devices are included.
    internal sealed class PlaybackRecorder : IDisposable
    {
        private const int PollMilliseconds = 5 * 60 * 60 * 1000;
        private const int RetryMilliseconds = 60 * 1000;
        private const string BaseUrl = "https://music.163.com";
        private const string Nonce = "0CoJUm6Qyw8W8jud";
        private const string Iv = "0102030405060708";
        private const string Modulus = "00e0b509f6259df8642dbc35662901477df22677ec152b5ff68ace615bb7b725152b3ab17a876aea8a5aa76d2e417629ec4ee341f56135fccf695280104e0312ecbda92557c93870114af6c9d05c4f7f0c3685b7a46bee255932575cce10b424d813cfe4875d3e82047b97ddef52741d546b8e289dc6935b3ece0462db0a22b8e7";

        private readonly string historyPath;
        private readonly string logPath;
        private readonly Action<string> notify;
        private readonly JavaScriptSerializer json = new JavaScriptSerializer { MaxJsonLength = 16 * 1024 * 1024 };
        private readonly HashSet<string> knownRecords = new HashSet<string>(StringComparer.Ordinal);
        private readonly object fileLock = new object();
        private Timer timer;
        private int polling;
        private bool disposed;

        public PlaybackRecorder(string historyPath, Action<string> notify)
        {
            this.historyPath = historyPath;
            this.notify = notify;
            logPath = Path.Combine(Path.GetDirectoryName(historyPath), "playback-recorder.log");
        }

        public async Task StartAsync()
        {
            Directory.CreateDirectory(Path.GetDirectoryName(historyPath));
            LoadKnownRecords();
            timer = new Timer(PollTimer, null, Timeout.Infinite, Timeout.Infinite);
            await PollWithRecoveryAsync().ConfigureAwait(false);
        }

        private async void PollTimer(object state)
        {
            if (disposed || Interlocked.Exchange(ref polling, 1) != 0) return;
            try { await PollWithRecoveryAsync().ConfigureAwait(false); }
            finally { Interlocked.Exchange(ref polling, 0); }
        }

        private async Task PollWithRecoveryAsync()
        {
            int next = PollMilliseconds;
            Log("正在同步网易云账号最近播放记录；手机端和其他设备的播放会一并读取。", false);
            try { await PollAsync().ConfigureAwait(false); }
            catch (Exception ex)
            {
                next = ex is InvalidOperationException ? PollMilliseconds : RetryMilliseconds;
                Log("读取网易云账号播放记录失败：" + ex.Message + "；可重新扫码登录，或等待自动重试。", true);
            }
            finally
            {
                if (!disposed)
                {
                    try { timer.Change(next, Timeout.Infinite); }
                    catch (ObjectDisposedException) { }
                }
            }
        }

        private async Task PollAsync()
        {
            string cookie = await Task.Factory.StartNew<string>(() => LoadCookie()).ConfigureAwait(false);
            Dictionary<string, object> response = await Task.Factory.StartNew<Dictionary<string, object>>(() => RequestRecentRecords(cookie)).ConfigureAwait(false);
            object code;
            if (!response.TryGetValue("code", out code) || Convert.ToInt32(code, CultureInfo.InvariantCulture) != 200)
                throw new InvalidOperationException("网易云未接受当前登录状态，请点击工具箱左侧账号按钮重新扫码登录。");

            if (disposed) return;
            var data = GetObject(response, "data");
            var items = data == null ? null : data["list"] as ArrayList;
            if (items == null) throw new InvalidOperationException("网易云返回的最近播放记录格式不完整。");

            int added = 0;
            foreach (var value in items)
            {
                var record = value as Dictionary<string, object>;
                if (record == null) continue;
                var song = record.ContainsKey("data") ? record["data"] as Dictionary<string, object> : null;
                if (song == null) continue;
                string songId = Text(song, "id");
                long playTime;
                if (string.IsNullOrWhiteSpace(songId) || !Long(record, "playTime", out playTime) || playTime <= 0)
                    continue; // Never substitute the computer's observation time for a missing server time.
                string key = songId + ":" + playTime.ToString(CultureInfo.InvariantCulture);
                lock (fileLock)
                {
                    if (knownRecords.Contains(key)) continue;
                    AppendRecord(songId, playTime, song, record);
                    knownRecords.Add(key);
                }
                added++;
            }
            Log(string.Format(CultureInfo.InvariantCulture, "账号播放记录同步完成：检查最近 {0} 条，新增 {1} 条（含手机端）；文件：{2}", items.Count, added, historyPath), false);
        }

        private Dictionary<string, object> RequestRecentRecords(string cookie)
        {
            try { return RequestRecentRecords(cookie, false); }
            catch (WebException ex)
            {
                if (ex.Status != WebExceptionStatus.ConnectFailure &&
                    ex.Status != WebExceptionStatus.NameResolutionFailure &&
                    ex.Status != WebExceptionStatus.Timeout &&
                    ex.Status != WebExceptionStatus.SendFailure &&
                    ex.Status != WebExceptionStatus.ReceiveFailure &&
                    ex.Status != WebExceptionStatus.ConnectionClosed) throw;
                var proxy = WebRequest.DefaultWebProxy;
                var uri = new Uri(BaseUrl);
                if (proxy == null || proxy.IsBypassed(uri) || proxy.GetProxy(uri) == uri)
                    throw new InvalidOperationException("网易云直连失败（" + ex.Status + "），请检查网络连接。", ex);
                try { return RequestRecentRecords(cookie, true); }
                catch (WebException proxyError)
                {
                    throw new InvalidOperationException("网易云直连（" + ex.Status + "）和系统代理（" + proxyError.Status + "）均失败，请检查网络及代理是否运行。", proxyError);
                }
            }
        }

        private Dictionary<string, object> RequestRecentRecords(string cookie, bool useProxy)
        {
            string secret = RandomSecretKey();
            string payload = json.Serialize(new { limit = 100 });
            string paramsValue = EncryptAes(EncryptAes(payload, Nonce), secret);
            string encryptedKey = EncryptRsa(secret);
            string body = "params=" + WebUtility.UrlEncode(paramsValue) + "&encSecKey=" + encryptedKey;

            var request = (HttpWebRequest)WebRequest.Create(BaseUrl + "/api/play-record/song/list");
            request.Proxy = useProxy ? WebRequest.DefaultWebProxy : null;
            request.Method = "POST";
            request.Timeout = 25000;
            request.ReadWriteTimeout = 25000;
            request.ContentType = "application/x-www-form-urlencoded; charset=UTF-8";
            request.UserAgent = "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 Chrome/128.0 Safari/537.36";
            request.Referer = BaseUrl + "/";
            request.Headers[HttpRequestHeader.Cookie] = cookie;
            byte[] bytes = Encoding.UTF8.GetBytes(body);
            request.ContentLength = bytes.Length;
            using (var stream = request.GetRequestStream()) stream.Write(bytes, 0, bytes.Length);
            using (var result = request.GetResponse())
            using (var stream = result.GetResponseStream())
            using (var reader = new StreamReader(stream, Encoding.UTF8))
            {
                string responseText = reader.ReadToEnd();
                var parsed = json.Deserialize<Dictionary<string, object>>(responseText);
                if (parsed == null)
                    throw new InvalidOperationException(string.Format(CultureInfo.InvariantCulture,
                        "网易云最近播放接口返回空响应（HTTP {0}，{1} 字节，{2}）。", (int)((HttpWebResponse)result).StatusCode,
                        responseText.Length, ((HttpWebResponse)result).ResponseUri.AbsolutePath));
                return parsed;
            }
        }

        private static string LoadCookie()
        {
            return NeteaseAuth.Cookie();
        }

        private void LoadKnownRecords()
        {
            lock (fileLock)
            {
                if (!File.Exists(historyPath)) return;
                using (var reader = new StreamReader(historyPath, Encoding.UTF8, true))
                {
                    string line;
                    while ((line = reader.ReadLine()) != null)
                    {
                        var fields = line.Split(',');
                        long time;
                        if (fields.Length >= 2 && LongText(fields[1], out time) && time > 0)
                            knownRecords.Add(fields[0] + ":" + time.ToString(CultureInfo.InvariantCulture));
                    }
                }
            }
        }

        private void AppendRecord(string songId, long playTime, Dictionary<string, object> song, Dictionary<string, object> record)
        {
            bool writeHeader = !File.Exists(historyPath) || new FileInfo(historyPath).Length == 0;
            var playedUtc = DateTimeOffset.FromUnixTimeMilliseconds(playTime);
            object artistValue;
            var artists = song.TryGetValue("ar", out artistValue) ? artistValue as ArrayList : null;
            string artist = artists == null ? string.Empty : string.Join(" / ", artists.Cast<object>()
                .Select(item => item as Dictionary<string, object>).Where(item => item != null).Select(item => Text(item, "name")).Where(name => !string.IsNullOrWhiteSpace(name)));
            var album = GetObject(song, "al");
            var device = GetObject(record, "multiTerminalInfo");
            string os = Text(record, "os");
            if (string.IsNullOrWhiteSpace(os) && device != null) os = Text(device, "os");
            string deviceName = device == null ? string.Empty : FirstText(device, "osText", "name");
            string duration = Text(song, "dt");
            using (var writer = new StreamWriter(historyPath, true, new UTF8Encoding(true)))
            {
                if (writeHeader) writer.WriteLine("歌曲ID,播放时间Unix毫秒,播放时间UTC,播放时间本地,歌曲,艺人,专辑,歌曲时长毫秒,设备系统,设备名称");
                writer.WriteLine(string.Join(",", new[] {
                    songId,
                    playTime.ToString(CultureInfo.InvariantCulture),
                    Csv(playedUtc.ToString("yyyy-MM-dd HH:mm:ss.fff 'UTC'", CultureInfo.InvariantCulture)),
                    Csv(playedUtc.ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss.fff zzz", CultureInfo.InvariantCulture)),
                    Csv(Text(song, "name")), Csv(artist), Csv(album == null ? string.Empty : Text(album, "name")),
                    Csv(duration), Csv(os), Csv(deviceName)
                }));
            }
        }

        private static Dictionary<string, object> GetObject(Dictionary<string, object> parent, string key)
        {
            object value;
            return parent != null && parent.TryGetValue(key, out value) ? value as Dictionary<string, object> : null;
        }

        private static string Text(Dictionary<string, object> value, string key)
        {
            object result;
            return value != null && value.TryGetValue(key, out result) && result != null ? Convert.ToString(result, CultureInfo.InvariantCulture) : string.Empty;
        }

        private static string FirstText(Dictionary<string, object> value, params string[] keys)
        {
            foreach (string key in keys) { string text = Text(value, key); if (!string.IsNullOrWhiteSpace(text)) return text; }
            return string.Empty;
        }

        private static bool Long(Dictionary<string, object> value, string key, out long result)
        {
            object item;
            result = 0;
            return value != null && value.TryGetValue(key, out item) && long.TryParse(Convert.ToString(item, CultureInfo.InvariantCulture), NumberStyles.Integer, CultureInfo.InvariantCulture, out result);
        }

        private static bool LongText(string value, out long result)
        {
            return long.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out result);
        }

        private static string RandomSecretKey()
        {
            const string chars = "0123456789abcdef";
            var bytes = new byte[16];
            using (var rng = RandomNumberGenerator.Create()) rng.GetBytes(bytes);
            return new string(bytes.Select(value => chars[value & 15]).ToArray());
        }

        private static string EncryptAes(string value, string key)
        {
            using (var aes = Aes.Create())
            {
                aes.Mode = CipherMode.CBC;
                aes.Padding = PaddingMode.PKCS7;
                aes.Key = Encoding.UTF8.GetBytes(key);
                aes.IV = Encoding.UTF8.GetBytes(Iv);
                using (var transform = aes.CreateEncryptor())
                {
                    byte[] input = Encoding.UTF8.GetBytes(value);
                    return Convert.ToBase64String(transform.TransformFinalBlock(input, 0, input.Length));
                }
            }
        }

        private static string EncryptRsa(string secret)
        {
            using (var rsa = new RSACryptoServiceProvider(1024))
            {
                rsa.PersistKeyInCsp = false;
                rsa.ImportParameters(new RSAParameters { Modulus = HexBytes(Modulus), Exponent = new byte[] { 1, 0, 1 } });
                byte[] reversed = Encoding.UTF8.GetBytes(new string(secret.Reverse().ToArray()));
                byte[] encrypted = rsa.Encrypt(reversed, false);
                return BitConverter.ToString(encrypted).Replace("-", string.Empty).ToLowerInvariant();
            }
        }

        private static byte[] HexBytes(string value)
        {
            if ((value.Length & 1) != 0) throw new FormatException("Invalid RSA modulus.");
            var bytes = new byte[value.Length / 2];
            for (int i = 0; i < bytes.Length; i++) bytes[i] = byte.Parse(value.Substring(i * 2, 2), NumberStyles.HexNumber, CultureInfo.InvariantCulture);
            if (bytes.Length == 129 && bytes[0] == 0) return bytes.Skip(1).ToArray();
            return bytes;
        }

        private static string Csv(string value)
        {
            value = value ?? string.Empty;
            return "\"" + value.Replace("\"", "\"\"").Replace("\r", " ").Replace("\n", " ") + "\"";
        }

        private void Log(string message, bool isError)
        {
            try
            {
                lock (fileLock)
                    File.AppendAllText(logPath, DateTimeOffset.Now.ToString("o", CultureInfo.InvariantCulture) + "  " + message + Environment.NewLine, new UTF8Encoding(true));
            }
            catch { }
            if (notify != null) notify((isError ? "失败：" : "") + message);
        }

        public void Dispose()
        {
            if (disposed) return;
            disposed = true;
            if (timer != null) timer.Dispose();
        }
    }
}
