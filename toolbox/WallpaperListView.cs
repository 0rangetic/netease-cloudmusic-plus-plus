using System;
using System.Drawing;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;
using System.Windows.Forms;

namespace NeteaseToolbox
{
    // Native ListView owns data and interaction. Only this renderer owns pixels.
    internal sealed class WallpaperListView : ListView
    {
        [DllImport("user32.dll")] private static extern IntPtr SendMessage(IntPtr window, int message, IntPtr wParam, IntPtr lParam);
        [DllImport("user32.dll", EntryPoint = "SendMessageW")]
        private static extern IntPtr GetRectangle(IntPtr window, int message, IntPtr index, ref NativeRectangle rectangle);
        [DllImport("user32.dll", EntryPoint = "SendMessageW")]
        private static extern IntPtr GetNativeText(IntPtr window, int message, IntPtr index, ref NativeItem item);
        [DllImport("user32.dll")] private static extern bool GetWindowRect(IntPtr window, out NativeRectangle rectangle);
        [DllImport("user32.dll")] private static extern bool InvalidateRect(IntPtr window, IntPtr rectangle, bool erase);
        [DllImport("user32.dll")] private static extern bool GetUpdateRect(IntPtr window, out NativeRectangle rectangle, bool erase);
        [DllImport("user32.dll")] private static extern IntPtr BeginPaint(IntPtr window, out PaintInfo info);
        [DllImport("user32.dll")] private static extern bool EndPaint(IntPtr window, ref PaintInfo info);
        [StructLayout(LayoutKind.Sequential)] private struct NativeRectangle { public int Left, Top, Right, Bottom; }
        [StructLayout(LayoutKind.Sequential)] private struct PaintInfo
        {
            public IntPtr Dc; public int Erase; public NativeRectangle Rectangle;
            public int Restore, Update; public long Reserved1, Reserved2, Reserved3, Reserved4;
        }
        [StructLayout(LayoutKind.Sequential)] private struct NativeItem
        {
            public uint Mask; public int Item, SubItem; public uint State, StateMask;
            public IntPtr Text; public int TextCapacity, Image; public IntPtr Parameter;
            public int Indent, Group; public uint Columns; public IntPtr ColumnIndices, ColumnFormats; public int GroupIndex;
        }
        private ToolboxForm wallpaperOwner;
        private Bitmap wallpaperSurface, viewportFrame;
        private int generation = -1;
        private Rectangle placement;
        private bool updating, frameDirty = true, rendering, scrollInProgress, redrawSuspended;
        private int frameRenderCount;
        private int cachedFirst = -1, cachedFirstY, cachedCount = -1, cachedHeaderX, cachedHeaderHeight;
        private Rectangle lastPaintRectangle;
        private readonly StringFormat frameTextFormat = new StringFormat { FormatFlags = StringFormatFlags.NoWrap, Trimming = StringTrimming.EllipsisCharacter, LineAlignment = StringAlignment.Center };
        public WallpaperListView() { DoubleBuffered = true; }
        internal void SetWallpaperOwner(ToolboxForm owner)
        {
            wallpaperOwner = owner;
            if (owner == null) {
                if (IsHandleCreated) {
                    var solid = new IntPtr(ColorTranslator.ToWin32(BackColor));
                    SendMessage(Handle, 0x1001, IntPtr.Zero, solid);
                    SendMessage(Handle, 0x1026, IntPtr.Zero, solid);
                }
                ReleaseFrames(); OwnerDraw = false; generation = -1; Invalidate();
            }
            else RefreshWallpaper();
        }
        internal void RefreshWallpaper()
        {
            if (updating || Disposing || IsDisposed || wallpaperOwner == null || wallpaperOwner.IsDisposed || wallpaperOwner.WallpaperLayoutPending || !wallpaperOwner.WallpaperEnabled
                || !Visible || !IsHandleCreated || ClientSize.Width <= 0 || ClientSize.Height <= 0) return;
            Point origin = wallpaperOwner.PointToClient(PointToScreen(Point.Empty));
            var bounds = new Rectangle(origin, ClientSize);
            int version = wallpaperOwner.WallpaperGeneration;
            if (generation == version && placement == bounds && wallpaperSurface != null) return;
            updating = true;
            try {
                var image = wallpaperOwner.CreateWallpaperSlice(this);
                var previous = wallpaperSurface; wallpaperSurface = image;
                if (previous != null) previous.Dispose();
                OwnerDraw = true;
                placement = bounds; generation = version; frameDirty = true; Invalidate();
            }
            finally { updating = false; }
        }
        protected override void OnHandleCreated(EventArgs e) { base.OnHandleCreated(e); generation = -1; RefreshWallpaper(); }
        protected override void OnVisibleChanged(EventArgs e) { base.OnVisibleChanged(e); frameDirty = true; RefreshWallpaper(); }
        protected override void OnSizeChanged(EventArgs e) { base.OnSizeChanged(e); frameDirty = true; Invalidate(); }
        protected override void OnLocationChanged(EventArgs e) { base.OnLocationChanged(e); frameDirty = true; Invalidate(); }
        protected override void OnInvalidated(InvalidateEventArgs e) { frameDirty = true; base.OnInvalidated(e); }
        protected override void OnItemSelectionChanged(ListViewItemSelectionChangedEventArgs e) { frameDirty = true; base.OnItemSelectionChanged(e); }
        protected override void OnColumnWidthChanged(ColumnWidthChangedEventArgs e) { frameDirty = true; base.OnColumnWidthChanged(e); Invalidate(); }
        protected override void OnGotFocus(EventArgs e) { frameDirty = true; base.OnGotFocus(e); Invalidate(); }
        protected override void OnLostFocus(EventArgs e) { frameDirty = true; base.OnLostFocus(e); Invalidate(); }
        private bool CanRender { get { return !Disposing && !IsDisposed && IsHandleCreated && wallpaperSurface != null && wallpaperOwner != null && !wallpaperOwner.Disposing && !wallpaperOwner.IsDisposed && wallpaperOwner.WallpaperEnabled; } }
        private static bool IsScrollMessage(int message)
        {
            return message == 0x115 || message == 0x114 || message == 0x20A || message == 0x20E || message == 0x1014 || message == 0x1013 || message == 0x100;
        }
        private static bool ChangesFrame(int message)
        {
            return IsScrollMessage(message) || message == 0x201 || message == 0x202 || message == 0x203 || message == 0x204E || message == 0x30 ||
                message == 0x1008 || message == 0x1009 || message == 0x104D || message == 0x104C || message == 0x1074 ||
                message == 0x102B || message == 0x1030 || message == 0x1051 || message == 0x1060 || message == 0x1061 ||
                message == 0x101C || message == 0x101E || message == 0x103A;
        }
        protected override void WndProc(ref Message message)
        {
            bool paint = message.Msg == 0x14 || message.Msg == 0xF || message.Msg == 0x317 || message.Msg == 0x318;
            if (paint && (Disposing || IsDisposed || !IsHandleCreated)) { message.Result = IntPtr.Zero; return; }
            if (message.Msg == 0xB) { redrawSuspended = message.WParam == IntPtr.Zero; frameDirty = true; }
            if (ChangesFrame(message.Msg)) frameDirty = true;
            if (paint && !updating && wallpaperOwner != null && !wallpaperOwner.IsDisposed) RefreshWallpaper();
            if (CanRender && message.Msg == 0x14) {
                // No background-only frame ever reaches the screen.
                message.Result = new IntPtr(1); return;
            }
            if (CanRender && message.Msg == 0xF) {
                PaintInfo info;
                // Native scrollbar tracking can move pixels internally and
                // invalidate only an exposed strip. Expand before BeginPaint
                // so its DC clip covers every pixel of our completed frame.
                if (!redrawSuspended) InvalidateRect(message.HWnd, IntPtr.Zero, false);
                NativeRectangle pending;
                GetUpdateRect(message.HWnd, out pending, false);
                lastPaintRectangle = Rectangle.FromLTRB(pending.Left, pending.Top, pending.Right, pending.Bottom);
                IntPtr window = message.HWnd, dc = BeginPaint(window, out info);
                try { if (dc != IntPtr.Zero && !redrawSuspended) PresentFrame(dc); }
                finally { EndPaint(window, ref info); }
                message.Result = IntPtr.Zero; return;
            }
            if (CanRender && (message.Msg == 0x317 || message.Msg == 0x318) && message.WParam != IntPtr.Zero) {
                PresentFrame(message.WParam);
                if (message.Msg == 0x317) {
                    // Preserve native non-client scrollbars/child header in
                    // screenshots without letting it draw any client rows.
                    var chrome = message;
                    chrome.LParam = new IntPtr(message.LParam.ToInt64() & ~(4L | 8L));
                    base.WndProc(ref chrome);
                }
                message.Result = IntPtr.Zero; return;
            }
            if (CanRender && IsScrollMessage(message.Msg) && !scrollInProgress && !redrawSuspended) {
                // Suppress native ScrollWindow pixel copying, retaining only
                // the scroll-position update and native input behavior.
                IntPtr window = Handle; scrollInProgress = true;
                SendMessage(window, 0xB, IntPtr.Zero, IntPtr.Zero);
                try { base.WndProc(ref message); }
                finally {
                    scrollInProgress = false;
                    if (!Disposing && !IsDisposed && IsHandleCreated && Handle == window) {
                        SendMessage(window, 0xB, new IntPtr(1), IntPtr.Zero);
                        InvalidateRect(window, IntPtr.Zero, false);
                    }
                }
                return;
            }
            base.WndProc(ref message);
        }
        private void PresentFrame(IntPtr dc)
        {
            EnsureViewportFrame();
            if (viewportFrame == null) return;
            using (var graphics = Graphics.FromHdc(dc)) graphics.DrawImageUnscaled(viewportFrame, 0, 0);
        }
        private void EnsureViewportFrame()
        {
            if (rendering || !CanRender || ClientSize.Width <= 0 || ClientSize.Height <= 0) return;
            // Never infer scroll position from input messages. Scrollbar
            // tracking and native nested loops can change it without passing
            // through our managed scroll branch.
            int nativeCount = SendMessage(Handle, 0x1004, IntPtr.Zero, IntPtr.Zero).ToInt32();
            int nativeFirst = Math.Max(0, SendMessage(Handle, 0x1027, IntPtr.Zero, IntPtr.Zero).ToInt32());
            var firstRectangle = new NativeRectangle();
            int nativeFirstY = nativeCount > 0 && GetRectangle(Handle, 0x100E, new IntPtr(nativeFirst), ref firstRectangle) != IntPtr.Zero ? firstRectangle.Top : 0;
            IntPtr nativeHeader = SendMessage(Handle, 0x101F, IntPtr.Zero, IntPtr.Zero);
            NativeRectangle headerBounds, listBounds;
            int nativeHeaderX = 0, nativeHeaderHeight = 0;
            if (nativeHeader != IntPtr.Zero && GetWindowRect(nativeHeader, out headerBounds) && GetWindowRect(Handle, out listBounds)) {
                nativeHeaderX = headerBounds.Left - listBounds.Left;
                nativeHeaderHeight = headerBounds.Bottom - listBounds.Top;
            }
            if (viewportFrame != null && viewportFrame.Size == ClientSize && !frameDirty &&
                cachedFirst == nativeFirst && cachedFirstY == nativeFirstY && cachedCount == nativeCount &&
                cachedHeaderX == nativeHeaderX && cachedHeaderHeight == nativeHeaderHeight) return;
            rendering = true;
            try {
                if (viewportFrame == null || viewportFrame.Size != ClientSize) {
                    if (viewportFrame != null) viewportFrame.Dispose();
                    viewportFrame = new Bitmap(ClientSize.Width, ClientSize.Height, PixelFormat.Format32bppRgb);
                }
                using (var graphics = Graphics.FromImage(viewportFrame)) {
                    graphics.TextRenderingHint = System.Drawing.Text.TextRenderingHint.ClearTypeGridFit;
                    graphics.Clear(BackColor); graphics.DrawImageUnscaled(wallpaperSurface, 0, 0);
                    var cells = new Rectangle[Columns.Count];
                    IntPtr header = SendMessage(Handle, 0x101F, IntPtr.Zero, IntPtr.Zero);
                    NativeRectangle headerScreen, listScreen;
                    int headerBottom = 0;
                    if (header != IntPtr.Zero && GetWindowRect(header, out headerScreen) && GetWindowRect(Handle, out listScreen)) {
                        int offsetX = headerScreen.Left - listScreen.Left, offsetY = headerScreen.Top - listScreen.Top;
                        headerBottom = headerScreen.Bottom - listScreen.Top;
                        for (int column = 0; column < cells.Length; column++) {
                            var rectangle = new NativeRectangle();
                            if (GetRectangle(header, 0x1207, new IntPtr(column), ref rectangle) == IntPtr.Zero) continue;
                            cells[column] = new Rectangle(rectangle.Left + offsetX, rectangle.Top + offsetY, rectangle.Right - rectangle.Left, rectangle.Bottom - rectangle.Top);
                            DrawText(graphics, Columns[column].Text, cells[column]);
                        }
                    }
                    var state = graphics.Save();
                    graphics.SetClip(new Rectangle(0, Math.Max(0, headerBottom), ClientSize.Width, Math.Max(0, ClientSize.Height - headerBottom)));
                    int count = SendMessage(Handle, 0x1004, IntPtr.Zero, IntPtr.Zero).ToInt32();
                    int first = Math.Max(0, SendMessage(Handle, 0x1027, IntPtr.Zero, IntPtr.Zero).ToInt32());
                    // Geometry/count always come from native rows. Managed
                    // text below is optional and never supplies row bounds.
                    IntPtr textBuffer = Marshal.AllocHGlobal(2048);
                    try {
                        for (int row = first; row < count; row++) {
                            var rectangle = new NativeRectangle();
                            if (GetRectangle(Handle, 0x100E, new IntPtr(row), ref rectangle) == IntPtr.Zero) break;
                            if (rectangle.Top >= ClientSize.Height) break;
                            if (rectangle.Bottom <= headerBottom) continue;
                            var bounds = new Rectangle(0, rectangle.Top, ClientSize.Width, rectangle.Bottom - rectangle.Top);
                            int itemState = SendMessage(Handle, 0x102C, new IntPtr(row), new IntPtr(3)).ToInt32();
                            if ((itemState & 2) != 0) using (var brush = new SolidBrush(Color.FromArgb(210, wallpaperOwner.WallpaperSelection))) graphics.FillRectangle(brush, bounds);
                            // Managed text is already in memory. Use it only
                            // when native and managed collections agree, and
                            // still tolerate missing objects during transitions.
                            ListViewItem managed = Items.Count == count && row < Items.Count ? Items[row] : null;
                            for (int column = 0; column < cells.Length; column++) {
                                string text;
                                if (managed != null) text = column < managed.SubItems.Count && managed.SubItems[column] != null ? managed.SubItems[column].Text : "";
                                else {
                                    var native = new NativeItem { SubItem = column, Text = textBuffer, TextCapacity = 1024 };
                                    int length = GetNativeText(Handle, 0x1073, new IntPtr(row), ref native).ToInt32();
                                    text = length > 0 ? Marshal.PtrToStringUni(textBuffer, Math.Min(length, 1023)) : "";
                                }
                                DrawText(graphics, text, new Rectangle(cells[column].X, bounds.Y, cells[column].Width, bounds.Height));
                            }
                            if ((itemState & 1) != 0 && Focused) ControlPaint.DrawFocusRectangle(graphics, bounds);
                        }
                    }
                    finally { Marshal.FreeHGlobal(textBuffer); graphics.Restore(state); }
                }
                cachedFirst = nativeFirst; cachedFirstY = nativeFirstY; cachedCount = nativeCount;
                cachedHeaderX = nativeHeaderX; cachedHeaderHeight = nativeHeaderHeight;
                frameDirty = false; frameRenderCount++;
            }
            finally { rendering = false; }
        }
        private void DrawText(Graphics graphics, string text, Rectangle bounds)
        {
            bounds.X += 4; bounds.Width = Math.Max(0, bounds.Width - 8);
            if (bounds.Width == 0 || bounds.Height <= 0) return;
            using (var brush = new SolidBrush(wallpaperOwner.WallpaperInk)) graphics.DrawString(text, Font, brush, bounds, frameTextFormat);
        }
        protected override void OnDrawItem(DrawListViewItemEventArgs e) { if (!CanRender) e.DrawDefault = true; }
        protected override void OnDrawSubItem(DrawListViewSubItemEventArgs e) { if (!CanRender) e.DrawDefault = true; }
        protected override void OnDrawColumnHeader(DrawListViewColumnHeaderEventArgs e)
        {
            if (!CanRender || e.Header == null) { e.DrawDefault = true; return; }
            wallpaperOwner.PaintWallpaperRegion(e.Graphics, this, e.Bounds); DrawText(e.Graphics, e.Header.Text, e.Bounds);
        }
        private void ReleaseFrames()
        {
            if (wallpaperSurface != null) { wallpaperSurface.Dispose(); wallpaperSurface = null; }
            if (viewportFrame != null) { viewportFrame.Dispose(); viewportFrame = null; }
            frameDirty = true;
        }
        protected override void Dispose(bool disposing)
        {
            wallpaperOwner = null;
            if (disposing) { ReleaseFrames(); frameTextFormat.Dispose(); }
            base.Dispose(disposing);
        }
    }
    internal sealed class WallpaperPanel : Panel
    {
        public WallpaperPanel() { DoubleBuffered = true; ResizeRedraw = true; }
        protected override void OnPaintBackground(PaintEventArgs e)
        {
            var form = FindForm() as ToolboxForm;
            if (BackColor == Color.Transparent && form != null && form.WallpaperEnabled) form.PaintWallpaperRegion(e.Graphics, this, e.ClipRectangle);
            else base.OnPaintBackground(e);
        }
    }
}
