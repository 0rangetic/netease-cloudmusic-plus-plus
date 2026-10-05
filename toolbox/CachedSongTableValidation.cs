// STA integration test: toolbox EXE, optional screenshot directory.
// Uses offscreen windows, isolated settings and no network/startup workers.
using System;
using System.Collections;
using System.ComponentModel;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text;
using System.Windows.Forms;

internal static class CachedSongTableValidation
{
    private const BindingFlags Private = BindingFlags.Instance | BindingFlags.NonPublic;
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern int GetClassName(IntPtr window, StringBuilder name, int size);
    [DllImport("user32.dll")] private static extern bool SetWindowPos(IntPtr window, IntPtr after, int x, int y, int width, int height, uint flags);
    [DllImport("user32.dll")] private static extern IntPtr SendMessage(IntPtr window, int message, IntPtr wParam, IntPtr lParam);
    private static object Call(object value, string method, params object[] args) { var member = value.GetType().GetMethod(method, Private | BindingFlags.Public); if (member == null) throw new Exception("Missing method: " + method); return member.Invoke(value, args); }
    private static object Field(object value, string name) { return value.GetType().GetField(name, Private).GetValue(value); }
    private static void SetField(object value, string name, object data) { value.GetType().GetField(name, Private).SetValue(value, data); }
    private static object Property(object value, string name) { return value.GetType().GetProperty(name).GetValue(value, null); }
    private static void SetProperty(object value, string name, object data) { value.GetType().GetProperty(name).SetValue(value, data, null); }
    private static void Check(bool condition, string message) { if (!condition) throw new Exception(message); }
    private static Bitmap Paint(Control table)
    {
        var bitmap = new Bitmap(Math.Max(1, table.Width), Math.Max(1, table.Height));
        table.DrawToBitmap(bitmap, new Rectangle(Point.Empty, bitmap.Size));
        return bitmap;
    }
    private static void CheckFrame(Control table)
    {
        using (var bitmap = Paint(table)) {
            int top = (int)Field(table, "firstRow");
            var indices = (int[])Field(table, "renderedRows");
            var items = (IList)Property(table, "Items");
            var body = (Rectangle)Field(table, "body");
            int rowHeight = (int)Field(table, "rowHeight");
            Check((int)Field(table, "displayedFirstRow") == top, "Frame presents the latest scroll position");
            Check(indices.SequenceEqual(Enumerable.Range(top, Math.Min(items.Count - top, (body.Height + rowHeight - 1) / rowHeight))), "Visible rows are consecutive, unique and match viewport size");
            var background = (Bitmap)Field(table, "backgroundCache");
            // Check transparent spaces between glyphs too, including horizontally
            // clipped columns in a small viewport. Text itself must not be sampled.
            var cache = (IDictionary)Field(table, "textRows");
            int samples = 0;
            for (int y = body.Top + 3; y < body.Bottom; y += 13) {
                int index = top + (y - body.Top) / rowHeight;
                var row = index < items.Count ? (Bitmap)cache[items[index]] : null;
                if (index == (int)Property(table, "SelectedIndex")) continue;
                for (int x = 8; x < body.Right; x += 61) {
                    if (row != null && row.GetPixel(x, (y - body.Top) % rowHeight).A != 0) continue;
                    Check(bitmap.GetPixel(x, y) == background.GetPixel(x, y), "Wallpaper stays fixed at x=" + x + ",y=" + y);
                    samples++;
                }
            }
            Check(samples > 20, "Many wallpaper pixels verified in the actual frame");
            Check(((IDictionary)Field(table, "textRows")).Count <= Math.Max(8, indices.Length * 3), "Text cache stays bounded by viewport rows");
        }
    }
    private static void CheckTextPixels(Control table)
    {
        using (var bitmap = Paint(table)) {
            var background = (Bitmap)Field(table, "backgroundCache");
            var body = (Rectangle)Field(table, "body");
            int height = (int)Field(table, "rowHeight");
            var columns = (IList)Property(table, "Columns");
            int rows = Math.Min(5, body.Height / height);
            for (int row = 0; row < rows; row++) {
                int left = 0;
                for (int column = 0; column < columns.Count; column++) {
                    int width = (int)Property(columns[column], "Width");
                    int pixels = 0;
                    for (int y = body.Top + row * height; y < body.Top + (row + 1) * height; y++)
                        for (int x = left + 4; x < Math.Min(body.Right, left + width - 4); x++)
                            if (bitmap.GetPixel(x, y) != background.GetPixel(x, y)) pixels++;
                    Check(pixels > 5, "Visible text in row=" + row + ", column=" + column);
                    left += width;
                }
            }
        }
    }
    private static void RemoveEvent(Control control, Type declaringType, string name)
    {
        var events = (EventHandlerList)typeof(Control).GetProperty("Events", Private).GetValue(control, null);
        foreach (var field in declaringType.GetFields(BindingFlags.Static | BindingFlags.NonPublic)) {
            if (field.Name.IndexOf(name, StringComparison.OrdinalIgnoreCase) < 0) continue;
            object key = field.GetValue(null);
            if (events[key] != null) events.RemoveHandler(key, events[key]);
        }
    }
    [STAThread]
    private static int Main(string[] args)
    {
        string directory = Path.Combine(Path.GetTempPath(), "ncm-cached-table-" + Guid.NewGuid());
        Directory.CreateDirectory(directory);
        try {
            Application.SetUnhandledExceptionMode(UnhandledExceptionMode.ThrowException);
            Application.EnableVisualStyles();
            var type = Assembly.LoadFrom(Path.GetFullPath(args[0])).GetType("NeteaseToolbox.ToolboxForm", true);
            using (var form = (Form)Activator.CreateInstance(type, new object[] { false })) {
                SetField(form, "customAppearancePath", Path.Combine(directory, "custom.json"));
                SetField(form, "themeSettingPath", Path.Combine(directory, "theme.txt"));
                RemoveEvent(form, typeof(Form), "shown");
                Call(form, "ResetCustomAppearance");
                string path = Path.Combine(directory, "background.png");
                using (var image = new Bitmap(1280, 800)) {
                    using (var graphics = Graphics.FromImage(image)) {
                        graphics.Clear(Color.SteelBlue);
                        using (var brush = new SolidBrush(Color.Coral)) graphics.FillEllipse(brush, 60, 30, 700, 700);
                        using (var brush = new SolidBrush(Color.Gold)) graphics.FillRectangle(brush, 930, 300, 250, 450);
                    }
                    image.Save(path);
                }
                Call(form, "InstallWallpaper", path);
                form.StartPosition = FormStartPosition.Manual;
                form.Location = new Point(-30000, -30000);
                form.MaximumSize = new Size(3840, 2160);
                form.Show();
                SetWindowPos(form.Handle, IntPtr.Zero, -30000, -30000, 2560, 1500, 0x414);
                Check(form.Size == new Size(2560, 1500), "Large viewport is not clamped to desktop");
                Call(form, "NavigateTo", 1);
                var table = form.Controls.Find("GenreSongList", true)[0];
                var items = (IList)Property(table, "Items");
                var name = new StringBuilder(256);
                GetClassName(table.Handle, name, name.Capacity);
                Check(name.ToString().IndexOf("SysListView32", StringComparison.OrdinalIgnoreCase) < 0 && !(table is ListView), "Genre table has no native ListView scrolling window");
                var data = (IDictionary)Field(form, "genreRows");
                Call(table, "BeginUpdate");
                for (int row = 0; row < 3000; row++) {
                    var item = new ListViewItem(new[] { "缓存验证歌曲 " + row.ToString("D4"), "流行", "电子 / 器乐", "轻快", "网易云", "已分析", "测试来源目录" });
                    item.Tag = "netease://song/" + (row + 10000);
                    item.ToolTipText = "歌曲 " + row;
                    items.Add(item);
                    data[item.Tag] = item;
                }
                Call(table, "EndUpdate");
                Application.DoEvents();
                CheckFrame(table);
                CheckTextPixels(table);
                var backgroundBefore = Field(table, "backgroundCache");
                var frameBefore = Field(table, "viewportFrame");
                int generation = (int)Field(form, "wallpaperCanvasGeneration");
                int renders = (int)Field(table, "frameRenderCount");
                var watch = Stopwatch.StartNew();
                for (int input = 0; input < 300; input++) SendMessage(table.Handle, 0x20A, new IntPtr(-120 << 16), IntPtr.Zero);
                watch.Stop();
                int lines = SystemInformation.MouseWheelScrollLines;
                int expected = lines < 0 ? 300 * ((VScrollBar)Field(table, "vertical")).LargeChange : 300 * lines;
                expected = Math.Min(expected, items.Count - ((VScrollBar)Field(table, "vertical")).LargeChange);
                Check((int)Field(table, "firstRow") == expected, "Burst wheel input reaches expected row");
                Check((int)Field(table, "frameRenderCount") == renders, "300 wheel messages coalesce without painting each event");
                CheckFrame(table);
                Check((int)Field(table, "frameRenderCount") == renders + 1, "Burst generates one latest frame");
                Check(object.ReferenceEquals(backgroundBefore, Field(table, "backgroundCache")) && object.ReferenceEquals(frameBefore, Field(table, "viewportFrame")), "Scrolling reuses background and viewport allocations");
                Check((int)Field(form, "wallpaperCanvasGeneration") == generation, "Wheel does not rebuild full-window wallpaper");
                Console.WriteLine("300 consecutive native wheel messages: " + watch.ElapsedMilliseconds + " ms; one completed frame; first row=" + expected);
                // Exercise the real timer's invalidation request. Windows clips
                // normal paints for fully offscreen windows, so explicitly print
                // the pending frame afterward; this is not a screen-flicker test.
                int invalidations = 0;
                table.Invalidated += delegate { invalidations++; };
                for (int input = 0; input < 20; input++) SendMessage(table.Handle, 0x20A, new IntPtr(-120 << 16), IntPtr.Zero);
                for (int pump = 0; pump < 20 && ((Timer)Field(table, "frameTimer")).Enabled; pump++) {
                    System.Threading.Thread.Sleep(10);
                    Application.DoEvents();
                }
                Check(invalidations > 0, "Real render timer requests a repaint after wheel burst");
                Check(!((Timer)Field(table, "frameTimer")).Enabled, "Render timer stops after requesting pending paint");
                CheckFrame(table);
                renders = (int)Field(table, "frameRenderCount");
                for (int repeat = 0; repeat < 10; repeat++) { using (var unchanged = Paint(table)) { } }
                Check((int)Field(table, "frameRenderCount") == renders, "Unchanged paint reuses completed frame without generating another");
                for (int pass = 0; pass < 30; pass++) {
                    for (int input = 0; input < 20; input++) Call(table, "OnMouseWheel", new HandledMouseEventArgs(MouseButtons.None, 0, 10, 100, pass % 2 == 0 ? -120 : 120));
                    CheckFrame(table);
                }
                Call(table, "SetTopRow", 100);
                CheckFrame(table);
                if (lines > 0) {
                    for (int partial = 0; partial < 4; partial++) Call(table, "OnMouseWheel", new HandledMouseEventArgs(MouseButtons.None, 0, 10, 100, -30));
                    Check((int)Field(table, "firstRow") == 100 + lines, "Fractional wheel deltas accumulate");
                }
                var body = (Rectangle)Field(table, "body");
                int rowHeight = (int)Field(table, "rowHeight");
                int target = (int)Field(table, "firstRow") + 2;
                int clickY = body.Top + 2 * rowHeight + rowHeight / 2;
                Call(table, "OnMouseDown", new MouseEventArgs(MouseButtons.Right, 1, 10, clickY, 0));
                Check((int)Property(table, "SelectedIndex") == target && ((IList)Property(table, "SelectedItems")).Count == 1, "Right click selects exactly the current song after pending wheel input");
                Check(table.ContextMenuStrip != null && table.ContextMenuStrip.Items.Count > 0, "Genre edit menu remains attached");
                RemoveEvent(table, typeof(Control), "mousedoubleclick");
                ListViewItem clicked = null;
                table.MouseDoubleClick += delegate(object sender, MouseEventArgs e) { clicked = (ListViewItem)table.GetType().GetMethod("GetItemAt").Invoke(table, new object[] { e.X, e.Y }); };
                SendMessage(table.Handle, 0x203, new IntPtr(1), new IntPtr((clickY << 16) | 10));
                SendMessage(table.Handle, 0x202, IntPtr.Zero, new IntPtr((clickY << 16) | 10));
                Check(object.ReferenceEquals(clicked, items[target]), "Double click maps to the correct song object");
                clicked = null;
                Call(table, "OnKeyDown", new KeyEventArgs(Keys.Enter));
                Check(object.ReferenceEquals(clicked, items[target]), "Enter maps to the selected song object");
                SetProperty(table, "SelectedIndex", -1);
                using (var before = Paint(table)) {
                    var edited = (ListViewItem)items[target];
                    Call(form, "ApplyGenreResult", (object)new[] { (string)edited.Tag, "古典", "钢琴", "安静", "手动", "已修正" });
                    Check(edited.SubItems[1].Text == "古典" && !((IDictionary)Field(table, "textRows")).Contains(edited), "Real genre update invalidates ownerless row cache");
                    using (var after = Paint(table)) {
                        bool changed = false;
                        for (int y = clickY - rowHeight / 2; y < clickY + rowHeight / 2; y++)
                            for (int x = 250; x < 400; x++) if (before.GetPixel(x, y) != after.GetPixel(x, y)) changed = true;
                        Check(changed, "Edited genre appears in rendered frame");
                    }
                }
                var columns = (IList)Property(table, "Columns");
                int divider = (int)Property(columns[0], "Width");
                Call(table, "OnMouseDown", new MouseEventArgs(MouseButtons.Left, 1, divider, 10, 0));
                Call(table, "OnMouseMove", new MouseEventArgs(MouseButtons.Left, 0, divider + 40, 10, 0));
                Call(table, "OnMouseUp", new MouseEventArgs(MouseButtons.Left, 1, divider + 40, 10, 0));
                Check((int)Property(columns[0], "Width") == divider + 40, "Column divider resizing works");
                Call(table, "SetTopRow", 0);
                for (int theme = 0; theme < 2; theme++) {
                    Call(form, "SetTheme", theme == 0);
                    foreach (string mode in new[] { "Fill", "Fit", "Stretch", "Tile", "Center" }) {
                        SetProperty(Field(form, "appearance"), "WallpaperFit", mode);
                        Call(form, "RebuildWallpaper");
                        CheckFrame(table);
                    }
                }
                if (args.Length > 1) {
                    Directory.CreateDirectory(args[1]);
                    SetProperty(Field(form, "appearance"), "WallpaperFit", "Fill");
                    Call(form, "RebuildWallpaper");
                    using (var image = Paint(table)) image.Save(Path.Combine(args[1], "cached-genre-large.png"));
                }
                using (var font = new Font("SimSun", 14)) {
                    table.Font = font;
                    Check((int)Field(table, "rowHeight") >= font.Height + 6, "Row size follows UI font");
                    CheckFrame(table);
                    CheckTextPixels(table);
                    SetWindowPos(form.Handle, IntPtr.Zero, -30000, -30000, 980, 640, 0x414);
                    Application.DoEvents();
                    var horizontal = (HScrollBar)Field(table, "horizontal");
                    Check(horizontal.Visible, "Small viewport exposes independent horizontal scrollbar");
                    horizontal.Value = Math.Min(300, horizontal.Maximum - horizontal.LargeChange + 1);
                    CheckFrame(table);
                    Check((int)Field(table, "horizontalOffset") == horizontal.Value, "Horizontal bar updates model offset");
                    if (args.Length > 1) using (var image = Paint(table)) image.Save(Path.Combine(args[1], "cached-genre-small.png"));
                    body = (Rectangle)Field(table, "body");
                    rowHeight = (int)Field(table, "rowHeight");
                    target = (int)Field(table, "firstRow") + (body.Height - 1) / rowHeight;
                    Call(table, "OnMouseDown", new MouseEventArgs(MouseButtons.Left, 1, 10, body.Bottom - 1, 0));
                    clicked = null;
                    Call(table, "OnKeyDown", new KeyEventArgs(Keys.Enter));
                    Check(object.ReferenceEquals(clicked, items[target]), "Enter also opens a selected row clipped by the bottom viewport edge");
                    SetProperty(table, "SelectedIndex", -1);
                    for (int pass = 0; pass < 20; pass++) {
                        SetWindowPos(form.Handle, IntPtr.Zero, -30000, -30000, pass % 2 == 0 ? 2560 : 980, pass % 2 == 0 ? 1500 : 640, 0x414);
                        Call(table, "SetTopRow", pass * 120);
                        CheckFrame(table);
                    }
                    Call(table, "OnKeyDown", new KeyEventArgs(Keys.End));
                    Check((int)Property(table, "SelectedIndex") == items.Count - 1, "End selects last song and scrolls into view");
                    Call(table, "OnKeyDown", new KeyEventArgs(Keys.Home));
                    Check((int)Property(table, "SelectedIndex") == 0, "Home selects first song");
                    SetProperty(table, "SelectedIndex", -1);
                    Call(form, "ClearWallpaper");
                    CheckFrame(table);
                    items.Clear();
                    CheckFrame(table);
                    Check(((IList)Property(table, "SelectedItems")).Count == 0 && (int)Field(table, "firstRow") == 0, "Empty table clears selection and scroll position");
                    items.Add(new ListViewItem(new[] { "重新加入", "流行", "电子", "轻快", "来源", "状态", "目录" }));
                    CheckFrame(table);
                    table.Dispose();
                    Check(((IDictionary)Field(table, "textRows")).Count == 0 && Field(table, "backgroundCache") == null && Field(table, "viewportFrame") == null, "Disposal frees all cached bitmaps");
                    Check(!((Timer)Field(table, "frameTimer")).Enabled, "Disposed control has no running render timer");
                }
            }
            Console.WriteLine("PASS: native wheel burst, bounded cache, fixed wallpaper, row content, selection/edit/play mapping, columns, themes/modes/fonts, resize, empty data and disposal.");
            return 0;
        }
        catch (Exception exception) { Console.Error.WriteLine(exception); return 1; }
        finally { Directory.Delete(directory, true); }
    }
}
