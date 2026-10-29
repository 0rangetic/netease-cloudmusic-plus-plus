// STA test: toolbox EXE. Offscreen, isolated settings, no startup workers.
using System;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Windows.Forms;

internal static class WallpaperScrollValidation
{
    private const BindingFlags Private = BindingFlags.Instance | BindingFlags.NonPublic;
    [StructLayout(LayoutKind.Sequential)]
    private struct Rect { public int Left, Top, Right, Bottom; }
    [DllImport("user32.dll")] private static extern IntPtr SendMessage(IntPtr window, int message, IntPtr wParam, IntPtr lParam);
    [DllImport("user32.dll")] private static extern bool GetUpdateRect(IntPtr window, out Rect rectangle, bool erase);
    [DllImport("user32.dll")] private static extern bool PostMessage(IntPtr window, int message, IntPtr wParam, IntPtr lParam);
    [DllImport("user32.dll")] private static extern bool SetWindowPos(IntPtr window, IntPtr after, int x, int y, int width, int height, uint flags);
    [DllImport("user32.dll")] private static extern bool InvalidateRect(IntPtr window, ref Rect rectangle, bool erase);
    [DllImport("user32.dll")] private static extern bool ValidateRect(IntPtr window, IntPtr rectangle);
    private static object Call(object value, string method, params object[] args) { return value.GetType().GetMethod(method, Private).Invoke(value, args); }
    private static object Field(object value, string name) { return value.GetType().GetField(name, Private).GetValue(value); }
    private static void Check(bool condition, string message) { if (!condition) throw new Exception(message); }
    private static void CheckQueuedFullPaint(ListView list, string action)
    {
        Rect pending;
        bool queued = GetUpdateRect(list.Handle, out pending, false);
        Check(queued && pending.Left == 0 && pending.Top == 0 &&
            pending.Right == list.ClientSize.Width && pending.Bottom == list.ClientSize.Height,
            action + " must invalidate the complete wallpaper viewport without form.Refresh; queued=" + queued + "; rect=" + pending.Left + "," + pending.Top + "," + pending.Right + "," + pending.Bottom + "; client=" + list.ClientSize);
        list.Update();
    }
    private static void CheckPixels(ListView list)
    {
        using (var bitmap = new Bitmap(list.ClientSize.Width, list.ClientSize.Height)) {
            using (var graphics = Graphics.FromImage(bitmap)) {
                IntPtr dc = graphics.GetHdc();
                try { SendMessage(list.Handle, 0x318, dc, new IntPtr(4 | 8)); }
                finally { graphics.ReleaseHdc(dc); }
            }
            var surface = (Bitmap)Field(list, "wallpaperSurface");
            // Right of every text column, sample many rows instead of one pixel.
            int x = bitmap.Width - 100;
            for (int y = 45; y < bitmap.Height - 25; y += 17)
                Check(bitmap.GetPixel(x, y).ToArgb() == surface.GetPixel(x, y).ToArgb(), "Stationary wallpaper pixels at row " + y);
        }
    }
    private static void CheckAtomicRow(ListView list)
    {
        var surface = (Bitmap)Field(list, "wallpaperSurface");
        Call(list, "EnsureViewportFrame");
        var bitmap = (Bitmap)Field(list, "viewportFrame");
        int first = SendMessage(list.Handle, 0x1027, IntPtr.Zero, IntPtr.Zero).ToInt32();
        int checkedRows = 0;
        for (int row = first; row < list.Items.Count; row++) {
            var item = list.Items[row];
            if (item.Bounds.Top >= list.ClientSize.Height) break;
            if (item.Bounds.Bottom >= list.ClientSize.Height || item.Bounds.Top < 30) continue;
            for (int column = 0; column < item.SubItems.Count; column++) {
                var cell = item.SubItems[column].Bounds;
                if (column == 0) cell.Width = list.Columns[0].Width;
                int textPixels = 0;
                for (int y = Math.Max(0, cell.Top); y < Math.Min(bitmap.Height, cell.Bottom); y++)
                    for (int x = Math.Max(0, cell.Left + 4); x < Math.Min(bitmap.Width, cell.Right - 4); x++)
                        if (bitmap.GetPixel(x, y).ToArgb() != surface.GetPixel(x, y).ToArgb()) textPixels++;
                Check(textPixels > 5, "Cached viewport must retain row " + row + ", column " + column);
            }
            checkedRows++;
        }
        Check(checkedRows > 10, "Check many visible rows, not just one");
        int renders = (int)Field(list, "frameRenderCount");
        for (int repeat = 0; repeat < 25; repeat++) CheckPixels(list);
        Check((int)Field(list, "frameRenderCount") == renders, "Unchanged paint reuses the completed frame");
        Check(object.ReferenceEquals(bitmap, Field(list, "viewportFrame")), "Reuse the viewport bitmap allocation");
        SendMessage(list.Handle, 0xF, IntPtr.Zero, IntPtr.Zero);
        Rect remaining;
        Check(!GetUpdateRect(list.Handle, out remaining, false), "Paint must settle without scheduling itself again");
        Console.WriteLine("PASS: all columns in " + checkedRows + " visible rows; 25 cached presentations; paint settles.");
    }
    private static void CheckNativeScrollAndPartialDamage(ListView list)
    {
        CheckPixels(list);
        int renders = (int)Field(list, "frameRenderCount");
        int first = SendMessage(list.Handle, 0x1027, IntPtr.Zero, IntPtr.Zero).ToInt32();
        // Call the actual control procedure, bypassing our input-message
        // interception, just as its internal tracking loop can scroll itself.
        Call(list, "DefWndProc", Message.Create(list.Handle, 0x1014, IntPtr.Zero, new IntPtr(240)));
        Check(SendMessage(list.Handle, 0x1027, IntPtr.Zero, IntPtr.Zero).ToInt32() > first, "Bypassed native scroll really moved rows");
        // Ensure the test does not accidentally depend on a dirty notification.
        list.GetType().GetField("frameDirty", Private).SetValue(list, false);
        CheckPixels(list);
        Check((int)Field(list, "frameRenderCount") > renders, "Native scroll position must invalidate cache even without managed dirty notification");
        // Repeat after scrollbar/client geometry has settled, so rebuilding
        // cannot accidentally be explained by a changed bitmap size.
        renders = (int)Field(list, "frameRenderCount");
        Call(list, "DefWndProc", Message.Create(list.Handle, 0x1014, IntPtr.Zero, new IntPtr(240)));
        list.GetType().GetField("frameDirty", Private).SetValue(list, false);
        CheckPixels(list);
        Check((int)Field(list, "frameRenderCount") > renders, "Scroll-only change must invalidate settled frame without dirty notification");
        CheckAtomicRow(list);
        ValidateRect(list.Handle, IntPtr.Zero);
        var strip = new Rect { Left = 0, Top = list.ClientSize.Height - 9, Right = list.ClientSize.Width, Bottom = list.ClientSize.Height };
        InvalidateRect(list.Handle, ref strip, false);
        SendMessage(list.Handle, 0xF, IntPtr.Zero, IntPtr.Zero);
        Check((Rectangle)Field(list, "lastPaintRectangle") == list.ClientRectangle, "Actual update region expanded before BeginPaint covers the full viewport");
        Rect remaining;
        Check(!GetUpdateRect(list.Handle, out remaining, false), "Expanded paint settles after one transaction");
        Console.WriteLine("PASS: bypassed native scrolling refreshes cache; partial damage paints full client once.");
    }
    [STAThread]
    private static int Main(string[] args)
    {
        string directory = Path.Combine(Path.GetTempPath(), "ncm-scroll-" + Guid.NewGuid());
        Directory.CreateDirectory(directory);
        try {
            Application.SetUnhandledExceptionMode(UnhandledExceptionMode.ThrowException);
            Application.EnableVisualStyles();
            var type = Assembly.LoadFrom(Path.GetFullPath(args[0])).GetType("NeteaseToolbox.ToolboxForm", true);
            using (var form = (Form)Activator.CreateInstance(type, new object[] { false })) {
                type.GetField("customAppearancePath", Private).SetValue(form, Path.Combine(directory, "custom.json"));
                type.GetField("themeSettingPath", Private).SetValue(form, Path.Combine(directory, "theme.txt"));
                var events = (System.ComponentModel.EventHandlerList)typeof(Control).GetProperty("Events", Private).GetValue(form, null);
                foreach (var field in typeof(Form).GetFields(BindingFlags.Static | BindingFlags.NonPublic)) {
                    if (field.Name.IndexOf("shown", StringComparison.OrdinalIgnoreCase) < 0) continue;
                    object key = field.GetValue(null);
                    if (events[key] != null) events.RemoveHandler(key, events[key]);
                }
                form.StartPosition = FormStartPosition.Manual;
                form.Location = new Point(-30000, -30000);
                form.MaximumSize = new Size(3840, 2160);
                form.Size = new Size(2560, 1500);
                Call(form, "ResetCustomAppearance");
                string path = Path.Combine(directory, "background.png");
                using (var image = new Bitmap(1280, 800)) {
                    using (var graphics = Graphics.FromImage(image))
                        for (int y = 0; y < image.Height; y++) using (var brush = new SolidBrush(Color.FromArgb(y % 255, (y * 3) % 255, (y * 7) % 255)))
                            graphics.FillRectangle(brush, 0, y, image.Width, 1);
                    image.Save(path);
                }
                Call(form, "InstallWallpaper", path);
                form.Show();
                SetWindowPos(form.Handle, IntPtr.Zero, -30000, -30000, 2560, 1500, 0x414);
                Check(form.Size == new Size(2560, 1500), "Exercise the user's large viewport without desktop size clamping");
                Call(form, "NavigateTo", 0);
                var list = (ListView)form.Controls.Find("PendingSongList", true)[0];
                list.BeginUpdate();
                for (int row = 0; row < 1500; row++) list.Items.Add(new ListViewItem(new[] { "歌曲 " + row, "目录" }));
                list.EndUpdate();
                Application.DoEvents();
                form.Update();
                CheckPixels(list);
                CheckAtomicRow(list);
                CheckNativeScrollAndPartialDamage(list);
                int generation = (int)Field(form, "wallpaperCanvasGeneration");
                var watch = Stopwatch.StartNew();
                for (int index = 0; index < 40; index++) {
                    SendMessage(list.Handle, 0x1014, IntPtr.Zero, new IntPtr(index % 2 == 0 ? 90 : -54));
                    CheckQueuedFullPaint(list, "LVM_SCROLL");
                    CheckPixels(list);
                }
                SendMessage(list.Handle, 0x1013, new IntPtr(1000), IntPtr.Zero);
                CheckQueuedFullPaint(list, "EnsureVisible");
                CheckPixels(list);
                SendMessage(list.Handle, 0x20A, new IntPtr(-120 << 16), IntPtr.Zero);
                CheckQueuedFullPaint(list, "Mouse wheel");
                int beforeWheel = SendMessage(list.Handle, 0x1027, IntPtr.Zero, IntPtr.Zero).ToInt32();
                for (int repeat = 0; repeat < 30; repeat++) {
                    SendMessage(list.Handle, 0x20A, new IntPtr(-120 << 16), IntPtr.Zero);
                    CheckQueuedFullPaint(list, "Continuous wheel");
                    CheckPixels(list);
                }
                Check(SendMessage(list.Handle, 0x1027, IntPtr.Zero, IntPtr.Zero).ToInt32() > beforeWheel, "Wheel actually changes native scroll position");
                CheckAtomicRow(list);
                SendMessage(list.Handle, 0x115, new IntPtr(2), IntPtr.Zero);
                CheckQueuedFullPaint(list, "Scrollbar page up");
                SendMessage(list.Handle, 0x100, new IntPtr(0x22), IntPtr.Zero);
                CheckQueuedFullPaint(list, "Keyboard page down");
                watch.Stop();
                Check((int)Field(form, "wallpaperCanvasGeneration") == generation, "Scrolling reuses the wallpaper canvas");
                Console.WriteLine("Scroll/pixel checks: " + watch.ElapsedMilliseconds + " ms; no form.Refresh.");
                // A burst of layout changes must not eagerly build intermediate canvases.
                for (int index = 0; index < 8; index++) form.Size = new Size(960 + index * 80, 620 + index * 40);
                Check((int)Field(form, "wallpaperCanvasGeneration") == generation, "Resize waits for paint, no intermediate canvas allocation");
                watch.Restart();
                Application.DoEvents();
                form.Update();
                watch.Stop();
                Check((int)Field(form, "wallpaperCanvasGeneration") == generation + 1, "Resize paints exactly one final canvas");
                var actual = (Rectangle)Field(list, "placement");
                var expected = new Rectangle(form.PointToClient(list.PointToScreen(Point.Empty)), list.ClientSize);
                Check(actual == expected, "Resized slice matches final layout");
                CheckPixels(list);
                Console.WriteLine("Final resize paint: " + watch.ElapsedMilliseconds + " ms; one canvas for 8 sizes.");
                int visibleRow = SendMessage(list.Handle, 0x1027, IntPtr.Zero, IntPtr.Zero).ToInt32() + 1;
                var hitBounds = list.Items[visibleRow].Bounds;
                var hitPoint = new Point(12, hitBounds.Top + hitBounds.Height / 2);
                Check(list.HitTest(hitPoint).Item == list.Items[visibleRow], "Native hit testing matches cached row geometry");
                IntPtr clickPosition = new IntPtr((hitPoint.Y << 16) | hitPoint.X);
                // Queue the complete click before pumping. Native drag
                // detection may run a nested loop inside WM_LBUTTONDOWN.
                PostMessage(list.Handle, 0x201, new IntPtr(1), clickPosition);
                PostMessage(list.Handle, 0x202, IntPtr.Zero, clickPosition);
                Application.DoEvents();
                list.Update();
                Check(list.SelectedItems.Count == 1 && list.Items[visibleRow].Selected, "Real mouse click selects the cached row");
                Call(list, "EnsureViewportFrame");
                var selectedFrame = (Bitmap)Field(list, "viewportFrame");
                var selectedSurface = (Bitmap)Field(list, "wallpaperSurface");
                Check(selectedFrame.GetPixel(selectedFrame.Width - 100, hitPoint.Y).ToArgb() != selectedSurface.GetPixel(selectedSurface.Width - 100, hitPoint.Y).ToArgb(), "Selection highlight is included in frame");
                int renderCount = (int)Field(list, "frameRenderCount");
                list.Items[visibleRow].SubItems[1].Text = "更新标签";
                Call(list, "EnsureViewportFrame");
                Check((int)Field(list, "frameRenderCount") > renderCount, "Changing cell data invalidates cached frame");
                list.Items.Clear();
                list.Update();
                CheckPixels(list);
                Call(form, "ClearWallpaper");
                SendMessage(list.Handle, 0x20A, new IntPtr(-120 << 16), IntPtr.Zero);
                form.Update();
                type.GetField("allowExit", Private).SetValue(form, true);
                form.Close();
            }
            Console.WriteLine("PASS: native scrolling, wallpaper alignment, deferred resize, selection, empty rows and no wallpaper.");
            return 0;
        }
        catch (Exception exception) { Console.Error.WriteLine(exception); return 1; }
        finally { Directory.Delete(directory, true); }
    }
}
