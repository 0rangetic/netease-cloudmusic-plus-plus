using System;
using System.Collections.Generic;
using System.IO;
using System.Threading.Tasks;
using System.Windows.Forms;

namespace NeteaseToolbox
{
    // Read-only state shown by the native WPF genre page.
    internal sealed class GenreSongSnapshot
    {
        internal string Source;
        internal string Title;
        internal string Genre;
        internal string Influences;
        internal string Moods;
        internal string Origin;
        internal string Status;
        internal string Directory;
        // Local converted audio can be edited; netease://song/<id> rows cannot.
        internal bool CanEdit;
    }

    internal sealed class GenrePageSnapshot
    {
        internal List<GenreSongSnapshot> Songs = new List<GenreSongSnapshot>();
        internal int Total;
        internal int Analyzed;
        internal int Pending;
        internal long Signature;
        internal string BusyText;
        internal bool Busy;
        internal bool CanAnalyze;
        internal bool CanExport;
        internal bool CanCreatePlaylist;
        internal bool ScriptAvailable;
        internal string LibraryStatus;
        internal string MusicFolder;
        internal bool MusicFolderExists;
        internal string Error;
    }

    internal sealed partial class ToolboxForm
    {
        // Minimal facade for GenreView: the WinForms page keeps owning the
        // controls and business logic, the WPF page only reads and triggers it.
        internal GenrePageSnapshot ReadGenreSnapshot()
        {
            var snapshot = new GenrePageSnapshot();
            snapshot.Busy = analyzingGenres || loadingGenreResults || buildingTagPlaylist;
            snapshot.BusyText = analyzingGenres ? "正在分析曲风，请稍候…"
                : buildingTagPlaylist ? "正在读取红心歌单与标签…"
                : loadingGenreResults ? "正在读取已有曲风记录…" : "";
            snapshot.MusicFolder = InputRoot ?? "";
            snapshot.MusicFolderExists = !string.IsNullOrEmpty(InputRoot) && Directory.Exists(InputRoot);
            snapshot.LibraryStatus = summary.Text ?? "";
            snapshot.ScriptAvailable = File.Exists(genreScriptPath);

            long signature = 17;
            foreach (ListViewItem item in genreSongs.Items)
            {
                var song = new GenreSongSnapshot
                {
                    Source = (item.Tag as string) ?? "",
                    Title = GenreCell(item, 0),
                    Genre = GenreCell(item, 1),
                    Influences = GenreCell(item, 2),
                    Moods = GenreCell(item, 3),
                    Origin = GenreCell(item, 4),
                    Status = GenreCell(item, 5),
                    Directory = GenreCell(item, 6)
                };
                song.CanEdit = song.Source.Length > 0
                    && !song.Source.StartsWith("netease://song/", StringComparison.OrdinalIgnoreCase);
                snapshot.Total++;
                if (song.Status.Length == 0 || song.Status == "未分析") snapshot.Pending++;
                else snapshot.Analyzed++;
                snapshot.Songs.Add(song);

                // Cheap content hash: avoids building a huge signature string every poll.
                GenreMix(ref signature, song.Source);
                GenreMix(ref signature, song.Title);
                GenreMix(ref signature, song.Genre);
                GenreMix(ref signature, song.Influences);
                GenreMix(ref signature, song.Moods);
                GenreMix(ref signature, song.Origin);
                GenreMix(ref signature, song.Status);
                GenreMix(ref signature, song.Directory);
            }
            snapshot.Signature = signature;

            snapshot.CanAnalyze = !snapshot.Busy && snapshot.Total > 0 && snapshot.ScriptAvailable && !converting;
            snapshot.CanExport = !snapshot.Busy && snapshot.Total > 0 && snapshot.ScriptAvailable;
            snapshot.CanCreatePlaylist = !snapshot.Busy && snapshot.ScriptAvailable && !converting;
            if (!snapshot.ScriptAvailable)
                snapshot.Error = "找不到曲风分析程序 genre_analysis.py；请把完整工具箱文件（含 runtime）放在同一目录后重试。";
            return snapshot;
        }

        private static string GenreCell(ListViewItem item, int index)
        {
            if (item == null || index < 0 || item.SubItems.Count <= index) return "";
            return item.SubItems[index].Text ?? "";
        }

        private static void GenreMix(ref long hash, string value)
        {
            hash = unchecked(hash * 31 + (string.IsNullOrEmpty(value) ? 0 : value.GetHashCode()));
        }

        internal Task GenreAnalyzeAsync() { return AnalyzeGenres(); }
        internal Task GenreExportAsync() { return ExportGenres(); }
        internal Task GenreCreatePlaylistAsync() { return GenerateTagPlaylist(); }

        // Reuses the WinForms result loader (guarded against concurrent runs) so a
        // freshly opened WPF page does not show "未分析" for songs already analysed.
        internal void GenreRefreshResults()
        {
            if (IsDisposed || loadingGenreResults || genreSongs.Items.Count == 0) return;
            LoadGenreResults();
        }

        // The startup scan is skipped when the toolbox starts in the background;
        // fill the shared table once so the WPF page is not permanently empty.
        internal void GenreEnsureScanned()
        {
            if (IsDisposed || converting || deduplicating || analyzingGenres) return;
            if (genreSongs.Items.Count > 0) return;
            if (string.IsNullOrEmpty(InputRoot) || !Directory.Exists(InputRoot)) return;
            ScanSongs();
        }

        internal async Task GenreEditSongAsync(string source)
        {
            if (string.IsNullOrWhiteSpace(source)) return;
            if (analyzingGenres)
            {
                ReportTask("曲风修正", "失败", "正在分析曲风，请完成后再手动修正。");
                return;
            }
            ListViewItem row;
            if (!genreRows.TryGetValue(source, out row) || row == null)
            {
                ReportTask("曲风修正", "失败", "歌曲列表里找不到这首歌，请刷新后重试。");
                return;
            }
            if (!File.Exists(source))
            {
                ReportTask("曲风修正", "失败", "这首歌没有本地音频文件（红心歌单条目），无法手动修正曲风。");
                return;
            }
            genreSongs.SelectedIndex = genreSongs.Items.IndexOf(row);
            await EditGenreResult();
        }

        internal async Task GenreOpenSongAsync(string source)
        {
            if (string.IsNullOrWhiteSpace(source)) return;
            ReportTask("打开网易云", "开始", "正在打开所选歌曲的播放页面。");
            try
            {
                string songId;
                if (source.StartsWith("netease://song/", StringComparison.OrdinalIgnoreCase))
                    songId = SongNavigation.ValidateId(source.Substring("netease://song/".Length));
                else
                    songId = await Task.Run(() => SongNavigation.ResolveSource(source, InputRoot));
                SongNavigation.OpenSong(songId, delegate(string task, string phase, string detail)
                {
                    ReportTask("打开网易云", phase, detail + "（歌曲 ID " + songId + "）");
                });
            }
            catch (Exception ex) { ReportTask("打开网易云", "失败", ex.Message); }
        }
    }
}
