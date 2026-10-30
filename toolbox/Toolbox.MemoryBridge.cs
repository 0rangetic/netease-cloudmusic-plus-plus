using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using System.Windows.Forms;

namespace NeteaseToolbox
{
    /// <summary>
    /// 音乐记忆 WPF 页的只读快照。内容全部来自现有 WinForms 检索页的控件状态，
    /// 不复制检索逻辑；WPF 页只负责呈现和轮询。
    /// </summary>
    internal sealed class MemoryRecallViewSnapshot
    {
        public bool Busy;
        public string Status, Error, Notice;
        public string Query;
        public bool LimitDates, UseAi;
        public DateTime StartDate, EndDate;
        public List<MemoryRecallViewItem> Results = new List<MemoryRecallViewItem>();
    }

    internal sealed class MemoryRecallViewItem
    {
        public string PlayedText, Title, Artist, Album, Evidence, LyricExcerpt, SongId;
    }

    internal sealed class MemoryRecallViewDetail
    {
        public string Title, Artist, Evidence;
        public bool HasSongId;
    }

    internal sealed partial class ToolboxForm
    {
        private string memoryRecallViewNotice = "";

        /// <summary>读取当前检索状态；轮询频繁，只做字段拷贝，不触发任何检索或绘制。</summary>
        internal MemoryRecallViewSnapshot ReadMemoryRecallViewSnapshot()
        {
            var snapshot = new MemoryRecallViewSnapshot
            {
                Busy = searchingMemory || !memorySearchButton.Enabled,
                Status = memorySourceStatus.Text,
                Notice = memoryRecallViewNotice,
                Query = memoryQuery.Text,
                LimitDates = memoryLimitDates.Checked,
                UseAi = memoryUseAi.Checked,
                StartDate = memoryStartDate.Value.Date,
                EndDate = memoryEndDate.Value.Date
            };
            if (!string.IsNullOrEmpty(snapshot.Status) && snapshot.Status.StartsWith("检索失败", StringComparison.Ordinal))
                snapshot.Error = snapshot.Status;

            foreach (ListViewItem row in memoryResults.Items)
            {
                var item = row.Tag as Dictionary<string, object>;
                snapshot.Results.Add(new MemoryRecallViewItem
                {
                    PlayedText = row.Text,
                    Title = RecallValue(item, "title"),
                    Artist = RecallValue(item, "artist"),
                    Album = RecallValue(item, "album"),
                    Evidence = row.SubItems.Count > 3 ? row.SubItems[3].Text
                        : string.Join("；", EvidenceLines(item)),
                    LyricExcerpt = RecallValue(item, "lyricExcerpt"),
                    SongId = RecallValue(item, "songId")
                });
            }
            return snapshot;
        }

        /// <summary>
        /// 用 WPF 页的输入驱动既有 SearchMemoryRecall。查询、日期范围、AI 开关都写回
        /// 不显示的 WinForms 检索页，保证只有一套检索实现。
        /// </summary>
        internal async Task<MemoryRecallViewSnapshot> RunMemoryRecallViewSearchAsync(
            string query, bool limitDates, DateTime startDate, DateTime endDate, bool useAi)
        {
            query = query ?? "";
            memoryRecallViewNotice = "";
            memoryQuery.Text = query;
            memoryStartDate.Value = startDate.Date;
            memoryEndDate.Value = endDate.Date;
            memoryLimitDates.Checked = limitDates;
            memoryUseAi.Checked = useAi;

            if (string.IsNullOrWhiteSpace(query) && !limitDates)
            {
                memoryRecallViewNotice = "输入日期、时段、歌名、艺人或记得的歌词片段，也可以勾选日期范围后浏览那段时间的播放记录。";
                return ReadMemoryRecallViewSnapshot();
            }
            if (limitDates && endDate.Date < startDate.Date)
            {
                memoryRecallViewNotice = "日期范围无效：结束日期早于开始日期。";
                return ReadMemoryRecallViewSnapshot();
            }

            await SearchMemoryRecall();
            var snapshot = ReadMemoryRecallViewSnapshot();
            if (string.IsNullOrEmpty(snapshot.Error))
            {
                string evidence = memoryEvidence.Text ?? "";
                if (snapshot.Results.Count == 0)
                {
                    memoryRecallViewNotice = string.IsNullOrWhiteSpace(evidence) ? "没有找到匹配项。" : evidence.Trim();
                }
                else if (evidence.Contains("数据源提示："))
                {
                    // 有结果但数据源不完整时（部分失败）在页面上明确标出，不能当作全部完成。
                    int at = evidence.LastIndexOf("数据源提示：", StringComparison.Ordinal);
                    memoryRecallViewNotice = evidence.Substring(at).Trim();
                }
            }
            return snapshot;
        }

        /// <summary>在隐藏的结果列表里选中一行，复用 ShowMemoryEvidence 的证据排版。</summary>
        internal MemoryRecallViewDetail SelectMemoryRecallViewResult(int index)
        {
            var detail = new MemoryRecallViewDetail { Evidence = "", Title = "", Artist = "", HasSongId = false };
            if (index < 0 || index >= memoryResults.Items.Count) return detail;

            var row = memoryResults.Items[index];
            var item = row.Tag as Dictionary<string, object>;
            memoryResults.SelectedItems.Clear();
            row.Selected = true;
            // 只为复用 ShowMemoryEvidence 的排版；不设置 Focused，避免隐藏页抢走 WPF 焦点。
            ShowMemoryEvidence();

            detail.Evidence = memoryEvidence.Text;
            detail.Title = RecallValue(item, "title");
            detail.Artist = RecallValue(item, "artist");
            long songId;
            detail.HasSongId = long.TryParse(RecallValue(item, "songId"), out songId) && songId > 0;
            return detail;
        }

        /// <summary>双击结果或按钮时在网易云打开；复用 SongNavigation 与任务反馈。</summary>
        internal void OpenMemoryRecallViewSong(int index)
        {
            if (index < 0 || index >= memoryResults.Items.Count) return;
            var item = memoryResults.Items[index].Tag as Dictionary<string, object>;
            if (item == null) return;
            string songId = RecallValue(item, "songId");
            long parsed;
            if (!long.TryParse(songId, out parsed) || parsed <= 0)
            {
                ReportTask("打开网易云", "失败", "这条记录没有歌曲 ID，无法打开播放页面。");
                return;
            }
            SongNavigation.OpenSong(songId, ReportTask);
        }

        private static IEnumerable<string> EvidenceLines(Dictionary<string, object> item)
        {
            var evidence = item != null && item.ContainsKey("evidence")
                ? item["evidence"] as System.Collections.ArrayList : null;
            if (evidence == null) yield break;
            foreach (var line in evidence) yield return Convert.ToString(line);
        }
    }
}
