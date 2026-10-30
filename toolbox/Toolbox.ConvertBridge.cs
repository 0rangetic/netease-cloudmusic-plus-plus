using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using System.Windows.Forms;

namespace NeteaseToolbox
{
    // Read-only rows and page state for the native WPF music conversion page.
    internal sealed class ConvertSongEntry
    {
        internal string Title = "";
        internal string Directory = "";
        internal string FullPath = "";
    }

    internal sealed class ConvertMessageEntry
    {
        internal string Time = "";
        internal string Task = "";
        internal string Phase = "";
        internal string Detail = "";
        internal string Text = "";
    }

    internal sealed class ConvertPageSnapshot
    {
        internal string InputFolder = "";
        internal string OutputStatus = "";
        internal string Summary = "";
        internal int PendingCount;
        internal int ConvertedCount;
        internal bool Converting;
        internal bool ExtractingCover;
        internal bool Deduplicating;
        internal bool ScriptAvailable;
        internal bool HasLatestOutput;
        internal string LatestOutputPath = "";
        internal string PendingVersion = "0";
        internal string ConvertedVersion = "0";
        internal string LastMessageText = "";
        internal string LastTask = "";
        internal string LastPhase = "";
        internal string LastDetail = "";
        internal string Error = "";

        internal bool IsBusy { get { return Converting || ExtractingCover || Deduplicating; } }
    }

    internal sealed partial class ToolboxForm
    {
        private static readonly HashSet<string> ConvertTaskNames = new HashSet<string>(StringComparer.Ordinal)
        {
            "转换歌曲", "重新转换", "重新扫描", "选择音乐目录", "封面提取", "重复检测", "打开输出目录"
        };

        // ------------------------------------------------------------------
        // Snapshot: the WinForms conversion page stays the owner of the song
        // lists and the conversion pipeline; the WPF page only reads this.
        // ------------------------------------------------------------------
        internal ConvertPageSnapshot ReadConvertSnapshot()
        {
            var snapshot = new ConvertPageSnapshot();
            try
            {
                snapshot.InputFolder = InputRoot ?? "";
                snapshot.OutputStatus = outputStatus.Text ?? "";
                snapshot.Summary = summary.Text ?? "";
                snapshot.PendingCount = songs.Items.Count;
                snapshot.ConvertedCount = convertedSongs.Items.Count;
                snapshot.Converting = converting;
                snapshot.ExtractingCover = extractingCover;
                snapshot.Deduplicating = deduplicating;
                snapshot.ScriptAvailable = File.Exists(scriptPath);
                snapshot.LatestOutputPath = latestOutputPath ?? "";
                snapshot.HasLatestOutput = !string.IsNullOrEmpty(latestOutputPath) && File.Exists(latestOutputPath);
                snapshot.PendingVersion = ConvertListVersion(songs);
                snapshot.ConvertedVersion = ConvertListVersion(convertedSongs);

                var last = ConvertNewestTaskMessage();
                if (last != null)
                {
                    snapshot.LastMessageText = last.Text;
                    snapshot.LastTask = last.Task;
                    snapshot.LastPhase = last.Phase;
                    snapshot.LastDetail = last.Detail;
                }
            }
            catch (Exception ex)
            {
                snapshot.Error = "读取转换状态失败：" + ex.Message;
            }
            return snapshot;
        }

        // Only called when the cheap version string of a list changed.
        internal List<ConvertSongEntry> ReadConvertSongs(bool converted)
        {
            var list = converted ? convertedSongs : songs;
            var entries = new List<ConvertSongEntry>(list.Items.Count);
            try
            {
                foreach (ListViewItem item in list.Items)
                {
                    entries.Add(new ConvertSongEntry
                    {
                        Title = item.Text ?? "",
                        Directory = item.SubItems.Count > 1 ? (item.SubItems[1].Text ?? "") : "",
                        FullPath = (item.Tag as string) ?? ""
                    });
                }
            }
            catch { }
            return entries;
        }

        internal List<ConvertMessageEntry> ReadConvertRecentMessages(int max)
        {
            var result = new List<ConvertMessageEntry>();
            if (max <= 0) return result;
            try
            {
                foreach (object item in taskMessages.Items)
                {
                    var entry = ConvertParseTaskMessage(item as string);
                    if (entry == null || !ConvertTaskNames.Contains(entry.Task)) continue;
                    result.Add(entry);
                    if (result.Count >= max) break;
                }
            }
            catch { }
            return result;
        }

        private static string ConvertListVersion(ListView list)
        {
            int count = list.Items.Count;
            if (count == 0) return "0";
            string first = Convert.ToString(list.Items[0].Tag) ?? "";
            string last = Convert.ToString(list.Items[count - 1].Tag) ?? "";
            return count + "|" + first + "|" + last;
        }

        private ConvertMessageEntry ConvertNewestTaskMessage()
        {
            try
            {
                foreach (object item in taskMessages.Items)
                {
                    var entry = ConvertParseTaskMessage(item as string);
                    if (entry != null && ConvertTaskNames.Contains(entry.Task)) return entry;
                }
            }
            catch { }
            var status = ConvertParseTaskMessage(taskStatus.Text);
            return status != null && ConvertTaskNames.Contains(status.Task) ? status : null;
        }

        // ReportTask writes "HH:mm:ss  任务 · 阶段：详情".
        private static ConvertMessageEntry ConvertParseTaskMessage(string raw)
        {
            if (string.IsNullOrWhiteSpace(raw)) return null;
            string text = raw.Trim();
            var entry = new ConvertMessageEntry { Text = text };
            int separator = text.IndexOf(" · ", StringComparison.Ordinal);
            string head = separator >= 0 ? text.Substring(0, separator) : text;
            int timeSplit = head.IndexOf("  ", StringComparison.Ordinal);
            if (timeSplit > 0)
            {
                entry.Time = head.Substring(0, timeSplit).Trim();
                entry.Task = head.Substring(timeSplit).Trim();
            }
            else entry.Task = head.Trim();
            if (separator >= 0)
            {
                string rest = text.Substring(separator + 3);
                int colon = rest.IndexOf('：');
                if (colon > 0)
                {
                    entry.Phase = rest.Substring(0, colon).Trim();
                    entry.Detail = rest.Substring(colon + 1).Trim();
                }
                else entry.Phase = rest.Trim();
            }
            return entry;
        }

        // ------------------------------------------------------------------
        // Actions: thin wrappers over the existing WinForms handlers, so the
        // conversion pipeline, duplicate detection and logging are unchanged.
        // ------------------------------------------------------------------
        private bool ConvertActionBlocked(string task)
        {
            if (!converting && !extractingCover && !deduplicating) return false;
            ReportTask(task, "已取消", "当前有转换或提取任务正在进行，请稍后再试。");
            return true;
        }

        internal void ConvertRescanFromView()
        {
            if (analyzingGenres) { ReportTask("重新扫描", "已取消", "曲风分析正在进行，请稍后再扫描。"); return; }
            if (ConvertActionBlocked("重新扫描")) return;
            ScanSongs(true);
        }

        internal void ConvertChooseInputFromView()
        {
            if (analyzingGenres) { ReportTask("选择音乐目录", "已取消", "曲风分析正在进行，请稍后再试。"); return; }
            if (ConvertActionBlocked("选择音乐目录")) return;
            ChooseInputFolder();
        }

        internal Task ConvertAllPendingFromViewAsync()
        {
            if (ConvertActionBlocked("转换歌曲")) return Task.CompletedTask;
            if (!File.Exists(scriptPath))
            {
                ReportTask("转换歌曲", "失败", "转换脚本不存在：" + scriptPath);
                return Task.CompletedTask;
            }
            var snapshot = pending.ToArray();
            if (snapshot.Length == 0)
            {
                ReportTask("转换歌曲", "失败", "当前没有待转换的歌曲，请先选择音乐目录或重新扫描。");
                return Task.CompletedTask;
            }
            return ConvertFiles(snapshot);
        }

        internal Task ConvertSelectedFromViewAsync(bool reconvert, string[] paths)
        {
            var taskName = reconvert ? "重新转换" : "转换歌曲";
            if (ConvertActionBlocked(taskName)) return Task.CompletedTask;
            var selected = (paths ?? new string[0])
                .Where(path => !string.IsNullOrWhiteSpace(path) && File.Exists(path))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToArray();
            if (selected.Length == 0)
            {
                ReportTask(taskName, "失败", "请先在列表中选择要转换的歌曲。");
                return Task.CompletedTask;
            }
            if (!File.Exists(scriptPath))
            {
                ReportTask(taskName, "失败", "转换脚本不存在：" + scriptPath);
                return Task.CompletedTask;
            }
            return ConvertFiles(selected, reconvert);
        }

        internal void ConvertOpenLatestOutputFromView()
        {
            try
            {
                if (string.IsNullOrEmpty(latestOutputPath) || !File.Exists(latestOutputPath))
                {
                    ReportTask("打开输出目录", "失败", "还没有找到已转换的音频文件，请先转换一首歌曲。");
                    return;
                }
                Process.Start("explorer.exe", "/select," + Quoted(latestOutputPath));
                WriteLog("已打开转换结果：" + latestOutputPath);
            }
            catch (Exception ex)
            {
                ReportTask("打开输出目录", "失败", ex.Message);
            }
        }

        // ExtractSelectedCover reads the selection from the WinForms list, so
        // mirror the WPF row into it for the duration of the call and restore
        // the previous selection afterwards.
        internal async Task ConvertExtractCoverFromViewAsync(string sourcePath, bool fromConverted)
        {
            if (string.IsNullOrWhiteSpace(sourcePath))
            {
                ReportTask("封面提取", "失败", "请先在列表中选择一首歌曲。");
                return;
            }
            if (ConvertActionBlocked("封面提取")) return;
            if (!File.Exists(scriptPath))
            {
                ReportTask("封面提取", "失败", "转换脚本不存在：" + scriptPath);
                return;
            }

            var list = fromConverted ? convertedSongs : songs;
            ListViewItem target = null;
            foreach (ListViewItem item in list.Items)
            {
                if (string.Equals(item.Tag as string, sourcePath, StringComparison.OrdinalIgnoreCase))
                {
                    target = item;
                    break;
                }
            }
            if (target == null)
            {
                ReportTask("封面提取", "失败", "列表中没有找到这首歌，请重新扫描后再试。");
                return;
            }

            int previousTab = songTabs.SelectedIndex;
            ListViewItem[] previousSelection = list.SelectedItems.Cast<ListViewItem>().ToArray();
            ListViewItem previousFocus = list.FocusedItem;
            try
            {
                songTabs.SelectedIndex = fromConverted ? 1 : 0;
                list.SelectedItems.Clear();
                target.Selected = true;
                target.Focused = true;
                await ExtractSelectedCover();
            }
            catch (Exception ex)
            {
                ReportTask("封面提取", "失败", ex.Message);
            }
            finally
            {
                try
                {
                    list.SelectedItems.Clear();
                    if (previousSelection != null)
                        foreach (var item in previousSelection)
                            if (item != null && list.Items.Contains(item)) item.Selected = true;
                    if (previousFocus != null && list.Items.Contains(previousFocus))
                    {
                        previousFocus.Focused = true;
                        previousFocus.Selected = true;
                    }
                }
                catch { }
                try { songTabs.SelectedIndex = previousTab; }
                catch { }
            }
        }
    }
}
