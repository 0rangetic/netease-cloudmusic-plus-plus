using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;

namespace NeteaseToolbox
{
    internal sealed class DedupePalette
    {
        public readonly Color Background, Panel, Ink, Muted, Accent, Border, Secondary, ButtonInk, Danger, Success, Error;

        public DedupePalette(Color background, Color panel, Color ink, Color muted, Color accent, Color border,
            Color secondary, Color buttonInk, Color danger, Color success, Color error)
        {
            Background = background;
            Panel = panel;
            Ink = ink;
            Muted = muted;
            Accent = accent;
            Border = border;
            Secondary = secondary;
            ButtonInk = buttonInk;
            Danger = danger;
            Success = success;
            Error = error;
        }

        public static readonly DedupePalette Dark = new DedupePalette(
            Color.FromArgb(20, 24, 33), Color.FromArgb(31, 37, 49), Color.FromArgb(236, 240, 247),
            Color.FromArgb(156, 167, 184), Color.FromArgb(76, 149, 245), Color.FromArgb(67, 77, 94),
            Color.FromArgb(48, 57, 72), Color.White, Color.FromArgb(176, 73, 65),
            Color.FromArgb(119, 213, 164), Color.FromArgb(255, 139, 124));

        public static readonly DedupePalette Light = new DedupePalette(
            Color.FromArgb(244, 246, 250), Color.White, Color.FromArgb(31, 41, 55),
            Color.FromArgb(91, 105, 124), Color.FromArgb(34, 103, 205), Color.FromArgb(203, 212, 225),
            Color.FromArgb(229, 237, 248), Color.FromArgb(31, 41, 55), Color.FromArgb(176, 73, 65),
            Color.FromArgb(24, 117, 73), Color.FromArgb(178, 53, 43));
    }

    internal static class DedupeUiStyle
    {
        public static Font Font(float size, bool bold)
        {
            try { return new Font("Microsoft YaHei UI", size, bold ? System.Drawing.FontStyle.Bold : System.Drawing.FontStyle.Regular); }
            catch { return new Font(FontFamily.GenericSansSerif, size, bold ? System.Drawing.FontStyle.Bold : System.Drawing.FontStyle.Regular); }
        }

        public static Label Label(string text, float size, bool bold, Color color)
        {
            return new Label
            {
                Text = text,
                AutoSize = true,
                BackColor = Color.Transparent,
                ForeColor = color,
                Font = Font(size, bold)
            };
        }

        public static void Style(Button button, DedupePalette palette, string role)
        {
            bool primary = role == "primary";
            bool danger = role == "danger";
            button.FlatStyle = FlatStyle.Flat;
            button.FlatAppearance.BorderSize = role == "secondary" ? 1 : 0;
            button.FlatAppearance.BorderColor = palette.Border;
            button.FlatAppearance.MouseOverBackColor = primary ? palette.Accent : danger ? palette.Danger : palette.Secondary;
            button.FlatAppearance.MouseDownBackColor = button.FlatAppearance.MouseOverBackColor;
            button.BackColor = primary ? palette.Accent : danger ? palette.Danger : role == "ghost" ? palette.Background : palette.Secondary;
            button.ForeColor = primary || danger ? Color.White : palette.ButtonInk;
            button.Font = Font(9F, primary || danger);
            button.UseVisualStyleBackColor = false;
            button.Cursor = Cursors.Hand;
            button.EnabledChanged += delegate
            {
                button.ForeColor = !button.Enabled ? palette.Muted : primary || danger ? Color.White : palette.ButtonInk;
            };
        }

        public static void StyleInput(Control control, DedupePalette palette)
        {
            control.BackColor = palette.Panel;
            control.ForeColor = palette.Ink;
            control.Font = Font(9F, false);
        }

        public static void StyleList(ListView list, DedupePalette palette)
        {
            list.View = View.Details;
            list.FullRowSelect = true;
            list.HideSelection = false;
            list.MultiSelect = false;
            list.BorderStyle = BorderStyle.FixedSingle;
            list.BackColor = palette.Panel;
            list.ForeColor = palette.Ink;
            list.Font = Font(9F, false);
        }

        public static void StyleGrid(DataGridView grid, DedupePalette palette)
        {
            grid.BackgroundColor = palette.Panel;
            grid.BorderStyle = BorderStyle.FixedSingle;
            grid.GridColor = palette.Border;
            grid.EnableHeadersVisualStyles = false;
            grid.ColumnHeadersDefaultCellStyle.BackColor = palette.Secondary;
            grid.ColumnHeadersDefaultCellStyle.ForeColor = palette.Ink;
            grid.ColumnHeadersDefaultCellStyle.SelectionBackColor = palette.Secondary;
            grid.ColumnHeadersDefaultCellStyle.SelectionForeColor = palette.Ink;
            grid.ColumnHeadersDefaultCellStyle.Font = Font(9F, true);
            grid.DefaultCellStyle.BackColor = palette.Panel;
            grid.DefaultCellStyle.ForeColor = palette.Ink;
            grid.DefaultCellStyle.SelectionBackColor = palette.Accent;
            grid.DefaultCellStyle.SelectionForeColor = Color.White;
            grid.DefaultCellStyle.Font = Font(9F, false);
            grid.RowHeadersVisible = false;
            grid.AllowUserToAddRows = false;
            grid.AllowUserToDeleteRows = false;
            grid.AllowUserToResizeRows = false;
            grid.AutoSizeRowsMode = DataGridViewAutoSizeRowsMode.None;
            grid.RowTemplate.Height = 30;
            grid.ColumnHeadersHeight = 32;
            grid.SelectionMode = DataGridViewSelectionMode.FullRowSelect;
            grid.MultiSelect = false;
            grid.EditMode = DataGridViewEditMode.EditOnEnter;
        }

        public static string Size(long bytes)
        {
            if (bytes < 1024) return bytes.ToString(CultureInfo.InvariantCulture) + " B";
            if (bytes < 1024L * 1024L) return (bytes / 1024.0).ToString("0.0", CultureInfo.InvariantCulture) + " KB";
            if (bytes < 1024L * 1024L * 1024L) return (bytes / (1024.0 * 1024.0)).ToString("0.0", CultureInfo.InvariantCulture) + " MB";
            return (bytes / (1024.0 * 1024.0 * 1024.0)).ToString("0.00", CultureInfo.InvariantCulture) + " GB";
        }

        public static string Time(string iso)
        {
            DateTime parsed;
            if (!DedupeFileSystem.TryParseUtc(iso, out parsed)) return "";
            return parsed.ToLocalTime().ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture);
        }

        public static string Quote(string value)
        {
            return "\"" + value.Replace("\"", "\\\"") + "\"";
        }
    }

    internal sealed class DedupeProgressForm : Form
    {
        private readonly Label status;
        private bool allowClose;

        public DedupeProgressForm(DedupePalette palette)
        {
            Text = "安全文件处理";
            StartPosition = FormStartPosition.CenterParent;
            FormBorderStyle = FormBorderStyle.FixedDialog;
            MaximizeBox = false;
            MinimizeBox = false;
            ShowInTaskbar = false;
            ControlBox = false;
            ClientSize = new Size(520, 150);
            BackColor = palette.Background;
            ForeColor = palette.Ink;
            Font = DedupeUiStyle.Font(9F, false);
            var title = DedupeUiStyle.Label("正在处理文件", 10F, true, palette.Ink);
            title.Location = new Point(22, 20);
            Controls.Add(title);
            status = DedupeUiStyle.Label("正在检查文件...", 9F, false, palette.Muted);
            status.AutoSize = false;
            status.Location = new Point(22, 52);
            status.Size = new Size(476, 40);
            status.AutoEllipsis = true;
            Controls.Add(status);
            var bar = new ProgressBar { Style = ProgressBarStyle.Marquee, MarqueeAnimationSpeed = 28, Location = new Point(22, 100), Size = new Size(476, 18) };
            Controls.Add(bar);
            var hint = DedupeUiStyle.Label("操作已开始后不能通过关闭本窗口终止；每个文件都会立即更新持久化记录。", 8F, false, palette.Muted);
            hint.AutoSize = false;
            hint.Location = new Point(22, 122);
            hint.Size = new Size(476, 18);
            hint.AutoEllipsis = true;
            Controls.Add(hint);
        }

        public void SetStatus(string text)
        {
            if (IsDisposed) return;
            if (InvokeRequired) { BeginInvoke((Action<string>)SetStatus, text); return; }
            status.Text = text ?? "";
        }

        public void CloseFromOwner()
        {
            allowClose = true;
            Close();
        }

        protected override void OnFormClosing(FormClosingEventArgs e)
        {
            if (!allowClose) { e.Cancel = true; return; }
            base.OnFormClosing(e);
        }
    }

    internal sealed class DedupePreviewForm : Form
    {
        private sealed class DetailTag
        {
            public DedupeGroup Group;
            public DedupeGroupFile Source;
            public DedupeAssociatedFile Associated;
        }

        private readonly string outputRoot;
        private readonly string scriptPath;
        private readonly DedupePalette palette;
        private readonly Action<string, string, string> report;
        private readonly Action filesChanged;
        private readonly TextBox rootBox = new TextBox();
        private readonly Button browseButton = new Button();
        private readonly CheckBox recurseBox = new CheckBox();
        private readonly CheckBox showAssociatedBox = new CheckBox();
        private readonly CheckBox includeSuspectedBox = new CheckBox();
        private readonly Button scanButton = new Button();
        private readonly Button cancelScanButton = new Button();
        private readonly Label scanStatus = new Label();
        private readonly Label summaryLabel = new Label();
        private readonly Label warningsLabel = new Label();
        private readonly ListView groupList = new ListView();
        private readonly DataGridView detailGrid = new DataGridView();
        private readonly Label detailTitle = new Label();
        private readonly CheckBox skipGroupBox = new CheckBox();
        private readonly Button setKeepButton = new Button();
        private readonly Button selectHighButton = new Button();
        private readonly Button clearButton = new Button();
        private readonly Button exportButton = new Button();
        private readonly Button executeButton = new Button();
        private readonly Button closeButton = new Button();
        private readonly ToolTip tips = new ToolTip();
        private DedupePlan plan;
        private CancellationTokenSource scanCancellation;
        private bool scanning;
        private bool executing;
        private bool allowClose;
        private bool updatingDetails;

        public DedupePreviewForm(string inputRoot, string outputRoot, string scriptPath, DedupePalette palette,
            Action<string, string, string> report, Action filesChanged)
        {
            this.outputRoot = outputRoot;
            this.scriptPath = scriptPath;
            this.palette = palette ?? DedupePalette.Dark;
            this.report = report;
            this.filesChanged = filesChanged;
            Text = "曲库安全去重";
            StartPosition = FormStartPosition.CenterParent;
            Size = new Size(1280, 820);
            MinimumSize = new Size(1060, 640);
            AutoScaleMode = AutoScaleMode.Dpi;
            AutoScaleDimensions = new SizeF(96F, 96F);
            BackColor = this.palette.Background;
            ForeColor = this.palette.Ink;
            Font = DedupeUiStyle.Font(9F, false);
            BuildUi();
            rootBox.Text = inputRoot ?? "";
            recurseBox.Checked = true;
            showAssociatedBox.Checked = true;
            includeSuspectedBox.Checked = false;
            UpdateSummary();
            Shown += async delegate
            {
                if (Directory.Exists(rootBox.Text.Trim())) await ScanAsync();
                else scanStatus.Text = "请选择包含 NCM 歌曲的目录，然后开始只读扫描。";
            };
            FormClosing += delegate(object sender, FormClosingEventArgs e)
            {
                if (executing && !allowClose)
                {
                    e.Cancel = true;
                    MessageBox.Show(this, "文件已开始移动，不能通过关闭窗口安全终止。请等待当前操作完成。", "安全去重",
                        MessageBoxButtons.OK, MessageBoxIcon.Warning);
                    return;
                }
                if (scanning && scanCancellation != null) scanCancellation.Cancel();
            };
        }

        private void BuildUi()
        {
            var layout = new TableLayoutPanel
            {
                Dock = DockStyle.Fill,
                ColumnCount = 1,
                RowCount = 4,
                Padding = new Padding(18, 14, 18, 14),
                BackColor = palette.Background
            };
            layout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 134));
            layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 106));
            layout.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
            layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 52));

            var config = new Panel { Name = "DedupeConfigPanel", Dock = DockStyle.Fill, BackColor = palette.Panel, Padding = new Padding(14, 12, 14, 12) };
            var rootLabel = DedupeUiStyle.Label("扫描目录", 9F, true, palette.Ink);
            rootLabel.Location = new Point(16, 18);
            config.Controls.Add(rootLabel);
            rootBox.Name = "DedupeRootBox";
            DedupeUiStyle.StyleInput(rootBox, palette);
            rootBox.Location = new Point(92, 14);
            rootBox.Height = 28;
            rootBox.Anchor = AnchorStyles.Left | AnchorStyles.Top | AnchorStyles.Right;
            config.Controls.Add(rootBox);
            browseButton.Name = "DedupeBrowseButton";
            browseButton.Text = "浏览…";
            browseButton.Size = new Size(88, 30);
            browseButton.Anchor = AnchorStyles.Top | AnchorStyles.Right;
            DedupeUiStyle.Style(browseButton, palette, "secondary");
            browseButton.Click += delegate { ChooseRoot(); };
            config.Controls.Add(browseButton);
            recurseBox.Name = "DedupeRecurseBox";
            recurseBox.Text = "扫描子目录";
            recurseBox.AutoSize = true;
            recurseBox.ForeColor = palette.Ink;
            recurseBox.BackColor = Color.Transparent;
            recurseBox.Location = new Point(94, 55);
            config.Controls.Add(recurseBox);
            showAssociatedBox.Name = "DedupeShowAssociatedBox";
            showAssociatedBox.Text = "显示关联的转换文件";
            showAssociatedBox.AutoSize = true;
            showAssociatedBox.ForeColor = palette.Ink;
            showAssociatedBox.BackColor = Color.Transparent;
            showAssociatedBox.Location = new Point(210, 55);
            config.Controls.Add(showAssociatedBox);
            includeSuspectedBox.Name = "DedupeIncludeSuspectedBox";
            includeSuspectedBox.Text = "包含仅凭歌名和歌手判断的疑似项";
            includeSuspectedBox.AutoSize = true;
            includeSuspectedBox.ForeColor = palette.Ink;
            includeSuspectedBox.BackColor = Color.Transparent;
            includeSuspectedBox.Location = new Point(390, 55);
            config.Controls.Add(includeSuspectedBox);
            scanButton.Name = "DedupeScanButton";
            scanButton.Text = "开始只读扫描";
            scanButton.Size = new Size(138, 36);
            scanButton.Location = new Point(94, 84);
            DedupeUiStyle.Style(scanButton, palette, "primary");
            scanButton.Click += async delegate { await ScanAsync(); };
            config.Controls.Add(scanButton);
            cancelScanButton.Name = "DedupeCancelScanButton";
            cancelScanButton.Text = "取消扫描";
            cancelScanButton.Size = new Size(96, 36);
            cancelScanButton.Location = new Point(240, 84);
            DedupeUiStyle.Style(cancelScanButton, palette, "secondary");
            cancelScanButton.Enabled = false;
            cancelScanButton.Click += delegate { if (scanCancellation != null) { scanCancellation.Cancel(); scanStatus.Text = "正在取消扫描…"; } };
            config.Controls.Add(cancelScanButton);
            scanStatus.Name = "DedupeScanStatus";
            scanStatus.AutoSize = false;
            scanStatus.ForeColor = palette.Muted;
            scanStatus.Location = new Point(350, 92);
            scanStatus.Size = new Size(760, 20);
            scanStatus.Anchor = AnchorStyles.Left | AnchorStyles.Top | AnchorStyles.Right;
            scanStatus.AutoEllipsis = true;
            scanStatus.Text = "扫描只读取元数据，不会修改文件。";
            config.Controls.Add(scanStatus);
            config.Resize += delegate
            {
                rootBox.Width = Math.Max(220, config.ClientSize.Width - 92 - 110);
                browseButton.Left = config.ClientSize.Width - browseButton.Width - 16;
                scanStatus.Width = Math.Max(200, config.ClientSize.Width - scanStatus.Left - 16);
            };
            layout.Controls.Add(config, 0, 0);

            var summaryPanel = new Panel { Name = "DedupeSummaryPanel", Dock = DockStyle.Fill, BackColor = palette.Panel };
            summaryLabel.Name = "DedupeSummaryLabel";
            summaryLabel.AutoSize = false;
            summaryLabel.Dock = DockStyle.Fill;
            summaryLabel.Padding = new Padding(16, 10, 16, 2);
            summaryLabel.ForeColor = palette.Ink;
            summaryLabel.Font = DedupeUiStyle.Font(9F, false);
            summaryLabel.Text = "尚未扫描。";
            summaryPanel.Controls.Add(summaryLabel);
            warningsLabel.Name = "DedupeWarningsLabel";
            warningsLabel.AutoSize = false;
            warningsLabel.Dock = DockStyle.Bottom;
            warningsLabel.Height = 20;
            warningsLabel.Padding = new Padding(16, 0, 16, 2);
            warningsLabel.ForeColor = palette.Muted;
            warningsLabel.Font = DedupeUiStyle.Font(8F, false);
            warningsLabel.AutoEllipsis = true;
            summaryPanel.Controls.Add(warningsLabel);
            layout.Controls.Add(summaryPanel, 0, 1);

            var split = new SplitContainer
            {
                Name = "DedupeSplit",
                Dock = DockStyle.Fill,
                Orientation = Orientation.Vertical,
                BackColor = palette.Background
            };
            split.Layout += delegate
            {
                try
                {
                    if (split.Width > 760) split.SplitterDistance = Math.Max(320, Math.Min(560, split.Width - 420));
                }
                catch { }
            };
            var groupPanel = new TableLayoutPanel
            {
                Name = "DedupeGroupPanel",
                Dock = DockStyle.Fill,
                BackColor = palette.Panel,
                ColumnCount = 1,
                RowCount = 2,
                Padding = new Padding(0)
            };
            groupPanel.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            groupPanel.RowStyles.Add(new RowStyle(SizeType.Absolute, 30));
            groupPanel.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
            var groupTitle = DedupeUiStyle.Label("重复组", 10F, true, palette.Ink);
            groupTitle.Name = "DedupeGroupTitle";
            groupTitle.AutoSize = false;
            groupTitle.Dock = DockStyle.Fill;
            groupTitle.TextAlign = ContentAlignment.MiddleLeft;
            groupTitle.Padding = new Padding(8, 0, 0, 0);
            groupPanel.Controls.Add(groupTitle, 0, 0);
            groupList.Name = "DedupeGroupList";
            groupList.Dock = DockStyle.Fill;
            DedupeUiStyle.StyleList(groupList, palette);
            groupList.Columns.Add("组", 40);
            groupList.Columns.Add("歌曲", 96);
            groupList.Columns.Add("歌手", 60);
            groupList.Columns.Add("匹配依据", 115);
            groupList.Columns.Add("文件", 40);
            groupList.Columns.Add("待清理", 52);
            groupList.Columns.Add("建议", 145);
            groupList.SelectedIndexChanged += delegate { ShowSelectedGroup(); };
            groupPanel.Controls.Add(groupList, 0, 1);
            split.Panel1.Controls.Add(groupPanel);

            var detailPanel = new TableLayoutPanel
            {
                Name = "DedupeDetailPanel",
                Dock = DockStyle.Fill,
                BackColor = palette.Panel,
                ColumnCount = 1,
                RowCount = 2,
                Padding = new Padding(0)
            };
            detailPanel.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            detailPanel.RowStyles.Add(new RowStyle(SizeType.Absolute, 38));
            detailPanel.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
            var detailHeader = new Panel { Name = "DedupeDetailHeader", Dock = DockStyle.Fill, BackColor = palette.Panel };
            detailTitle.Name = "DedupeDetailTitle";
            detailTitle.Text = "组内文件（勾选要移入回收站的项目）";
            detailTitle.AutoSize = false;
            detailTitle.AutoEllipsis = true;
            detailTitle.BackColor = Color.Transparent;
            detailTitle.ForeColor = palette.Ink;
            detailTitle.Font = DedupeUiStyle.Font(9F, true);
            detailTitle.Location = new Point(8, 10);
            detailTitle.Size = new Size(280, 22);
            detailHeader.Controls.Add(detailTitle);
            skipGroupBox.Name = "DedupeSkipGroupBox";
            skipGroupBox.Text = "整组跳过";
            skipGroupBox.AutoSize = true;
            skipGroupBox.ForeColor = palette.Ink;
            skipGroupBox.BackColor = Color.Transparent;
            skipGroupBox.Anchor = AnchorStyles.Top | AnchorStyles.Right;
            skipGroupBox.CheckedChanged += delegate { ChangeSkipGroup(); };
            detailHeader.Controls.Add(skipGroupBox);
            setKeepButton.Name = "DedupeSetKeepButton";
            setKeepButton.Text = "将选中行设为保留";
            setKeepButton.Size = new Size(150, 30);
            setKeepButton.Anchor = AnchorStyles.Top | AnchorStyles.Right;
            DedupeUiStyle.Style(setKeepButton, palette, "secondary");
            setKeepButton.Click += delegate { SetSelectedKeep(); };
            detailHeader.Controls.Add(setKeepButton);
            detailHeader.Resize += delegate
            {
                setKeepButton.Left = Math.Max(260, detailHeader.ClientSize.Width - setKeepButton.Width - 8);
                skipGroupBox.Left = Math.Max(180, setKeepButton.Left - skipGroupBox.Width - 10);
                detailTitle.Width = Math.Max(120, skipGroupBox.Left - detailTitle.Left - 10);
            };
            detailGrid.Name = "DedupeDetailGrid";
            detailGrid.Dock = DockStyle.Fill;
            DedupeUiStyle.StyleGrid(detailGrid, palette);
            detailGrid.Columns.Add(new DataGridViewCheckBoxColumn { Name = "Select", HeaderText = "清理", Width = 44, SortMode = DataGridViewColumnSortMode.NotSortable });
            detailGrid.Columns.Add(new DataGridViewTextBoxColumn { Name = "File", HeaderText = "文件", Width = 115, ReadOnly = true, SortMode = DataGridViewColumnSortMode.NotSortable });
            detailGrid.Columns.Add(new DataGridViewTextBoxColumn { Name = "Type", HeaderText = "类型", Width = 76, ReadOnly = true, SortMode = DataGridViewColumnSortMode.NotSortable });
            detailGrid.Columns.Add(new DataGridViewTextBoxColumn { Name = "Quality", HeaderText = "音质", Width = 130, ReadOnly = true, SortMode = DataGridViewColumnSortMode.NotSortable });
            detailGrid.Columns.Add(new DataGridViewTextBoxColumn { Name = "Size", HeaderText = "大小", Width = 56, ReadOnly = true, SortMode = DataGridViewColumnSortMode.NotSortable });
            detailGrid.Columns.Add(new DataGridViewTextBoxColumn { Name = "Modified", HeaderText = "修改时间", Width = 90, ReadOnly = true, SortMode = DataGridViewColumnSortMode.NotSortable });
            var recommendationColumn = new DataGridViewTextBoxColumn
            {
                Name = "Recommendation",
                HeaderText = "建议 / 关联依据",
                AutoSizeMode = DataGridViewAutoSizeColumnMode.Fill,
                MinimumWidth = 120,
                ReadOnly = true,
                SortMode = DataGridViewColumnSortMode.NotSortable
            };
            detailGrid.Columns.Add(recommendationColumn);
            detailGrid.CurrentCellDirtyStateChanged += delegate
            {
                if (detailGrid.IsCurrentCellDirty) detailGrid.CommitEdit(DataGridViewDataErrorContexts.Commit);
            };
            detailGrid.CellValueChanged += delegate(object sender, DataGridViewCellEventArgs e)
            {
                if (updatingDetails || plan == null || e.RowIndex < 0 || e.ColumnIndex != 0) return;
                ApplyDetailCheckbox(e.RowIndex);
            };
            detailPanel.Controls.Add(detailHeader, 0, 0);
            detailPanel.Controls.Add(detailGrid, 0, 1);
            split.Panel2.Controls.Add(detailPanel);
            layout.Controls.Add(split, 0, 2);

            var footer = new Panel { Name = "DedupeFooter", Dock = DockStyle.Fill, BackColor = palette.Background };
            var leftButtons = new FlowLayoutPanel { Dock = DockStyle.Left, Width = 510, FlowDirection = FlowDirection.LeftToRight, WrapContents = false, BackColor = Color.Transparent };
            selectHighButton.Name = "DedupeSelectHighButton";
            selectHighButton.Text = "仅选高可信低音质";
            selectHighButton.Size = new Size(154, 38);
            DedupeUiStyle.Style(selectHighButton, palette, "secondary");
            selectHighButton.Click += delegate { if (plan == null) return; DedupePlanner.ResetDefaults(plan); RefreshAll(); };
            leftButtons.Controls.Add(selectHighButton);
            clearButton.Name = "DedupeClearButton";
            clearButton.Text = "取消全部选择";
            clearButton.Size = new Size(120, 38);
            DedupeUiStyle.Style(clearButton, palette, "secondary");
            clearButton.Click += delegate { if (plan == null) return; DedupePlanner.ClearSelections(plan); RefreshAll(); };
            leftButtons.Controls.Add(clearButton);
            exportButton.Name = "DedupeExportButton";
            exportButton.Text = "导出扫描清单";
            exportButton.Size = new Size(130, 38);
            DedupeUiStyle.Style(exportButton, palette, "secondary");
            exportButton.Click += delegate { ExportPlan(); };
            leftButtons.Controls.Add(exportButton);
            footer.Controls.Add(leftButtons);
            var rightButtons = new FlowLayoutPanel { Dock = DockStyle.Right, Width = 320, FlowDirection = FlowDirection.RightToLeft, WrapContents = false, BackColor = Color.Transparent };
            executeButton.Name = "DedupeExecuteButton";
            executeButton.Text = "确认移入回收站";
            executeButton.Size = new Size(160, 38);
            DedupeUiStyle.Style(executeButton, palette, "danger");
            executeButton.Click += async delegate { await ExecuteQuarantine(); };
            rightButtons.Controls.Add(executeButton);
            closeButton.Name = "DedupeCloseButton";
            closeButton.Text = "取消";
            closeButton.Size = new Size(92, 38);
            DedupeUiStyle.Style(closeButton, palette, "secondary");
            closeButton.Click += delegate { Close(); };
            rightButtons.Controls.Add(closeButton);
            footer.Controls.Add(rightButtons);
            layout.Controls.Add(footer, 0, 3);

            Controls.Add(layout);
        }

        private void ChooseRoot()
        {
            using (var dialog = new FolderBrowserDialog())
            {
                dialog.Description = "选择包含 NCM 歌曲的目录";
                dialog.ShowNewFolderButton = false;
                if (Directory.Exists(rootBox.Text)) dialog.SelectedPath = rootBox.Text;
                if (dialog.ShowDialog(this) != DialogResult.OK) return;
                rootBox.Text = dialog.SelectedPath;
                scanStatus.Text = "目录已更改，点击“开始只读扫描”检查重复歌曲。";
            }
        }

        private async Task ScanAsync()
        {
            if (scanning || executing) return;
            var root = rootBox.Text.Trim();
            if (!Directory.Exists(root)) { MessageBox.Show(this, "扫描目录不存在，请重新选择。", "安全去重", MessageBoxButtons.OK, MessageBoxIcon.Warning); return; }
            if (!File.Exists(scriptPath)) { MessageBox.Show(this, "转换脚本不存在，无法读取歌曲元数据。", "安全去重", MessageBoxButtons.OK, MessageBoxIcon.Error); return; }
            scanning = true;
            SetBusy(true);
            scanButton.Text = "正在扫描…";
            cancelScanButton.Enabled = true;
            scanStatus.Text = "正在只读扫描，不会修改任何文件。";
            groupList.Items.Clear();
            detailGrid.Rows.Clear();
            plan = null;
            UpdateSummary();
            scanCancellation = new CancellationTokenSource();
            var token = scanCancellation.Token;
            if (report != null) report("安全去重", "开始", "正在只读扫描曲库。");
            try
            {
                var options = new DedupePlanOptions
                {
                    Root = root,
                    OutputRoot = outputRoot,
                    Recurse = recurseBox.Checked,
                    ShowAssociated = showAssociatedBox.Checked,
                    IncludeSuspected = includeSuspectedBox.Checked,
                    VerifyAssociatedHashes = true
                };
                var newPlan = await Task.Run(delegate
                {
                    var scan = DedupeScanner.Scan(scriptPath, root, options.Recurse, token);
                    token.ThrowIfCancellationRequested();
                    List<string> warnings;
                    var records = DedupeConversionStore.LoadRecords(out warnings);
                    return DedupePlanner.Build(scan, options, records, warnings);
                }, token);
                if (IsDisposed) return;
                plan = newPlan;
                PopulateGroups();
                UpdateSummary();
                scanStatus.Text = "扫描完成：未修改任何文件。请逐组检查后再确认。";
                if (report != null) report("安全去重", "完成", "扫描完成，未修改任何文件；确认重复 " + plan.ConfirmedGroupCount + " 组。");
            }
            catch (OperationCanceledException)
            {
                if (!IsDisposed)
                {
                    scanStatus.Text = "已取消扫描，未修改任何文件。";
                    if (report != null) report("安全去重", "已取消", "扫描已取消，未修改任何文件。");
                }
            }
            catch (Exception ex)
            {
                if (!IsDisposed)
                {
                    scanStatus.Text = "扫描失败：" + ex.Message;
                    if (report != null) report("安全去重", "失败", ex.Message);
                    MessageBox.Show(this, "扫描失败，未修改任何文件：" + ex.Message, "安全去重", MessageBoxButtons.OK, MessageBoxIcon.Error);
                }
            }
            finally
            {
                if (scanCancellation != null) { scanCancellation.Dispose(); scanCancellation = null; }
                scanning = false;
                if (!IsDisposed)
                {
                    SetBusy(false);
                    scanButton.Text = "重新扫描";
                    cancelScanButton.Enabled = false;
                }
            }
        }

        private void SetBusy(bool busy)
        {
            rootBox.Enabled = !busy;
            browseButton.Enabled = !busy;
            recurseBox.Enabled = !busy;
            showAssociatedBox.Enabled = !busy;
            includeSuspectedBox.Enabled = !busy;
            scanButton.Enabled = !busy;
            groupList.Enabled = !busy;
            detailGrid.Enabled = !busy;
            selectHighButton.Enabled = !busy && plan != null;
            clearButton.Enabled = !busy && plan != null;
            exportButton.Enabled = !busy && plan != null;
            setKeepButton.Enabled = !busy && plan != null;
            skipGroupBox.Enabled = !busy && plan != null;
            executeButton.Enabled = !busy && plan != null && plan.HasPendingSelection;
        }

        private void PopulateGroups()
        {
            groupList.BeginUpdate();
            try
            {
                groupList.Items.Clear();
                if (plan == null) return;
                foreach (var group in plan.Groups)
                {
                    var item = new ListViewItem(group.GroupId);
                    item.SubItems.Add(group.Title ?? "");
                    item.SubItems.Add(group.Artists ?? "");
                    item.SubItems.Add(group.MatchBasis ?? "");
                    item.SubItems.Add(group.Files.Count.ToString(CultureInfo.InvariantCulture));
                    item.SubItems.Add(PendingCount(group).ToString(CultureInfo.InvariantCulture));
                    item.SubItems.Add(GroupRecommendation(group));
                    item.Tag = group;
                    item.ToolTipText = (group.Title ?? "") + " · " + (group.MatchBasis ?? "")
                        + "\r\n" + (GroupRecommendation(group));
                    if (group.Confidence == DedupeConfidence.Medium) item.ForeColor = palette.Muted;
                    else if (group.Confidence == DedupeConfidence.Unconfirmed) item.ForeColor = palette.Error;
                    groupList.Items.Add(item);
                }
                if (groupList.Items.Count > 0) groupList.Items[0].Selected = true;
            }
            finally { groupList.EndUpdate(); }
            if (groupList.Items.Count > 0) ShowSelectedGroup();
            else { detailGrid.Rows.Clear(); }
        }

        private void ShowSelectedGroup()
        {
            var selected = groupList.SelectedItems.Count > 0 ? groupList.SelectedItems[0]
                : groupList.Items.Count > 0 ? groupList.Items[0] : null;
            if (selected == null || plan == null) { detailGrid.Rows.Clear(); return; }
            var group = selected.Tag as DedupeGroup;
            if (group == null) return;
            updatingDetails = true;
            detailGrid.SuspendLayout();
            try
            {
                detailTitle.Text = "组内文件 · " + group.GroupId + " · " + (group.Title ?? "")
                    + (string.IsNullOrWhiteSpace(group.SongId) ? "" : " · 歌曲 ID " + group.SongId)
                    + " · " + DedupePlanner.ConfidenceLabel(group.Confidence);
                skipGroupBox.Checked = group.SkipGroup;
                detailGrid.Rows.Clear();
                foreach (var file in group.Files)
                {
                    var index = detailGrid.Rows.Add(file.IsSelected, file.Path, "NCM 源文件",
                        file.Quality == null ? "" : file.Quality.Label, DedupeUiStyle.Size(file.Size),
                        DedupeUiStyle.Time(file.ModifiedUtc), file.Recommendation ?? "");
                    var row = detailGrid.Rows[index];
                    row.Tag = new DetailTag { Group = group, Source = file };
                    row.Cells[1].ToolTipText = file.Path;
                    row.Cells[6].ToolTipText = (file.Recommendation ?? "") + (string.IsNullOrWhiteSpace(file.Warning) ? "" : "\r\n" + file.Warning);
                    row.Cells[0].ReadOnly = file.IsKeep || !file.CanSelect;
                    if (file.IsKeep) row.DefaultCellStyle.BackColor = palette.Secondary;
                }
                foreach (var associated in group.Associated)
                {
                    var text = associated.Basis ?? "";
                    if (!string.IsNullOrWhiteSpace(associated.Warning)) text += "；" + associated.Warning;
                    var index = detailGrid.Rows.Add(associated.IsSelected, associated.Path,
                        associated.KindLabel, "", DedupeUiStyle.Size(associated.Size),
                        DedupeUiStyle.Time(associated.ModifiedUtc), text);
                    var row = detailGrid.Rows[index];
                    row.Tag = new DetailTag { Group = group, Associated = associated };
                    row.Cells[1].ToolTipText = associated.Path;
                    row.Cells[6].ToolTipText = text;
                    row.Cells[0].ReadOnly = !associated.CanSelect;
                    if (!associated.Verified) row.DefaultCellStyle.ForeColor = palette.Muted;
                    else if (associated.IsOnlyPlayableCopy) row.DefaultCellStyle.ForeColor = palette.Error;
                }
                if (detailGrid.Rows.Count > 0) detailGrid.ClearSelection();
            }
            finally
            {
                updatingDetails = false;
                detailGrid.ResumeLayout();
            }
        }

        private void ApplyDetailCheckbox(int rowIndex)
        {
            if (rowIndex < 0 || rowIndex >= detailGrid.Rows.Count) return;
            var row = detailGrid.Rows[rowIndex];
            var tag = row.Tag as DetailTag;
            if (tag == null) return;
            var value = row.Cells[0].Value != null && Convert.ToBoolean(row.Cells[0].Value, CultureInfo.InvariantCulture);
            if (tag.Source != null)
            {
                if (tag.Source.IsKeep || !tag.Source.CanSelect) { RefreshDetailCheckboxes(); return; }
                tag.Source.IsSelected = value;
            }
            else if (tag.Associated != null)
            {
                if (!tag.Associated.CanSelect) { RefreshDetailCheckboxes(); return; }
                tag.Associated.IsSelected = value;
            }
            DedupePlanner.Recalculate(plan);
            RefreshDetailCheckboxes();
            UpdateGroupRow(tag.Group);
            UpdateSummary();
        }

        private void RefreshDetailCheckboxes()
        {
            updatingDetails = true;
            try
            {
                foreach (DataGridViewRow row in detailGrid.Rows)
                {
                    var tag = row.Tag as DetailTag;
                    if (tag == null) continue;
                    if (tag.Source != null)
                    {
                        row.Cells[0].ReadOnly = tag.Source.IsKeep || !tag.Source.CanSelect;
                        row.Cells[0].Value = tag.Source.IsSelected;
                        row.Cells[6].Value = tag.Source.Recommendation ?? "";
                    }
                    else if (tag.Associated != null)
                    {
                        row.Cells[0].ReadOnly = !tag.Associated.CanSelect;
                        row.Cells[0].Value = tag.Associated.IsSelected;
                    }
                }
            }
            finally { updatingDetails = false; }
        }

        private void ChangeSkipGroup()
        {
            if (updatingDetails || plan == null) return;
            if (groupList.SelectedItems.Count == 0) return;
            var group = groupList.SelectedItems[0].Tag as DedupeGroup;
            if (group == null) return;
            group.SkipGroup = skipGroupBox.Checked;
            if (group.SkipGroup)
            {
                foreach (var file in group.Files) file.IsSelected = false;
                foreach (var associated in group.Associated) associated.IsSelected = false;
            }
            else
            {
                DedupePlanner.SetKeep(group, group.KeepPath);
            }
            DedupePlanner.Recalculate(plan);
            ShowSelectedGroup();
            UpdateGroupRow(group);
            UpdateSummary();
        }

        private static string GroupRecommendation(DedupeGroup group)
        {
            return group.SkipGroup ? "已跳过本组" : (group.Recommendation ?? "");
        }

        private void SetSelectedKeep()
        {
            if (plan == null || detailGrid.CurrentRow == null)
            {
                MessageBox.Show(this, "请先在右侧组内文件中选择一个 NCM 源文件。", "设置保留项", MessageBoxButtons.OK, MessageBoxIcon.Information);
                return;
            }
            var tag = detailGrid.CurrentRow.Tag as DetailTag;
            if (tag == null || tag.Source == null)
            {
                MessageBox.Show(this, "关联输出不能设为保留项；请选择一个 NCM 源文件。", "设置保留项", MessageBoxButtons.OK, MessageBoxIcon.Information);
                return;
            }
            DedupePlanner.SetKeep(tag.Group, tag.Source.Path);
            DedupePlanner.Recalculate(plan);
            ShowSelectedGroup();
            UpdateGroupRow(tag.Group);
            UpdateSummary();
        }

        private void RefreshAll()
        {
            PopulateGroups();
            UpdateSummary();
        }

        private void UpdateGroupRow(DedupeGroup group)
        {
            if (group == null) return;
            foreach (ListViewItem item in groupList.Items)
            {
                if (!ReferenceEquals(item.Tag, group)) continue;
                item.SubItems[5].Text = PendingCount(group).ToString(CultureInfo.InvariantCulture);
                item.SubItems[6].Text = GroupRecommendation(group);
                break;
            }
        }

        private static int PendingCount(DedupeGroup group)
        {
            return group.Files.Count(file => file.IsSelected) + group.Associated.Count(file => file.IsSelected);
        }

        private void UpdateSummary()
        {
            if (plan == null)
            {
                summaryLabel.Text = "尚未扫描。\r\n扫描只读取 NCM 元数据和转换来源记录，不会移动、覆盖或删除文件。";
                warningsLabel.Text = "";
                executeButton.Enabled = false;
                selectHighButton.Enabled = false;
                clearButton.Enabled = false;
                exportButton.Enabled = false;
                setKeepButton.Enabled = false;
                skipGroupBox.Enabled = false;
                return;
            }
            var verified = plan.Groups.Sum(group => group.Associated.Count(file => file.Verified));
            var suspected = plan.AssociatedCount - verified;
            var onlyCopy = plan.Groups.Sum(group => group.Associated.Count(file => file.IsSelected && file.IsOnlyPlayableCopy));
            var text = new StringBuilder();
            text.Append("扫描文件 ").Append(plan.ScannedFileCount).Append(" 首 · 确认重复 ").Append(plan.ConfirmedGroupCount)
                .Append(" 组 · 疑似 ").Append(plan.SuspectedGroupCount).Append(" 组 · 需人工检查 ").Append(plan.ReviewGroupCount).Append(" 组\r\n");
            text.Append("建议保留 ").Append(plan.KeepFileCount).Append(" 个源文件 · 待处理 ").Append(plan.SelectedSourceCount)
                .Append(" 个源文件 + ").Append(plan.SelectedAssociatedCount).Append(" 个已确认关联文件 · 待回收 ")
                .Append(DedupeUiStyle.Size(plan.SelectedBytes)).Append("\r\n");
            text.Append("关联输出 ").Append(plan.AssociatedCount).Append(" 个（已确认 ").Append(verified)
                .Append("，疑似/异常 ").Append(suspected)
                .Append("）· 移入回收站后仍占用磁盘空间，彻底清空后才释放");
            if (onlyCopy > 0) text.Append(" · 其中 ").Append(onlyCopy).Append(" 个音频可能是唯一可播放副本");
            summaryLabel.Text = text.ToString();
            var warningText = plan.Warnings.Count == 0 ? "" : string.Join(" · ", plan.Warnings.Take(3).ToArray());
            warningsLabel.Text = warningText;
            tips.SetToolTip(warningsLabel, plan.Warnings.Count == 0 ? "" : string.Join(Environment.NewLine, plan.Warnings.ToArray()));
            executeButton.Enabled = !scanning && !executing && plan.HasPendingSelection;
        }

        private void ExportPlan()
        {
            if (plan == null) { MessageBox.Show(this, "请先完成扫描。", "导出扫描清单", MessageBoxButtons.OK, MessageBoxIcon.Information); return; }
            using (var dialog = new SaveFileDialog())
            {
                dialog.Title = "导出安全去重扫描清单";
                dialog.Filter = "CSV 文件 (*.csv)|*.csv";
                dialog.FileName = "安全去重扫描清单.csv";
                if (dialog.ShowDialog(this) != DialogResult.OK) return;
                try
                {
                    DedupePlanner.ExportCsv(plan, dialog.FileName);
                    scanStatus.Text = "扫描清单已导出：" + dialog.FileName;
                    if (report != null) report("安全去重", "完成", "扫描清单已导出，未修改任何歌曲文件。");
                }
                catch (Exception ex)
                {
                    MessageBox.Show(this, "导出失败：" + ex.Message, "导出扫描清单", MessageBoxButtons.OK, MessageBoxIcon.Error);
                }
            }
        }

        private async Task ExecuteQuarantine()
        {
            if (plan == null || executing || scanning) return;
            var errors = DedupePlanner.ValidateSelection(plan);
            if (errors.Count > 0)
            {
                MessageBox.Show(this, string.Join(Environment.NewLine, errors.Take(15).ToArray()), "无法开始处理", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                return;
            }
            var onlyCopy = plan.Groups.Sum(group => group.Associated.Count(file => file.IsSelected && file.IsOnlyPlayableCopy));
            var message = "本次将把 " + plan.SelectedSourceCount + " 个源文件和 " + plan.SelectedAssociatedCount
                + " 个已确认关联文件移入工具箱回收站。\r\n\r\n待回收大小：" + DedupeUiStyle.Size(plan.SelectedBytes)
                + "\r\n\r\n文件不会立即永久删除，可以从“设置与维护 → 工具箱回收站”恢复。"
                + "\r\n彻底清空回收站后才会释放磁盘空间。";
            if (onlyCopy > 0) message += "\r\n\r\n注意：其中 " + onlyCopy + " 个音频可能是该歌曲唯一的可播放副本，请确认仍要清理。";
            if (MessageBox.Show(this, message, "确认安全去重", MessageBoxButtons.YesNo, MessageBoxIcon.Warning, MessageBoxDefaultButton.Button2) != DialogResult.Yes)
                return;
            executing = true;
            SetBusy(true);
            scanStatus.Text = "正在检查文件并移入回收站…";
            DedupeProgressForm progress = null;
            try
            {
                progress = new DedupeProgressForm(palette);
                progress.Show(this);
                var result = await Task.Run(() => DedupeQuarantine.Execute(plan, progress.SetStatus));
                if (!IsDisposed)
                {
                    progress.CloseFromOwner();
                    progress = null;
                    var detail = "已移入回收站 " + result.MovedCount + " 个文件（" + DedupeUiStyle.Size(result.MovedBytes) + "）"
                        + (result.FailedCount > 0 ? "，失败 " + result.FailedCount + " 个。" : "。")
                        + (string.IsNullOrEmpty(result.OperationId) ? "" : " 操作编号：" + result.OperationId + "。");
                    if (result.MovedCount > 0)
                    {
                        if (report != null) report("安全去重", result.FailedCount > 0 ? "部分失败" : "完成", detail);
                        if (filesChanged != null) filesChanged();
                        MessageBox.Show(this, detail + "\r\n\r\n可在“设置与维护 → 工具箱回收站”恢复。", "安全去重",
                            MessageBoxButtons.OK, result.FailedCount > 0 ? MessageBoxIcon.Warning : MessageBoxIcon.Information);
                        allowClose = true;
                        DialogResult = DialogResult.OK;
                        Close();
                    }
                    else
                    {
                        if (report != null) report("安全去重", "失败", detail + " " + result.Error);
                        MessageBox.Show(this, detail + "\r\n\r\n" + (result.Error ?? ""), "安全去重", MessageBoxButtons.OK, MessageBoxIcon.Error);
                        scanStatus.Text = "未移动任何文件：" + (result.Error ?? "");
                    }
                }
            }
            catch (Exception ex)
            {
                if (!IsDisposed)
                {
                    if (report != null) report("安全去重", "失败", ex.Message);
                    MessageBox.Show(this, "处理失败，未移动或只移动了部分文件：" + ex.Message + "\r\n请到工具箱回收站查看已有记录。", "安全去重", MessageBoxButtons.OK, MessageBoxIcon.Error);
                }
            }
            finally
            {
                if (progress != null) { try { progress.CloseFromOwner(); } catch { } }
                executing = false;
                if (!IsDisposed)
                {
                    SetBusy(false);
                    UpdateSummary();
                }
            }
        }
    }

    internal sealed class DedupeTrashForm : Form
    {
        private sealed class TrashRow
        {
            public DedupeOperation Operation;
            public DedupeOperationItem Item;
        }

        private sealed class TrashLoadResult
        {
            public List<DedupeOperation> Operations;
            public List<string> Warnings;
            public string Notice;
        }

        private readonly DedupePalette palette;
        private readonly Action<string, string, string> report;
        private readonly Action filesChanged;
        private readonly Label summaryLabel = new Label();
        private readonly ComboBox taskFilter = new ComboBox();
        private readonly CheckBox restorableOnly = new CheckBox();
        private readonly ListView trashList = new ListView();
        private readonly Label statusLabel = new Label();
        private readonly Button restoreSelectedButton = new Button();
        private readonly Button restoreTaskButton = new Button();
        private readonly Button restoreGroupButton = new Button();
        private readonly Button openFolderButton = new Button();
        private readonly Button viewLogButton = new Button();
        private readonly Button purgeButton = new Button();
        private readonly Button refreshButton = new Button();
        private readonly Button closeButton = new Button();
        private List<DedupeOperation> operations = new List<DedupeOperation>();
        private List<TrashRow> allRows = new List<TrashRow>();
        private bool busy;
        private DedupeProgressForm progress;

        public DedupeTrashForm(DedupePalette palette, Action<string, string, string> report, Action filesChanged)
        {
            this.palette = palette ?? DedupePalette.Dark;
            this.report = report;
            this.filesChanged = filesChanged;
            Text = "工具箱回收站";
            StartPosition = FormStartPosition.CenterParent;
            Size = new Size(1160, 720);
            MinimumSize = new Size(940, 580);
            AutoScaleMode = AutoScaleMode.Dpi;
            AutoScaleDimensions = new SizeF(96F, 96F);
            BackColor = this.palette.Background;
            ForeColor = this.palette.Ink;
            Font = DedupeUiStyle.Font(9F, false);
            BuildUi();
            Shown += async delegate { await LoadRowsAsync(); };
            FormClosing += delegate(object sender, FormClosingEventArgs e)
            {
                if (busy)
                {
                    e.Cancel = true;
                    MessageBox.Show(this, "回收站正在处理文件，请等待当前操作完成。", "工具箱回收站", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                }
            };
        }

        private void BuildUi()
        {
            var layout = new TableLayoutPanel
            {
                Dock = DockStyle.Fill,
                ColumnCount = 1,
                RowCount = 4,
                Padding = new Padding(18, 14, 18, 14),
                BackColor = palette.Background
            };
            layout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 76));
            layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 42));
            layout.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
            layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 92));

            var header = new Panel { Dock = DockStyle.Fill, BackColor = palette.Panel, Padding = new Padding(14, 10, 14, 10) };
            var title = DedupeUiStyle.Label("工具箱回收站", 12F, true, palette.Ink);
            title.Location = new Point(14, 10);
            header.Controls.Add(title);
            summaryLabel.AutoSize = false;
            summaryLabel.Location = new Point(14, 38);
            summaryLabel.Size = new Size(1000, 24);
            summaryLabel.ForeColor = palette.Muted;
            summaryLabel.AutoEllipsis = true;
            header.Controls.Add(summaryLabel);
            refreshButton.Text = "刷新";
            refreshButton.Size = new Size(84, 32);
            refreshButton.Anchor = AnchorStyles.Top | AnchorStyles.Right;
            DedupeUiStyle.Style(refreshButton, palette, "secondary");
            refreshButton.Click += async delegate { await LoadRowsAsync(); };
            header.Controls.Add(refreshButton);
            closeButton.Text = "关闭";
            closeButton.Size = new Size(84, 32);
            closeButton.Anchor = AnchorStyles.Top | AnchorStyles.Right;
            DedupeUiStyle.Style(closeButton, palette, "secondary");
            closeButton.Click += delegate { Close(); };
            header.Controls.Add(closeButton);
            header.Resize += delegate
            {
                closeButton.Left = header.ClientSize.Width - closeButton.Width - 14;
                refreshButton.Left = closeButton.Left - refreshButton.Width - 8;
                summaryLabel.Width = Math.Max(200, refreshButton.Left - summaryLabel.Left - 12);
            };
            layout.Controls.Add(header, 0, 0);

            var filterPanel = new FlowLayoutPanel { Dock = DockStyle.Fill, FlowDirection = FlowDirection.LeftToRight, WrapContents = false, BackColor = palette.Panel, Padding = new Padding(12, 6, 12, 6) };
            var filterLabel = DedupeUiStyle.Label("任务", 9F, true, palette.Ink);
            filterLabel.Margin = new Padding(0, 8, 8, 0);
            filterPanel.Controls.Add(filterLabel);
            taskFilter.Name = "TrashTaskFilter";
            taskFilter.DropDownStyle = ComboBoxStyle.DropDownList;
            taskFilter.Width = 240;
            taskFilter.Margin = new Padding(0, 3, 16, 0);
            DedupeUiStyle.StyleInput(taskFilter, palette);
            taskFilter.SelectedIndexChanged += delegate { ApplyFilter(); };
            filterPanel.Controls.Add(taskFilter);
            restorableOnly.Text = "仅显示可恢复文件";
            restorableOnly.AutoSize = true;
            restorableOnly.ForeColor = palette.Ink;
            restorableOnly.BackColor = Color.Transparent;
            restorableOnly.Margin = new Padding(0, 6, 0, 0);
            restorableOnly.CheckedChanged += delegate { ApplyFilter(); };
            filterPanel.Controls.Add(restorableOnly);
            statusLabel.AutoSize = false;
            statusLabel.Width = 420;
            statusLabel.Height = 24;
            statusLabel.Margin = new Padding(18, 6, 0, 0);
            statusLabel.ForeColor = palette.Muted;
            statusLabel.AutoEllipsis = true;
            filterPanel.Controls.Add(statusLabel);
            layout.Controls.Add(filterPanel, 0, 1);

            trashList.Name = "TrashList";
            trashList.Dock = DockStyle.Fill;
            DedupeUiStyle.StyleList(trashList, palette);
            trashList.MultiSelect = true;
            trashList.Columns.Add("任务", 150);
            trashList.Columns.Add("文件", 190);
            trashList.Columns.Add("来源", 320);
            trashList.Columns.Add("操作时间", 130);
            trashList.Columns.Add("大小", 78);
            trashList.Columns.Add("状态", 90);
            trashList.Columns.Add("重复组", 70);
            trashList.Columns.Add("隔离位置", 300);
            trashList.SelectedIndexChanged += delegate { UpdateButtons(); };
            layout.Controls.Add(trashList, 0, 2);

            var footer = new FlowLayoutPanel { Dock = DockStyle.Fill, FlowDirection = FlowDirection.LeftToRight, WrapContents = true, BackColor = palette.Background };
            restoreSelectedButton.Text = "恢复选中";
            restoreSelectedButton.Size = new Size(108, 36);
            DedupeUiStyle.Style(restoreSelectedButton, palette, "primary");
            restoreSelectedButton.Click += async delegate { await RestoreSelectedAsync(); };
            footer.Controls.Add(restoreSelectedButton);
            restoreTaskButton.Text = "恢复整个任务";
            restoreTaskButton.Size = new Size(126, 36);
            DedupeUiStyle.Style(restoreTaskButton, palette, "secondary");
            restoreTaskButton.Click += async delegate { await RestoreTaskAsync(); };
            footer.Controls.Add(restoreTaskButton);
            restoreGroupButton.Text = "恢复同组文件";
            restoreGroupButton.Size = new Size(120, 36);
            DedupeUiStyle.Style(restoreGroupButton, palette, "secondary");
            restoreGroupButton.Click += async delegate { await RestoreGroupAsync(); };
            footer.Controls.Add(restoreGroupButton);
            openFolderButton.Text = "打开隔离目录";
            openFolderButton.Size = new Size(120, 36);
            DedupeUiStyle.Style(openFolderButton, palette, "secondary");
            openFolderButton.Click += delegate { OpenIsolation(); };
            footer.Controls.Add(openFolderButton);
            viewLogButton.Text = "查看操作日志";
            viewLogButton.Size = new Size(120, 36);
            DedupeUiStyle.Style(viewLogButton, palette, "secondary");
            viewLogButton.Click += delegate { ViewLog(); };
            footer.Controls.Add(viewLogButton);
            purgeButton.Text = "永久清理选中";
            purgeButton.Size = new Size(130, 36);
            DedupeUiStyle.Style(purgeButton, palette, "danger");
            purgeButton.Click += async delegate { await PurgeSelectedAsync(); };
            footer.Controls.Add(purgeButton);
            layout.Controls.Add(footer, 0, 3);
            Controls.Add(layout);
        }

        private async Task LoadRowsAsync(bool force = false)
        {
            if (busy && !force) return;
            busy = true;
            SetBusy(true);
            statusLabel.Text = "正在读取回收站记录…";
            try
            {
                var data = await Task.Run(delegate
                {
                    var notice = DedupeQuarantine.MarkInterruptedOperations();
                    List<string> warnings;
                    var loaded = DedupeQuarantine.LoadOperations(out warnings);
                    return new TrashLoadResult { Operations = loaded, Warnings = warnings, Notice = notice };
                });
                if (IsDisposed) return;
                operations = data.Operations ?? new List<DedupeOperation>();
                allRows = new List<TrashRow>();
                foreach (var operation in operations)
                    foreach (var item in operation.Items)
                        allRows.Add(new TrashRow { Operation = operation, Item = item });
                var selectedTask = taskFilter.SelectedItem as string;
                taskFilter.Items.Clear();
                taskFilter.Items.Add("全部任务");
                foreach (var operation in operations) taskFilter.Items.Add(operation.OperationId);
                if (selectedTask != null && taskFilter.Items.Contains(selectedTask)) taskFilter.SelectedItem = selectedTask;
                else taskFilter.SelectedIndex = 0;
                ApplyFilter();
                var notices = new List<string>();
                if (!string.IsNullOrWhiteSpace(data.Notice)) notices.Add(data.Notice);
                if (data.Warnings != null) notices.AddRange(data.Warnings);
                statusLabel.Text = notices.Count == 0 ? "未发现异常。" : string.Join("；", notices.Take(2).ToArray());
                if (notices.Count > 0 && report != null) report("工具箱回收站", "部分失败", notices[0]);
            }
            catch (Exception ex)
            {
                if (!IsDisposed)
                {
                    statusLabel.Text = "读取回收站失败：" + ex.Message;
                    if (report != null) report("工具箱回收站", "失败", ex.Message);
                }
            }
            finally
            {
                busy = false;
                if (!IsDisposed) SetBusy(false);
            }
        }

        private void ApplyFilter()
        {
            var selectedTask = taskFilter.SelectedItem as string;
            var onlyRestorable = restorableOnly.Checked;
            var rows = allRows.Where(row =>
                (selectedTask == null || selectedTask == "全部任务" || row.Operation.OperationId == selectedTask)
                && (!onlyRestorable || row.Item.Status == DedupeStatuses.Moved || row.Item.Status == DedupeStatuses.RestoreFailed))
                .OrderByDescending(row => row.Operation.CreatedUtc)
                .ThenBy(row => row.Item.GroupId)
                .ThenBy(row => row.Item.OriginalPath)
                .ToList();
            trashList.BeginUpdate();
            try
            {
                trashList.Items.Clear();
                foreach (var row in rows)
                {
                    var item = new ListViewItem(row.Operation.OperationId);
                    item.SubItems.Add(Path.GetFileName(row.Item.OriginalPath));
                    item.SubItems.Add(row.Item.OriginalPath);
                    item.SubItems.Add(DedupeUiStyle.Time(!string.IsNullOrWhiteSpace(row.Item.MovedUtc) ? row.Item.MovedUtc : row.Operation.CreatedUtc));
                    item.SubItems.Add(DedupeUiStyle.Size(row.Item.Size));
                    item.SubItems.Add(DedupeStatuses.Label(row.Item.Status));
                    item.SubItems.Add(row.Item.GroupId ?? "");
                    item.SubItems.Add(row.Item.QuarantinePath ?? "");
                    item.Tag = row;
                    item.ToolTipText = row.Item.OriginalPath + Environment.NewLine + "隔离文件：" + row.Item.QuarantinePath
                        + (string.IsNullOrWhiteSpace(row.Item.Error) ? "" : Environment.NewLine + "说明：" + row.Item.Error);
                    if (row.Item.Status == DedupeStatuses.Failed || row.Item.Status == DedupeStatuses.RestoreFailed || row.Item.Status == DedupeStatuses.PurgeFailed)
                        item.ForeColor = palette.Error;
                    else if (row.Item.Status == DedupeStatuses.Restored || row.Item.Status == DedupeStatuses.Purged)
                        item.ForeColor = palette.Muted;
                    else
                        item.ForeColor = palette.Ink;
                    trashList.Items.Add(item);
                }
            }
            finally { trashList.EndUpdate(); }
            UpdateSummary();
            UpdateButtons();
        }

        private void UpdateSummary()
        {
            long bytes = 0;
            int restorable = 0;
            int total = 0;
            foreach (var operation in operations)
            {
                bytes += DedupeQuarantine.RemainingBytes(operation);
                foreach (var item in operation.Items)
                {
                    total++;
                    if (item.Status == DedupeStatuses.Moved || item.Status == DedupeStatuses.RestoreFailed) restorable++;
                }
            }
            summaryLabel.Text = "隔离占用 " + DedupeUiStyle.Size(bytes) + " · 可恢复 " + restorable + " 个 · 记录 " + total
                + " 个。隔离文件仍占用磁盘空间，永久清理选中项后才会释放；不会自动过期清空。";
        }

        private List<TrashRow> SelectedRows()
        {
            var result = new List<TrashRow>();
            foreach (ListViewItem item in trashList.SelectedItems)
            {
                var row = item.Tag as TrashRow;
                if (row != null) result.Add(row);
            }
            return result;
        }

        private void SetBusy(bool value)
        {
            taskFilter.Enabled = !value;
            restorableOnly.Enabled = !value;
            refreshButton.Enabled = !value;
            UpdateButtons();
        }

        private void UpdateButtons()
        {
            var rows = SelectedRows();
            var any = rows.Count > 0;
            var restorable = rows.Any(row => row.Item.Status == DedupeStatuses.Moved || row.Item.Status == DedupeStatuses.RestoreFailed);
            restoreSelectedButton.Enabled = !busy && restorable;
            restoreTaskButton.Enabled = !busy && any;
            restoreGroupButton.Enabled = !busy && any;
            openFolderButton.Enabled = !busy && (any || operations.Count > 0);
            viewLogButton.Enabled = !busy && (any || operations.Count > 0);
            purgeButton.Enabled = !busy && rows.Any(row => row.Item.Status == DedupeStatuses.Moved);
            refreshButton.Enabled = !busy;
            closeButton.Enabled = true;
        }

        private async Task RestoreSelectedAsync()
        {
            var rows = SelectedRows();
            if (rows.Count == 0) { MessageBox.Show(this, "请先在列表中选择要恢复的文件。", "恢复文件", MessageBoxButtons.OK, MessageBoxIcon.Information); return; }
            busy = true;
            SetBusy(true);
            var restored = 0;
            var failures = new List<string>();
            var index = 0;
            try
            {
                foreach (var row in rows)
                {
                    index++;
                    if (row.Item.Status != DedupeStatuses.Moved && row.Item.Status != DedupeStatuses.RestoreFailed) continue;
                    statusLabel.Text = "正在恢复 " + index + "/" + rows.Count + "：" + Path.GetFileName(row.Item.OriginalPath);
                    var result = await Task.Run(() => DedupeQuarantine.RestoreItem(row.Operation.OperationId, row.Item.ItemId,
                        DedupeQuarantine.ConflictCancel, null, null));
                    if (result.Conflict)
                    {
                        var choice = MessageBox.Show(this,
                            "原位置已存在同名文件，未覆盖：\r\n" + row.Item.OriginalPath
                            + "\r\n\r\n是：自动生成新文件名恢复\r\n否：选择其他文件夹恢复\r\n取消：暂不恢复",
                            "恢复冲突", MessageBoxButtons.YesNoCancel, MessageBoxIcon.Question);
                        if (choice == DialogResult.Yes)
                        {
                            result = await Task.Run(() => DedupeQuarantine.RestoreItem(row.Operation.OperationId, row.Item.ItemId,
                                DedupeQuarantine.ConflictRename, null, null));
                        }
                        else if (choice == DialogResult.No)
                        {
                            using (var dialog = new FolderBrowserDialog())
                            {
                                dialog.Description = "选择恢复文件保存的文件夹";
                                if (dialog.ShowDialog(this) == DialogResult.OK)
                                {
                                    var destination = dialog.SelectedPath;
                                    result = await Task.Run(() => DedupeQuarantine.RestoreItem(row.Operation.OperationId, row.Item.ItemId,
                                        DedupeQuarantine.ConflictCustomDirectory, destination, null));
                                }
                                else result = new DedupeRestoreResult { Error = "已取消恢复。" };
                            }
                        }
                        else result = new DedupeRestoreResult { Error = "已取消恢复。" };
                    }
                    if (result.Success) restored++;
                    else if (!string.IsNullOrWhiteSpace(result.Error)) failures.Add(Path.GetFileName(row.Item.OriginalPath) + "：" + result.Error);
                }
                await LoadRowsAsync(true);
                if (restored > 0 && filesChanged != null) filesChanged();
                var message = "已恢复 " + restored + " 个文件。" + (failures.Count > 0 ? "\r\n失败/取消 " + failures.Count + " 个：" + string.Join("；", failures.Take(5).ToArray()) : "");
                statusLabel.Text = message;
                if (report != null) report("工具箱回收站", failures.Count > 0 ? "部分失败" : "完成", message);
                MessageBox.Show(this, message, "恢复文件", MessageBoxButtons.OK, failures.Count > 0 ? MessageBoxIcon.Warning : MessageBoxIcon.Information);
            }
            catch (Exception ex)
            {
                if (report != null) report("工具箱回收站", "失败", ex.Message);
                MessageBox.Show(this, "恢复失败：" + ex.Message, "恢复文件", MessageBoxButtons.OK, MessageBoxIcon.Error);
            }
            finally
            {
                busy = false;
                if (!IsDisposed) { SetBusy(false); UpdateButtons(); }
            }
        }

        private async Task RestoreTaskAsync()
        {
            var rows = SelectedRows();
            if (rows.Count == 0) { MessageBox.Show(this, "请先选择任务中的任意文件。", "恢复整个任务", MessageBoxButtons.OK, MessageBoxIcon.Information); return; }
            var operationId = rows[0].Operation.OperationId;
            if (MessageBox.Show(this, "将恢复任务 " + operationId + " 中所有可恢复文件。\r\n同名冲突会自动生成新文件名，不会覆盖现有文件。\r\n\r\n是否继续？",
                "恢复整个任务", MessageBoxButtons.YesNo, MessageBoxIcon.Question, MessageBoxDefaultButton.Button2) != DialogResult.Yes) return;
            busy = true;
            SetBusy(true);
            progress = new DedupeProgressForm(palette);
            progress.SetStatus("正在恢复任务 " + operationId);
            progress.Show(this);
            try
            {
                var result = await Task.Run(() => DedupeQuarantine.RestoreOperation(operationId, DedupeQuarantine.ConflictRename, progress.SetStatus));
                if (progress != null) { progress.CloseFromOwner(); progress = null; }
                await LoadRowsAsync(true);
                if (result.MovedCount > 0 && filesChanged != null) filesChanged();
                var message = "已恢复 " + result.MovedCount + " 个文件。" + (result.FailedCount > 0 ? "\r\n失败 " + result.FailedCount + " 个：" + string.Join("；", result.Failures.Take(5).ToArray()) : "");
                statusLabel.Text = message;
                if (report != null) report("工具箱回收站", result.FailedCount > 0 ? "部分失败" : "完成", message);
                MessageBox.Show(this, message, "恢复整个任务", MessageBoxButtons.OK, result.FailedCount > 0 ? MessageBoxIcon.Warning : MessageBoxIcon.Information);
            }
            catch (Exception ex)
            {
                if (report != null) report("工具箱回收站", "失败", ex.Message);
                MessageBox.Show(this, "恢复失败：" + ex.Message, "恢复整个任务", MessageBoxButtons.OK, MessageBoxIcon.Error);
            }
            finally
            {
                if (progress != null) { try { progress.CloseFromOwner(); } catch { } progress = null; }
                busy = false;
                if (!IsDisposed) { SetBusy(false); UpdateButtons(); }
            }
        }

        private async Task RestoreGroupAsync()
        {
            var rows = SelectedRows();
            if (rows.Count == 0) { MessageBox.Show(this, "请先选择重复组中的任意文件。", "恢复同组文件", MessageBoxButtons.OK, MessageBoxIcon.Information); return; }
            var row = rows[0];
            var sameGroup = row.Operation.Items.Where(item => item.GroupId == row.Item.GroupId
                && (item.Status == DedupeStatuses.Moved || item.Status == DedupeStatuses.RestoreFailed)).ToList();
            if (sameGroup.Count == 0) { MessageBox.Show(this, "该重复组没有可恢复文件。", "恢复同组文件", MessageBoxButtons.OK, MessageBoxIcon.Information); return; }
            if (MessageBox.Show(this, "将恢复重复组 " + row.Item.GroupId + " 中的 " + sameGroup.Count + " 个文件。\r\n同名冲突会自动生成新文件名，不会覆盖现有文件。\r\n\r\n是否继续？",
                "恢复同组文件", MessageBoxButtons.YesNo, MessageBoxIcon.Question, MessageBoxDefaultButton.Button2) != DialogResult.Yes) return;
            busy = true;
            SetBusy(true);
            progress = new DedupeProgressForm(palette);
            progress.SetStatus("正在恢复重复组 " + row.Item.GroupId);
            progress.Show(this);
            try
            {
                var fail = new List<string>();
                var restored = 0;
                foreach (var item in sameGroup)
                {
                    var result = await Task.Run(() => DedupeQuarantine.RestoreItem(row.Operation.OperationId, item.ItemId,
                        DedupeQuarantine.ConflictRename, null, null));
                    if (result.Success) restored++;
                    else fail.Add(Path.GetFileName(item.OriginalPath) + "：" + result.Error);
                }
                if (progress != null) { progress.CloseFromOwner(); progress = null; }
                await LoadRowsAsync(true);
                if (restored > 0 && filesChanged != null) filesChanged();
                var message = "已恢复 " + restored + " 个文件。" + (fail.Count > 0 ? "\r\n失败 " + fail.Count + " 个：" + string.Join("；", fail.Take(5).ToArray()) : "");
                statusLabel.Text = message;
                if (report != null) report("工具箱回收站", fail.Count > 0 ? "部分失败" : "完成", message);
                MessageBox.Show(this, message, "恢复同组文件", MessageBoxButtons.OK, fail.Count > 0 ? MessageBoxIcon.Warning : MessageBoxIcon.Information);
            }
            catch (Exception ex)
            {
                if (report != null) report("工具箱回收站", "失败", ex.Message);
                MessageBox.Show(this, "恢复失败：" + ex.Message, "恢复同组文件", MessageBoxButtons.OK, MessageBoxIcon.Error);
            }
            finally
            {
                if (progress != null) { try { progress.CloseFromOwner(); } catch { } progress = null; }
                busy = false;
                if (!IsDisposed) { SetBusy(false); UpdateButtons(); }
            }
        }

        private async Task PurgeSelectedAsync()
        {
            var rows = SelectedRows().Where(row => row.Item.Status == DedupeStatuses.Moved).ToList();
            if (rows.Count == 0) { MessageBox.Show(this, "请选择状态为“可恢复”的文件；已恢复或异常文件不会永久清理。", "永久清理", MessageBoxButtons.OK, MessageBoxIcon.Information); return; }
            var bytes = rows.Sum(row => row.Item.Size);
            if (MessageBox.Show(this, "永久清理后文件无法恢复，也没有 Windows 系统回收站备份。\r\n将清理 " + rows.Count + " 个文件（" + DedupeUiStyle.Size(bytes) + "）。\r\n\r\n是否继续？",
                "永久清理警告", MessageBoxButtons.YesNo, MessageBoxIcon.Warning, MessageBoxDefaultButton.Button2) != DialogResult.Yes) return;
            if (MessageBox.Show(this, "请再次确认：永久清理选中项？", "再次确认永久清理", MessageBoxButtons.YesNo, MessageBoxIcon.Warning, MessageBoxDefaultButton.Button2) != DialogResult.Yes) return;
            busy = true;
            SetBusy(true);
            progress = new DedupeProgressForm(palette);
            progress.SetStatus("正在永久清理选中文件");
            progress.Show(this);
            try
            {
                var purged = 0;
                var fail = new List<string>();
                foreach (var group in rows.GroupBy(row => row.Operation.OperationId))
                {
                    var operationId = group.Key;
                    var itemIds = group.Select(row => row.Item.ItemId).ToList();
                    var result = await Task.Run(() => DedupeQuarantine.PurgeItems(operationId, itemIds, progress.SetStatus));
                    purged += result.MovedCount;
                    if (result.Failures != null) fail.AddRange(result.Failures);
                }
                if (progress != null) { progress.CloseFromOwner(); progress = null; }
                await LoadRowsAsync(true);
                var message = "已永久清理 " + purged + " 个文件。" + (fail.Count > 0 ? "\r\n失败 " + fail.Count + " 个：" + string.Join("；", fail.Take(5).ToArray()) : "");
                statusLabel.Text = message;
                if (report != null) report("工具箱回收站", fail.Count > 0 ? "部分失败" : "完成", message);
                MessageBox.Show(this, message, "永久清理", MessageBoxButtons.OK, fail.Count > 0 ? MessageBoxIcon.Warning : MessageBoxIcon.Information);
            }
            catch (Exception ex)
            {
                if (report != null) report("工具箱回收站", "失败", ex.Message);
                MessageBox.Show(this, "永久清理失败：" + ex.Message, "永久清理", MessageBoxButtons.OK, MessageBoxIcon.Error);
            }
            finally
            {
                if (progress != null) { try { progress.CloseFromOwner(); } catch { } progress = null; }
                busy = false;
                if (!IsDisposed) { SetBusy(false); UpdateButtons(); }
            }
        }

        private void OpenIsolation()
        {
            var rows = SelectedRows();
            string path = null;
            if (rows.Count > 0) path = rows[0].Item.QuarantinePath;
            if (string.IsNullOrWhiteSpace(path))
            {
                var first = allRows.FirstOrDefault();
                if (first == null) { MessageBox.Show(this, "回收站中还没有隔离文件。", "打开隔离目录", MessageBoxButtons.OK, MessageBoxIcon.Information); return; }
                path = DedupeQuarantine.TrashRootFor(first.Item.Root);
            }
            try
            {
                if (File.Exists(path)) Process.Start("explorer.exe", "/select," + DedupeUiStyle.Quote(path));
                else if (Directory.Exists(path)) Process.Start("explorer.exe", DedupeUiStyle.Quote(path));
                else
                {
                    var root = Directory.Exists(path) ? path : Path.GetDirectoryName(path);
                    if (Directory.Exists(root)) Process.Start("explorer.exe", DedupeUiStyle.Quote(root));
                    else MessageBox.Show(this, "隔离目录不存在：" + path, "打开隔离目录", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                }
            }
            catch (Exception ex) { MessageBox.Show(this, "无法打开隔离目录：" + ex.Message, "打开隔离目录", MessageBoxButtons.OK, MessageBoxIcon.Error); }
        }

        private void ViewLog()
        {
            var rows = SelectedRows();
            var operation = rows.Count > 0 ? rows[0].Operation : operations.FirstOrDefault();
            if (operation == null) { MessageBox.Show(this, "没有可查看的操作记录。", "操作日志", MessageBoxButtons.OK, MessageBoxIcon.Information); return; }
            string text;
            try { text = File.ReadAllText(DedupeQuarantine.OperationPath(operation.OperationId), Encoding.UTF8); }
            catch (Exception ex) { MessageBox.Show(this, "无法读取操作日志：" + ex.Message, "操作日志", MessageBoxButtons.OK, MessageBoxIcon.Error); return; }
            using (var dialog = new Form
            {
                Text = "操作日志 · " + operation.OperationId,
                StartPosition = FormStartPosition.CenterParent,
                Size = new Size(820, 620),
                MinimumSize = new Size(620, 460),
                BackColor = palette.Background,
                ForeColor = palette.Ink,
                Font = DedupeUiStyle.Font(9F, false)
            })
            {
                var box = new TextBox
                {
                    Multiline = true,
                    ReadOnly = true,
                    ScrollBars = ScrollBars.Both,
                    WordWrap = false,
                    Dock = DockStyle.Fill,
                    BackColor = palette.Panel,
                    ForeColor = palette.Ink,
                    Font = DedupeUiStyle.Font(9F, false)
                };
                box.Text = text;
                dialog.Controls.Add(box);
                var close = new Button { Text = "关闭", Dock = DockStyle.Bottom, Height = 38, DialogResult = DialogResult.OK };
                DedupeUiStyle.Style(close, palette, "secondary");
                dialog.Controls.Add(close);
                dialog.AcceptButton = close;
                dialog.CancelButton = close;
                dialog.ShowDialog(this);
            }
        }
    }
}
