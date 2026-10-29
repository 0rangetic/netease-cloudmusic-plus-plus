using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Web.Script.Serialization;

namespace NeteaseToolbox
{
    internal static class NeteaseAuth
    {
        internal static Dictionary<string, object> Load()
        {
            throw new InvalidOperationException("Validation uses the injected UID provider.");
        }

        internal static string Text(Dictionary<string, object> data, string key)
        {
            object value;
            return data != null && data.TryGetValue(key, out value) && value != null
                ? Convert.ToString(value)
                : "";
        }
    }

    internal static class PhoneSyncValidation
    {
        private const long Uid = 424242L;

        private static void Check(bool condition, string message)
        {
            if (!condition) throw new InvalidOperationException(message);
        }

        private static void Main()
        {
            string root = Path.Combine(Path.GetTempPath(), "phone-sync-validation-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(root);
            var server = new PhoneSyncServer(root, delegate { return Uid; });
            try
            {
                server.Start();
                Check(server.Running, "server is running");
                Check(server.Port >= 47822, "TCP port allocated");
                string code = server.EnsurePairingCode();
                Check(code.Length == 6, "pairing code has six digits");

                var discovery = Discover();
                Check(Convert.ToString(discovery["service"]) == "musicplus-sync", "discovery service");
                Check(!Convert.ToBoolean(discovery["paired"]), "discovery starts unpaired");
                int port = Convert.ToInt32(discovery["port"]);

                var wrong = Pair(port, "000000", Uid, "phone-validation-1", "Validation Phone");
                Check(!Convert.ToBoolean(wrong["ok"]) && Convert.ToString(wrong["code"]) == "pair_code", "wrong code rejected");

                var mismatch = Pair(port, code, 999L, "phone-validation-2", "Other Account");
                Check(!Convert.ToBoolean(mismatch["ok"]) && Convert.ToString(mismatch["code"]) == "account_mismatch", "account mismatch rejected");

                var paired = Pair(port, code, Uid, "phone-validation-1", "Validation Phone");
                Check(Convert.ToBoolean(paired["ok"]), "pair succeeds");
                string token = Convert.ToString(paired["token"]);
                Check(token.Length == 64, "token has 64 characters");
                Check(Convert.ToString(paired["pcId"]) == Convert.ToString(discovery["pcId"]), "pair returns stable PC id");

                var event1 = Event("evt-1", "100", "com.netease.cloudmusic", 1000000L);
                var event2 = Event("evt-2", "", "com.netease.cloudmusic", 1200000L);
                event2["mediaId"] = "other_id__100";
                var other = Event("evt-3", "100", "com.tencent.qqmusic", 1400000L);
                var first = Sync(port, token, Uid, "phone-validation-1", new[] { event1, event2, other, event1 });
                Check(Convert.ToBoolean(first["ok"]), "sync succeeds");
                Check(Convert.ToInt32(first["accepted"]) == 2, "two NetEase events accepted");
                Check(Convert.ToInt32(first["duplicates"]) == 1, "same-request duplicate detected");
                Check(Convert.ToInt32(first["skipped"]) == 1, "other media skipped");

                string file = Path.Combine(root, Uid.ToString(), "phone-repeats.jsonl");
                Check(File.Exists(file), "archive file exists");
                string[] lines = File.ReadAllLines(file, Encoding.UTF8);
                Check(lines.Length == 2, "two archive lines");
                Check(lines[0].Contains("\"evt-1\"") && lines[1].Contains("\"evt-2\""), "event ids archived in order");
                Check(File.ReadAllText(file).Contains("\"song\":\"100\""), "other_id media id normalized to a numeric song id");
                Check(!File.ReadAllText(file).Contains("com.tencent.qqmusic"), "other media is not archived");

                var again = Sync(port, token, Uid, "phone-validation-1", new[] { event1, event2 });
                Check(Convert.ToInt32(again["accepted"]) == 0, "retry accepts nothing");
                Check(Convert.ToInt32(again["duplicates"]) == 2, "retry sees both duplicates");
                Check(File.ReadAllLines(file, Encoding.UTF8).Length == 2, "retry does not append");

                var badToken = Sync(port, new string('0', 64), Uid, "phone-validation-1", new[] { event2 });
                Check(!Convert.ToBoolean(badToken["ok"]) && Convert.ToString(badToken["code"]) == "token_invalid", "invalid token rejected");

                var syncMismatch = Sync(port, token, 999L, "phone-validation-1", new[] { event2 });
                Check(!Convert.ToBoolean(syncMismatch["ok"]) && Convert.ToString(syncMismatch["code"]) == "account_mismatch", "sync account mismatch rejected");

                var pairedDiscovery = Discover();
                Check(Convert.ToBoolean(pairedDiscovery["paired"]), "discovery reports paired device");
                Check(server.StatusText().Contains("Validation Phone"), "status lists paired phone");
                Console.WriteLine("PASS: discovery, pairing, account binding, NetEase-only archive, same-request idempotency and token checks");
            }
            finally
            {
                server.Dispose();
                try { Directory.Delete(root, true); } catch { }
            }
        }

        private static Dictionary<string, object> Discover()
        {
            using (var client = new UdpClient())
            {
                client.Client.ReceiveTimeout = 3000;
                byte[] message = Encoding.UTF8.GetBytes("MUSICPLUS_SYNC_V1");
                client.Send(message, message.Length, new IPEndPoint(IPAddress.Loopback, 47821));
                var remote = new IPEndPoint(IPAddress.Any, 0);
                byte[] reply = client.Receive(ref remote);
                return new JavaScriptSerializer().Deserialize<Dictionary<string, object>>(Encoding.UTF8.GetString(reply).Trim());
            }
        }

        private static Dictionary<string, object> Pair(int port, string code, long uid, string deviceId, string deviceName)
        {
            return Request(port, new Dictionary<string, object>
            {
                { "op", "pair" },
                { "code", code },
                { "uid", uid },
                { "deviceId", deviceId },
                { "deviceName", deviceName }
            });
        }

        private static Dictionary<string, object> Sync(int port, string token, long uid, string deviceId, Dictionary<string, object>[] events)
        {
            var list = new ArrayList();
            foreach (var item in events) list.Add(item);
            return Request(port, new Dictionary<string, object>
            {
                { "op", "sync" },
                { "token", token },
                { "uid", uid },
                { "deviceId", deviceId },
                { "events", list }
            });
        }

        private static Dictionary<string, object> Event(string eventId, string song, string packageName, long startedAt)
        {
            return new Dictionary<string, object>
            {
                { "eventId", eventId },
                { "song", song },
                { "mediaId", song },
                { "packageName", packageName },
                { "trigger", "repeat" },
                { "startedAt", startedAt },
                { "endedAt", startedAt + 180000L },
                { "listenedMs", 180000L },
                { "durationMs", 180000L },
                { "title", "歌曲" },
                { "artist", "歌手" },
                { "album", "专辑" },
                { "completed", true },
                { "deviceName", "Validation Phone" }
            };
        }

        private static Dictionary<string, object> Request(int port, Dictionary<string, object> request)
        {
            using (var client = new TcpClient())
            {
                client.Connect(IPAddress.Loopback, port);
                client.ReceiveTimeout = 5000;
                var json = new JavaScriptSerializer();
                byte[] payload = Encoding.UTF8.GetBytes(json.Serialize(request) + "\n");
                NetworkStream stream = client.GetStream();
                stream.Write(payload, 0, payload.Length);
                stream.Flush();
                var reader = new StreamReader(stream, Encoding.UTF8);
                string line = reader.ReadLine();
                Check(!string.IsNullOrWhiteSpace(line), "server returned a response");
                return json.Deserialize<Dictionary<string, object>>(line);
            }
        }
    }
}
