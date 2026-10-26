using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using System.Web.Script.Serialization;
using System.Windows.Forms;

namespace NeteaseToolbox
{
    internal sealed partial class ToolboxForm
    {
        private readonly Button tagPlaylistButton = new Button();
        private readonly List<TagPlaylistSong> tagPlaylistBuffer = new List<TagPlaylistSong>();
        private int tagPlaylistLiked;
        private int tagPlaylistTagged;
        private bool buildingTagPlaylist;

        private void ConfigureTagPlaylistButton(Control toolbar)
        {
            tagPlaylistButton.Name = "TagPlaylistButton";
            tagPlaylistButton.Text = "筛选生成歌单";
            tagPlaylistButton.Size = new Size(126, 40);
            tagPlaylistButton.Location = new Point(4, 7);
            tagPlaylistButton.Enabled = File.Exists(genreScriptPath);
            StyleButton(tagPlaylistButton, ButtonRole.Secondary);
            tagPlaylistButton.Click += async delegate { await GenerateTagPlaylist(); };
            tips.SetToolTip(tagPlaylistButton, "按情绪和曲风筛选红心歌曲，确认后创建新的网易云歌单。");
            toolbar.Controls.Add(tagPlaylistButton);
            toolbar.Resize += delegate {
                tagPlaylistButton.Left = toolbar.ClientSize.Width - tagPlaylistButton.Width;
                tagPlaylistButton.Top = Math.Max(0, (toolbar.ClientSize.Height - tagPlaylistButton.Height) / 2);
            };
        }

        private void CaptureTagPlaylistMeta(string json)
        {
            var data = new JavaScriptSerializer().Deserialize<Dictionary<string, object>>(json);
            tagPlaylistLiked = data != null && data.ContainsKey("liked") ? Convert.ToInt32(data["liked"]) : 0;
            tagPlaylistTagged = data != null && data.ContainsKey("tagged") ? Convert.ToInt32(data["tagged"]) : 0;
        }

        private void CaptureTagPlaylistSong(string json)
        {
            var song = new JavaScriptSerializer().Deserialize<TagPlaylistSong>(json);
            if (song != null && !string.IsNullOrEmpty(song.SongId)) tagPlaylistBuffer.Add(song);
        }

        private async Task GenerateTagPlaylist()
        {
            if (buildingTagPlaylist || analyzingGenres || converting || loadingGenreResults) return;
            if (!File.Exists(genreScriptPath)) { ReportTask("标签歌单", "失败", "找不到曲风分析程序。"); return; }
            buildingTagPlaylist = true;
            tagPlaylistButton.Enabled = false;
            analyzeGenresButton.Enabled = false;
            tagPlaylistButton.Text = "正在读取…";
            ReportTask("标签歌单", "开始", "正在读取红心歌单与曲风/情绪标签。");
            try
            {
                var snapshot = await Task.Run(() => LoadTagPlaylistSnapshot());
                if (snapshot.Songs.Count == 0)
                {
                    ReportTask("标签歌单", "失败", "没有读取到红心歌曲，请先登录网易云。");
                    return;
                }
                if (!snapshot.Songs.Any(song => song.HasTags))
                {
                    ReportTask("标签歌单", "失败", "红心歌曲还没有标签，请先点击“分析曲风”。");
                    return;
                }
                var selection = ShowTagPlaylistDialog(snapshot);
                if (selection == null)
                {
                    ReportTask("标签歌单", "取消", "未创建歌单。");
                    return;
                }
                ReportTask("标签歌单", "开始", "正在创建《" + selection.Name + "》。");
                try
                {
                    var created = await Task.Run(() => TagPlaylistApi.Create(selection.Name, selection.IsPrivate, selection.SongIds));
                    ReportTask("标签歌单", "完成", "已创建《" + selection.Name + "》，加入 " + created.Added + " 首歌曲。");
                }
                catch (TagPlaylistPartialException ex)
                {
                    ReportTask("标签歌单", "部分失败", "已创建歌单（ID " + ex.PlaylistId + "），成功加入 "
                        + ex.Added + " 首，失败 " + ex.Failed + " 首未加入；" + ex.Message);
                }
                catch (Exception ex) { ReportTask("标签歌单", "失败", ex.Message); }
            }
            catch (Exception ex) { ReportTask("标签歌单", "失败", ex.Message); }
            finally
            {
                buildingTagPlaylist = false;
                tagPlaylistButton.Text = "筛选生成歌单";
                tagPlaylistButton.Enabled = File.Exists(genreScriptPath) && !analyzingGenres && !converting && !loadingGenreResults;
                analyzeGenresButton.Enabled = genreSongs.Items.Count > 0 && File.Exists(genreScriptPath);
            }
        }

        private TagPlaylistSnapshot LoadTagPlaylistSnapshot()
        {
            tagPlaylistBuffer.Clear();
            tagPlaylistLiked = 0;
            tagPlaylistTagged = 0;
            var result = RunGenreWorker(new[] { "--playlist-candidates" });
            if (result.ExitCode != 0 || result.ErrorCount > 0)
                throw new InvalidOperationException(result.FirstError ?? "读取红心歌单失败（退出码 " + result.ExitCode + "）。");
            if (tagPlaylistLiked <= 0 || tagPlaylistBuffer.Count != tagPlaylistLiked)
                throw new InvalidOperationException("红心歌单读取不完整（应读取 " + tagPlaylistLiked + " 首，实际 " + tagPlaylistBuffer.Count + " 首）。");
            return new TagPlaylistSnapshot {
                Songs = tagPlaylistBuffer.ToList(),
                Liked = tagPlaylistLiked,
                Tagged = tagPlaylistTagged
            };
        }

        private sealed class TagPlaylistSnapshot
        {
            internal List<TagPlaylistSong> Songs = new List<TagPlaylistSong>();
            internal int Liked;
            internal int Tagged;
        }

        private sealed class TagPlaylistDialogResult
        {
            internal string Name;
            internal bool IsPrivate;
            internal List<string> SongIds;
        }

        private sealed class TagPlaylistGroup
        {
            internal GroupBox Box;
            internal TagSelectionList List;
            internal Button All;
            internal Button None;
            internal bool Updating;
        }

        private sealed class TagSelectionList : ListBox
        {
            private enum TagState { None, Included, Excluded }

            private readonly Dictionary<TagPlaylistCount, TagState> states = new Dictionary<TagPlaylistCount, TagState>();
            private readonly Color inkColor;
            private readonly Color accentColor;
            private readonly Color dangerColor;
            private readonly Color borderColor;

            internal TagSelectionList(Color panelColor, Color inkValue, Color accentValue,
                Color dangerValue, Color borderValue)
            {
                BackColor = panelColor;
                ForeColor = inkValue;
                inkColor = inkValue;
                accentColor = accentValue;
                dangerColor = dangerValue;
                borderColor = borderValue;
                DrawMode = DrawMode.OwnerDrawFixed;
                BorderStyle = BorderStyle.None;
                IntegralHeight = false;
                HorizontalScrollbar = true;
                SelectionMode = SelectionMode.One;
                ItemHeight = Math.Max(24, Font.Height + 10);
                SetStyle(ControlStyles.OptimizedDoubleBuffer, true);
            }

            internal event EventHandler TagStateChanged;

            internal List<string> IncludedTags
            {
                get { return TagsWithState(TagState.Included); }
            }

            internal List<string> ExcludedTags
            {
                get { return TagsWithState(TagState.Excluded); }
            }

            internal bool IsIncluded(int index)
            {
                return StateOf(index) == TagState.Included;
            }

            internal bool IsExcluded(int index)
            {
                return StateOf(index) == TagState.Excluded;
            }

            internal void SetIncluded(int index, bool value)
            {
                if (value) SetState(index, TagState.Included);
                else if (StateOf(index) == TagState.Included) SetState(index, TagState.None);
            }

            internal void SetExcluded(int index, bool value)
            {
                if (value) SetState(index, TagState.Excluded);
                else if (StateOf(index) == TagState.Excluded) SetState(index, TagState.None);
            }

            internal void ToggleIncluded(int index)
            {
                SetState(index, StateOf(index) == TagState.Included ? TagState.None : TagState.Included);
            }

            internal void ToggleExcluded(int index)
            {
                SetState(index, StateOf(index) == TagState.Excluded ? TagState.None : TagState.Excluded);
            }

            internal void SetAllIncluded()
            {
                states.Clear();
                foreach (TagPlaylistCount item in Items) states[item] = TagState.Included;
                Invalidate();
                NotifyChanged();
            }

            internal void ClearAll()
            {
                if (states.Count == 0) return;
                states.Clear();
                Invalidate();
                NotifyChanged();
            }

            internal void ApplySelection(IEnumerable<string> included, IEnumerable<string> excluded)
            {
                var includedSet = new HashSet<string>((included ?? Enumerable.Empty<string>())
                    .Where(value => !string.IsNullOrWhiteSpace(value)), StringComparer.Ordinal);
                var excludedSet = new HashSet<string>((excluded ?? Enumerable.Empty<string>())
                    .Where(value => !string.IsNullOrWhiteSpace(value)), StringComparer.Ordinal);
                states.Clear();
                foreach (TagPlaylistCount item in Items)
                {
                    if (excludedSet.Contains(item.Name)) states[item] = TagState.Excluded;
                    else if (includedSet.Contains(item.Name)) states[item] = TagState.Included;
                }
                Invalidate();
                NotifyChanged();
            }

            private List<string> TagsWithState(TagState state)
            {
                var result = new List<string>();
                for (int index = 0; index < Items.Count; index++)
                {
                    if (StateOf(index) != state) continue;
                    var item = Items[index] as TagPlaylistCount;
                    if (item != null) result.Add(item.Name);
                }
                return result;
            }

            private TagState StateOf(int index)
            {
                if (index < 0 || index >= Items.Count) return TagState.None;
                var item = Items[index] as TagPlaylistCount;
                if (item == null) return TagState.None;
                TagState state;
                return states.TryGetValue(item, out state) ? state : TagState.None;
            }

            private void SetState(int index, TagState state)
            {
                if (index < 0 || index >= Items.Count) return;
                var item = Items[index] as TagPlaylistCount;
                if (item == null || StateOf(index) == state) return;
                if (state == TagState.None) states.Remove(item);
                else states[item] = state;
                InvalidateItem(index);
                NotifyChanged();
            }

            private void InvalidateItem(int index)
            {
                if (!IsHandleCreated)
                {
                    Invalidate();
                    return;
                }
                var bounds = GetItemRectangle(index);
                if (bounds.Height > 0) Invalidate(bounds);
                else Invalidate();
            }

            private void NotifyChanged()
            {
                var handler = TagStateChanged;
                if (handler != null) handler(this, EventArgs.Empty);
            }

            protected override void OnFontChanged(EventArgs e)
            {
                base.OnFontChanged(e);
                ItemHeight = Math.Max(24, Font.Height + 10);
            }

            protected override void OnMouseDown(MouseEventArgs e)
            {
                base.OnMouseDown(e);
                int index = IndexFromPoint(e.Location);
                if (index < 0 || index >= Items.Count) return;
                if (e.Button == MouseButtons.Left) ToggleIncluded(index);
                else if (e.Button == MouseButtons.Right) ToggleExcluded(index);
            }

            protected override void OnKeyDown(KeyEventArgs e)
            {
                if (e.KeyCode == Keys.Space)
                {
                    ToggleIncluded(SelectedIndex);
                    e.SuppressKeyPress = true;
                }
                else if (e.KeyCode == Keys.X)
                {
                    ToggleExcluded(SelectedIndex);
                    e.SuppressKeyPress = true;
                }
                base.OnKeyDown(e);
            }

            protected override void OnDrawItem(DrawItemEventArgs e)
            {
                if (e.Index < 0 || e.Index >= Items.Count) return;
                var item = Items[e.Index] as TagPlaylistCount;
                if (item == null) return;

                var state = StateOf(e.Index);
                bool selected = (e.State & DrawItemState.Selected) == DrawItemState.Selected;
                using (var background = new SolidBrush(selected ? Color.FromArgb(28, accentColor) : BackColor))
                    e.Graphics.FillRectangle(background, e.Bounds);

                const int boxSize = 16;
                int boxX = e.Bounds.Left + 5;
                int boxY = e.Bounds.Top + (e.Bounds.Height - boxSize) / 2;
                if (state == TagState.Included)
                {
                    using (var fill = new SolidBrush(accentColor)) e.Graphics.FillRectangle(fill, boxX, boxY, boxSize, boxSize);
                }
                else if (state == TagState.Excluded)
                {
                    using (var fill = new SolidBrush(dangerColor)) e.Graphics.FillRectangle(fill, boxX, boxY, boxSize, boxSize);
                }
                using (var border = new Pen(state == TagState.None ? borderColor : state == TagState.Included ? accentColor : dangerColor,
                    state == TagState.None ? 1F : 1.5F))
                    e.Graphics.DrawRectangle(border, boxX, boxY, boxSize, boxSize);

                using (var mark = new Pen(Color.White, 2F))
                {
                    if (state == TagState.Included)
                    {
                        e.Graphics.DrawLines(mark, new[] {
                            new PointF(boxX + 4, boxY + 8), new PointF(boxX + 7, boxY + 11), new PointF(boxX + 12, boxY + 5)
                        });
                    }
                    else if (state == TagState.Excluded)
                    {
                        e.Graphics.DrawLine(mark, boxX + 5, boxY + 5, boxX + 11, boxY + 11);
                        e.Graphics.DrawLine(mark, boxX + 11, boxY + 5, boxX + 5, boxY + 11);
                    }
                }

                var textBounds = new Rectangle(boxX + boxSize + 7, e.Bounds.Top,
                    Math.Max(10, e.Bounds.Right - boxX - boxSize - 7), e.Bounds.Height);
                TextRenderer.DrawText(e.Graphics, item.ToString(), e.Font, textBounds,
                    state == TagState.Excluded ? dangerColor : inkColor,
                    TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis | TextFormatFlags.NoPrefix);
                if ((e.State & DrawItemState.Focus) == DrawItemState.Focus) e.DrawFocusRectangle();
            }
        }

        private sealed class TagMatchChart : Control
        {
            private int[] counts = new int[0];
            private int threshold;
            private readonly Color accentColor;
            private readonly Color mutedColor;
            private readonly Color inkColor;
            private readonly Color borderColor;

            internal TagMatchChart(Color panelColor, Color inkValue, Color mutedValue, Color accentValue, Color borderValue)
            {
                BackColor = panelColor;
                ForeColor = inkValue;
                accentColor = accentValue;
                mutedColor = mutedValue;
                inkColor = inkValue;
                borderColor = borderValue;
                DoubleBuffered = true;
                SetStyle(ControlStyles.OptimizedDoubleBuffer | ControlStyles.AllPaintingInWmPaint
                    | ControlStyles.UserPaint | ControlStyles.ResizeRedraw, true);
            }

            internal void SetData(int[] values, int minimumHits)
            {
                counts = values == null ? new int[0] : (int[])values.Clone();
                threshold = minimumHits;
                Invalidate();
            }

            protected override void OnPaint(PaintEventArgs e)
            {
                base.OnPaint(e);
                var g = e.Graphics;
                g.SmoothingMode = SmoothingMode.AntiAlias;
                g.TextRenderingHint = System.Drawing.Text.TextRenderingHint.ClearTypeGridFit;
                using (var background = new SolidBrush(BackColor)) g.FillRectangle(background, ClientRectangle);
                using (var titleFont = new Font(Font.FontFamily, 9F))
                using (var smallFont = new Font(Font.FontFamily, 8F))
                using (var titleBrush = new SolidBrush(mutedColor))
                using (var textBrush = new SolidBrush(inkColor))
                using (var mutedBrush = new SolidBrush(mutedColor))
                using (var center = new StringFormat { Alignment = StringAlignment.Center, LineAlignment = StringAlignment.Center })
                using (var topCenter = new StringFormat { Alignment = StringAlignment.Center, LineAlignment = StringAlignment.Near })
                using (var farCenter = new StringFormat { Alignment = StringAlignment.Far, LineAlignment = StringAlignment.Center })
                {
                    g.DrawString("至少命中 k 个标签的歌曲数（首）", titleFont, titleBrush, new PointF(2, 2));
                    if (counts.Length == 0)
                    {
                        g.DrawString("勾选情绪或曲风标签后显示匹配数量", titleFont, mutedBrush, ClientRectangle, center);
                        return;
                    }

                    const int left = 44;
                    const int top = 40;
                    const int right = 8;
                    const int bottom = 28;
                    var plot = new Rectangle(left, top, Math.Max(10, ClientSize.Width - left - right),
                        Math.Max(10, ClientSize.Height - top - bottom));
                    int maximum = Math.Max(1, counts.Max());
                    using (var gridPen = new Pen(Color.FromArgb(70, borderColor)))
                    {
                        for (int line = 0; line <= 2; line++)
                        {
                            int y = plot.Bottom - (plot.Height * line / 2);
                            g.DrawLine(gridPen, plot.Left, y, plot.Right, y);
                            g.DrawString((maximum * line / 2).ToString(), smallFont, mutedBrush,
                                new Rectangle(0, y - 8, left - 6, 16), farCenter);
                        }
                    }

                    int count = counts.Length;
                    float slot = plot.Width / (float)count;
                    float barWidth = Math.Min(54F, Math.Max(6F, slot * 0.62F));
                    int labelStep = Math.Max(1, (int)Math.Ceiling(count / 8F));
                    for (int index = 0; index < count; index++)
                    {
                        int value = counts[index];
                        float height = plot.Height * value / (float)maximum;
                        if (value > 0 && height < 1F) height = 1F;
                        float x = plot.Left + slot * index + (slot - barWidth) / 2F;
                        var bar = new RectangleF(x, plot.Bottom - height, barWidth, height);
                        bool active = index + 1 == threshold;
                        using (var barBrush = new SolidBrush(active ? accentColor : Color.FromArgb(95, accentColor)))
                            g.FillRectangle(barBrush, bar);
                        if (active)
                        {
                            using (var pen = new Pen(accentColor, 2F)) g.DrawRectangle(pen, bar.X, bar.Y, bar.Width, bar.Height);
                        }
                        if (active || slot >= 28F)
                        {
                            g.DrawString(value.ToString(), smallFont, active ? textBrush : mutedBrush,
                                new RectangleF(x - 10, bar.Y - 18, barWidth + 20, 16), topCenter);
                        }
                        bool showLabel = index == 0 || index == count - 1 || active || index % labelStep == 0;
                        if (showLabel)
                        {
                            g.DrawString("≥" + (index + 1), smallFont, active ? textBrush : mutedBrush,
                                new RectangleF(plot.Left + slot * index, plot.Bottom + 6, slot, 16), topCenter);
                        }
                    }
                }
            }
        }

        private TagPlaylistGroup BuildTagPlaylistGroup(string title, List<TagPlaylistCount> counts, string name)
        {
            var group = new GroupBox {
                Text = title,
                Dock = DockStyle.Fill,
                ForeColor = ink,
                BackColor = panel,
                Padding = new Padding(10, 8, 10, 10),
                Margin = new Padding(0, 0, 6, 0)
            };
            var layout = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 2 };
            layout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            layout.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
            layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 38));
            var list = new TagSelectionList(panel, ink, accent, theme.Danger, theme.Border) {
                Name = name,
                Dock = DockStyle.Fill,
                Font = UiFont(9.5F),
                AccessibleName = title + "标签",
                AccessibleDescription = "左键勾选，右键标记 × 强制排除包含该标签的歌曲。"
            };
            foreach (var count in counts) list.Items.Add(count);
            layout.Controls.Add(list, 0, 0);
            var actions = new FlowLayoutPanel {
                Dock = DockStyle.Fill,
                FlowDirection = FlowDirection.LeftToRight,
                WrapContents = false,
                BackColor = panel,
                Padding = new Padding(0, 4, 0, 0)
            };
            var all = Button("全选", false);
            all.Name = name + "All";
            all.Size = new Size(70, 30);
            StyleButton(all, ButtonRole.Ghost);
            var none = Button("清空", false);
            none.Name = name + "None";
            none.Size = new Size(70, 30);
            StyleButton(none, ButtonRole.Ghost);
            actions.Controls.Add(all);
            actions.Controls.Add(none);
            layout.Controls.Add(actions, 0, 1);
            group.Controls.Add(layout);
            tips.SetToolTip(list, "左键勾选；右键标记 ×，强制排除包含该标签的歌曲。再次右键可取消排除。");
            return new TagPlaylistGroup { Box = group, List = list, All = all, None = none };
        }

        private static void SetAllTagChecks(TagPlaylistGroup group, bool value, Action changed)
        {
            group.Updating = true;
            try
            {
                if (value) group.List.SetAllIncluded();
                else group.List.ClearAll();
            }
            finally { group.Updating = false; }
            changed();
        }

        private TagPlaylistDialogResult ShowTagPlaylistDialog(TagPlaylistSnapshot snapshot)
        {
            using (var dialog = new Form {
                Text = "按标签生成红心歌单",
                Size = new Size(880, 820),
                MinimumSize = new Size(780, 740),
                StartPosition = FormStartPosition.CenterParent,
                BackColor = panel,
                ForeColor = ink,
                Font = UiFont(9F),
                AutoScaleMode = AutoScaleMode.Dpi,
                AutoScaleDimensions = new SizeF(96F, 96F)
            })
            {
                TagPlaylistDialogResult result = null;
                var root = new TableLayoutPanel {
                    Dock = DockStyle.Fill,
                    Padding = new Padding(18, 14, 18, 14),
                    ColumnCount = 1,
                    RowCount = 9,
                    BackColor = panel
                };
                root.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
                root.RowStyles.Add(new RowStyle(SizeType.AutoSize));
                root.RowStyles.Add(new RowStyle(SizeType.AutoSize));
                root.RowStyles.Add(new RowStyle(SizeType.AutoSize));
                root.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
                root.RowStyles.Add(new RowStyle(SizeType.Absolute, 132));
                root.RowStyles.Add(new RowStyle(SizeType.AutoSize));
                root.RowStyles.Add(new RowStyle(SizeType.AutoSize));
                root.RowStyles.Add(new RowStyle(SizeType.AutoSize));
                root.RowStyles.Add(new RowStyle(SizeType.AutoSize));
                dialog.Controls.Add(root);

                var title = Label("按标签生成红心歌单", 13, true, ink);
                title.Margin = new Padding(0, 0, 0, 4);
                root.Controls.Add(title, 0, 0);
                var description = Label("左键勾选标签；右键标记 ×，强制排除包含该标签的歌曲。默认按勾选标签数的一半向上取整作为最低命中数；可先预览匹配歌曲并手动调整，再确认创建。", 9, false, muted);
                description.MaximumSize = new Size(800, 0);
                description.Margin = new Padding(0, 0, 0, 4);
                root.Controls.Add(description, 0, 1);

                var presetBar = new TableLayoutPanel {
                    Dock = DockStyle.Fill,
                    AutoSize = true,
                    ColumnCount = 4,
                    RowCount = 1,
                    Margin = new Padding(0, 4, 0, 6),
                    BackColor = panel
                };
                presetBar.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
                presetBar.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 176));
                presetBar.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
                presetBar.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
                presetBar.RowStyles.Add(new RowStyle(SizeType.AutoSize));
                var presetLabel = Label("规则预设", 9, false, muted);
                presetLabel.Margin = new Padding(0, 8, 8, 0);
                presetBar.Controls.Add(presetLabel, 0, 0);
                var presetCombo = new ComboBox {
                    Name = "TagPlaylistPresetCombo",
                    Dock = DockStyle.Fill,
                    DropDownStyle = ComboBoxStyle.DropDownList,
                    Font = UiFont(9F),
                    Margin = new Padding(0, 3, 8, 0)
                };
                presetCombo.AccessibleName = "规则预设列表";
                presetBar.Controls.Add(presetCombo, 1, 0);
                var presetActions = new FlowLayoutPanel {
                    AutoSize = true,
                    Dock = DockStyle.Fill,
                    FlowDirection = FlowDirection.LeftToRight,
                    WrapContents = false,
                    BackColor = panel,
                    Margin = new Padding(0)
                };
                var presetLoad = Button("载入", false);
                presetLoad.Name = "TagPlaylistPresetLoad";
                presetLoad.Size = new Size(52, 30);
                presetLoad.Margin = new Padding(0, 2, 4, 0);
                StyleButton(presetLoad, ButtonRole.Ghost);
                var presetSave = Button("保存", false);
                presetSave.Name = "TagPlaylistPresetSave";
                presetSave.Size = new Size(52, 30);
                presetSave.Margin = new Padding(0, 2, 4, 0);
                StyleButton(presetSave, ButtonRole.Ghost);
                var presetRename = Button("重命名", false);
                presetRename.Name = "TagPlaylistPresetRename";
                presetRename.Size = new Size(66, 30);
                presetRename.Margin = new Padding(0, 2, 4, 0);
                StyleButton(presetRename, ButtonRole.Ghost);
                var presetDelete = Button("删除", false);
                presetDelete.Name = "TagPlaylistPresetDelete";
                presetDelete.Size = new Size(52, 30);
                presetDelete.Margin = new Padding(0, 2, 4, 0);
                StyleButton(presetDelete, ButtonRole.Ghost);
                presetActions.Controls.Add(presetLoad);
                presetActions.Controls.Add(presetSave);
                presetActions.Controls.Add(presetRename);
                presetActions.Controls.Add(presetDelete);
                presetBar.Controls.Add(presetActions, 2, 0);
                var presetHint = Label("", 9, false, muted);
                presetHint.Name = "TagPlaylistPresetHint";
                presetHint.AutoSize = false;
                presetHint.Dock = DockStyle.Fill;
                presetHint.AutoEllipsis = true;
                presetHint.TextAlign = ContentAlignment.MiddleLeft;
                presetHint.Margin = new Padding(4, 6, 0, 0);
                presetBar.Controls.Add(presetHint, 3, 0);
                root.Controls.Add(presetBar, 0, 2);
                tips.SetToolTip(presetCombo, "规则预设只保存筛选条件和默认歌单名，不保存歌曲 ID 或手动取消的歌曲。");
                tips.SetToolTip(presetLoad, "载入所选预设；当前手动取消会重置。");
                tips.SetToolTip(presetSave, "把当前标签、硬排除、最低命中数和歌单名保存为预设。");
                tips.SetToolTip(presetRename, "重命名所选预设，不改变筛选条件。");
                tips.SetToolTip(presetDelete, "删除所选预设；不会修改任何歌单或歌曲标签。");

                var moodGroup = BuildTagPlaylistGroup("情绪（左键勾选 / 右键×）", TagPlaylistFilter.MoodCounts(snapshot.Songs), "MoodTagList");
                var genreGroup = BuildTagPlaylistGroup("曲风（左键勾选 / 右键×）", TagPlaylistFilter.GenreCounts(snapshot.Songs), "GenreTagList");
                genreGroup.Box.Margin = new Padding(6, 0, 0, 0);
                var tagArea = new TableLayoutPanel {
                    Dock = DockStyle.Fill,
                    ColumnCount = 2,
                    RowCount = 1,
                    Margin = new Padding(0, 8, 0, 8),
                    BackColor = panel
                };
                tagArea.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 50));
                tagArea.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 50));
                tagArea.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
                tagArea.Controls.Add(moodGroup.Box, 0, 0);
                tagArea.Controls.Add(genreGroup.Box, 1, 0);
                root.Controls.Add(tagArea, 0, 3);

                var chart = new TagMatchChart(panel, ink, muted, accent, theme.Border) {
                    Name = "TagMatchChart",
                    Dock = DockStyle.Fill,
                    Font = UiFont(9F),
                    Margin = new Padding(0, 4, 0, 6),
                    TabStop = false
                };
                root.Controls.Add(chart, 0, 4);

                var summary = Label("", 9, false, muted);
                summary.Name = "TagPlaylistSummary";
                summary.AutoSize = true;
                summary.MaximumSize = new Size(800, 0);
                summary.Margin = new Padding(0, 0, 0, 2);
                root.Controls.Add(summary, 0, 5);

                var finalCounts = Label("", 9, true, accent);
                finalCounts.Name = "TagPlaylistFinalCounts";
                finalCounts.AutoSize = true;
                finalCounts.MaximumSize = new Size(800, 0);
                finalCounts.Margin = new Padding(0, 0, 0, 6);
                root.Controls.Add(finalCounts, 0, 6);
                tips.SetToolTip(finalCounts, "符合规则 = 当前标签和最低命中数匹配的歌曲；手动排除 = 其中被你取消的歌曲；最终创建 = 实际提交的歌曲。");

                var options = new TableLayoutPanel {
                    Dock = DockStyle.Fill,
                    AutoSize = true,
                    ColumnCount = 2,
                    RowCount = 3,
                    Margin = new Padding(0, 4, 0, 4),
                    BackColor = panel
                };
                options.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 90));
                options.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
                options.RowStyles.Add(new RowStyle(SizeType.AutoSize));
                options.RowStyles.Add(new RowStyle(SizeType.AutoSize));
                options.RowStyles.Add(new RowStyle(SizeType.AutoSize));
                var matchLabel = Label("最低命中", 9, false, muted);
                matchLabel.Margin = new Padding(0, 8, 0, 0);
                options.Controls.Add(matchLabel, 0, 0);
                var matchControls = new FlowLayoutPanel {
                    Dock = DockStyle.Fill,
                    AutoSize = true,
                    FlowDirection = FlowDirection.LeftToRight,
                    WrapContents = false,
                    BackColor = panel,
                    Margin = new Padding(0, 0, 0, 4)
                };
                var matchAuto = new CheckBox {
                    Name = "TagMatchAuto",
                    Text = "按半数自动",
                    AutoSize = true,
                    Checked = true,
                    BackColor = panel,
                    ForeColor = ink,
                    Margin = new Padding(0, 4, 12, 0)
                };
                var matchThreshold = new NumericUpDown {
                    Name = "TagMatchThreshold",
                    Minimum = 1,
                    Maximum = 1,
                    Value = 1,
                    Width = 58,
                    Enabled = false,
                    TextAlign = HorizontalAlignment.Center,
                    Font = UiFont(10F),
                    Margin = new Padding(0, 2, 4, 0)
                };
                var matchSuffix = Label("个标签", 9, false, muted);
                matchSuffix.Margin = new Padding(0, 7, 6, 0);
                var matchHint = Label("（单数向上取整）", 9, false, muted);
                matchHint.Name = "TagMatchHint";
                matchHint.Margin = new Padding(0, 7, 0, 0);
                matchControls.Controls.Add(matchAuto);
                matchControls.Controls.Add(matchThreshold);
                matchControls.Controls.Add(matchSuffix);
                matchControls.Controls.Add(matchHint);
                options.Controls.Add(matchControls, 1, 0);
                tips.SetToolTip(matchAuto, "默认取选中标签数的一半，单数向上取整；取消勾选后可手动设置。");
                tips.SetToolTip(matchThreshold, "一首歌至少命中多少个已选标签才会加入歌单。");
                tips.SetToolTip(chart, "柱状图为至少命中 1、2、… 个已勾选标签的歌曲数量；带 × 的硬排除标签已剔除。");
                var nameLabel = Label("歌单名称", 9, false, muted);
                nameLabel.Margin = new Padding(0, 4, 0, 0);
                options.Controls.Add(nameLabel, 0, 1);
                var nameBox = new TextBox {
                    Name = "TagPlaylistName",
                    Dock = DockStyle.Fill,
                    MaxLength = 40,
                    Font = UiFont(10F),
                    Margin = new Padding(0, 2, 0, 2)
                };
                options.Controls.Add(nameBox, 1, 1);
                var privacy = new CheckBox {
                    Name = "TagPlaylistPrivate",
                    Text = "设为私密歌单（仅自己可见）",
                    Checked = true,
                    AutoSize = true,
                    BackColor = panel,
                    ForeColor = ink,
                    Margin = new Padding(0, 4, 0, 0)
                };
                options.Controls.Add(privacy, 1, 2);
                root.Controls.Add(options, 0, 7);

                var footer = new FlowLayoutPanel {
                    Dock = DockStyle.Fill,
                    FlowDirection = FlowDirection.RightToLeft,
                    WrapContents = false,
                    AutoSize = true,
                    BackColor = panel,
                    Margin = new Padding(0, 4, 0, 0)
                };
                var create = Button("创建歌单", true);
                create.Name = "TagPlaylistCreate";
                create.Size = new Size(126, 40);
                var previewButton = Button("预览匹配歌曲", false);
                previewButton.Name = "TagPlaylistPreview";
                previewButton.Size = new Size(124, 40);
                previewButton.Margin = new Padding(0, 0, 8, 0);
                var cancel = Button("取消", false);
                cancel.Name = "TagPlaylistCancel";
                cancel.Size = new Size(96, 40);
                cancel.DialogResult = DialogResult.Cancel;
                footer.Controls.Add(create);
                footer.Controls.Add(previewButton);
                footer.Controls.Add(cancel);
                root.Controls.Add(footer, 0, 8);
                tips.SetToolTip(previewButton, "查看匹配歌曲、每首歌的匹配依据，并手动取消或恢复；关闭预览不会清空当前筛选。");

                bool nameTouched = false;
                bool suppressName = false;
                bool updatingThreshold = false;
                var selection = new TagPlaylistSelectionState();
                var latestEvaluations = new List<TagPlaylistEvaluation>();
                var latestMatches = new List<TagPlaylistSong>();
                int latestMinimum = 1;
                int latestTagCount = 0;
                int? presetMinimum = null;
                TagPlaylistPreviewWindow preview = null;
                PlaylistRuleStore ruleStore = null;
                var rulePresets = new List<PlaylistRulePreset>();
                bool rulesAvailable = true;

                Action<bool> recompute = delegate(bool refreshPreview) {
                    var moods = moodGroup.List.IncludedTags;
                    var genres = genreGroup.List.IncludedTags;
                    var excludedMoods = moodGroup.List.ExcludedTags;
                    var excludedGenres = genreGroup.List.ExcludedTags;
                    var excludedTags = excludedMoods.Concat(excludedGenres).ToList();
                    int excludedCount = TagPlaylistFilter.SelectedCount(excludedMoods, excludedGenres);
                    int tagCount = TagPlaylistFilter.SelectedCount(moods, genres);
                    int recommended = tagCount == 0 ? 1 : (tagCount + 1) / 2;
                    updatingThreshold = true;
                    if (tagCount == 0)
                    {
                        matchAuto.Enabled = false;
                        matchThreshold.Minimum = 1;
                        matchThreshold.Maximum = 1;
                        matchThreshold.Value = 1;
                        matchThreshold.Enabled = false;
                    }
                    else
                    {
                        matchAuto.Enabled = true;
                        matchThreshold.Minimum = 1;
                        matchThreshold.Maximum = tagCount;
                        if (matchAuto.Checked) matchThreshold.Value = recommended;
                        else
                        {
                            int desired = presetMinimum.HasValue ? presetMinimum.Value : (int)matchThreshold.Value;
                            matchThreshold.Value = Math.Max(1, Math.Min(tagCount, desired));
                        }
                        matchThreshold.Enabled = !matchAuto.Checked;
                    }
                    presetMinimum = null;
                    updatingThreshold = false;

                    int minimum = tagCount == 0 ? 1 : (int)matchThreshold.Value;
                    latestTagCount = tagCount;
                    latestMinimum = minimum;
                    latestEvaluations = TagPlaylistFilter.Evaluate(snapshot.Songs, moods, genres, excludedTags, minimum);
                    latestMatches = latestEvaluations.Where(item => item.IsMatched).Select(item => item.Song).ToList();
                    int removedCount = selection.RemovedCount(latestMatches);
                    int finalCount = latestMatches.Count - removedCount;
                    var counts = TagPlaylistFilter.CountAtLeast(snapshot.Songs, moods, genres, excludedTags);
                    chart.SetData(counts, minimum);
                    int untagged = Math.Max(0, snapshot.Liked - snapshot.Tagged);
                    summary.Text = string.Format("红心 {0} 首 · 有标签 {1} 首 · 未分析 {2} 首不会加入", snapshot.Liked, snapshot.Tagged, untagged)
                        + (excludedCount > 0 ? " · 硬排除 " + excludedCount + " 个标签" : "")
                        + string.Format(" · 至少命中 {0} 个：匹配 {1} 首", minimum, latestMatches.Count);
                    finalCounts.Text = string.Format("符合规则：{0} 首；手动排除：{1} 首；最终创建：{2} 首。",
                        latestMatches.Count, removedCount, finalCount);
                    finalCounts.ForeColor = latestMatches.Count > 0 && finalCount == 0 ? theme.Error : accent;
                    matchHint.Text = tagCount == 0 ? "先勾选标签"
                        : matchAuto.Checked ? "（" + tagCount + " 个 → " + recommended + " 个）" : "（手动）";
                    if (!nameTouched)
                    {
                        suppressName = true;
                        nameBox.Text = TagPlaylistFilter.DefaultName(moods, genres);
                        suppressName = false;
                    }
                    create.Enabled = finalCount > 0 && !string.IsNullOrWhiteSpace(nameBox.Text);
                    previewButton.Enabled = snapshot.Songs.Count > 0;
                    if (refreshPreview && preview != null && !preview.IsDisposed)
                        preview.UpdateData(latestEvaluations, minimum, tagCount,
                            TagPlaylistRuleSummary(moods, genres, excludedTags, minimum));
                };
                moodGroup.List.TagStateChanged += delegate { if (!moodGroup.Updating && dialog.IsHandleCreated && !dialog.IsDisposed) dialog.BeginInvoke((Action)delegate { recompute(true); }); };
                genreGroup.List.TagStateChanged += delegate { if (!genreGroup.Updating && dialog.IsHandleCreated && !dialog.IsDisposed) dialog.BeginInvoke((Action)delegate { recompute(true); }); };
                moodGroup.All.Click += delegate { SetAllTagChecks(moodGroup, true, delegate { recompute(true); }); };
                moodGroup.None.Click += delegate { SetAllTagChecks(moodGroup, false, delegate { recompute(true); }); };
                genreGroup.All.Click += delegate { SetAllTagChecks(genreGroup, true, delegate { recompute(true); }); };
                genreGroup.None.Click += delegate { SetAllTagChecks(genreGroup, false, delegate { recompute(true); }); };
                matchAuto.CheckedChanged += delegate { if (!updatingThreshold) recompute(true); };
                matchThreshold.ValueChanged += delegate { if (!updatingThreshold) recompute(true); };
                nameBox.TextChanged += delegate {
                    if (suppressName) return;
                    nameTouched = true;
                    recompute(false);
                };

                Action updatePresetButtons = delegate {
                    var selectedPreset = presetCombo.SelectedItem as PlaylistRulePreset;
                    bool hasSelection = rulesAvailable && selectedPreset != null;
                    presetCombo.Enabled = rulesAvailable && rulePresets.Count > 0;
                    presetLoad.Enabled = hasSelection;
                    presetSave.Enabled = rulesAvailable;
                    presetRename.Enabled = hasSelection;
                    presetDelete.Enabled = hasSelection;
                };
                Action<string> refreshPresetCombo = delegate(string selectId) {
                    presetCombo.BeginUpdate();
                    try
                    {
                        presetCombo.Items.Clear();
                        foreach (var preset in rulePresets.OrderBy(item => item.Name, StringComparer.CurrentCulture))
                            presetCombo.Items.Add(preset);
                    }
                    finally { presetCombo.EndUpdate(); }
                    int selectedIndex = -1;
                    if (!string.IsNullOrEmpty(selectId))
                    {
                        for (int index = 0; index < presetCombo.Items.Count; index++)
                        {
                            var preset = presetCombo.Items[index] as PlaylistRulePreset;
                            if (preset != null && preset.Id == selectId) { selectedIndex = index; break; }
                        }
                    }
                    if (selectedIndex < 0 && presetCombo.Items.Count > 0) selectedIndex = 0;
                    presetCombo.SelectedIndex = selectedIndex;
                    updatePresetButtons();
                };
                presetCombo.SelectedIndexChanged += delegate {
                    updatePresetButtons();
                    var selectedPreset = presetCombo.SelectedItem as PlaylistRulePreset;
                    if (selectedPreset != null)
                    {
                        string hint = "已选择「" + selectedPreset.Name + "」，点“载入”应用";
                        presetHint.Text = hint;
                        tips.SetToolTip(presetHint, hint);
                    }
                };
                dialog.Shown += delegate {
                    try
                    {
                        ruleStore = new PlaylistRuleStore(NeteaseAuth.AccountFolder("PlaylistRules"));
                        rulePresets = ruleStore.Load();
                        rulesAvailable = true;
                    }
                    catch (Exception ex)
                    {
                        rulePresets = new List<PlaylistRulePreset>();
                        rulesAvailable = false;
                        presetHint.Text = "规则预设读取失败";
                        tips.SetToolTip(presetHint, ex.Message);
                        MessageBox.Show(dialog, ex.Message, "规则预设", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                    }
                    refreshPresetCombo(null);
                    if (rulesAvailable && rulePresets.Count == 0)
                    {
                        presetHint.Text = "暂无预设；可保存当前规则";
                        tips.SetToolTip(presetHint, presetHint.Text);
                    }
                };

                presetSave.Click += delegate {
                    if (!rulesAvailable) return;
                    var moods = moodGroup.List.IncludedTags;
                    var genres = genreGroup.List.IncludedTags;
                    if (TagPlaylistFilter.SelectedCount(moods, genres) == 0)
                    {
                        MessageBox.Show(dialog, "请先勾选至少一个情绪或曲风标签，再保存规则预设。", "规则预设", MessageBoxButtons.OK, MessageBoxIcon.Information);
                        return;
                    }
                    var selectedPreset = presetCombo.SelectedItem as PlaylistRulePreset;
                    string initial = selectedPreset != null ? selectedPreset.Name : nameBox.Text.Trim();
                    string presetName = PromptTagPlaylistRuleName(dialog, "保存规则预设", initial);
                    if (presetName == null) return;
                    var existing = rulePresets.FirstOrDefault(item => string.Equals(item.Name, presetName, StringComparison.CurrentCultureIgnoreCase));
                    bool needsConfirmation = existing != null && (selectedPreset == null || existing.Id != selectedPreset.Id);
                    if (needsConfirmation && MessageBox.Show(dialog, "已存在同名预设“" + presetName + "”，是否覆盖？", "规则预设",
                            MessageBoxButtons.YesNo, MessageBoxIcon.Question) != DialogResult.Yes) return;
                    var included = moods.Concat(genres).Distinct(StringComparer.Ordinal).ToList();
                    var excluded = moodGroup.List.ExcludedTags.Concat(genreGroup.List.ExcludedTags)
                        .Distinct(StringComparer.Ordinal).Where(tag => !included.Contains(tag)).ToArray();
                    var next = rulePresets.Select(CopyPlaylistRulePreset).ToList();
                    var target = existing == null ? null : next.FirstOrDefault(item => item.Id == existing.Id);
                    if (target == null)
                    {
                        target = new PlaylistRulePreset { Id = Guid.NewGuid().ToString("N") };
                        next.Add(target);
                    }
                    target.Name = presetName;
                    target.Moods = moods.ToArray();
                    target.Genres = genres.ToArray();
                    target.ExcludedTags = excluded;
                    target.MinimumMode = matchAuto.Checked ? "auto" : "manual";
                    target.MinimumHits = (int)matchThreshold.Value;
                    target.PlaylistName = nameBox.Text.Trim();
                    target.UpdatedAt = DateTime.UtcNow.ToString("o");
                    try
                    {
                        ruleStore.Save(next);
                        rulePresets = next;
                        refreshPresetCombo(target.Id);
                        presetHint.Text = "已保存「" + presetName + "」";
                        tips.SetToolTip(presetHint, presetHint.Text);
                    }
                    catch (Exception ex)
                    {
                        presetHint.Text = "预设保存失败";
                        tips.SetToolTip(presetHint, ex.Message);
                        MessageBox.Show(dialog, ex.Message, "规则预设", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                    }
                };

                presetLoad.Click += delegate {
                    var preset = presetCombo.SelectedItem as PlaylistRulePreset;
                    if (!rulesAvailable || preset == null) return;
                    selection.Clear();
                    var presetMoods = preset.Moods ?? new string[0];
                    var presetGenres = preset.Genres ?? new string[0];
                    var presetExcluded = preset.ExcludedTags ?? new string[0];
                    var catalog = new HashSet<string>(
                        moodGroup.List.Items.Cast<TagPlaylistCount>().Select(item => item.Name)
                            .Concat(genreGroup.List.Items.Cast<TagPlaylistCount>().Select(item => item.Name)),
                        StringComparer.Ordinal);
                    var missing = presetMoods.Concat(presetGenres).Concat(presetExcluded)
                        .Where(tag => !string.IsNullOrWhiteSpace(tag) && !catalog.Contains(tag))
                        .Distinct(StringComparer.Ordinal).ToList();
                    moodGroup.Updating = true;
                    genreGroup.Updating = true;
                    try
                    {
                        moodGroup.List.ApplySelection(presetMoods, presetExcluded);
                        genreGroup.List.ApplySelection(presetGenres, presetExcluded);
                    }
                    finally
                    {
                        moodGroup.Updating = false;
                        genreGroup.Updating = false;
                    }
                    updatingThreshold = true;
                    matchAuto.Checked = !string.Equals(preset.MinimumMode, "manual", StringComparison.OrdinalIgnoreCase);
                    presetMinimum = preset.MinimumHits;
                    updatingThreshold = false;
                    nameTouched = true;
                    suppressName = true;
                    nameBox.Text = string.IsNullOrWhiteSpace(preset.PlaylistName) ? preset.Name : preset.PlaylistName;
                    suppressName = false;
                    recompute(true);
                    string detail = "已载入「" + preset.Name + "」，手动调整已重置"
                        + (missing.Count == 0 ? "" : "；忽略 " + missing.Count + " 个当前不存在的标签");
                    presetHint.Text = detail;
                    tips.SetToolTip(presetHint, missing.Count == 0 ? detail
                        : detail + Environment.NewLine + "当前标签列表不存在：" + string.Join("、", missing));
                };

                presetRename.Click += delegate {
                    var preset = presetCombo.SelectedItem as PlaylistRulePreset;
                    if (!rulesAvailable || preset == null) return;
                    string newName = PromptTagPlaylistRuleName(dialog, "重命名规则预设", preset.Name);
                    if (newName == null || newName == preset.Name) return;
                    if (rulePresets.Any(item => item.Id != preset.Id && string.Equals(item.Name, newName, StringComparison.CurrentCultureIgnoreCase)))
                    {
                        MessageBox.Show(dialog, "已存在同名预设“" + newName + "”。", "规则预设", MessageBoxButtons.OK, MessageBoxIcon.Information);
                        return;
                    }
                    var next = rulePresets.Select(CopyPlaylistRulePreset).ToList();
                    var target = next.First(item => item.Id == preset.Id);
                    target.Name = newName;
                    target.UpdatedAt = DateTime.UtcNow.ToString("o");
                    try
                    {
                        ruleStore.Save(next);
                        rulePresets = next;
                        refreshPresetCombo(target.Id);
                        presetHint.Text = "已重命名为「" + newName + "」";
                        tips.SetToolTip(presetHint, presetHint.Text);
                    }
                    catch (Exception ex)
                    {
                        presetHint.Text = "预设重命名失败";
                        tips.SetToolTip(presetHint, ex.Message);
                        MessageBox.Show(dialog, ex.Message, "规则预设", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                    }
                };

                presetDelete.Click += delegate {
                    var preset = presetCombo.SelectedItem as PlaylistRulePreset;
                    if (!rulesAvailable || preset == null) return;
                    if (MessageBox.Show(dialog, "删除规则预设“" + preset.Name + "”？" + Environment.NewLine + Environment.NewLine
                            + "只删除预设，不会修改任何歌单或歌曲标签。", "规则预设",
                            MessageBoxButtons.YesNo, MessageBoxIcon.Question) != DialogResult.Yes) return;
                    var next = rulePresets.Where(item => item.Id != preset.Id).ToList();
                    try
                    {
                        ruleStore.Save(next);
                        rulePresets = next;
                        refreshPresetCombo(null);
                        presetHint.Text = "已删除「" + preset.Name + "」";
                        tips.SetToolTip(presetHint, presetHint.Text);
                    }
                    catch (Exception ex)
                    {
                        presetHint.Text = "预设删除失败";
                        tips.SetToolTip(presetHint, ex.Message);
                        MessageBox.Show(dialog, ex.Message, "规则预设", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                    }
                };

                previewButton.Click += delegate {
                    if (preview != null && !preview.IsDisposed)
                    {
                        preview.Activate();
                        preview.BringToFront();
                        return;
                    }
                    preview = new TagPlaylistPreviewWindow(this, selection);
                    preview.SelectionChanged += delegate {
                        if (dialog.IsHandleCreated && !dialog.IsDisposed)
                            dialog.BeginInvoke((Action)delegate { recompute(false); });
                    };
                    preview.FormClosed += delegate { preview = null; };
                    preview.UpdateData(latestEvaluations, latestMinimum, latestTagCount,
                        TagPlaylistRuleSummary(moodGroup.List.IncludedTags, genreGroup.List.IncludedTags,
                            moodGroup.List.ExcludedTags.Concat(genreGroup.List.ExcludedTags).ToList(), latestMinimum));
                    preview.PlaceBeside(dialog);
                    preview.Show(dialog);
                    preview.BringToFront();
                };
                dialog.FormClosed += delegate { if (preview != null && !preview.IsDisposed) preview.Close(); };

                create.Click += delegate {
                    recompute(false);
                    var moods = moodGroup.List.IncludedTags;
                    var genres = genreGroup.List.IncludedTags;
                    if (TagPlaylistFilter.SelectedCount(moods, genres) == 0)
                    {
                        MessageBox.Show(dialog, "请至少勾选一个情绪或曲风标签。", "标签歌单", MessageBoxButtons.OK, MessageBoxIcon.Information);
                        return;
                    }
                    if (latestMatches.Count == 0)
                    {
                        MessageBox.Show(dialog, "当前筛选没有匹配的红心歌曲，请调整标签或最低命中数。", "标签歌单", MessageBoxButtons.OK, MessageBoxIcon.Information);
                        return;
                    }
                    int removedCount = selection.RemovedCount(latestMatches);
                    var finalSongs = selection.FinalSongs(latestMatches);
                    if (finalSongs.Count == 0)
                    {
                        MessageBox.Show(dialog, "所有匹配歌曲都被手动排除，不能创建空歌单。请恢复至少一首歌曲。", "标签歌单", MessageBoxButtons.OK, MessageBoxIcon.Information);
                        return;
                    }
                    var name = nameBox.Text.Trim();
                    if (name.Length == 0)
                    {
                        MessageBox.Show(dialog, "歌单名称不能为空。", "标签歌单", MessageBoxButtons.OK, MessageBoxIcon.Information);
                        nameBox.Focus();
                        return;
                    }
                    var included = moods.Concat(genres).Distinct(StringComparer.Ordinal).ToList();
                    var excluded = moodGroup.List.ExcludedTags.Concat(genreGroup.List.ExcludedTags)
                        .Where(tag => !included.Contains(tag)).Distinct(StringComparer.Ordinal).ToList();
                    if (!ConfirmTagPlaylistCreation(dialog, name, privacy.Checked, included, excluded,
                            latestMinimum, latestMatches.Count, removedCount, finalSongs.Count)) return;
                    result = new TagPlaylistDialogResult {
                        Name = name,
                        IsPrivate = privacy.Checked,
                        SongIds = finalSongs.Select(song => song.SongId).ToList()
                    };
                    dialog.DialogResult = DialogResult.OK;
                    dialog.Close();
                };
                dialog.AcceptButton = create;
                dialog.CancelButton = cancel;
                recompute(true);
                PrepareThemedDialog(dialog);
                if (dialog.ShowDialog(this) != DialogResult.OK) return null;
                return result;
            }
        }

        private string PromptTagPlaylistRuleName(Form owner, string title, string initial)
        {
            using (var dialog = new Form {
                Text = title,
                Size = new Size(440, 210),
                MinimumSize = new Size(400, 200),
                StartPosition = FormStartPosition.CenterParent,
                FormBorderStyle = FormBorderStyle.FixedDialog,
                MaximizeBox = false,
                MinimizeBox = false,
                ShowInTaskbar = false,
                BackColor = panel,
                ForeColor = ink,
                Font = UiFont(9F),
                AutoScaleMode = AutoScaleMode.Dpi,
                AutoScaleDimensions = new SizeF(96F, 96F)
            })
            {
                var root = new TableLayoutPanel {
                    Dock = DockStyle.Fill,
                    Padding = new Padding(18, 14, 18, 14),
                    ColumnCount = 1,
                    RowCount = 4,
                    BackColor = panel
                };
                root.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
                root.RowStyles.Add(new RowStyle(SizeType.AutoSize));
                root.RowStyles.Add(new RowStyle(SizeType.AutoSize));
                root.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
                root.RowStyles.Add(new RowStyle(SizeType.AutoSize));
                dialog.Controls.Add(root);
                var label = Label("预设名称", 9, false, muted);
                label.Margin = new Padding(0, 0, 0, 4);
                root.Controls.Add(label, 0, 0);
                var input = new TextBox {
                    Name = "TagPlaylistPresetNameInput",
                    Dock = DockStyle.Top,
                    MaxLength = 30,
                    Font = UiFont(10F),
                    Margin = new Padding(0, 0, 0, 6)
                };
                string text = initial ?? "";
                input.Text = text.Length > 30 ? text.Substring(0, 30) : text;
                root.Controls.Add(input, 0, 1);
                var hint = Label("最多 30 个字符；只保存筛选条件和默认歌单名。", 9, false, muted);
                hint.AutoSize = true;
                hint.MaximumSize = new Size(380, 0);
                hint.Margin = new Padding(0, 4, 0, 0);
                root.Controls.Add(hint, 0, 2);
                var footer = new FlowLayoutPanel {
                    Dock = DockStyle.Fill,
                    FlowDirection = FlowDirection.RightToLeft,
                    WrapContents = false,
                    AutoSize = true,
                    BackColor = panel,
                    Margin = new Padding(0)
                };
                var ok = Button("确定", true);
                ok.Name = "TagPlaylistPresetNameOk";
                ok.Size = new Size(92, 36);
                ok.DialogResult = DialogResult.OK;
                var cancel = Button("取消", false);
                cancel.Name = "TagPlaylistPresetNameCancel";
                cancel.Size = new Size(86, 36);
                cancel.DialogResult = DialogResult.Cancel;
                footer.Controls.Add(ok);
                footer.Controls.Add(cancel);
                root.Controls.Add(footer, 0, 3);
                dialog.AcceptButton = ok;
                dialog.CancelButton = cancel;
                dialog.Shown += delegate { input.Focus(); input.SelectAll(); };
                PrepareThemedDialog(dialog);
                if (dialog.ShowDialog(owner) != DialogResult.OK) return null;
                string name = input.Text.Trim();
                if (name.Length == 0)
                {
                    MessageBox.Show(owner, "预设名称不能为空。", "规则预设", MessageBoxButtons.OK, MessageBoxIcon.Information);
                    return null;
                }
                return name;
            }
        }

        private bool ConfirmTagPlaylistCreation(Form owner, string name, bool isPrivate,
            List<string> includedTags, List<string> excludedTags, int minimumHits,
            int matchedCount, int removedCount, int finalCount)
        {
            using (var dialog = new Form {
                Text = "确认创建歌单",
                Size = new Size(600, 530),
                MinimumSize = new Size(540, 460),
                StartPosition = FormStartPosition.CenterParent,
                FormBorderStyle = FormBorderStyle.FixedDialog,
                MaximizeBox = false,
                MinimizeBox = false,
                ShowInTaskbar = false,
                BackColor = panel,
                ForeColor = ink,
                Font = UiFont(9F),
                AutoScaleMode = AutoScaleMode.Dpi,
                AutoScaleDimensions = new SizeF(96F, 96F)
            })
            {
                var scroll = new Panel { Dock = DockStyle.Fill, AutoScroll = true, BackColor = panel };
                var root = new TableLayoutPanel {
                    Dock = DockStyle.Top,
                    AutoSize = true,
                    Padding = new Padding(18, 14, 18, 14),
                    ColumnCount = 2,
                    RowCount = 11,
                    BackColor = panel
                };
                root.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 112));
                root.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
                for (int index = 0; index < 11; index++) root.RowStyles.Add(new RowStyle(SizeType.AutoSize));
                scroll.Controls.Add(root);
                dialog.Controls.Add(scroll);
                int row = 0;
                var title = Label("创建歌单前确认", 13, true, ink);
                title.Margin = new Padding(0, 0, 0, 8);
                root.Controls.Add(title, 0, row);
                root.SetColumnSpan(title, 2);
                row++;
                Action<string, string, string, Color, bool> addRow = delegate(string labelText, string valueText, string controlName, Color valueColor, bool bold) {
                    var label = Label(labelText, 9, false, muted);
                    label.Margin = new Padding(0, 4, 8, 4);
                    root.Controls.Add(label, 0, row);
                    var value = Label(valueText, 9, bold, valueColor);
                    value.Name = controlName;
                    value.AutoSize = true;
                    value.MaximumSize = new Size(420, 0);
                    value.Margin = new Padding(0, 4, 0, 4);
                    root.Controls.Add(value, 1, row);
                    row++;
                };
                addRow("歌单名称", name, "TagPlaylistConfirmName", ink, true);
                addRow("可见性", isPrivate ? "私密（仅自己可见）" : "公开", "TagPlaylistConfirmPrivacy", ink, false);
                addRow("筛选标签", includedTags == null || includedTags.Count == 0 ? "无" : string.Join("、", includedTags),
                    "TagPlaylistConfirmTags", ink, false);
                addRow("硬性排除", excludedTags == null || excludedTags.Count == 0 ? "无" : string.Join("、", excludedTags),
                    "TagPlaylistConfirmExcluded", ink, false);
                addRow("最低命中", minimumHits + " 个", "TagPlaylistConfirmMinimum", ink, false);
                addRow("符合规则", matchedCount + " 首", "TagPlaylistConfirmMatched", ink, false);
                addRow("手动排除", removedCount + " 首", "TagPlaylistConfirmRemoved", ink, false);
                addRow("最终加入", finalCount + " 首", "TagPlaylistConfirmFinal",
                    finalCount > 0 ? accent : theme.Error, true);
                var note = Label("确认后只提交“最终加入”的歌曲；不会修改红心歌单或歌曲标签。", 9, false, muted);
                note.AutoSize = true;
                note.MaximumSize = new Size(520, 0);
                note.Margin = new Padding(0, 8, 0, 8);
                root.Controls.Add(note, 0, row);
                root.SetColumnSpan(note, 2);
                row++;
                var footer = new FlowLayoutPanel {
                    Dock = DockStyle.Fill,
                    FlowDirection = FlowDirection.RightToLeft,
                    WrapContents = false,
                    AutoSize = true,
                    BackColor = panel,
                    Margin = new Padding(0)
                };
                var confirm = Button("创建歌单", true);
                confirm.Name = "TagPlaylistConfirmCreate";
                confirm.Size = new Size(126, 40);
                confirm.DialogResult = DialogResult.OK;
                confirm.Enabled = finalCount > 0;
                var back = Button("返回修改", false);
                back.Name = "TagPlaylistConfirmBack";
                back.Size = new Size(110, 40);
                back.DialogResult = DialogResult.Cancel;
                footer.Controls.Add(confirm);
                footer.Controls.Add(back);
                root.Controls.Add(footer, 0, row);
                root.SetColumnSpan(footer, 2);
                dialog.AcceptButton = confirm;
                dialog.CancelButton = back;
                PrepareThemedDialog(dialog);
                return dialog.ShowDialog(owner) == DialogResult.OK;
            }
        }

        private static string TagPlaylistRuleSummary(IEnumerable<string> moods, IEnumerable<string> genres,
            IEnumerable<string> excludedTags, int minimumHits)
        {
            var included = new List<string>();
            var seen = new HashSet<string>(StringComparer.Ordinal);
            if (moods != null)
                foreach (var tag in moods)
                    if (!string.IsNullOrWhiteSpace(tag) && seen.Add(tag)) included.Add(tag);
            if (genres != null)
                foreach (var tag in genres)
                    if (!string.IsNullOrWhiteSpace(tag) && seen.Add(tag)) included.Add(tag);
            var excluded = new List<string>();
            seen.Clear();
            if (excludedTags != null)
                foreach (var tag in excludedTags)
                    if (!string.IsNullOrWhiteSpace(tag) && seen.Add(tag)) excluded.Add(tag);
            return "勾选：" + (included.Count == 0 ? "无" : string.Join("、", included))
                + " · 硬排除：" + (excluded.Count == 0 ? "无" : string.Join("、", excluded))
                + " · 最低命中：" + minimumHits + " 个";
        }

        private static PlaylistRulePreset CopyPlaylistRulePreset(PlaylistRulePreset source)
        {
            return new PlaylistRulePreset {
                Id = source.Id,
                Name = source.Name,
                Moods = source.Moods == null ? new string[0] : (string[])source.Moods.Clone(),
                Genres = source.Genres == null ? new string[0] : (string[])source.Genres.Clone(),
                ExcludedTags = source.ExcludedTags == null ? new string[0] : (string[])source.ExcludedTags.Clone(),
                MinimumMode = source.MinimumMode,
                MinimumHits = source.MinimumHits,
                PlaylistName = source.PlaylistName,
                UpdatedAt = source.UpdatedAt
            };
        }
    }

    internal sealed class TagPlaylistCreationResult
    {
        internal string PlaylistId;
        internal int Added;
    }

    internal sealed class TagPlaylistPartialException : Exception
    {
        internal readonly string PlaylistId;
        internal readonly int Added;
        internal readonly int Failed;

        internal TagPlaylistPartialException(string playlistId, int added, int failed, string message) : base(message)
        {
            PlaylistId = playlistId;
            Added = added;
            Failed = failed;
        }
    }

    internal static class TagPlaylistApi
    {
        internal static TagPlaylistCreationResult Create(string name, bool isPrivate, IList<string> songIds)
        {
            if (string.IsNullOrWhiteSpace(name)) throw new InvalidOperationException("歌单名称不能为空。");
            if (songIds == null || songIds.Count == 0) throw new InvalidOperationException("没有可加入歌单的歌曲。");
            var ids = new List<long>();
            var seen = new HashSet<long>();
            foreach (var text in songIds)
            {
                long id;
                if (!long.TryParse(text, out id) || id <= 0 || !seen.Add(id)) continue;
                ids.Add(id);
            }
            if (ids.Count == 0) throw new InvalidOperationException("没有可加入歌单的有效歌曲 ID。");
            var session = NeteaseAuth.Load();
            string cookie = NeteaseAuth.Text(session, "cookie");
            string csrf = CookieValue(cookie, "__csrf") ?? CookieValue(cookie, "_csrf");
            if (string.IsNullOrEmpty(csrf))
                throw new InvalidOperationException("登录凭证缺少 CSRF 令牌，请退出账号后重新扫码登录。");
            string ignored;
            var created = NeteaseAuth.Request("/api/playlist/create", new Dictionary<string, object> {
                { "name", name.Trim() },
                { "privacy", isPrivate ? 10 : 0 },
                { "type", 0 },
                { "csrf_token", csrf }
            }, cookie, out ignored);
            EnsureSuccess(created, "创建歌单");
            var playlist = NeteaseAuth.Object(created, "playlist");
            string playlistId = NeteaseAuth.Text(playlist, "id");
            if (string.IsNullOrEmpty(playlistId)) playlistId = NeteaseAuth.Text(created, "id");
            if (string.IsNullOrEmpty(playlistId))
                throw new InvalidOperationException("网易云没有返回新歌单 ID，歌单可能未创建。");

            int added = 0;
            try
            {
                for (int start = 0; start < ids.Count; start += 200)
                {
                    var chunk = ids.Skip(start).Take(200).ToArray();
                    var response = NeteaseAuth.Request("/api/playlist/manipulate/tracks", new Dictionary<string, object> {
                        { "pid", playlistId },
                        { "trackIds", new JavaScriptSerializer().Serialize(chunk) },
                        { "op", "add" },
                        { "csrf_token", csrf }
                    }, cookie, out ignored);
                    EnsureSuccess(response, "加入歌曲");
                    added += chunk.Length;
                }
            }
            catch (Exception ex)
            {
                throw new TagPlaylistPartialException(playlistId, added, ids.Count - added, ex.Message);
            }
            return new TagPlaylistCreationResult { PlaylistId = playlistId, Added = added };
        }

        private static void EnsureSuccess(Dictionary<string, object> data, string action)
        {
            string code = NeteaseAuth.Text(data, "code");
            if (code == "200") return;
            string message = NeteaseAuth.Text(data, "message");
            if (string.IsNullOrWhiteSpace(message)) message = NeteaseAuth.Text(data, "msg");
            throw new InvalidOperationException(action + "失败（code " + (string.IsNullOrEmpty(code) ? "未知" : code) + "）"
                + (string.IsNullOrWhiteSpace(message) ? "。" : "：" + message));
        }

        private static string CookieValue(string cookie, string name)
        {
            foreach (var part in (cookie ?? "").Split(';'))
            {
                int equals = part.IndexOf('=');
                if (equals <= 0) continue;
                if (!part.Substring(0, equals).Trim().Equals(name, StringComparison.OrdinalIgnoreCase)) continue;
                return Uri.UnescapeDataString(part.Substring(equals + 1).Trim());
            }
            return null;
        }
    }
}
