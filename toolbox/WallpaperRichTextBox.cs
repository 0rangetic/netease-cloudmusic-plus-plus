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
        [DllImport("user32.dll")] private static extern IntPtr SendMessage(IntPtr window, int message, IntPtr wParam, IntPtr lParam);
        [StructLayout(LayoutKind.Sequential)] private struct NativeRect { public int Left, Top, Right, Bottom; }
        [StructLayout(LayoutKind.Sequential)] private struct FormatRange { public IntPtr Dc, TargetDc; public NativeRect Area, Page; public int First, Last; }
        private ToolboxForm wallpaperOwner;
        private bool transparentWallpaper;
        internal bool WallpaperTransparent { get { return transparentWallpaper; } }

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

        internal void RefreshWallpaper() { if (!IsDisposed && !Disposing) Invalidate(); }

        private bool CanPaintWallpaper
        {
            get { return transparentWallpaper && wallpaperOwner != null && !wallpaperOwner.IsDisposed && !wallpaperOwner.Disposing && wallpaperOwner.WallpaperEnabled; }
        }

        protected override void WndProc(ref Message message)
        {
            if (IsDisposed || Disposing) { base.WndProc(ref message); return; }
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
        }

        protected override void Dispose(bool disposing) { wallpaperOwner = null; base.Dispose(disposing); }
    }
}
