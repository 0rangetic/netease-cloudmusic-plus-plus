// Compile as a separate console program with System.Windows.Forms/System.Drawing.
// Arguments: toolbox EXE, optional screenshot directory. Uses isolated settings.
using System;
using System.Drawing;
using System.Drawing.Imaging;
using System.IO;
using System.Reflection;
using System.Windows.Forms;

internal static class AppearanceValidation
{
    private const BindingFlags Private = BindingFlags.Instance | BindingFlags.NonPublic;
    private static void Check(bool condition, string message) { if (!condition) throw new Exception(message); }
    private static object Call(Form form, string name, params object[] args) { return form.GetType().GetMethod(name, Private).Invoke(form, args); }
    private static object Field(Form form, string name) { return form.GetType().GetField(name, Private).GetValue(form); }
    private static void SetField(Form form, string name, object value) { form.GetType().GetField(name, Private).SetValue(form, value); }
    private static Control Find(Form form, string name) { return form.Controls.Find(name, true)[0]; }
    private static void SetPreference(Form form, string name, object value) { var preferences = Field(form, "appearance"); preferences.GetType().GetProperty(name).SetValue(preferences, value, null); }
    private static object Preference(Form form, string name) { var preferences = Field(form, "appearance"); return preferences.GetType().GetProperty(name).GetValue(preferences, null); }
    private static void ShowForScreenshot(Form form)
    {
        var events = (System.ComponentModel.EventHandlerList)typeof(Control).GetProperty("Events", Private).GetValue(form, null);
        foreach (var field in typeof(Form).GetFields(BindingFlags.Static | BindingFlags.NonPublic)) {
            if (field.Name.IndexOf("shown", StringComparison.OrdinalIgnoreCase) < 0) continue;
            object key = field.GetValue(null);
            if (events[key] != null) events.RemoveHandler(key, events[key]);
        }
        form.StartPosition = FormStartPosition.Manual;
        form.Location = new Point(-30000, -30000);
        form.Show();
    }
    private static void Snapshot(Form form, string path)
    {
        form.PerformLayout();
        Application.DoEvents();
        using (var bitmap = new Bitmap(form.Width, form.Height)) {
            form.DrawToBitmap(bitmap, new Rectangle(Point.Empty, form.Size));
            bitmap.Save(path);
        }
    }
    private static void CheckTableBackground(ListView list)
    {
        var surface = (Bitmap)list.GetType().GetField("wallpaperSurface", Private).GetValue(list);
        using (var bitmap = new Bitmap(list.ClientSize.Width, list.ClientSize.Height)) {
            list.DrawToBitmap(bitmap, new Rectangle(Point.Empty, bitmap.Size));
            Check(bitmap.GetPixel(bitmap.Width - 80, 200).ToArgb() == surface.GetPixel(bitmap.Width - 80, 200).ToArgb(), "Table pixels match aligned wallpaper after painting/scrolling");
        }
    }
    [STAThread]
    private static int Main(string[] args)
    {
        string temporary = Path.Combine(Path.GetTempPath(), "ncm-appearance-test-" + Guid.NewGuid());
        Directory.CreateDirectory(temporary);
        try {
            Application.EnableVisualStyles();
            Application.SetCompatibleTextRenderingDefault(false);
            var type = Assembly.LoadFrom(Path.GetFullPath(args[0])).GetType("NeteaseToolbox.ToolboxForm", true);
            var blurMethod = type.GetMethod("BlurBitmap", BindingFlags.Static | BindingFlags.NonPublic);
            var placement = type.GetMethod("WallpaperRectangle", BindingFlags.Static | BindingFlags.NonPublic);
            Check((Rectangle)placement.Invoke(null, new object[] { new Size(100, 100), new Size(200, 100), "Fill" }) == new Rectangle(-50, 0, 200, 100), "Fill crops without distortion");
            Check((Rectangle)placement.Invoke(null, new object[] { new Size(100, 100), new Size(200, 100), "Fit" }) == new Rectangle(0, 25, 100, 50), "Fit preserves complete image and letterbox");
            Check((Rectangle)placement.Invoke(null, new object[] { new Size(100, 100), new Size(200, 100), "Stretch" }) == new Rectangle(0, 0, 100, 100), "Stretch fills independently of aspect ratio");
            Check((Rectangle)placement.Invoke(null, new object[] { new Size(100, 100), new Size(40, 20), "Center" }) == new Rectangle(30, 40, 40, 20), "Center retains original dimensions");
            Check((Rectangle)placement.Invoke(null, new object[] { new Size(100, 100), new Size(40, 20), "Tile" }) == new Rectangle(0, 0, 40, 20), "Tile retains original dimensions");
            Check((Rectangle)placement.Invoke(null, new object[] { new Size(0, 0), new Size(40, 20), "Fit" }) == Rectangle.Empty, "Empty viewport is safe");
            Check((Rectangle)placement.Invoke(null, new object[] { new Size(100, 100), new Size(200, 100), "unknown" }) == new Rectangle(-50, 0, 200, 100), "Unknown and legacy mode default to fill");
            var painter = type.GetMethod("DrawWallpaper", BindingFlags.Static | BindingFlags.NonPublic);
            using (var image = new Bitmap(10, 6))
            using (var canvas = new Bitmap(120, 120))
            using (var g = Graphics.FromImage(canvas)) {
                using (var ig = Graphics.FromImage(image)) ig.Clear(Color.Red);
                foreach (string mode in new[] { "Fill", "Fit", "Stretch", "Tile", "Center" }) {
                    g.Clear(Color.Navy);
                    painter.Invoke(null, new object[] { g, image, new Size(20, 12), new Rectangle(10, 10, 100, 100), mode });
                    Check(canvas.GetPixel(0, 0).ToArgb() == Color.Navy.ToArgb(), "Background respects clipping: " + mode);
                    Check(canvas.GetPixel(60, 60).R > 200, "Image drawn at center: " + mode);
                    Check((canvas.GetPixel(12, 12).ToArgb() == Color.Navy.ToArgb()) == (mode == "Fit" || mode == "Center"), "Letterbox/native-size margins: " + mode);
                }
            }
            using (var image = new Bitmap(4, 2))
            using (var canvas = new Bitmap(12, 6))
            using (var g = Graphics.FromImage(canvas)) {
                for (int x = 0; x < 4; x++) for (int y = 0; y < 2; y++) image.SetPixel(x, y, x < 2 ? Color.Red : Color.Blue);
                painter.Invoke(null, new object[] { g, image, image.Size, new Rectangle(0, 0, 12, 6), "Tile" });
                Check(canvas.GetPixel(1, 0) == canvas.GetPixel(5, 2) && canvas.GetPixel(1, 0) != canvas.GetPixel(3, 0), "Tiles repeat at native image period");
            }
            var supportsFont = type.GetMethod("FontSupportsUi", BindingFlags.Static | BindingFlags.NonPublic);
            string chineseFont;
            using (var family = new FontFamily("SimSun")) chineseFont = family.Name;
            Check(!(bool)supportsFont.Invoke(null, new object[] { "Arial" }), "Reject fonts missing Chinese glyphs");
            Check((bool)supportsFont.Invoke(null, new object[] { "SimSun" }), "Accept Chinese UI font");
            using (var tiny = new Bitmap(7, 3, PixelFormat.Format24bppRgb)) {
                using (var g = Graphics.FromImage(tiny)) g.Clear(Color.FromArgb(60, 90, 120));
                using (var blurred = (Bitmap)blurMethod.Invoke(null, new object[] { tiny, 30 }))
                    Check(blurred.GetPixel(6, 2).ToArgb() == tiny.GetPixel(6, 2).ToArgb(), "Blur edge clamp/padded rows preserve uniform color");
            }
            using (var form = (Form)Activator.CreateInstance(type, new object[] { false })) {
                var handle = form.Handle;
                SetField(form, "themeSettingPath", Path.Combine(temporary, "appearance.txt"));
                SetField(form, "customAppearancePath", Path.Combine(temporary, "appearance-custom.json"));
                Call(form, "ResetCustomAppearance");
                Call(form, "LoadTheme");
                var fitSelector = (ComboBox)Find(form, "WallpaperFitSelector");
                Check(fitSelector.SelectedIndex == 0 && fitSelector.Items.Count == 5, "Five modes with legacy fill default");
                SetPreference(form, "ButtonColor", Color.White.ToArgb());
                Call(form, "SetTheme", false);
                var primary = Find(form, "ConvertButton");
                Check(primary.BackColor.ToArgb() == Color.White.ToArgb() && primary.ForeColor.ToArgb() == Color.Black.ToArgb(), "White buttons get black text");
                SetPreference(form, "ButtonColor", Color.Black.ToArgb());
                Call(form, "SetTheme", true);
                Check(primary.ForeColor.ToArgb() == Color.White.ToArgb(), "Black buttons get white text");
                Check(Find(form, "DeduplicateButton").BackColor == Color.FromArgb(176, 73, 65), "Danger action retains red warning");
                SetPreference(form, "ButtonColor", Color.FromArgb(112, 76, 190).ToArgb());
                SetPreference(form, "FontFamily", chineseFont);
                Call(form, "ApplyUiFonts", form);
                Call(form, "NavigateTo", 4);
                Check(Find(form, "SettingsNavigation").Font.FontFamily.Name == chineseFont, "Font survives navigation");
                Check(Find(form, "AppTitle").Font.SizeInPoints == 16, "Heading size preserved");
                int appearanceButtonHeight = -1;
                foreach (string name in new[] { "ButtonColorPicker", "WallpaperPicker", "WallpaperClear", "UiFontPicker", "AppearanceReset" }) {
                    var button = (Button)Find(form, name);
                    int requiredWidth = TextRenderer.MeasureText(button.Text, button.Font).Width + 28;
                    Check(button.Width >= requiredWidth, name + " text fits without wrapping");
                    if (appearanceButtonHeight < 0) appearanceButtonHeight = button.Height;
                    Check(button.Height == appearanceButtonHeight, name + " keeps equal row height");
                }
                string source = Path.Combine(temporary, "source.png");
                using (var bitmap = new Bitmap(640, 400)) {
                    using (var g = Graphics.FromImage(bitmap)) {
                        g.Clear(Color.SteelBlue);
                        using (var brush = new SolidBrush(Color.Coral)) g.FillEllipse(brush, 50, 30, 350, 350);
                        using (var brush = new SolidBrush(Color.Gold)) g.FillRectangle(brush, 420, 120, 150, 260);
                    }
                    bitmap.Save(source);
                }
                Call(form, "InstallWallpaper", source);
                File.Delete(source);
                Check(File.Exists(Path.Combine(temporary, "appearance-background.png")), "Background copied locally without source lock");
                Check(Find(form, "PageHost").BackColor == Color.Transparent, "Wallpaper visible behind pages");
                var slider = (TrackBar)Find(form, "BackgroundBlurSlider");
                slider.Value = 0;
                Call(form, "CommitBlur");
                var clearPixel = ((Bitmap)Field(form, "wallpaperBlurred")).GetPixel(50, 180);
                slider.Value = 30;
                Call(form, "CommitBlur");
                Check(((Bitmap)Field(form, "wallpaperBlurred")).GetPixel(50, 180) != clearPixel, "Blur changes edge pixels");
                bool rejected = false;
                string invalid = Path.Combine(temporary, "invalid.png");
                File.WriteAllText(invalid, "invalid image");
                try { Call(form, "InstallWallpaper", invalid); } catch (TargetInvocationException) { rejected = true; }
                Check(rejected && Field(form, "wallpaperBlurred") != null, "Invalid image rejected while existing background remains");
                Call(form, "SaveCustomAppearance", "test");
                fitSelector.SelectedIndex = 3;
                Check((string)Preference(form, "WallpaperFit") == "Tile", "Mode selector updates preferences");
                var defaults = Activator.CreateInstance(Field(form, "appearance").GetType());
                SetField(form, "appearance", defaults);
                Call(form, "ClearWallpaper");
                Call(form, "LoadCustomAppearance");
                Check((int)Preference(form, "Blur") == 30 && (string)Preference(form, "FontFamily") == chineseFont, "Restore blur and font preferences");
                Check((int)Preference(form, "ButtonColor") == Color.FromArgb(112, 76, 190).ToArgb(), "Restore custom button color");
                Check(Field(form, "wallpaperBlurred") != null, "Restore copied wallpaper after source removal");
                Check(fitSelector.SelectedIndex == 3 && (string)Preference(form, "WallpaperFit") == "Tile", "Restore saved fit selector");
                Check((int)Preference(form, "WallpaperWidth") == 640 && (int)Preference(form, "WallpaperHeight") == 400, "Restore original dimensions");
                for (int page = 0; page < 5; page++) { Call(form, "NavigateTo", page); Call(form, "SetTheme", page % 2 == 0); }
                Call(form, "NavigateTo", 4);
                if (args.Length > 1) {
                    Directory.CreateDirectory(args[1]);
                    ShowForScreenshot(form);
                    Call(form, "SetTheme", true);
                    Snapshot(form, Path.Combine(args[1], "appearance-light.png"));
                    Call(form, "SetTheme", false);
                    Snapshot(form, Path.Combine(args[1], "appearance-dark.png"));
                    form.Size = form.MinimumSize;
                    Snapshot(form, Path.Combine(args[1], "appearance-small.png"));
                    form.Size = new Size(1040, 740);
                    Call(form, "SetTheme", true);
                    for (int mode = 0; mode < 5; mode++) {
                        fitSelector.SelectedIndex = mode;
                        Snapshot(form, Path.Combine(args[1], "wallpaper-mode-" + mode + ".png"));
                    }
                    var row = Find(form, "CustomAppearanceSettings");
                    Check(row.Right <= row.Parent.ClientSize.Width, "Appearance settings fit minimum window width");
                    var list = (ListView)Find(form, "PendingSongList");
                    for (int index = 0; index < 100; index++) list.Items.Add(new ListViewItem(new[] {
                        "背景验证歌曲 " + index, "流行", "电子", "轻快", "网易云", "已分析", "测试来源" }));
                    form.Size = new Size(1600, 900);
                    fitSelector.SelectedIndex = 0;
                    Call(form, "NavigateTo", 0);
                    Call(form, "SetTheme", true);
                    form.Refresh();
                    Application.DoEvents();
                    Check(Find(form, "Sidebar").BackColor == Color.Transparent, "Sidebar is transparent with wallpaper");
                    Check(list.GetType().GetField("wallpaperSurface", Private).GetValue(list) != null && list.OwnerDraw, "Native table uses aligned wallpaper including header");
                    Snapshot(form, Path.Combine(args[1], "table-transparent-light.png"));
                    CheckTableBackground(list);
                    list.Items[80].Selected = true;
                    list.Items[80].EnsureVisible();
                    Application.DoEvents();
                    list.Update();
                    Call(form, "SetTheme", false);
                    Snapshot(form, Path.Combine(args[1], "table-transparent-dark.png"));
                    CheckTableBackground(list);
                    list.Items[81].Selected = true;
                    Check(list.SelectedItems.Count == 2 && list.Items[81].Selected, "Wallpaper preserves conversion multi-selection");
                    Call(form, "NavigateTo", 0);
                    var pending = (ListView)Find(form, "PendingSongList");
                    pending.Items.Clear();
                    pending.Items.Add(new ListViewItem(new[] { "测试歌曲一", "测试来源" }));
                    pending.Items.Add(new ListViewItem(new[] { "测试歌曲二", "测试来源" }));
                    pending.Items[0].Selected = true;
                    pending.Items[1].Selected = true;
                    Check(pending.SelectedItems.Count == 2, "Wallpaper preserves conversion multi-selection");
                    // Warm each page once, then verify normal page changes reuse the bitmap.
                    for (int page = 0; page < 5; page++) { Call(form, "NavigateTo", page); form.Refresh(); }
                    var timer = System.Diagnostics.Stopwatch.StartNew();
                    int generation = (int)Field(form, "wallpaperCanvasGeneration");
                    for (int repeat = 0; repeat < 3; repeat++) for (int page = 0; page < 5; page++) { Call(form, "NavigateTo", page); form.Update(); }
                    timer.Stop();
                    Check((int)Field(form, "wallpaperCanvasGeneration") == generation, "Page switching reuses full-window background cache");
                    Console.WriteLine("15 warm page switches and redraws: " + timer.ElapsedMilliseconds + " ms; wallpaper cache reused.");
                    Call(form, "NavigateTo", 0);
                    list.Items.Clear();
                    CheckTableBackground(list);
                }
                Call(form, "ResetCustomAppearance");
                Check(Field(form, "wallpaperBlurred") == null && Preference(form, "ButtonColor") == null, "Reset clears wallpaper/color");
                Check(fitSelector.SelectedIndex == 0, "Reset returns fit selector to fill");
                Check(Find(form, "AppTitle").Font.FontFamily.Name == "Microsoft YaHei UI", "Reset restores font");
                Check(Find(form, "PageHost").BackColor != Color.Transparent, "Reset restores solid surfaces");
                Check(Find(form, "Sidebar").BackColor != Color.Transparent, "Reset restores sidebar surface");
                Check(Find(form, "PendingSongList").GetType().GetField("wallpaperSurface", Private).GetValue(Find(form, "PendingSongList")) == null, "Reset clears native table background");
                SetField(form, "customAppearancePath", temporary);
                Call(form, "SaveCustomAppearance", "test");
                Check(Find(form, "TaskStatus").Text.Contains("部分失败"), "Save failure visible in task messages");
            }
            Console.WriteLine("PASS: five wallpaper modes, geometry, raster/clipping/tiling, original dimensions, persistence and appearance regression.");
            return 0;
        }
        catch (Exception ex) { Console.Error.WriteLine(ex); return 1; }
        finally { Directory.Delete(temporary, true); }
    }
}
