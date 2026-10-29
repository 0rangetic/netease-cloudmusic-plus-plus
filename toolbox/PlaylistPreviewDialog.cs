using System;
using System.Collections.Generic;
using System.Drawing;
using System.Linq;
using System.Windows.Forms;

namespace NeteaseToolbox
{
    internal sealed partial class ToolboxForm
    {
        private sealed class TagPlaylistPreviewWindow : Form
        {
            private enum PreviewFilter
            {
                Matched,
                All,
                Included,
                Removed,
                BelowMinimum,
                HardExcluded,
                NoTags,
                NoSelection
            }

            private enum PreviewSort
            {
                Source,
                HitsDescending,
                HitsAscending,
                Title,
                Artist
            }

            private readonly ToolboxForm owner;
            private readonly TagPlaylistSelectionState selection;
            private readonly List<TagPlaylistEvaluation> evaluations = new List<TagPlaylistEvaluation>();
            private readonly DataGridView grid = new DataGridView();
            private readonly TextBox search = new TextBox();
            private readonly ComboBox status = new ComboBox();
            private readonly ComboBox sort = new ComboBox();
            private readonly Label ruleLabel = new Label();
            private readonly Label countsLabel = new Label();
            private readonly TextBox details = new TextBox();
            private readonly Timer searchTimer = new Timer();
            private readonly ToolTip tips = new ToolTip();
            private bool updatingGrid;
            private int selectedTagCount;
            private string ruleSummary = "";

            internal event EventHandler SelectionChanged;

            internal TagPlaylistPreviewWindow(ToolboxForm owner, TagPlaylistSelectionState selection)
            {
                this.owner = owner;
                this.selection = selection;
                Text = "匹配歌曲预览";
                BackColor = owner.theme.Background;
                ForeColor = owner.theme.Ink;
                Font = owner.UiFont(9F);
                AutoScaleMode = AutoScaleMode.Dpi;
                AutoScaleDimensions = new SizeF(96F, 96F);
                StartPosition = FormStartPosition.Manual;
                Size = new Size(1100, 680);
                MinimumSize = new Size(840, 480);
                ShowInTaskbar = false;
                KeyPreview = true;
                BuildUi();
                owner.PrepareThemedDialog(this);
            }

            internal void UpdateData(List<TagPlaylistEvaluation> data, int minimum, int selectedCount, string summary)
            {
                evaluations.Clear();
                if (data != null) evaluations.AddRange(data);
                selectedTagCount = selectedCount;
                ruleSummary = summary ?? "";
                ruleLabel.Text = selectedCount == 0
                    ? "请先勾选筛选标签；可切换“全部歌曲”查看每首歌曲的未匹配原因。"
                    : (ruleSummary.Length == 0 ? "最低命中：" + minimum + " 个" : ruleSummary);
                tips.SetToolTip(ruleLabel, ruleLabel.Text);
                Rebuild();
            }

            internal void PlaceBeside(Form ownerDialog)
            {
                var screen = Screen.FromControl(ownerDialog).WorkingArea;
                int x = ownerDialog.Right + 12;
                if (x + Width > screen.Right) x = ownerDialog.Left - Width - 12;
                if (x < screen.Left + 12) x = Math.Max(screen.Left + 12, screen.Right - Width - 12);
                int y = Math.Max(screen.Top + 12, Math.Min(ownerDialog.Top, screen.Bottom - Height - 12));
                Location = new Point(x, y);
            }

            private void BuildUi()
            {
                var root = new TableLayoutPanel {
                    Dock = DockStyle.Fill,
                    Padding = new Padding(14, 12, 14, 12),
                    ColumnCount = 1,
                    RowCount = 6,
                    BackColor = owner.theme.Background
                };
                root.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
                root.RowStyles.Add(new RowStyle(SizeType.AutoSize));
                root.RowStyles.Add(new RowStyle(SizeType.AutoSize));
                root.RowStyles.Add(new RowStyle(SizeType.AutoSize));
                root.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
                root.RowStyles.Add(new RowStyle(SizeType.Absolute, 108));
                root.RowStyles.Add(new RowStyle(SizeType.AutoSize));
                Controls.Add(root);

                var header = new TableLayoutPanel {
                    Dock = DockStyle.Fill,
                    AutoSize = true,
                    ColumnCount = 2,
                    RowCount = 1,
                    Margin = new Padding(0),
                    BackColor = owner.theme.Background
                };
                header.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
                header.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
                var title = new Label {
                    Text = "匹配歌曲预览",
                    AutoSize = true,
                    ForeColor = owner.theme.Ink,
                    BackColor = Color.Transparent,
                    Font = owner.UiFont(13F, FontStyle.Bold),
                    Margin = new Padding(0)
                };
                countsLabel.Name = "PlaylistPreviewCounts";
                countsLabel.AutoSize = true;
                countsLabel.ForeColor = owner.theme.Accent;
                countsLabel.BackColor = Color.Transparent;
                countsLabel.Font = owner.UiFont(9.5F, FontStyle.Bold);
                countsLabel.TextAlign = ContentAlignment.MiddleRight;
                countsLabel.Margin = new Padding(12, 4, 0, 0);
                header.Controls.Add(title, 0, 0);
                header.Controls.Add(countsLabel, 1, 0);
                root.Controls.Add(header, 0, 0);

                ruleLabel.Name = "PlaylistPreviewRule";
                ruleLabel.AutoSize = false;
                ruleLabel.Dock = DockStyle.Fill;
                ruleLabel.AutoEllipsis = true;
                ruleLabel.TextAlign = ContentAlignment.MiddleLeft;
                ruleLabel.MinimumSize = new Size(0, 22);
                ruleLabel.ForeColor = owner.theme.Muted;
                ruleLabel.BackColor = Color.Transparent;
                ruleLabel.Margin = new Padding(0, 2, 0, 6);
                root.Controls.Add(ruleLabel, 0, 1);

                var filters = new TableLayoutPanel {
                    Dock = DockStyle.Fill,
                    AutoSize = true,
                    ColumnCount = 6,
                    RowCount = 1,
                    Margin = new Padding(0, 0, 0, 6),
                    BackColor = owner.theme.Background
                };
                filters.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
                filters.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
                filters.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
                filters.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 178));
                filters.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
                filters.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 148));
                filters.RowStyles.Add(new RowStyle(SizeType.AutoSize));
                var searchLabel = new Label {
                    Text = "搜索", AutoSize = true, ForeColor = owner.theme.Muted,
                    BackColor = Color.Transparent, Margin = new Padding(0, 5, 6, 0)
                };
                search.Name = "PlaylistPreviewSearch";
                search.Dock = DockStyle.Fill;
                search.Font = owner.UiFont(9.5F);
                search.BackColor = owner.theme.Panel;
                search.ForeColor = owner.theme.Ink;
                search.Margin = new Padding(0, 2, 10, 0);
                tips.SetToolTip(search, "搜索歌曲名、歌手、曲风、情绪或命中标签。");
                var statusLabel = new Label {
                    Text = "状态", AutoSize = true, ForeColor = owner.theme.Muted,
                    BackColor = Color.Transparent, Margin = new Padding(0, 5, 6, 0)
                };
                status.Name = "PlaylistPreviewStatus";
                status.DropDownStyle = ComboBoxStyle.DropDownList;
                status.Font = owner.UiFont(9F);
                status.Margin = new Padding(0, 2, 10, 0);
                status.Items.AddRange(new object[] {
                    "符合规则（含手动排除）", "全部歌曲", "已加入", "手动排除",
                    "未达最低命中", "硬性排除", "尚未分析", "未选择标签"
                });
                status.SelectedIndex = 0;
                tips.SetToolTip(status, "按规则状态筛选列表；手动排除只统计仍然符合规则的歌曲。");
                var sortLabel = new Label {
                    Text = "排序", AutoSize = true, ForeColor = owner.theme.Muted,
                    BackColor = Color.Transparent, Margin = new Padding(0, 5, 6, 0)
                };
                sort.Name = "PlaylistPreviewSort";
                sort.DropDownStyle = ComboBoxStyle.DropDownList;
                sort.Font = owner.UiFont(9F);
                sort.Margin = new Padding(0, 2, 0, 0);
                sort.Items.AddRange(new object[] { "默认顺序", "命中数从高到低", "命中数从低到高", "歌曲名", "歌手" });
                sort.SelectedIndex = 0;
                filters.Controls.Add(searchLabel, 0, 0);
                filters.Controls.Add(search, 1, 0);
                filters.Controls.Add(statusLabel, 2, 0);
                filters.Controls.Add(status, 3, 0);
                filters.Controls.Add(sortLabel, 4, 0);
                filters.Controls.Add(sort, 5, 0);
                root.Controls.Add(filters, 0, 2);

                ConfigureGrid();
                root.Controls.Add(grid, 0, 3);

                details.Name = "PlaylistPreviewDetails";
                details.Dock = DockStyle.Fill;
                details.Multiline = true;
                details.ReadOnly = true;
                details.ScrollBars = ScrollBars.Vertical;
                details.BorderStyle = BorderStyle.FixedSingle;
                details.BackColor = owner.theme.LogBackground;
                details.ForeColor = owner.theme.Ink;
                details.Font = owner.UiFont(9F);
                details.Margin = new Padding(0, 2, 0, 8);
                details.Text = "选择一首歌曲查看匹配依据。";
                root.Controls.Add(details, 0, 4);

                var footer = new FlowLayoutPanel {
                    Dock = DockStyle.Fill,
                    FlowDirection = FlowDirection.RightToLeft,
                    WrapContents = false,
                    AutoSize = true,
                    BackColor = owner.theme.Background,
                    Margin = new Padding(0)
                };
                var close = owner.Button("关闭", false);
                close.Name = "PlaylistPreviewClose";
                close.Size = new Size(86, 34);
                close.Click += delegate { Close(); };
                var includeAll = owner.Button("全部加入", false);
                includeAll.Name = "PlaylistPreviewIncludeAll";
                includeAll.Size = new Size(94, 34);
                includeAll.Click += delegate {
                    selection.RestoreAll();
                    Rebuild();
                    RaiseSelectionChanged();
                };
                var excludeAll = owner.Button("全部取消", false);
                excludeAll.Name = "PlaylistPreviewExcludeAll";
                excludeAll.Size = new Size(94, 34);
                excludeAll.Click += delegate {
                    selection.RemoveAll(MatchedSongs());
                    Rebuild();
                    RaiseSelectionChanged();
                };
                footer.Controls.Add(close);
                footer.Controls.Add(includeAll);
                footer.Controls.Add(excludeAll);
                root.Controls.Add(footer, 0, 5);
                tips.SetToolTip(includeAll, "恢复所有当前符合规则的歌曲。");
                tips.SetToolTip(excludeAll, "取消所有当前符合规则的歌曲；创建按钮会同步禁用。");

                searchTimer.Interval = 250;
                searchTimer.Tick += delegate { searchTimer.Stop(); Rebuild(); };
                search.TextChanged += delegate {
                    searchTimer.Stop();
                    searchTimer.Start();
                };
                status.SelectedIndexChanged += delegate { Rebuild(); };
                sort.SelectedIndexChanged += delegate { Rebuild(); };
            }

            private void ConfigureGrid()
            {
                grid.Name = "PlaylistPreviewGrid";
                grid.Dock = DockStyle.Fill;
                grid.Margin = new Padding(0, 0, 0, 2);
                grid.AllowUserToAddRows = false;
                grid.AllowUserToDeleteRows = false;
                grid.AllowUserToResizeRows = false;
                grid.AllowUserToOrderColumns = false;
                grid.RowHeadersVisible = false;
                grid.MultiSelect = false;
                grid.SelectionMode = DataGridViewSelectionMode.FullRowSelect;
                grid.EditMode = DataGridViewEditMode.EditOnEnter;
                grid.AutoSizeRowsMode = DataGridViewAutoSizeRowsMode.None;
                grid.RowTemplate.Height = 28;
                grid.ColumnHeadersHeight = 30;
                grid.ColumnHeadersHeightSizeMode = DataGridViewColumnHeadersHeightSizeMode.DisableResizing;
                grid.BackgroundColor = owner.theme.Panel;
                grid.BorderStyle = BorderStyle.FixedSingle;
                grid.GridColor = owner.theme.Border;
                grid.EnableHeadersVisualStyles = false;
                grid.ColumnHeadersDefaultCellStyle.BackColor = owner.theme.Secondary;
                grid.ColumnHeadersDefaultCellStyle.ForeColor = owner.theme.Ink;
                grid.ColumnHeadersDefaultCellStyle.SelectionBackColor = owner.theme.Secondary;
                grid.ColumnHeadersDefaultCellStyle.SelectionForeColor = owner.theme.Ink;
                grid.ColumnHeadersDefaultCellStyle.Font = owner.UiFont(9F, FontStyle.Bold);
                grid.DefaultCellStyle.BackColor = owner.theme.Panel;
                grid.DefaultCellStyle.ForeColor = owner.theme.Ink;
                grid.DefaultCellStyle.SelectionBackColor = owner.theme.Accent;
                grid.DefaultCellStyle.SelectionForeColor = Color.White;
                grid.DefaultCellStyle.Font = owner.UiFont(9F);
                grid.DefaultCellStyle.WrapMode = DataGridViewTriState.False;
                grid.ScrollBars = ScrollBars.Both;

                grid.Columns.Add(new DataGridViewCheckBoxColumn {
                    Name = "Include",
                    HeaderText = "加入",
                    Width = 48,
                    MinimumWidth = 44,
                    SortMode = DataGridViewColumnSortMode.NotSortable
                });
                grid.Columns.Add(TextColumn("Title", "歌曲", 200));
                grid.Columns.Add(TextColumn("Artist", "歌手", 140));
                grid.Columns.Add(TextColumn("Genres", "曲风", 150));
                grid.Columns.Add(TextColumn("Moods", "情绪", 150));
                grid.Columns.Add(TextColumn("HitTags", "命中筛选标签", 190));
                grid.Columns.Add(TextColumn("HitCount", "命中数", 62));
                var stateColumn = TextColumn("State", "状态", 108);
                stateColumn.AutoSizeMode = DataGridViewAutoSizeColumnMode.Fill;
                stateColumn.MinimumWidth = 90;
                grid.Columns.Add(stateColumn);

                grid.CurrentCellDirtyStateChanged += delegate {
                    if (grid.IsCurrentCellDirty) grid.CommitEdit(DataGridViewDataErrorContexts.Commit);
                };
                grid.CellBeginEdit += delegate(object sender, DataGridViewCellCancelEventArgs e) {
                    if (e.ColumnIndex < 0 || grid.Columns[e.ColumnIndex].Name != "Include") { e.Cancel = true; return; }
                    var evaluation = grid.Rows[e.RowIndex].Tag as TagPlaylistEvaluation;
                    if (evaluation == null || !evaluation.IsMatched) e.Cancel = true;
                };
                grid.CellValueChanged += delegate(object sender, DataGridViewCellEventArgs e) {
                    if (updatingGrid || e.RowIndex < 0 || e.ColumnIndex < 0) return;
                    if (grid.Columns[e.ColumnIndex].Name != "Include") return;
                    var row = grid.Rows[e.RowIndex];
                    var evaluation = row.Tag as TagPlaylistEvaluation;
                    if (evaluation == null) return;
                    bool included = Convert.ToBoolean(row.Cells["Include"].Value ?? false);
                    if (!evaluation.IsMatched)
                    {
                        updatingGrid = true;
                        row.Cells["Include"].Value = false;
                        updatingGrid = false;
                        return;
                    }
                    selection.SetRemoved(evaluation.Song.SongId, !included);
                    UpdateRowState(row, evaluation);
                    UpdateSummary();
                    UpdateDetails();
                    RaiseSelectionChanged();
                    if (status.SelectedIndex == (int)PreviewFilter.Included
                        || status.SelectedIndex == (int)PreviewFilter.Removed)
                    {
                        if (IsHandleCreated && !IsDisposed)
                            BeginInvoke((Action)delegate { if (!IsDisposed) Rebuild(); });
                    }
                };
                grid.SelectionChanged += delegate { UpdateDetails(); };
            }

            private static DataGridViewTextBoxColumn TextColumn(string name, string header, int width)
            {
                return new DataGridViewTextBoxColumn {
                    Name = name,
                    HeaderText = header,
                    Width = width,
                    MinimumWidth = 50,
                    ReadOnly = true,
                    SortMode = DataGridViewColumnSortMode.NotSortable
                };
            }

            private List<TagPlaylistSong> MatchedSongs()
            {
                return evaluations.Where(item => item.IsMatched).Select(item => item.Song).ToList();
            }

            private void Rebuild()
            {
                if (updatingGrid || IsDisposed) return;
                string selectedSongId = SelectedSongId();
                int firstRow = grid.Rows.Count == 0 || grid.FirstDisplayedScrollingRowIndex < 0
                    ? -1 : grid.FirstDisplayedScrollingRowIndex;
                var visible = VisibleEvaluations();
                updatingGrid = true;
                grid.SuspendLayout();
                try
                {
                    grid.Rows.Clear();
                    grid.RowCount = visible.Count;
                    for (int index = 0; index < visible.Count; index++)
                    {
                        var evaluation = visible[index];
                        var row = grid.Rows[index];
                        row.Tag = evaluation;
                        var song = evaluation.Song;
                        bool matched = evaluation.IsMatched;
                        bool removed = matched && selection.IsRemoved(song.SongId);
                        var include = row.Cells["Include"];
                        include.Value = matched && !removed;
                        include.ReadOnly = !matched;
                        row.Cells["Title"].Value = song.Title ?? "";
                        row.Cells["Artist"].Value = song.Artist ?? "";
                        row.Cells["Genres"].Value = JoinTags(song.Genres);
                        row.Cells["Moods"].Value = JoinTags(song.Moods);
                        row.Cells["HitTags"].Value = evaluation.HitTags.Length == 0
                            ? "无" : string.Join("、", evaluation.HitTags);
                        row.Cells["HitCount"].Value = evaluation.HitCount;
                        row.Cells["State"].Value = matched ? (removed ? "手动排除" : "已加入") : evaluation.StateText;
                        row.DefaultCellStyle.ForeColor = matched && !removed ? owner.theme.Ink : owner.theme.Muted;
                        include.Style.ForeColor = matched ? owner.theme.Ink : owner.theme.Muted;
                        row.Cells["State"].Style.ForeColor = !matched ? owner.theme.Muted
                            : removed ? owner.theme.Danger : owner.theme.Accent;
                        include.ToolTipText = matched
                            ? (removed ? "已手动取消；勾选可恢复到本次歌单。" : "取消后将不加入本次歌单。")
                            : "只有符合规则的歌曲可以加入。";
                        row.Cells["HitTags"].ToolTipText = evaluation.HitTags.Length == 0
                            ? "没有命中任何已勾选标签。" : string.Join("、", evaluation.HitTags);
                        row.Cells["State"].ToolTipText = TagPlaylistFilter.Explain(evaluation);
                    }
                }
                finally
                {
                    grid.ResumeLayout();
                    updatingGrid = false;
                }
                RestoreSelection(selectedSongId, firstRow);
                UpdateSummary();
                UpdateDetails();
            }

            private void RestoreSelection(string songId, int firstRow)
            {
                DataGridViewRow target = null;
                if (!string.IsNullOrEmpty(songId))
                {
                    foreach (DataGridViewRow row in grid.Rows)
                    {
                        var evaluation = row.Tag as TagPlaylistEvaluation;
                        if (evaluation != null && evaluation.Song != null && evaluation.Song.SongId == songId)
                        {
                            target = row;
                            break;
                        }
                    }
                }
                grid.ClearSelection();
                if (target != null)
                {
                    target.Selected = true;
                    try { grid.CurrentCell = target.Cells["Title"]; } catch (InvalidOperationException) { }
                }
                if (firstRow >= 0 && grid.Rows.Count > 0)
                {
                    try { grid.FirstDisplayedScrollingRowIndex = Math.Min(firstRow, grid.Rows.Count - 1); }
                    catch (InvalidOperationException) { }
                }
            }

            private void UpdateRowState(DataGridViewRow row, TagPlaylistEvaluation evaluation)
            {
                bool matched = evaluation.IsMatched;
                bool removed = matched && selection.IsRemoved(evaluation.Song.SongId);
                row.Cells["State"].Value = matched ? (removed ? "手动排除" : "已加入") : evaluation.StateText;
                row.DefaultCellStyle.ForeColor = matched && !removed ? owner.theme.Ink : owner.theme.Muted;
                row.Cells["State"].Style.ForeColor = !matched ? owner.theme.Muted
                    : removed ? owner.theme.Danger : owner.theme.Accent;
                row.Cells["Include"].ToolTipText = matched
                    ? (removed ? "已手动取消；勾选可恢复到本次歌单。" : "取消后将不加入本次歌单。")
                    : "只有符合规则的歌曲可以加入。";
            }

            private void UpdateSummary()
            {
                int matched = evaluations.Count(item => item.IsMatched);
                var matchedSongs = MatchedSongs();
                int removed = selection.RemovedCount(matchedSongs);
                int final = matched - removed;
                countsLabel.Text = selectedTagCount == 0
                    ? "尚未勾选筛选标签"
                    : string.Format("符合规则 {0} 首 · 手动排除 {1} 首 · 最终创建 {2} 首", matched, removed, final);
                countsLabel.ForeColor = matched > 0 && final == 0 ? owner.theme.Danger : owner.theme.Accent;
            }

            private void UpdateDetails()
            {
                var evaluation = grid.CurrentRow == null ? null : grid.CurrentRow.Tag as TagPlaylistEvaluation;
                if (evaluation == null)
                {
                    details.Text = "选择一首歌曲查看匹配依据。";
                    return;
                }
                string text = TagPlaylistFilter.Explain(evaluation);
                if (evaluation.IsMatched && selection.IsRemoved(evaluation.Song.SongId))
                    text += Environment.NewLine + "手动调整：本次歌单已取消加入；勾选“加入”可恢复。";
                else if (evaluation.IsMatched)
                    text += Environment.NewLine + "手动调整：当前会加入本次歌单；取消“加入”可排除。";
                details.Text = text;
            }

            private string SelectedSongId()
            {
                if (grid.CurrentRow == null) return null;
                var evaluation = grid.CurrentRow.Tag as TagPlaylistEvaluation;
                return evaluation == null || evaluation.Song == null ? null : evaluation.Song.SongId;
            }

            private List<TagPlaylistEvaluation> VisibleEvaluations()
            {
                string query = (search.Text ?? "").Trim();
                int filter = Math.Max(0, status.SelectedIndex);
                var pairs = new List<KeyValuePair<int, TagPlaylistEvaluation>>();
                for (int index = 0; index < evaluations.Count; index++)
                {
                    var evaluation = evaluations[index];
                    if (!MatchesFilter(evaluation, filter)) continue;
                    if (query.Length > 0 && !MatchesQuery(evaluation, query)) continue;
                    pairs.Add(new KeyValuePair<int, TagPlaylistEvaluation>(index, evaluation));
                }
                IEnumerable<KeyValuePair<int, TagPlaylistEvaluation>> ordered = pairs;
                switch ((PreviewSort)Math.Max(0, sort.SelectedIndex))
                {
                    case PreviewSort.HitsDescending:
                        ordered = pairs.OrderByDescending(pair => pair.Value.HitCount).ThenBy(pair => pair.Key);
                        break;
                    case PreviewSort.HitsAscending:
                        ordered = pairs.OrderBy(pair => pair.Value.HitCount).ThenBy(pair => pair.Key);
                        break;
                    case PreviewSort.Title:
                        ordered = pairs.OrderBy(pair => pair.Value.Song.Title ?? "", StringComparer.CurrentCultureIgnoreCase)
                            .ThenBy(pair => pair.Key);
                        break;
                    case PreviewSort.Artist:
                        ordered = pairs.OrderBy(pair => pair.Value.Song.Artist ?? "", StringComparer.CurrentCultureIgnoreCase)
                            .ThenBy(pair => pair.Key);
                        break;
                }
                return ordered.Select(pair => pair.Value).ToList();
            }

            private bool MatchesFilter(TagPlaylistEvaluation evaluation, int filter)
            {
                bool matched = evaluation.IsMatched;
                bool removed = matched && selection.IsRemoved(evaluation.Song.SongId);
                switch ((PreviewFilter)filter)
                {
                    case PreviewFilter.All: return true;
                    case PreviewFilter.Included: return matched && !removed;
                    case PreviewFilter.Removed: return matched && removed;
                    case PreviewFilter.BelowMinimum: return evaluation.State == TagPlaylistMatchState.BelowMinimum;
                    case PreviewFilter.HardExcluded: return evaluation.State == TagPlaylistMatchState.HardExcluded;
                    case PreviewFilter.NoTags: return evaluation.State == TagPlaylistMatchState.NoTags;
                    case PreviewFilter.NoSelection: return evaluation.State == TagPlaylistMatchState.NoSelection;
                    default: return matched;
                }
            }

            private static bool MatchesQuery(TagPlaylistEvaluation evaluation, string query)
            {
                var song = evaluation.Song;
                return Contains(song.Title, query) || Contains(song.Artist, query)
                    || Contains(song.Genres, query) || Contains(song.Moods, query)
                    || Contains(evaluation.HitTags, query) || Contains(evaluation.StateText, query);
            }

            private static bool Contains(string value, string query)
            {
                return !string.IsNullOrEmpty(value) && value.IndexOf(query, StringComparison.CurrentCultureIgnoreCase) >= 0;
            }

            private static bool Contains(string[] values, string query)
            {
                if (values == null) return false;
                foreach (var value in values)
                    if (Contains(value, query)) return true;
                return false;
            }

            private static string JoinTags(string[] tags)
            {
                if (tags == null) return "无";
                var values = new List<string>();
                var seen = new HashSet<string>(StringComparer.Ordinal);
                foreach (var tag in tags)
                    if (!string.IsNullOrWhiteSpace(tag) && seen.Add(tag)) values.Add(tag);
                return values.Count == 0 ? "无" : string.Join("、", values);
            }

            private void RaiseSelectionChanged()
            {
                var handler = SelectionChanged;
                if (handler != null) handler(this, EventArgs.Empty);
            }

            protected override void OnKeyDown(KeyEventArgs e)
            {
                if (e.KeyCode == Keys.Escape)
                {
                    e.SuppressKeyPress = true;
                    Close();
                    return;
                }
                base.OnKeyDown(e);
            }

            protected override void OnFormClosed(FormClosedEventArgs e)
            {
                searchTimer.Stop();
                searchTimer.Dispose();
                tips.Dispose();
                base.OnFormClosed(e);
            }
        }
    }
}
