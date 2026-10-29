using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Web.Script.Serialization;

namespace NeteaseToolbox
{
    internal sealed class PlaylistRulePreset
    {
        public string Id { get; set; }
        public string Name { get; set; }
        public string[] Moods { get; set; }
        public string[] Genres { get; set; }
        public string[] ExcludedTags { get; set; }
        public string MinimumMode { get; set; }
        public int MinimumHits { get; set; }
        public string PlaylistName { get; set; }
        public string UpdatedAt { get; set; }

        public override string ToString()
        {
            return string.IsNullOrWhiteSpace(Name) ? "未命名规则" : Name;
        }
    }

    internal sealed class PlaylistRuleException : Exception
    {
        internal PlaylistRuleException(string message) : base(message) { }
        internal PlaylistRuleException(string message, Exception inner) : base(message, inner) { }
    }

    internal sealed class PlaylistRuleFile
    {
        public int Version { get; set; }
        public List<PlaylistRulePreset> Presets { get; set; }
    }

    internal sealed class PlaylistRuleStore
    {
        internal const int CurrentVersion = 1;
        private static readonly object Gate = new object();
        private readonly string folder;

        internal PlaylistRuleStore(string folder)
        {
            if (string.IsNullOrWhiteSpace(folder)) throw new ArgumentException("规则预设目录不能为空。", "folder");
            this.folder = Path.GetFullPath(folder);
        }

        internal string Folder { get { return folder; } }

        internal string FilePath { get { return Path.Combine(folder, "presets.json"); } }

        internal List<PlaylistRulePreset> Load()
        {
            lock (Gate)
            {
                var result = new List<PlaylistRulePreset>();
                if (!File.Exists(FilePath)) return result;
                string text;
                try
                {
                    text = File.ReadAllText(FilePath, Encoding.UTF8);
                }
                catch (Exception ex)
                {
                    throw new PlaylistRuleException("无法读取规则预设文件：" + ex.Message, ex);
                }
                PlaylistRuleFile file;
                try
                {
                    file = new JavaScriptSerializer().Deserialize<PlaylistRuleFile>(text);
                }
                catch (Exception ex)
                {
                    throw new PlaylistRuleException("规则预设文件格式损坏，未做任何修改。文件位置：" + FilePath, ex);
                }
                if (file == null)
                    throw new PlaylistRuleException("规则预设文件内容为空或格式无效，未做任何修改。文件位置：" + FilePath);
                if (file.Version <= 0)
                    throw new PlaylistRuleException("规则预设文件缺少有效的格式版本，未做任何修改。文件位置：" + FilePath);
                if (file.Version > CurrentVersion)
                    throw new PlaylistRuleException("规则预设文件由更新版本的工具箱创建，请升级后再打开。文件位置：" + FilePath);
                var ids = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                var names = new HashSet<string>(StringComparer.CurrentCultureIgnoreCase);
                foreach (var preset in file.Presets ?? new List<PlaylistRulePreset>())
                {
                    var normalized = Normalize(preset);
                    if (normalized == null || !names.Add(normalized.Name)) continue;
                    if (!ids.Add(normalized.Id)) normalized.Id = Guid.NewGuid().ToString("N");
                    result.Add(normalized);
                }
                return result;
            }
        }

        internal void Save(IList<PlaylistRulePreset> presets)
        {
            lock (Gate)
            {
                var normalized = new List<PlaylistRulePreset>();
                var names = new HashSet<string>(StringComparer.CurrentCultureIgnoreCase);
                var ids = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                foreach (var preset in presets ?? new List<PlaylistRulePreset>())
                {
                    var item = Normalize(preset);
                    if (item == null) throw new PlaylistRuleException("规则预设名称不能为空，未保存任何修改。");
                    if (!names.Add(item.Name))
                        throw new PlaylistRuleException("规则预设名称重复：“" + item.Name + "”，未保存任何修改。");
                    if (!ids.Add(item.Id)) item.Id = Guid.NewGuid().ToString("N");
                    if (string.IsNullOrWhiteSpace(item.UpdatedAt)) item.UpdatedAt = DateTime.UtcNow.ToString("o");
                    normalized.Add(item);
                }
                string json = new JavaScriptSerializer().Serialize(new PlaylistRuleFile {
                    Version = CurrentVersion,
                    Presets = normalized
                });
                string temporary = FilePath + "." + Guid.NewGuid().ToString("N") + ".tmp";
                try
                {
                    Directory.CreateDirectory(folder);
                    using (var stream = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None))
                    using (var writer = new StreamWriter(stream, new UTF8Encoding(false)))
                    {
                        writer.Write(json);
                        writer.Flush();
                        stream.Flush(true);
                    }
                    if (File.Exists(FilePath)) File.Replace(temporary, FilePath, null, true);
                    else File.Move(temporary, FilePath);
                }
                catch (Exception ex)
                {
                    throw new PlaylistRuleException("保存规则预设失败：" + ex.Message + "。原有预设未修改。", ex);
                }
                finally
                {
                    try { if (File.Exists(temporary)) File.Delete(temporary); } catch { }
                }
            }
        }

        private static PlaylistRulePreset Normalize(PlaylistRulePreset preset)
        {
            if (preset == null) return null;
            string name = (preset.Name ?? "").Trim();
            if (name.Length == 0) return null;
            string playlistName = (preset.PlaylistName ?? "").Trim();
            return new PlaylistRulePreset {
                Id = string.IsNullOrWhiteSpace(preset.Id) ? Guid.NewGuid().ToString("N") : preset.Id.Trim(),
                Name = name,
                Moods = NormalizeTags(preset.Moods),
                Genres = NormalizeTags(preset.Genres),
                ExcludedTags = NormalizeTags(preset.ExcludedTags),
                MinimumMode = string.Equals(preset.MinimumMode, "manual", StringComparison.OrdinalIgnoreCase) ? "manual" : "auto",
                MinimumHits = Math.Max(1, preset.MinimumHits),
                PlaylistName = playlistName.Length == 0 ? name : playlistName,
                UpdatedAt = (preset.UpdatedAt ?? "").Trim()
            };
        }

        private static string[] NormalizeTags(string[] tags)
        {
            var result = new List<string>();
            var seen = new HashSet<string>(StringComparer.Ordinal);
            if (tags != null)
            {
                foreach (var tag in tags)
                {
                    if (string.IsNullOrWhiteSpace(tag)) continue;
                    string value = tag.Trim();
                    if (seen.Add(value)) result.Add(value);
                }
            }
            return result.ToArray();
        }
    }
}
