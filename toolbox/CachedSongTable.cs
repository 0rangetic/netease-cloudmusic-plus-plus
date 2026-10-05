using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Drawing;
using System.Drawing.Imaging;
using System.Linq;
using System.Windows.Forms;

namespace NeteaseToolbox
{
    // No SysListView32 window or native content scrolling. Data objects remain
    // compatible with the existing workers, but have no native ListView owner.
    internal sealed class CachedSongTable : Control
    {
        internal sealed class TableColumn
        {
            private readonly CachedSongTable owner;
            private int width;
            public string Text { get; private set; }
            public int Width { get { return width; } set { int next = Math.Max(40, value); if (next == width) return; width = next; owner.ClearTextCache(); owner.LayoutTable(); owner.QueueFrame(); } }
            internal TableColumn(CachedSongTable owner, string text, int width) { this.owner = owner; Text = text; this.width = Math.Max(40, width); }
        }
        internal sealed class TableColumns : Collection<TableColumn>
        {
            private readonly CachedSongTable owner;
            internal TableColumns(CachedSongTable owner) { this.owner = owner; }
            public void Add(string text, int width) { base.Add(new TableColumn(owner, text, width)); owner.LayoutTable(); owner.QueueFrame(); }
        }
        internal sealed class TableItems : Collection<ListViewItem>
        {
            private readonly CachedSongTable owner;
            internal TableItems(CachedSongTable owner) { this.owner = owner; }
            protected override void InsertItem(int index, ListViewItem item) { if (item == null) throw new ArgumentNullException("item"); base.InsertItem(index, item); owner.DataChanged(); }
            protected override void SetItem(int index, ListViewItem item) { if (item == null) throw new ArgumentNullException("item"); owner.RemoveCachedText(this[index]); base.SetItem(index, item); owner.DataChanged(); }
            protected override void RemoveItem(int index) { owner.RemoveCachedText(this[index]); base.RemoveItem(index); owner.DataChanged(); }
            protected override void ClearItems() { base.ClearItems(); owner.selected = null; owner.firstRow = 0; owner.ClearTextCache(); owner.DataChanged(); }
            public void AddRange(ListViewItem[] items) { owner.BeginUpdate(); try { foreach (var item in items) Add(item); } finally { owner.EndUpdate(); } }
        }
        public TableItems Items { get; private set; }
        public TableColumns Columns { get; private set; }
        public List<ListViewItem> SelectedItems { get { return selected != null && Items.Contains(selected) ? new List<ListViewItem> { selected } : new List<ListViewItem>(); } }
        public int SelectedIndex { get { return selected == null ? -1 : Items.IndexOf(selected); } set { SelectRow(value); } }
        public bool FillLastColumn { get; set; }
        private readonly VScrollBar vertical = new VScrollBar { TabStop = false };
        private readonly HScrollBar horizontal = new HScrollBar { TabStop = false };
        private readonly Timer frameTimer = new Timer { Interval = 16 };
        private readonly ToolTip itemTips = new ToolTip();
        private readonly Dictionary<ListViewItem, Bitmap> textRows = new Dictionary<ListViewItem, Bitmap>();
        private readonly StringFormat textFormat = new StringFormat { FormatFlags = StringFormatFlags.NoWrap, Trimming = StringTrimming.EllipsisCharacter, LineAlignment = StringAlignment.Center };
        private ToolboxForm wallpaperOwner;
        private Bitmap backgroundCache, viewportFrame;
        private Rectangle backgroundPlacement, body;
        private int backgroundGeneration = -1, firstRow, horizontalOffset, updateDepth, wheelRemainder;
        private int rowHeight = 24, headerHeight = 30, frameRenderCount, displayedFirstRow, resizeColumn = -1, resizeStartX, resizeStartWidth;
        private int[] renderedRows = new int[0];
        private bool frameDirty = true, layingOut;
        private ListViewItem selected;
        private string currentTip = "";

        public CachedSongTable()
        {
            SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw | ControlStyles.Selectable | ControlStyles.StandardClick | ControlStyles.StandardDoubleClick, true);
            Items = new TableItems(this); Columns = new TableColumns(this); TabStop = true;
            Controls.Add(vertical); Controls.Add(horizontal);
            vertical.ValueChanged += delegate { if (layingOut) return; firstRow = vertical.Value; QueueFrame(); };
            horizontal.ValueChanged += delegate { if (layingOut) return; horizontalOffset = horizontal.Value; ClearTextCache(); QueueFrame(); };
            frameTimer.Tick += delegate { frameTimer.Stop(); if (!IsDisposed && Visible && updateDepth == 0) Invalidate(); };
            UpdateMetrics();
            AccessibleRole = AccessibleRole.Table;
        }
        public void BeginUpdate() { updateDepth++; }
        public void EndUpdate() { if (updateDepth > 0) updateDepth--; if (updateDepth == 0) { LayoutTable(); QueueFrame(); } }
        internal void SetWallpaperOwner(ToolboxForm owner)
        {
            if (wallpaperOwner != owner) {
                if (wallpaperOwner != null) wallpaperOwner.VisibleChanged -= OwnerVisibleChanged;
                wallpaperOwner = owner;
                if (wallpaperOwner != null) wallpaperOwner.VisibleChanged += OwnerVisibleChanged;
            }
            DropBackground(); ClearTextCache(); QueueFrame();
        }
        private void OwnerVisibleChanged(object sender, EventArgs e)
        {
            // Form.Hide does not always propagate VisibleChanged through every
            // nested page. Stop the pending timer directly when the owner hides.
            if (wallpaperOwner == null || !wallpaperOwner.Visible) frameTimer.Stop();
            else QueueFrame();
        }
        internal void RefreshWallpaper() { DropBackground(); QueueFrame(); }
        internal void NotifyDataChanged(ListViewItem item) { RemoveCachedText(item); QueueFrame(); }
        private void DataChanged() { if (selected != null && !Items.Contains(selected)) selected = null; if (updateDepth == 0) LayoutTable(); QueueFrame(); }
        private void QueueFrame() { frameDirty = true; if (frameTimer == null || updateDepth > 0 || !Visible || IsDisposed || Disposing) return; if (!frameTimer.Enabled) frameTimer.Start(); }
        private void RemoveCachedText(ListViewItem item) { Bitmap image; if (item != null && textRows.TryGetValue(item, out image)) { textRows.Remove(item); image.Dispose(); } }
        private void ClearTextCache() { if (textRows == null) return; foreach (var image in textRows.Values) image.Dispose(); textRows.Clear(); }
        private void DropBackground() { if (backgroundCache != null) { backgroundCache.Dispose(); backgroundCache = null; } backgroundGeneration = -1; frameDirty = true; }
        private void UpdateMetrics() { rowHeight = Math.Max(22, Font.Height + 6); headerHeight = rowHeight + 6; ClearTextCache(); LayoutTable(); QueueFrame(); }
        protected override void OnFontChanged(EventArgs e) { base.OnFontChanged(e); UpdateMetrics(); }
        protected override void OnForeColorChanged(EventArgs e) { base.OnForeColorChanged(e); ClearTextCache(); QueueFrame(); }
        protected override void OnBackColorChanged(EventArgs e) { base.OnBackColorChanged(e); DropBackground(); QueueFrame(); }
        protected override void OnSizeChanged(EventArgs e) { base.OnSizeChanged(e); DropBackground(); ClearTextCache(); LayoutTable(); QueueFrame(); }
        protected override void OnLocationChanged(EventArgs e) { base.OnLocationChanged(e); DropBackground(); QueueFrame(); }
        protected override void OnVisibleChanged(EventArgs e) { base.OnVisibleChanged(e); if (frameTimer != null && !Visible) frameTimer.Stop(); QueueFrame(); }
        protected override void OnGotFocus(EventArgs e) { base.OnGotFocus(e); QueueFrame(); }
        protected override void OnLostFocus(EventArgs e) { base.OnLostFocus(e); QueueFrame(); }
        protected override void OnPaintBackground(PaintEventArgs e) { }
        private void LayoutTable()
        {
            if (layingOut || Columns == null || Items == null || vertical == null) return;
            layingOut = true;
            var oldBody = body;
            int oldHorizontal = horizontalOffset;
            try {
                int width = Math.Max(0, ClientSize.Width), height = Math.Max(0, ClientSize.Height - headerHeight);
                int totalWidth = Columns.Sum(column => column.Width);
                bool needV = (long)Items.Count * rowHeight > height;
                if (FillLastColumn && Columns.Count > 0) { Columns[Columns.Count - 1].Width = Math.Max(160, width - (needV ? SystemInformation.VerticalScrollBarWidth : 0) - Columns.Take(Columns.Count - 1).Sum(column => column.Width)); totalWidth = Columns.Sum(column => column.Width); }
                bool needH = totalWidth > width - (needV ? SystemInformation.VerticalScrollBarWidth : 0);
                needV = (long)Items.Count * rowHeight > height - (needH ? SystemInformation.HorizontalScrollBarHeight : 0);
                if (FillLastColumn && Columns.Count > 0) { Columns[Columns.Count - 1].Width = Math.Max(160, width - (needV ? SystemInformation.VerticalScrollBarWidth : 0) - Columns.Take(Columns.Count - 1).Sum(column => column.Width)); totalWidth = Columns.Sum(column => column.Width); }
                needH = totalWidth > width - (needV ? SystemInformation.VerticalScrollBarWidth : 0);
                body = new Rectangle(0, headerHeight, Math.Max(0, width - (needV ? SystemInformation.VerticalScrollBarWidth : 0)), Math.Max(0, height - (needH ? SystemInformation.HorizontalScrollBarHeight : 0)));
                vertical.Visible = needV; horizontal.Visible = needH;
                vertical.Bounds = new Rectangle(body.Right, headerHeight, SystemInformation.VerticalScrollBarWidth, body.Height);
                horizontal.Bounds = new Rectangle(0, body.Bottom, body.Width, SystemInformation.HorizontalScrollBarHeight);
                vertical.Minimum = 0; vertical.Maximum = Math.Max(0, Items.Count - 1); vertical.LargeChange = Math.Max(1, body.Height / rowHeight); vertical.SmallChange = 1;
                firstRow = Math.Max(0, Math.Min(firstRow, Math.Max(0, Items.Count - vertical.LargeChange))); vertical.Value = firstRow;
                horizontal.Minimum = 0; horizontal.Maximum = Math.Max(0, totalWidth - 1); horizontal.LargeChange = Math.Max(1, body.Width); horizontal.SmallChange = Math.Max(8, rowHeight);
                horizontalOffset = Math.Max(0, Math.Min(horizontalOffset, Math.Max(0, totalWidth - body.Width))); horizontal.Value = horizontalOffset;
            }
            finally { layingOut = false; }
            if (oldBody != body || oldHorizontal != horizontalOffset) { ClearTextCache(); frameDirty = true; }
        }
        internal void SetTopRow(int index) { LayoutTable(); int next = Math.Max(0, Math.Min(index, Math.Max(0, Items.Count - vertical.LargeChange))); if (next == firstRow) return; firstRow = next; vertical.Value = next; QueueFrame(); }
        internal void EnsureVisible(int index) { if (index < firstRow) SetTopRow(index); else if (index >= firstRow + vertical.LargeChange) SetTopRow(index - vertical.LargeChange + 1); }
        private void SelectRow(int index) { selected = index >= 0 && index < Items.Count ? Items[index] : null; if (selected != null) EnsureVisible(index); QueueFrame(); }
        public ListViewItem GetItemAt(int x, int y) { if (!body.Contains(x, y)) return null; int index = firstRow + (y - body.Top) / rowHeight; return index >= 0 && index < Items.Count ? Items[index] : null; }
        protected override void OnMouseWheel(MouseEventArgs e)
        {
            base.OnMouseWheel(e);
            var handled = e as HandledMouseEventArgs; if (handled != null) handled.Handled = true;
            wheelRemainder += e.Delta; int ticks = wheelRemainder / 120; wheelRemainder -= ticks * 120;
            int lines = SystemInformation.MouseWheelScrollLines; if (lines < 0) lines = vertical.LargeChange;
            if (ticks != 0 && lines != 0) SetTopRow(firstRow - ticks * lines);
        }
        private int HeaderDividerAt(int x, int y) { if (y < 0 || y >= headerHeight) return -1; int right = -horizontalOffset; for (int column = 0; column < Columns.Count; column++) { right += Columns[column].Width; if (Math.Abs(x - right) <= 4) return column; } return -1; }
        protected override void OnMouseDown(MouseEventArgs e)
        {
            Focus();
            // Flush a pending wheel frame before mapping a click to its row.
            if (frameDirty && updateDepth == 0) { frameTimer.Stop(); Invalidate(); Update(); }
            if (e.Button == MouseButtons.Left && (resizeColumn = HeaderDividerAt(e.X, e.Y)) >= 0) { if (resizeColumn == Columns.Count - 1) FillLastColumn = false; resizeStartX = e.X; resizeStartWidth = Columns[resizeColumn].Width; Capture = true; }
            else if (body.Contains(e.Location)) { var item = GetItemAt(e.X, e.Y); selected = item; QueueFrame(); }
            base.OnMouseDown(e);
        }
        protected override void OnMouseMove(MouseEventArgs e)
        {
            if (resizeColumn >= 0 && Capture) Columns[resizeColumn].Width = Math.Min(3000, Math.Max(40, resizeStartWidth + e.X - resizeStartX));
            else {
                Cursor = HeaderDividerAt(e.X, e.Y) >= 0 ? Cursors.VSplit : Cursors.Default;
                var item = GetItemAt(e.X, e.Y); string tip = item == null ? "" : item.ToolTipText;
                if (tip != currentTip) { currentTip = tip; itemTips.SetToolTip(this, tip); }
            }
            base.OnMouseMove(e);
        }
        protected override void OnMouseUp(MouseEventArgs e) { if (resizeColumn >= 0) { resizeColumn = -1; Capture = false; } base.OnMouseUp(e); }
        protected override bool IsInputKey(Keys keyData) { switch (keyData & Keys.KeyCode) { case Keys.Up: case Keys.Down: case Keys.PageUp: case Keys.PageDown: case Keys.Home: case Keys.End: return true; } return base.IsInputKey(keyData); }
        protected override void OnKeyDown(KeyEventArgs e)
        {
            int index = SelectedIndex; bool move = true;
            switch (e.KeyCode) {
                case Keys.Up: index = Math.Max(0, index - 1); break;
                case Keys.Down: index = Math.Min(Items.Count - 1, index + 1); break;
                case Keys.PageUp: index = Math.Max(0, index - vertical.LargeChange); break;
                case Keys.PageDown: index = Math.Min(Items.Count - 1, index + vertical.LargeChange); break;
                case Keys.Home: index = 0; break; case Keys.End: index = Items.Count - 1; break;
                case Keys.Enter:
                    if (index >= 0) { EnsureVisible(index); OnMouseDoubleClick(new MouseEventArgs(MouseButtons.Left, 2, 10, body.Top + (index - firstRow) * rowHeight + rowHeight / 2, 0)); }
                    e.Handled = true; move = false; break;
                default: move = false; break;
            }
            if (move) { SelectRow(index); e.Handled = true; }
            base.OnKeyDown(e);
        }
        private void EnsureBackground()
        {
            bool wallpaper = wallpaperOwner != null && !wallpaperOwner.IsDisposed && !wallpaperOwner.Disposing && wallpaperOwner.WallpaperEnabled;
            int version = wallpaper ? wallpaperOwner.WallpaperGeneration : -1;
            Point origin = wallpaper ? wallpaperOwner.PointToClient(PointToScreen(Point.Empty)) : Point.Empty;
            var placement = new Rectangle(origin, ClientSize);
            if (backgroundCache != null && backgroundPlacement == placement && backgroundGeneration == version) return;
            if (backgroundCache != null) backgroundCache.Dispose();
            backgroundCache = new Bitmap(Math.Max(1, ClientSize.Width), Math.Max(1, ClientSize.Height), PixelFormat.Format32bppRgb);
            using (var graphics = Graphics.FromImage(backgroundCache)) {
                graphics.Clear(BackColor);
                if (wallpaper) wallpaperOwner.PaintWallpaperRegion(graphics, this, ClientRectangle);
            }
            backgroundPlacement = placement; backgroundGeneration = version; frameDirty = true;
        }
        private Bitmap TextRow(ListViewItem item)
        {
            Bitmap cached; if (textRows.TryGetValue(item, out cached)) return cached;
            cached = new Bitmap(Math.Max(1, body.Width), rowHeight, PixelFormat.Format32bppArgb);
            using (var graphics = Graphics.FromImage(cached)) {
                graphics.TextRenderingHint = System.Drawing.Text.TextRenderingHint.AntiAliasGridFit;
                using (var brush = new SolidBrush(ForeColor)) {
                    int x = -horizontalOffset;
                    for (int column = 0; column < Columns.Count; column++) {
                        if (column < item.SubItems.Count) graphics.DrawString(item.SubItems[column].Text, Font, brush, new Rectangle(x + 4, 0, Math.Max(0, Columns[column].Width - 8), rowHeight), textFormat);
                        x += Columns[column].Width;
                    }
                }
            }
            textRows[item] = cached; return cached;
        }
        private void EnsureFrame()
        {
            if (ClientSize.Width <= 0 || ClientSize.Height <= 0 || updateDepth > 0) return;
            EnsureBackground();
            if (!frameDirty && viewportFrame != null && viewportFrame.Size == ClientSize) return;
            if (viewportFrame == null || viewportFrame.Size != ClientSize) { if (viewportFrame != null) viewportFrame.Dispose(); viewportFrame = new Bitmap(ClientSize.Width, ClientSize.Height, PixelFormat.Format32bppRgb); }
            var indices = new List<int>(); var visible = new HashSet<ListViewItem>();
            using (var graphics = Graphics.FromImage(viewportFrame)) {
                graphics.DrawImageUnscaled(backgroundCache, 0, 0);
                using (var brush = new SolidBrush(ForeColor)) {
                    int x = -horizontalOffset;
                    var headerState = graphics.Save(); graphics.SetClip(new Rectangle(0, 0, body.Width, headerHeight));
                    for (int column = 0; column < Columns.Count; column++) { graphics.DrawString(Columns[column].Text, Font, brush, new Rectangle(x + 4, 0, Math.Max(0, Columns[column].Width - 8), headerHeight), textFormat); x += Columns[column].Width; }
                    graphics.Restore(headerState);
                }
                var state = graphics.Save(); graphics.SetClip(body);
                for (int index = firstRow, y = body.Top; index < Items.Count && y < body.Bottom; index++, y += rowHeight) {
                    var item = Items[index]; indices.Add(index); visible.Add(item);
                    var bounds = new Rectangle(0, y, body.Width, rowHeight);
                    if (item == selected) using (var brush = new SolidBrush(Color.FromArgb(210, wallpaperOwner == null ? SystemColors.Highlight : wallpaperOwner.WallpaperSelection))) graphics.FillRectangle(brush, bounds);
                    graphics.DrawImageUnscaled(TextRow(item), 0, y);
                    if (item == selected && Focused) ControlPaint.DrawFocusRectangle(graphics, bounds);
                }
                graphics.Restore(state);
            }
            int limit = Math.Max(8, indices.Count * 3);
            if (textRows.Count > limit) foreach (var item in textRows.Keys.Where(item => !visible.Contains(item)).ToArray()) { RemoveCachedText(item); if (textRows.Count <= limit) break; }
            renderedRows = indices.ToArray(); displayedFirstRow = firstRow; frameRenderCount++; frameDirty = false;
        }
        protected override void OnPaint(PaintEventArgs e) { frameTimer.Stop(); EnsureFrame(); if (viewportFrame != null) e.Graphics.DrawImageUnscaled(viewportFrame, 0, 0); base.OnPaint(e); }
        protected override void Dispose(bool disposing)
        {
            if (disposing) { if (wallpaperOwner != null) wallpaperOwner.VisibleChanged -= OwnerVisibleChanged; frameTimer.Stop(); frameTimer.Dispose(); itemTips.Dispose(); textFormat.Dispose(); ClearTextCache(); DropBackground(); if (viewportFrame != null) { viewportFrame.Dispose(); viewportFrame = null; } }
            wallpaperOwner = null; base.Dispose(disposing);
        }
    }
}
