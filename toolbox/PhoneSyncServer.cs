using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Web.Script.Serialization;

namespace NeteaseToolbox
{
    // Receives NetEase replay events from the paired phone and archives them beside
    // the desktop local-playback database. It deliberately does not talk to NetEase.
    internal sealed class PhoneSyncServer : IDisposable
    {
        private const int DiscoveryPort = 47821;
        private const int FirstTcpPort = 47822;
        private const int LastTcpPort = 47832;
        private const int ProtocolVersion = 1;
        private const int MaxRequestBytes = 4 * 1024 * 1024;
        private const int PairCodeMinutes = 10;
        private const string Magic = "MUSICPLUS_SYNC_V1";
        private const string ServiceName = "musicplus-sync";
        private const string NeteasePackage = "com.netease.cloudmusic";

        private sealed class PairedDevice
        {
            internal string DeviceId;
            internal string Token;
            internal long Uid;
            internal string Name;
            internal DateTime PairedAtUtc;
            internal DateTime LastSyncUtc;
        }

        private sealed class PairFailure
        {
            internal int Count;
            internal DateTime ResetUtc;
        }

        private readonly string root;
        private readonly string statePath;
        private readonly string logPath;
        private readonly Func<long> currentUid;
        private readonly object stateLock = new object();
        private readonly object fileLock = new object();
        private readonly Dictionary<string, PairedDevice> devices = new Dictionary<string, PairedDevice>(StringComparer.Ordinal);
        private readonly HashSet<string> knownEventIds = new HashSet<string>(StringComparer.Ordinal);
        private readonly Dictionary<string, PairFailure> pairFailures = new Dictionary<string, PairFailure>(StringComparer.Ordinal);
        private readonly JavaScriptSerializer json = new JavaScriptSerializer { MaxJsonLength = MaxRequestBytes * 2 };

        private UdpClient discovery;
        private TcpListener listener;
        private Thread discoveryThread;
        private Thread acceptThread;
        private volatile bool running;
        private volatile bool disposed;
        private volatile int tcpPort;
        private volatile string lastError = "";
        private string pcId;
        private string pairingCode;
        private DateTime pairingCodeExpiresUtc;

        internal PhoneSyncServer(string root) : this(root, null) { }

        internal PhoneSyncServer(string root, Func<long> currentUid)
        {
            this.root = root;
            this.currentUid = currentUid;
            Directory.CreateDirectory(root);
            statePath = Path.Combine(root, "phone-sync-state.json");
            logPath = Path.Combine(root, "phone-sync.log");
            LoadState();
            LoadKnownEvents();
            RefreshPairingCode();
        }

        internal bool Running { get { return running; } }
        internal int Port { get { return tcpPort; } }
        internal int PairedCount { get { lock (stateLock) return devices.Count; } }
        internal string LastError { get { return lastError; } }
        internal string PairingCode { get { return EnsurePairingCode(); } }
        internal DateTime PairingCodeExpiresUtc { get { lock (stateLock) return pairingCodeExpiresUtc; } }

        internal string EnsurePairingCode()
        {
            lock (stateLock)
            {
                if (string.IsNullOrEmpty(pairingCode) || DateTime.UtcNow >= pairingCodeExpiresUtc)
                    RefreshPairingCodeLocked();
                return pairingCode;
            }
        }

        internal void RefreshPairingCode()
        {
            lock (stateLock) RefreshPairingCodeLocked();
            Log("手机配对码已刷新");
        }

        internal void Start()
        {
            if (running) return;
            disposed = false;
            lastError = "";
            try
            {
                StartTcp();
                StartDiscovery();
                running = true;
                Log("手机同步接收端已启动：UDP " + DiscoveryPort.ToString(CultureInfo.InvariantCulture)
                    + "，TCP " + tcpPort.ToString(CultureInfo.InvariantCulture));
            }
            catch
            {
                running = false;
                CloseListeners();
                throw;
            }
        }

        internal string StatusText()
        {
            var builder = new StringBuilder();
            if (disposed)
            {
                builder.AppendLine("接收端已停止");
                return builder.ToString();
            }
            if (!running)
            {
                builder.AppendLine("接收端未启动");
                if (!string.IsNullOrWhiteSpace(lastError)) builder.AppendLine("错误：" + lastError);
                return builder.ToString();
            }
            builder.AppendLine("接收端已启动：UDP " + DiscoveryPort.ToString(CultureInfo.InvariantCulture)
                + " · TCP " + tcpPort.ToString(CultureInfo.InvariantCulture));
            long uid = ReadCurrentUid();
            builder.AppendLine(uid > 0
                ? "电脑账号：UID " + uid.ToString(CultureInfo.InvariantCulture)
                : "电脑账号：未登录网易云（登录后才能配对）");
            lock (stateLock)
            {
                if (devices.Count == 0) builder.AppendLine("已配对手机：无");
                else
                {
                    builder.AppendLine("已配对手机：");
                    foreach (PairedDevice device in devices.Values)
                    {
                        string name = string.IsNullOrWhiteSpace(device.Name) ? device.DeviceId : device.Name;
                        string last = device.LastSyncUtc == DateTime.MinValue
                            ? "从未同步"
                            : "上次同步 " + device.LastSyncUtc.ToLocalTime().ToString("MM-dd HH:mm", CultureInfo.InvariantCulture);
                        builder.AppendLine("  " + name + " · UID " + device.Uid.ToString(CultureInfo.InvariantCulture) + " · " + last);
                    }
                }
            }
            if (!string.IsNullOrWhiteSpace(lastError)) builder.AppendLine("最近错误：" + lastError);
            return builder.ToString();
        }

        internal void ClearPairings()
        {
            lock (stateLock)
            {
                devices.Clear();
                SaveStateLocked();
            }
            Log("已解除全部手机配对");
        }

        public void Dispose()
        {
            if (disposed) return;
            disposed = true;
            running = false;
            CloseListeners();
            Log("手机同步接收端已停止");
        }

        private void StartTcp()
        {
            Exception last = null;
            for (int port = FirstTcpPort; port <= LastTcpPort; port++)
            {
                try
                {
                    var probe = new TcpListener(IPAddress.Any, port);
                    probe.Start();
                    listener = probe;
                    tcpPort = port;
                    acceptThread = new Thread(AcceptLoop) { IsBackground = true, Name = "PhoneSyncTcp" };
                    acceptThread.Start();
                    return;
                }
                catch (SocketException ex) { last = ex; }
            }
            throw new InvalidOperationException("无法监听手机同步端口 "
                + FirstTcpPort.ToString(CultureInfo.InvariantCulture) + "-"
                + LastTcpPort.ToString(CultureInfo.InvariantCulture) + "："
                + (last == null ? "端口不可用" : last.Message));
        }

        private void StartDiscovery()
        {
            var socket = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);
            socket.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.ReuseAddress, true);
            socket.Bind(new IPEndPoint(IPAddress.Any, DiscoveryPort));
            discovery = new UdpClient { Client = socket };
            discoveryThread = new Thread(DiscoveryLoop) { IsBackground = true, Name = "PhoneSyncDiscovery" };
            discoveryThread.Start();
        }

        private void CloseListeners()
        {
            try { if (discovery != null) discovery.Close(); } catch { }
            try { if (listener != null) listener.Stop(); } catch { }
            discovery = null;
            listener = null;
        }

        private void DiscoveryLoop()
        {
            var remote = new IPEndPoint(IPAddress.Any, 0);
            while (!disposed)
            {
                try
                {
                    byte[] data = discovery.Receive(ref remote);
                    if (!running || data == null || data.Length == 0) continue;
                    if (Encoding.UTF8.GetString(data).Trim() != Magic) continue;
                    if (!IsPrivate(remote.Address)) continue;
                    var reply = new Dictionary<string, object>
                    {
                        { "service", ServiceName },
                        { "version", ProtocolVersion },
                        { "name", Environment.MachineName },
                        { "pcId", pcId },
                        { "port", tcpPort },
                        { "paired", PairedCount > 0 }
                    };
                    byte[] payload = Encoding.UTF8.GetBytes(json.Serialize(reply) + "\n");
                    discovery.Send(payload, payload.Length, remote);
                }
                catch (ObjectDisposedException) { break; }
                catch (SocketException ex)
                {
                    if (disposed) break;
                    SetError("发现服务错误：" + ex.Message);
                    Thread.Sleep(250);
                }
                catch (Exception ex)
                {
                    if (disposed) break;
                    SetError("发现服务异常：" + ex.Message);
                    Thread.Sleep(250);
                }
            }
        }

        private void AcceptLoop()
        {
            while (!disposed)
            {
                try
                {
                    TcpClient client = listener.AcceptTcpClient();
                    ThreadPool.QueueUserWorkItem(delegate(object state) { HandleClient((TcpClient)state); }, client);
                }
                catch (ObjectDisposedException) { break; }
                catch (SocketException ex)
                {
                    if (disposed) break;
                    SetError("手机连接监听错误：" + ex.Message);
                    Thread.Sleep(250);
                }
                catch (Exception ex)
                {
                    if (disposed) break;
                    SetError("手机连接监听异常：" + ex.Message);
                    Thread.Sleep(250);
                }
            }
        }

        private void HandleClient(TcpClient client)
        {
            using (client)
            {
                try
                {
                    client.ReceiveTimeout = 15000;
                    client.SendTimeout = 15000;
                    NetworkStream stream = client.GetStream();
                    string line = ReadLine(stream);
                    if (string.IsNullOrWhiteSpace(line)) return;
                    Dictionary<string, object> request = null;
                    try { request = json.Deserialize<Dictionary<string, object>>(line); }
                    catch { WriteResponse(stream, Error("bad_json", "请求格式无效")); return; }
                    if (request == null) { WriteResponse(stream, Error("bad_json", "请求为空")); return; }
                    string operation = Text(request, "op");
                    IPEndPoint remote = client.Client.RemoteEndPoint as IPEndPoint;
                    Dictionary<string, object> response;
                    if (operation == "pair") response = HandlePair(request, remote);
                    else if (operation == "sync") response = HandleSync(request);
                    else response = Error("bad_op", "未知操作");
                    WriteResponse(stream, response);
                }
                catch (ObjectDisposedException) { }
                catch (IOException ex) { Log("手机连接中断：" + ex.Message); }
                catch (Exception ex)
                {
                    SetError("处理手机请求失败：" + ex.Message);
                    try
                    {
                        NetworkStream stream = client.GetStream();
                        WriteResponse(stream, Error("server", "电脑端处理失败"));
                    }
                    catch { }
                }
            }
        }

        private Dictionary<string, object> HandlePair(Dictionary<string, object> request, IPEndPoint remote)
        {
            string code = Text(request, "code");
            long phoneUid = Long(request, "uid");
            string deviceId = CleanId(Text(request, "deviceId"));
            string deviceName = Trim(Text(request, "deviceName"), 120);
            long currentUid = ReadCurrentUid();
            if (currentUid <= 0)
            {
                SetError("手机配对失败：电脑端尚未登录网易云");
                return Error("account_required", "电脑端尚未登录网易云，请先在工具箱登录");
            }
            if (phoneUid <= 0 || phoneUid != currentUid)
            {
                SetError("手机配对失败：手机与电脑的网易云账号不一致");
                return Error("account_mismatch", "手机和电脑登录的网易云账号不一致（手机 UID "
                    + phoneUid.ToString(CultureInfo.InvariantCulture) + "，电脑 UID "
                    + currentUid.ToString(CultureInfo.InvariantCulture) + "）");
            }
            if (deviceId.Length < 8 || deviceId.Length > 128)
            {
                SetError("手机配对失败：设备标识无效");
                return Error("bad_device", "手机设备标识无效");
            }
            if (!PairAllowed(remote))
            {
                SetError("手机配对失败：尝试次数过多");
                return Error("pair_rate", "配对尝试次数过多，请稍后再试");
            }
            if (!CheckPairingCode(code))
            {
                RecordPairFailure(remote);
                SetError("手机配对失败：配对码错误或已过期");
                return Error("pair_code", "配对码错误或已过期，请在电脑端刷新配对码");
            }
            string token = RandomToken();
            lock (stateLock)
            {
                devices[deviceId] = new PairedDevice
                {
                    DeviceId = deviceId,
                    Token = token,
                    Uid = phoneUid,
                    Name = deviceName,
                    PairedAtUtc = DateTime.UtcNow,
                    LastSyncUtc = DateTime.MinValue
                };
                pairingCode = null;
                pairingCodeExpiresUtc = DateTime.MinValue;
                SaveStateLocked();
            }
            ClearPairFailures(remote);
            lastError = "";
            Log("手机配对成功：" + (string.IsNullOrWhiteSpace(deviceName) ? deviceId : deviceName)
                + "，UID " + phoneUid.ToString(CultureInfo.InvariantCulture));
            return new Dictionary<string, object>
            {
                { "ok", true },
                { "token", token },
                { "pcName", Environment.MachineName },
                { "pcId", pcId },
                { "uid", phoneUid }
            };
        }

        private Dictionary<string, object> HandleSync(Dictionary<string, object> request)
        {
            string deviceId = CleanId(Text(request, "deviceId"));
            string token = Text(request, "token");
            long phoneUid = Long(request, "uid");
            PairedDevice device;
            lock (stateLock)
            {
                if (string.IsNullOrEmpty(deviceId) || !devices.TryGetValue(deviceId, out device)
                    || !string.Equals(device.Token, token, StringComparison.Ordinal))
                    return Error("token_invalid", "配对凭证无效，请重新配对");
            }
            long currentUid = ReadCurrentUid();
            if (currentUid <= 0)
            {
                SetError("手机同步失败：电脑端尚未登录网易云");
                return Error("account_required", "电脑端尚未登录网易云，请先在工具箱登录");
            }
            if (phoneUid != device.Uid || phoneUid != currentUid)
            {
                SetError("手机同步失败：手机与电脑的网易云账号不一致");
                return Error("account_mismatch", "手机和电脑登录的网易云账号不一致（手机 UID "
                    + phoneUid.ToString(CultureInfo.InvariantCulture) + "，电脑 UID "
                    + currentUid.ToString(CultureInfo.InvariantCulture) + "）");
            }
            ArrayList events = request.ContainsKey("events") ? request["events"] as ArrayList : null;
            if (events == null)
            {
                SetError("手机同步失败：请求缺少播放事件");
                return Error("bad_events", "请求缺少播放事件");
            }
            if (events.Count > 1000)
            {
                SetError("手机同步失败：单次事件过多");
                return Error("bad_events", "单次事件过多");
            }

            int accepted = 0, duplicates = 0, skipped = 0;
            var lines = new List<string>();
            var newIds = new List<string>();
            lock (fileLock)
            {
                var batchIds = new HashSet<string>(knownEventIds, StringComparer.Ordinal);
                foreach (object value in events)
                {
                    var item = value as Dictionary<string, object>;
                    if (item == null) { skipped++; continue; }
                    if (Text(item, "packageName") != NeteasePackage || Text(item, "trigger") != "repeat")
                    {
                        skipped++;
                        continue;
                    }
                    string eventId = CleanId(Text(item, "eventId"));
                    if (string.IsNullOrEmpty(eventId)) { skipped++; continue; }
                    if (batchIds.Contains(eventId)) { duplicates++; continue; }
                    long startedAt = Long(item, "startedAt");
                    long endedAt = Long(item, "endedAt");
                    long listenedMs = Long(item, "listenedMs");
                    long durationMs = Long(item, "durationMs");
                    if (startedAt <= 0 || endedAt < startedAt || listenedMs < 0 || durationMs < 0)
                    {
                        skipped++;
                        continue;
                    }
                    string title = Trim(Text(item, "title"), 512);
                    if (string.IsNullOrWhiteSpace(title)) { skipped++; continue; }
                    string mediaId = Trim(Text(item, "mediaId"), 256);
                    string artist = Trim(Text(item, "artist"), 512);
                    string song = NormalizeSong(Trim(Text(item, "song"), 512), mediaId, title, artist);
                    var row = new Dictionary<string, object>
                    {
                        { "v", 1 },
                        { "eventId", eventId },
                        { "deviceId", deviceId },
                        { "deviceName", string.IsNullOrWhiteSpace(device.Name) ? Trim(Text(item, "deviceName"), 120) : device.Name },
                        { "uid", device.Uid },
                        { "song", song },
                        { "mediaId", mediaId },
                        { "packageName", NeteasePackage },
                        { "startedAt", startedAt },
                        { "endedAt", endedAt },
                        { "listenedMs", listenedMs },
                        { "durationMs", durationMs },
                        { "title", title },
                        { "artist", artist },
                        { "album", Trim(Text(item, "album"), 512) },
                        { "trigger", "repeat" },
                        { "completed", Bool(item, "completed") },
                        { "receivedAt", NowMs() }
                    };
                    lines.Add(json.Serialize(row));
                    newIds.Add(eventId);
                    batchIds.Add(eventId);
                    accepted++;
                }
                if (accepted > 0)
                {
                    string directory = Path.Combine(root, device.Uid.ToString(CultureInfo.InvariantCulture));
                    Directory.CreateDirectory(directory);
                    string file = Path.Combine(directory, "phone-repeats.jsonl");
                    var builder = new StringBuilder();
                    foreach (string line in lines) builder.AppendLine(line);
                    File.AppendAllText(file, builder.ToString(), new UTF8Encoding(false));
                    foreach (string id in newIds) knownEventIds.Add(id);
                }
            }
            lock (stateLock)
            {
                device.LastSyncUtc = DateTime.UtcNow;
                SaveStateLocked();
            }
            lastError = "";
            Log("接收手机重播：新增 " + accepted.ToString(CultureInfo.InvariantCulture)
                + "，重复 " + duplicates.ToString(CultureInfo.InvariantCulture)
                + "，跳过 " + skipped.ToString(CultureInfo.InvariantCulture));
            return new Dictionary<string, object>
            {
                { "ok", true },
                { "accepted", accepted },
                { "duplicates", duplicates },
                { "skipped", skipped },
                { "serverTime", NowMs() }
            };
        }

        private static string ReadLine(NetworkStream stream)
        {
            var buffer = new MemoryStream();
            var chunk = new byte[4096];
            while (true)
            {
                int read = stream.Read(chunk, 0, chunk.Length);
                if (read <= 0) break;
                for (int i = 0; i < read; i++)
                {
                    byte value = chunk[i];
                    if (value == (byte)'\n') return Encoding.UTF8.GetString(buffer.ToArray());
                    if (value == (byte)'\r') continue;
                    buffer.WriteByte(value);
                    if (buffer.Length > MaxRequestBytes) throw new InvalidOperationException("请求体过大");
                }
            }
            return buffer.Length == 0 ? null : Encoding.UTF8.GetString(buffer.ToArray());
        }

        private void WriteResponse(NetworkStream stream, Dictionary<string, object> response)
        {
            byte[] payload = Encoding.UTF8.GetBytes(json.Serialize(response) + "\n");
            stream.Write(payload, 0, payload.Length);
            stream.Flush();
        }

        private static Dictionary<string, object> Error(string code, string message)
        {
            return new Dictionary<string, object>
            {
                { "ok", false },
                { "code", code },
                { "error", message }
            };
        }

        private bool CheckPairingCode(string code)
        {
            lock (stateLock)
            {
                if (string.IsNullOrEmpty(pairingCode) || DateTime.UtcNow >= pairingCodeExpiresUtc)
                {
                    RefreshPairingCodeLocked();
                    return false;
                }
                return string.Equals(pairingCode, (code ?? "").Trim(), StringComparison.Ordinal);
            }
        }

        private void RefreshPairingCodeLocked()
        {
            var bytes = new byte[4];
            using (var random = RandomNumberGenerator.Create()) random.GetBytes(bytes);
            int value = BitConverter.ToInt32(bytes, 0) & 0x7fffffff;
            pairingCode = (100000 + value % 900000).ToString("D6", CultureInfo.InvariantCulture);
            pairingCodeExpiresUtc = DateTime.UtcNow.AddMinutes(PairCodeMinutes);
        }

        private static string RandomToken()
        {
            var bytes = new byte[32];
            using (var random = RandomNumberGenerator.Create()) random.GetBytes(bytes);
            return BitConverter.ToString(bytes).Replace("-", "").ToLowerInvariant();
        }

        private bool PairAllowed(IPEndPoint remote)
        {
            string key = remote == null ? "?" : remote.Address.ToString();
            DateTime now = DateTime.UtcNow;
            lock (pairFailures)
            {
                PairFailure failure;
                if (!pairFailures.TryGetValue(key, out failure) || failure.ResetUtc <= now)
                {
                    pairFailures[key] = new PairFailure { Count = 0, ResetUtc = now.AddMinutes(PairCodeMinutes) };
                    return true;
                }
                return failure.Count < 5;
            }
        }

        private void RecordPairFailure(IPEndPoint remote)
        {
            string key = remote == null ? "?" : remote.Address.ToString();
            DateTime now = DateTime.UtcNow;
            lock (pairFailures)
            {
                PairFailure failure;
                if (!pairFailures.TryGetValue(key, out failure) || failure.ResetUtc <= now)
                    pairFailures[key] = new PairFailure { Count = 1, ResetUtc = now.AddMinutes(PairCodeMinutes) };
                else failure.Count++;
            }
        }

        private void ClearPairFailures(IPEndPoint remote)
        {
            lock (pairFailures) pairFailures.Remove(remote == null ? "?" : remote.Address.ToString());
        }

        private long ReadCurrentUid()
        {
            return currentUid != null ? currentUid() : CurrentUid();
        }

        private static long CurrentUid()
        {
            try
            {
                long uid;
                return long.TryParse(NeteaseAuth.Text(NeteaseAuth.Load(), "userId"), NumberStyles.Integer,
                    CultureInfo.InvariantCulture, out uid) ? uid : 0;
            }
            catch { return 0; }
        }

        private static bool IsPrivate(IPAddress address)
        {
            if (address == null) return false;
            if (IPAddress.IsLoopback(address)) return true;
            if (address.AddressFamily == AddressFamily.InterNetworkV6)
            {
                if (address.IsIPv6LinkLocal) return true;
                byte[] v6 = address.GetAddressBytes();
                return (v6[0] & 0xfe) == 0xfc;
            }
            byte[] bytes = address.GetAddressBytes();
            if (bytes.Length != 4) return false;
            return bytes[0] == 10
                || (bytes[0] == 172 && bytes[1] >= 16 && bytes[1] <= 31)
                || (bytes[0] == 192 && bytes[1] == 168)
                || (bytes[0] == 169 && bytes[1] == 254);
        }

        private void LoadState()
        {
            pcId = Guid.NewGuid().ToString("N");
            if (!File.Exists(statePath))
            {
                lock (stateLock) SaveStateLocked();
                return;
            }
            try
            {
                var state = json.Deserialize<Dictionary<string, object>>(File.ReadAllText(statePath, Encoding.UTF8));
                if (state == null) return;
                string savedId = Text(state, "PcId");
                if (!string.IsNullOrWhiteSpace(savedId)) pcId = savedId;
                var list = state.ContainsKey("Devices") ? state["Devices"] as ArrayList : null;
                if (list == null) return;
                foreach (object value in list)
                {
                    var item = value as Dictionary<string, object>;
                    if (item == null) continue;
                    string id = CleanId(Text(item, "DeviceId"));
                    string token = Text(item, "Token");
                    long uid = Long(item, "Uid");
                    if (string.IsNullOrEmpty(id) || string.IsNullOrEmpty(token) || uid <= 0) continue;
                    devices[id] = new PairedDevice
                    {
                        DeviceId = id,
                        Token = token,
                        Uid = uid,
                        Name = Trim(Text(item, "Name"), 120),
                        PairedAtUtc = ParseUtc(Text(item, "PairedAtUtc")),
                        LastSyncUtc = ParseUtc(Text(item, "LastSyncUtc"))
                    };
                }
            }
            catch (Exception ex)
            {
                SetError("读取手机配对状态失败：" + ex.Message);
            }
        }

        private void SaveStateLocked()
        {
            try
            {
                var list = new ArrayList();
                foreach (PairedDevice device in devices.Values)
                {
                    list.Add(new Dictionary<string, object>
                    {
                        { "DeviceId", device.DeviceId },
                        { "Token", device.Token },
                        { "Uid", device.Uid },
                        { "Name", device.Name },
                        { "PairedAtUtc", device.PairedAtUtc.ToString("o", CultureInfo.InvariantCulture) },
                        { "LastSyncUtc", device.LastSyncUtc == DateTime.MinValue ? "" : device.LastSyncUtc.ToString("o", CultureInfo.InvariantCulture) }
                    });
                }
                var state = new Dictionary<string, object>
                {
                    { "Version", 1 },
                    { "PcId", pcId },
                    { "Devices", list }
                };
                string temporary = statePath + ".tmp";
                File.WriteAllText(temporary, json.Serialize(state), new UTF8Encoding(false));
                try
                {
                    if (File.Exists(statePath)) File.Replace(temporary, statePath, null);
                    else File.Move(temporary, statePath);
                }
                catch
                {
                    File.Copy(temporary, statePath, true);
                    try { File.Delete(temporary); } catch { }
                }
            }
            catch (Exception ex)
            {
                SetError("保存手机配对状态失败：" + ex.Message);
            }
        }

        private void LoadKnownEvents()
        {
            if (!Directory.Exists(root)) return;
            foreach (string directory in Directory.GetDirectories(root))
            {
                string file = Path.Combine(directory, "phone-repeats.jsonl");
                if (!File.Exists(file)) continue;
                try
                {
                    foreach (string line in File.ReadLines(file, Encoding.UTF8))
                    {
                        if (string.IsNullOrWhiteSpace(line)) continue;
                        try
                        {
                            var item = json.Deserialize<Dictionary<string, object>>(line);
                            string id = Text(item, "eventId");
                            if (!string.IsNullOrWhiteSpace(id)) knownEventIds.Add(id);
                        }
                        catch { }
                    }
                }
                catch (Exception ex)
                {
                    SetError("读取手机已接收记录失败：" + ex.Message);
                }
            }
        }

        private static DateTime ParseUtc(string value)
        {
            DateTime parsed;
            return DateTime.TryParse(value, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out parsed)
                ? parsed.ToUniversalTime()
                : DateTime.MinValue;
        }

        private static string Text(Dictionary<string, object> data, string key)
        {
            object value;
            return data != null && data.TryGetValue(key, out value) && value != null
                ? Convert.ToString(value, CultureInfo.InvariantCulture)
                : "";
        }

        private static long Long(Dictionary<string, object> data, string key)
        {
            object value;
            if (data == null || !data.TryGetValue(key, out value) || value == null) return 0;
            if (value is long) return (long)value;
            if (value is int) return (int)value;
            if (value is decimal) return (long)(decimal)value;
            long parsed;
            return long.TryParse(Convert.ToString(value, CultureInfo.InvariantCulture), NumberStyles.Integer,
                CultureInfo.InvariantCulture, out parsed) ? parsed : 0;
        }

        private static bool Bool(Dictionary<string, object> data, string key)
        {
            object value;
            if (data == null || !data.TryGetValue(key, out value) || value == null) return false;
            if (value is bool) return (bool)value;
            bool parsed;
            return bool.TryParse(Convert.ToString(value, CultureInfo.InvariantCulture), out parsed) && parsed;
        }

        private static string Trim(string value, int maximum)
        {
            if (value == null) return "";
            value = value.Trim();
            return value.Length <= maximum ? value : value.Substring(0, maximum);
        }

        private static string NormalizeSong(string song, string mediaId, string title, string artist)
        {
            string[] candidates = new string[]
            {
                song,
                mediaId,
                !string.IsNullOrEmpty(song) && song.StartsWith("media:", StringComparison.Ordinal)
                    ? song.Substring(6)
                    : ""
            };
            foreach (string candidate in candidates)
            {
                if (string.IsNullOrWhiteSpace(candidate)) continue;
                string value = candidate.Trim();
                if (Digits(value)) return value;
                if (value.StartsWith("other_id__", StringComparison.Ordinal))
                {
                    string suffix = value.Substring(10);
                    if (Digits(suffix)) return suffix;
                }
            }
            if (!string.IsNullOrWhiteSpace(song)) return song;
            if (!string.IsNullOrWhiteSpace(mediaId)) return "media:" + mediaId;
            return "text:" + title + "|" + artist;
        }

        private static bool Digits(string value)
        {
            if (string.IsNullOrEmpty(value)) return false;
            foreach (char character in value)
                if (character < '0' || character > '9') return false;
            return true;
        }

        private static string CleanId(string value)
        {
            if (string.IsNullOrWhiteSpace(value)) return "";
            var builder = new StringBuilder(value.Length);
            foreach (char character in value.Trim())
            {
                if (char.IsControl(character)) continue;
                builder.Append(character);
                if (builder.Length >= 128) break;
            }
            return builder.ToString();
        }

        private static long NowMs()
        {
            return (long)(DateTime.UtcNow - new DateTime(1970, 1, 1, 0, 0, 0, DateTimeKind.Utc)).TotalMilliseconds;
        }

        private void SetError(string message)
        {
            lastError = message;
            Log(message);
        }

        private void Log(string message)
        {
            try
            {
                lock (fileLock)
                    File.AppendAllText(logPath,
                        DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture) + "  " + message + Environment.NewLine,
                        new UTF8Encoding(false));
            }
            catch { }
        }
    }
}
