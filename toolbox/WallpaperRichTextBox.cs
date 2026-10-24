using System;
using System.Drawing;
using System.Runtime.InteropServices;
using System.Windows.Forms;

namespace NeteaseToolbox
{
    // RichEdit supplies native IME, editing, selection and clipboard handling.
    // Its transparent window style lets the cached wallpaper show beneath it.
    internal sealed class WallpaperRichTextBox : RichTextBox
    {
        [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern IntPtr SendMessage(IntPtr window, int message, IntPtr wParam, IntPtr lParam);
        [DllImport("user32.dll")] private static extern bool InvalidateRect(IntPtr window, IntPtr rectangle, bool erase);
        [DllImport("user32.dll")] private static extern IntPtr BeginPaint(IntPtr window, out PaintState paint);
        [DllImport("user32.dll")] private static extern bool EndPaint(IntPtr window, ref PaintState paint);
        [StructLayout(LayoutKind.Sequential)] private struct NativeRect { public int Left, Top, Right, Bottom; }
        [StructLayout(LayoutKind.Sequential)] private struct PaintState {
            public IntPtr Dc; public int Erase; public NativeRect Area; public int Restore, Update;
            [MarshalAs(UnmanagedType.ByValArray, SizeConst = 32)] public byte[] Reserved;
        }
        [StructLayout(LayoutKind.Sequential)] private struct FormatRange { public IntPtr Dc, TargetDc; public NativeRect Area, Page; public int First, Last; }
        private ToolboxForm wallpaperOwner;
        private bool transparentWallpaper;
        private bool applyingUniformFont;
        internal bool UseUniformFont { get; set; }
        internal bool WallpaperTransparent { get { return transparentWallpaper; } }

        private void ApplyUniformFont()
        {
            if (!UseUniformFont || applyingUniformFont || !IsHandleCreated || IsDisposed || Disposing) return;
            applyingUniformFont = true;
            int start = SelectionStart, length = SelectionLength;
            IntPtr position = Marshal.AllocHGlobal(8);
            try {
                SendMessage(Handle, 0x4DD, IntPtr.Zero, position);
                SelectAll();
                SelectionFont = Font;
                Select(Math.Min(start, TextLength), Math.Min(length, Math.Max(0, TextLength - start)));
                SendMessage(Handle, 0x4DE, IntPtr.Zero, position);
            }
            finally { Marshal.FreeHGlobal(position); applyingUniformFont = false; }
        }

        protected override void OnHandleCreated(EventArgs e) { base.OnHandleCreated(e); ApplyUniformFont(); }
        protected override void OnFontChanged(EventArgs e) { base.OnFontChanged(e); ApplyUniformFont(); RefreshWallpaper(); }
        protected override void OnTextChanged(EventArgs e) { ApplyUniformFont(); base.OnTextChanged(e); RefreshWallpaper(); }

        protected override CreateParams CreateParams
        {
            get { var parameters = base.CreateParams; if (transparentWallpaper) parameters.ExStyle |= 0x20; return parameters; }
        }

        internal void SetWallpaperOwner(ToolboxForm owner)
        {
            wallpaperOwner = owner;
            bool next = owner != null && owner.WallpaperEnabled;
            if (next != transparentWallpaper) {
                int start = SelectionStart, length = SelectionLength;
                transparentWallpaper = next;
                if (IsHandleCreated && !IsDisposed && !Disposing) { RecreateHandle(); Select(Math.Min(start, TextLength), Math.Min(length, Math.Max(0, TextLength - start))); }
            }
            RefreshWallpaper();
        }

        internal void RefreshWallpaper()
        {
            if (!IsDisposed && !Disposing && IsHandleCreated) InvalidateRect(Handle, IntPtr.Zero, true);
        }

        private bool CanPaintWallpaper
        {
            get { return transparentWallpaper && wallpaperOwner != null && !wallpaperOwner.IsDisposed && !wallpaperOwner.Disposing && wallpaperOwner.WallpaperEnabled; }
        }

        protected override void WndProc(ref Message message)
        {
            if (IsDisposed || Disposing) { base.WndProc(ref message); return; }
            // A transparent RichEdit normally only paints changed glyphs and
            // scrolls its existing pixels. Those pixels include the old text
            // and wallpaper. Expand every paint to the whole client and request
            // an erase before RichEdit draws, including partial internal paints.
            if (message.Msg == 0xF && CanPaintWallpaper) {
                InvalidateRect(Handle, IntPtr.Zero, true);
                // Empty RichEdit can clear its transparent surface to black.
                // There are no native glyphs/selection to draw in this case.
                if (TextLength == 0) {
                    PaintState paint;
                    IntPtr dc = BeginPaint(Handle, out paint);
                    try {
                        using (var graphics = Graphics.FromHdc(dc))
                            wallpaperOwner.PaintWallpaperRegion(graphics, this, ClientRectangle);
                    }
                    finally { EndPaint(Handle, ref paint); }
                    message.Result = IntPtr.Zero;
                    return;
                }
                // WS_EX_TRANSPARENT can suppress WM_ERASEBKGND. Restore the
                // full background explicitly; native WM_PAINT still owns its
                // BeginPaint/EndPaint and renders editing/selection normally.
                using (var graphics = Graphics.FromHwnd(Handle))
                    wallpaperOwner.PaintWallpaperRegion(graphics, this, ClientRectangle);
            }
            if (message.Msg == 0x14 && message.WParam != IntPtr.Zero && CanPaintWallpaper) {
                using (var graphics = Graphics.FromHdc(message.WParam)) wallpaperOwner.PaintWallpaperRegion(graphics, this, ClientRectangle);
                message.Result = new IntPtr(1);
                return;
            }
            // RichEdit does not implement WM_PRINTCLIENT. Render its text using
            // its native formatting API for snapshots, without changing the
            // live editing/selection/IME paint path.
            if ((message.Msg == 0x317 || message.Msg == 0x318) && message.WParam != IntPtr.Zero && CanPaintWallpaper) {
                using (var graphics = Graphics.FromHdc(message.WParam)) {
                    wallpaperOwner.PaintWallpaperRegion(graphics, this, ClientRectangle);
                    var area = new NativeRect { Right = (int)Math.Round(ClientSize.Width * 1440f / graphics.DpiX), Bottom = (int)Math.Round(ClientSize.Height * 1440f / graphics.DpiY) };
                    var range = new FormatRange { Dc = message.WParam, TargetDc = message.WParam, Area = area, Page = area, First = GetCharIndexFromPosition(Point.Empty), Last = -1 };
                    IntPtr pointer = Marshal.AllocHGlobal(Marshal.SizeOf(typeof(FormatRange)));
                    try { Marshal.StructureToPtr(range, pointer, false); SendMessage(Handle, 0x439, new IntPtr(1), pointer); }
                    finally { SendMessage(Handle, 0x439, IntPtr.Zero, IntPtr.Zero); Marshal.FreeHGlobal(pointer); }
                }
                message.Result = IntPtr.Zero;
                return;
            }
            base.WndProc(ref message);
            // RichEdit can draw/ScrollWindow synchronously while processing
            // input, bypassing WM_PAINT. Queue a complete background + text
            // repaint after scrolling, zooming, editing or formatting changes.
            if (CanPaintWallpaper) {
                switch (message.Msg) {
                    case 0x5: case 0xC: case 0x30: // size, text, font
                    case 0x100: case 0x102: case 0x302: case 0x303: // keyboard/paste/clear
                    case 0x114: case 0x115: case 0x20A: case 0x20E: // scrollbars/wheels
                    case 0xB5: case 0xB6: case 0xB7: case 0xC2: // scroll, line scroll, caret, replace
                    case 0x444: case 0x449: case 0x4DE: case 0x4E1: // format, stream, scroll pos, zoom
                        RefreshWallpaper(); break;
                }
            }
        }

        protected override void Dispose(bool disposing) { wallpaperOwner = null; base.Dispose(disposing); }
    }
}
