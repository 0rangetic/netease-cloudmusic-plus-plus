using System;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Web.Script.Serialization;

namespace NeteaseToolbox
{
    internal static class SongNavigation
    {
        internal static string ClientUrl(string songId)
        {
            // The Windows client's --webcmd parser expects Base64 JSON, and
            // channel=webset opens its vinyl playback page with the song.
            var message = new { cmd = "play", type = "song", id = ValidateId(songId), channel = "webset" };
            return "orpheus://" + Convert.ToBase64String(Encoding.UTF8.GetBytes(new JavaScriptSerializer().Serialize(message)));
        }

        internal static string ValidateId(string value)
        {
            long id;
            if (!long.TryParse(value, out id) || id <= 0)
                throw new InvalidDataException("没有有效的网易云歌曲 ID，无法打开播放页面。");
            return id.ToString(System.Globalization.CultureInfo.InvariantCulture);
        }

        internal static string ResolveSource(string source, string inputRoot)
        {
            const string prefix = "netease://song/";
            if (string.IsNullOrWhiteSpace(source)) throw new InvalidDataException("没有歌曲来源信息。");
            if (source.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
                return ValidateId(source.Substring(prefix.Length));
            if (!string.Equals(Path.GetExtension(source), ".ncm", StringComparison.OrdinalIgnoreCase))
            {
                var matches = Directory.EnumerateFiles(inputRoot, "*.ncm", SearchOption.AllDirectories)
                    .Where(path => string.Equals(Path.GetFileNameWithoutExtension(path),
                        Path.GetFileNameWithoutExtension(source), StringComparison.OrdinalIgnoreCase)).Take(2).ToArray();
                if (matches.Length != 1) throw new InvalidDataException(matches.Length == 0
                    ? "找不到对应 NCM 源文件，无法取得歌曲 ID。"
                    : "存在多个同名 NCM 源文件，无法确定歌曲 ID。");
                source = matches[0];
            }
            using (var stream = File.OpenRead(source))
            using (var reader = new BinaryReader(stream))
            {
                if (Encoding.ASCII.GetString(reader.ReadBytes(8)) != "CTENFDAM")
                    throw new InvalidDataException("NCM 文件格式无效。");
                reader.ReadBytes(2);
                uint keyLength = reader.ReadUInt32();
                if (keyLength == 0 || keyLength > 1048576) throw new InvalidDataException("NCM 密钥块无效。");
                stream.Seek(keyLength, SeekOrigin.Current);
                uint length = reader.ReadUInt32();
                if (length < 22 || length > 1048576) throw new InvalidDataException("NCM 元数据块无效。");
                var metadata = reader.ReadBytes((int)length);
                if (metadata.Length != length) throw new EndOfStreamException("NCM 元数据不完整。");
                for (int i = 0; i < metadata.Length; i++) metadata[i] ^= 0x63;
                var cipher = Convert.FromBase64String(Encoding.ASCII.GetString(metadata, 22, metadata.Length - 22));
                using (var aes = Aes.Create())
                {
                    aes.Key = new byte[] { 0x23, 0x31, 0x34, 0x6c, 0x6a, 0x6b, 0x5f, 0x21, 0x5c, 0x5d, 0x26, 0x30, 0x55, 0x3c, 0x27, 0x28 };
                    aes.Mode = CipherMode.ECB;
                    aes.Padding = PaddingMode.PKCS7;
                    using (var decryptor = aes.CreateDecryptor())
                    {
                        var json = Encoding.UTF8.GetString(decryptor.TransformFinalBlock(cipher, 0, cipher.Length));
                        if (!json.StartsWith("music:", StringComparison.Ordinal)) throw new InvalidDataException("NCM 元数据格式无效。");
                        var values = new JavaScriptSerializer().DeserializeObject(json.Substring(6)) as System.Collections.Generic.Dictionary<string, object>;
                        object id;
                        if (values == null || (!values.TryGetValue("musicId", out id) && !values.TryGetValue("id", out id)))
                            throw new InvalidDataException("NCM 未包含网易云歌曲 ID。");
                        return ValidateId(Convert.ToString(id));
                    }
                }
            }
        }
    }
}
