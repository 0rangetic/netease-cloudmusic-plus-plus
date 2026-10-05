using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Web.Script.Serialization;
using System.Windows.Forms;

namespace NeteaseToolbox
{
    internal sealed class AppearancePreferences
    {
        public int? ButtonColor { get; set; }
        public string FontFamily { get; set; }
        public int Blur { get; set; }
        public bool HasWallpaper { get; set; }
        public string WallpaperFit { get; set; }
        public int WallpaperWidth { get; set; }
        public int WallpaperHeight { get; set; }
    }

    internal sealed partial class ToolboxForm
    {
        [DllImport("gdi32.dll", CharSet = CharSet.Unicode, EntryPoint = "GetGlyphIndicesW")]
        private static extern uint GetGlyphIndices(IntPtr dc, string text, int count, [Out] ushort[] glyphs, uint flags);
        [DllImport("gdi32.dll")]
        private static extern IntPtr SelectObject(IntPtr dc, IntPtr value);
        [DllImport("gdi32.dll")]
        private static extern bool DeleteObject(IntPtr value);
        private readonly string customAppearancePath = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), @"NeteaseToolbox\appearance-custom.json");
        private AppearancePreferences appearance = new AppearancePreferences { FontFamily = "Microsoft YaHei UI", Blur = 12 };
        private Bitmap wallpaperSource, wallpaperBlurred;
        private Bitmap wallpaperCanvas;
        private Size wallpaperCanvasSize;
        private ThemePalette wallpaperCanvasTheme;
        private string wallpaperCanvasFit;
        private int wallpaperCanvasGeneration;
        private bool wallpaperLayoutPending;
        internal bool WallpaperLayoutPending { get { return wallpaperLayoutPending; } }
        private TrackBar blurSlider;
        private ComboBox wallpaperFitSelector;
        private static readonly string[] WallpaperFits = { "Fill", "Fit", "Stretch", "Tile", "Center" };
        private static readonly string[] WallpaperFitLabels = { "填充", "适应", "拉伸", "平铺", "居中" };
        private Label appearanceSummary;
        private readonly Dictionary<Control, bool> wallpaperSurfaces = new Dictionary<Control, bool>();
        private readonly Dictionary<string, Font> uiFonts = new Dictionary<string, Font>();
        private int selectedNavigationIndex;
        private bool updatingAppearance;
        private string WallpaperPath { get { return Path.Combine(Path.GetDirectoryName(customAppearancePath), "appearance-background.png"); } }

        private Font UiFont(float size, FontStyle style = FontStyle.Regular)
        {
            string family = appearance.FontFamily ?? "Microsoft YaHei UI";
            string key = family + "/" + size.ToString(System.Globalization.CultureInfo.InvariantCulture) + "/" + style;
            Font font;
            if (!uiFonts.TryGetValue(key, out font)) {
                font = new Font(family, size, style);
                uiFonts.Add(key, font);
            }
            return font;
        }

        private Control BuildCustomAppearance()
        {
            var layout = new FlowLayoutPanel {
                Name = "CustomAppearanceSettings", Location = new Point(28, 0), Size = new Size(720, 126),
                AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink,
                FlowDirection = FlowDirection.TopDown, WrapContents = false,
                BackColor = Color.Transparent
            };
            var actions = new FlowLayoutPanel { AutoSize = true, WrapContents = true, Width = 700, BackColor = Color.Transparent };
            AddAppearanceButton(actions, "ButtonColorPicker", "按钮颜色", ChooseButtonColor);
            AddAppearanceButton(actions, "WallpaperPicker", "选择背景图片", ChooseWallpaper);
            AddAppearanceButton(actions, "WallpaperClear", "移除背景", delegate { ClearWallpaper(); SaveCustomAppearance("背景图片已移除。"); });
            AddAppearanceButton(actions, "UiFontPicker", "UI 字体", ChooseUiFont);
            AddAppearanceButton(actions, "AppearanceReset", "恢复默认外观", ResetCustomAppearance);
            layout.Controls.Add(actions);
            var blurRow = new FlowLayoutPanel { AutoSize = true, WrapContents = false, BackColor = Color.Transparent };
            var caption = Label("背景模糊", 9, false, muted);
            caption.Margin = new Padding(0, 9, 14, 0);
            blurRow.Controls.Add(caption);
            blurSlider = new TrackBar { Name = "BackgroundBlurSlider", Minimum = 0, Maximum = 30,
                Value = 12, Width = 230, Height = 36, TickFrequency = 5, AutoSize = false };
            tips.SetToolTip(blurSlider, "0 为清晰原图，30 为强模糊。松开鼠标或使用键盘调整后自动保存。");
            blurSlider.ValueChanged += delegate { if (!updatingAppearance) UpdateAppearanceSummary(); };
            blurSlider.MouseUp += delegate { CommitBlur(); };
            blurSlider.KeyUp += delegate { CommitBlur(); };
            blurRow.Controls.Add(blurSlider);
            layout.Controls.Add(blurRow);
            var fitRow = new FlowLayoutPanel { AutoSize = true, WrapContents = false, BackColor = Color.Transparent };
            var fitCaption = Label("图片适应模式", 9, false, muted);
            fitCaption.Margin = new Padding(0, 6, 14, 0);
            fitRow.Controls.Add(fitCaption);
            wallpaperFitSelector = new ComboBox { Name = "WallpaperFitSelector", DropDownStyle = ComboBoxStyle.DropDownList,
                Width = 156, BackColor = panel, ForeColor = ink };
            wallpaperFitSelector.Items.AddRange(WallpaperFitLabels);
            wallpaperFitSelector.SelectedIndex = 0;
            tips.SetToolTip(wallpaperFitSelector, "填充：等比例铺满，可能裁剪；适应：完整显示，可能留边；拉伸：铺满但比例可能改变；平铺：按原始尺寸重复；居中：按原始尺寸居中。");
            wallpaperFitSelector.SelectedIndexChanged += delegate {
                if (updatingAppearance || wallpaperFitSelector.SelectedIndex < 0) return;
                appearance.WallpaperFit = WallpaperFits[wallpaperFitSelector.SelectedIndex];
                RefreshWallpaperLists(this);
                Invalidate(true);
                SaveCustomAppearance("背景适应模式已切换为“" + WallpaperFitLabels[wallpaperFitSelector.SelectedIndex] + "”。");
            };
            fitRow.Controls.Add(wallpaperFitSelector);
            layout.Controls.Add(fitRow);
            appearanceSummary = Label("默认按钮颜色 · 无背景图片 · Microsoft YaHei UI", 9, false, muted);
            appearanceSummary.Name = "AppearanceSummary";
            layout.Controls.Add(appearanceSummary);
            layout.ParentChanged += delegate {
                if (layout.Parent == null) return;
                Action resize = () => {
                    int width = Math.Max(300, layout.Parent.ClientSize.Width - 56);
                    layout.MaximumSize = new Size(width, 0);
                    actions.MaximumSize = new Size(width, 0);
                    actions.Width = width;
                    appearanceSummary.MaximumSize = new Size(width, 0);
                };
                layout.Parent.SizeChanged += delegate { resize(); };
                resize();
            };
            return layout;
        }

        private void AddAppearanceButton(Control row, string name, string caption, Action action)
        {
            var button = Button(caption, false);
            button.Name = name;
            ResizeAppearanceButton(button, caption);
            button.FontChanged += delegate { ResizeAppearanceButton(button, caption); };
            button.Margin = new Padding(0, 0, 8, 6);
            button.Click += delegate { action(); };
            row.Controls.Add(button);
        }

        private static void ResizeAppearanceButton(Button button, string caption)
        {
            int textWidth = TextRenderer.MeasureText(caption, button.Font).Width;
            button.Width = Math.Max(120, textWidth + 28);
            button.Height = 38;
            button.AutoEllipsis = false;
        }

        private void UpdateAppearanceSummary()
        {
            if (appearanceSummary == null) return;
            appearanceSummary.Text = (appearance.ButtonColor.HasValue ? "按钮 " + ColorTranslator.ToHtml(Color.FromArgb(appearance.ButtonColor.Value)) : "默认按钮颜色")
                + " · " + (appearance.HasWallpaper ? "自定义背景" : "无背景图片")
                + " · " + WallpaperFitLabels[Array.IndexOf(WallpaperFits, NormalizeWallpaperFit(appearance.WallpaperFit))]
                + " · 模糊 " + blurSlider.Value + " · " + appearance.FontFamily;
        }

        private void ApplyButtonAppearance(Button button, ButtonRole role, bool? selected = null)
        {
            bool active = selected ?? (role == ButtonRole.Navigation && Convert.ToInt32(button.Tag) == selectedNavigationIndex);
            bool tinted = appearance.ButtonColor.HasValue && (role == ButtonRole.Primary || role == ButtonRole.Secondary || (role == ButtonRole.Navigation && active));
            Color surface = tinted ? Color.FromArgb(appearance.ButtonColor.Value)
                : role == ButtonRole.Primary ? theme.Accent : role == ButtonRole.Danger ? theme.Danger
                : role == ButtonRole.Navigation ? (active ? theme.Secondary : wallpaperBlurred == null ? theme.Panel : Color.Transparent)
                : role == ButtonRole.Ghost ? theme.Background : theme.Secondary;
            button.BackColor = surface;
            if (button != taskMessagesButton || button.ForeColor.ToArgb() != theme.Error.ToArgb())
                button.ForeColor = tinted ? ContrastInk(surface)
                    : role == ButtonRole.Primary || role == ButtonRole.Danger ? Color.White
                    : role == ButtonRole.Navigation && !active ? theme.Muted : theme.ButtonInk;
            button.FlatAppearance.BorderColor = tinted ? surface : theme.Border;
            button.FlatAppearance.MouseOverBackColor = surface;
            button.FlatAppearance.MouseDownBackColor = surface;
        }

        internal static Color ContrastInk(Color surface)
        {
            Func<int, double> linear = value => { double s = value / 255.0; return s <= 0.04045 ? s / 12.92 : Math.Pow((s + 0.055) / 1.055, 2.4); };
            double luminance = 0.2126 * linear(surface.R) + 0.7152 * linear(surface.G) + 0.0722 * linear(surface.B);
            return (luminance + 0.05) / 0.05 >= 1.05 / (luminance + 0.05) ? Color.Black : Color.White;
        }

        private void ChooseButtonColor()
        {
            using (var dialog = new ColorDialog { FullOpen = true, Color = appearance.ButtonColor.HasValue ? Color.FromArgb(appearance.ButtonColor.Value) : theme.Accent }) {
                if (dialog.ShowDialog(this) != DialogResult.OK) return;
                appearance.ButtonColor = dialog.Color.ToArgb();
                SetTheme(theme == ThemePalette.Light);
                SaveCustomAppearance("按钮颜色已更新。");
            }
        }

        private void ChooseUiFont()
        {
            using (var dialog = new FontDialog { Font = UiFont(9), MinSize = 9, MaxSize = 9,
                FontMustExist = true, ShowEffects = false, AllowVerticalFonts = false }) {
                if (dialog.ShowDialog(this) != DialogResult.OK) return;
                if (!FontSupportsUi(dialog.Font.FontFamily.Name)) {
                    ReportTask("UI 字体", "失败", "所选字体缺少中文字符，请选择微软雅黑、宋体等支持中文的字体。");
                    return;
                }
                appearance.FontFamily = dialog.Font.FontFamily.Name;
                ApplyUiFonts(this);
                if (logWindow != null) ApplyUiFonts(logWindow);
                if (taskMessagesWindow != null) ApplyUiFonts(taskMessagesWindow);
                SaveCustomAppearance("UI 字体已更新；保留标题和正文的字号层级。");
            }
        }

        private void ApplyUiFonts(Control root)
        {
            root.Font = UiFont(root.Font.SizeInPoints, root.Font.Style);
            foreach (Control child in root.Controls) ApplyUiFonts(child);
            root.PerformLayout();
        }

        internal static bool FontSupportsUi(string family)
        {
            const string sample = "网易云工具箱设置转换歌曲背景字体";
            using (var font = new Font(family, 9))
            using (var bitmap = new Bitmap(1, 1))
            using (var graphics = Graphics.FromImage(bitmap)) {
                IntPtr dc = graphics.GetHdc(), nativeFont = font.ToHfont();
                IntPtr previous = SelectObject(dc, nativeFont);
                try {
                    var glyphs = new ushort[sample.Length];
                    if (GetGlyphIndices(dc, sample, sample.Length, glyphs, 1) == uint.MaxValue) return false;
                    foreach (var glyph in glyphs) if (glyph == ushort.MaxValue) return false;
                    return true;
                }
                finally { SelectObject(dc, previous); DeleteObject(nativeFont); graphics.ReleaseHdc(dc); }
            }
        }

        private void SaveCustomAppearance(string detail)
        {
            UpdateAppearanceSummary();
            try {
                Directory.CreateDirectory(Path.GetDirectoryName(customAppearancePath));
                string temporary = customAppearancePath + ".tmp";
                File.WriteAllText(temporary, new JavaScriptSerializer().Serialize(appearance), Encoding.UTF8);
                if (File.Exists(customAppearancePath)) File.Replace(temporary, customAppearancePath, null);
                else File.Move(temporary, customAppearancePath);
                ReportTask("外观设置", "完成", detail + " 下次启动继续使用。");
            }
            catch (Exception ex) { ReportTask("外观设置", "部分失败", "外观已应用，但偏好保存失败：" + ex.Message); }
        }

        private void LoadCustomAppearance()
        {
            try {
                if (File.Exists(customAppearancePath)) {
                    var saved = new JavaScriptSerializer().Deserialize<AppearancePreferences>(File.ReadAllText(customAppearancePath, Encoding.UTF8));
                    if (saved == null || saved.Blur < 0 || saved.Blur > 30) throw new InvalidDataException("外观配置无效。");
                    using (var family = new FontFamily(saved.FontFamily ?? "Microsoft YaHei UI")) { saved.FontFamily = family.Name; }
                    if (!FontSupportsUi(saved.FontFamily)) throw new InvalidDataException("保存的字体缺少中文字符，请重新选择支持中文的字体。");
                    if (saved.ButtonColor.HasValue) saved.ButtonColor = Color.FromArgb(255, Color.FromArgb(saved.ButtonColor.Value)).ToArgb();
                    appearance = saved;
                }
                updatingAppearance = true;
                blurSlider.Value = appearance.Blur;
                appearance.WallpaperFit = NormalizeWallpaperFit(appearance.WallpaperFit);
                wallpaperFitSelector.SelectedIndex = Array.IndexOf(WallpaperFits, appearance.WallpaperFit);
                updatingAppearance = false;
                ApplyUiFonts(this);
                SetTheme(theme == ThemePalette.Light);
                if (appearance.HasWallpaper) {
                    wallpaperSource = ReadWallpaper(WallpaperPath);
                    RebuildWallpaper();
                }
                UpdateAppearanceSummary();
            }
            catch (Exception ex) {
                string detail = "部分外观未能恢复：" + ex.Message + "；可重新选择图片或恢复默认外观。";
                if (IsHandleCreated) ReportTask("外观设置", "部分失败", detail);
                else {
                    EventHandler handler = null;
                    handler = delegate { HandleCreated -= handler; ReportTask("外观设置", "部分失败", detail); };
                    HandleCreated += handler;
                }
            }
            finally { updatingAppearance = false; UpdateAppearanceSummary(); }
        }

        private void ChooseWallpaper()
        {
            using (var dialog = new OpenFileDialog { Title = "选择背景图片", Filter = "图片|*.jpg;*.jpeg;*.png;*.bmp|所有文件|*.*" }) {
                if (dialog.ShowDialog(this) != DialogResult.OK) return;
                ReportTask("背景图片", "开始", "正在读取并处理背景图片。");
                try { InstallWallpaper(dialog.FileName); SaveCustomAppearance("背景图片已更新。"); }
                catch (Exception ex) { ReportTask("背景图片", "失败", "无法使用此图片：" + ex.Message); }
            }
        }

        private void InstallWallpaper(string path)
        {
            Size originalSize;
            using (var original = Image.FromFile(path)) originalSize = original.Size;
            using (var source = ReadWallpaper(path)) {
                Directory.CreateDirectory(Path.GetDirectoryName(WallpaperPath));
                string temporary = WallpaperPath + ".tmp";
                source.Save(temporary, ImageFormat.Png);
                if (File.Exists(WallpaperPath)) File.Replace(temporary, WallpaperPath, null);
                else File.Move(temporary, WallpaperPath);
                var replacement = (Bitmap)source.Clone();
                if (wallpaperSource != null) wallpaperSource.Dispose();
                wallpaperSource = replacement;
                appearance.HasWallpaper = true;
                appearance.WallpaperWidth = originalSize.Width;
                appearance.WallpaperHeight = originalSize.Height;
                RebuildWallpaper();
            }
        }

        internal static Bitmap ReadWallpaper(string path)
        {
            using (var source = Image.FromFile(path)) {
                if ((long)source.Width * source.Height > 40000000) throw new InvalidDataException("图片超过 4000 万像素，请先缩小。");
                double scale = Math.Min(1, 1280.0 / Math.Max(source.Width, source.Height));
                var bitmap = new Bitmap(Math.Max(1, (int)(source.Width * scale)), Math.Max(1, (int)(source.Height * scale)), PixelFormat.Format24bppRgb);
                using (var graphics = Graphics.FromImage(bitmap)) {
                    graphics.Clear(Color.White);
                    graphics.InterpolationMode = InterpolationMode.HighQualityBicubic;
                    graphics.DrawImage(source, new Rectangle(Point.Empty, bitmap.Size));
                }
                return bitmap;
            }
        }

        private void CommitBlur()
        {
            if (updatingAppearance || appearance.Blur == blurSlider.Value) return;
            appearance.Blur = blurSlider.Value;
            ReportTask("背景模糊", "开始", "正在更新背景模糊效果。");
            try { RebuildWallpaper(); SaveCustomAppearance("背景模糊程度已更新。"); }
            catch (Exception ex) { ReportTask("背景模糊", "失败", ex.Message); }
        }

        private void RebuildWallpaper()
        {
            Bitmap replacement = wallpaperSource == null ? null : BlurBitmap(wallpaperSource, appearance.Blur);
            var old = wallpaperBlurred;
            wallpaperBlurred = replacement;
            if (old != null) old.Dispose();
            if (wallpaperCanvas != null) { wallpaperCanvas.Dispose(); wallpaperCanvas = null; }
            ApplyWallpaperSurfaces(this);
            Invalidate(true);
        }

        private void ApplyWallpaperSurfaces(Control root)
        {
            foreach (Control child in root.Controls) ApplyWallpaperSurfaces(child);
            var table = root as CachedSongTable;
            if (table != null) { table.SetWallpaperOwner(this); return; }
            var list = root as WallpaperListView;
            if (list != null) { list.SetWallpaperOwner(wallpaperBlurred == null ? null : this); return; }
            var text = root as WallpaperRichTextBox;
            if (text != null) { text.SetWallpaperOwner(this); return; }
            if (root == this || !(root is Panel || root is TableLayoutPanel || root is Label || root is CheckBox)) return;
            bool sidebar = root.Name == "Sidebar";
            if (!wallpaperSurfaces.ContainsKey(root) && (sidebar || root.BackColor.ToArgb() == theme.Background.ToArgb() || root.BackColor.ToArgb() == theme.Panel.ToArgb()))
                wallpaperSurfaces.Add(root, sidebar || root.BackColor.ToArgb() == theme.Panel.ToArgb());
            bool panelSurface;
            if (wallpaperSurfaces.TryGetValue(root, out panelSurface)) root.BackColor = wallpaperBlurred == null ? (panelSurface ? theme.Panel : theme.Background) : Color.Transparent;
            if (sidebar) foreach (var button in navigationButtons)
                if (Convert.ToInt32(button.Tag) != selectedNavigationIndex) button.BackColor = wallpaperBlurred == null ? theme.Panel : Color.Transparent;
        }

        protected override void OnPaintBackground(PaintEventArgs e)
        {
            if (wallpaperBlurred == null || ClientSize.Width == 0 || ClientSize.Height == 0) { base.OnPaintBackground(e); return; }
            EnsureWallpaperCanvas();
            e.Graphics.DrawImage(wallpaperCanvas, e.ClipRectangle, e.ClipRectangle, GraphicsUnit.Pixel);
        }

        private void EnsureWallpaperCanvas()
        {
            if (wallpaperLayoutPending && wallpaperCanvas != null) return;
            string fit = NormalizeWallpaperFit(appearance.WallpaperFit);
            if (wallpaperCanvas != null && wallpaperCanvasSize == ClientSize && wallpaperCanvasTheme == theme && wallpaperCanvasFit == fit) return;
            var replacement = new Bitmap(Math.Max(1, ClientSize.Width), Math.Max(1, ClientSize.Height), PixelFormat.Format32bppRgb);
            var imageSize = appearance.WallpaperWidth > 0 && appearance.WallpaperHeight > 0
                && (long)appearance.WallpaperWidth * appearance.WallpaperHeight <= 40000000
                ? new Size(appearance.WallpaperWidth, appearance.WallpaperHeight) : wallpaperBlurred.Size;
            using (var graphics = Graphics.FromImage(replacement)) {
                graphics.Clear(theme.Background);
                DrawWallpaper(graphics, wallpaperBlurred, imageSize, ClientRectangle, fit);
                using (var wash = new SolidBrush(Color.FromArgb(theme == ThemePalette.Light ? 180 : 165, theme.Background))) graphics.FillRectangle(wash, ClientRectangle);
            }
            if (wallpaperCanvas != null) wallpaperCanvas.Dispose();
            wallpaperCanvas = replacement;
            wallpaperCanvasSize = ClientSize;
            wallpaperCanvasTheme = theme;
            wallpaperCanvasFit = fit;
            wallpaperCanvasGeneration++;
        }

        internal int WallpaperGeneration { get { EnsureWallpaperCanvas(); return wallpaperCanvasGeneration; } }
        internal bool WallpaperEnabled { get { return wallpaperBlurred != null; } }
        internal Color WallpaperInk { get { return theme.Ink; } }
        internal Color WallpaperSelection { get { return theme.Secondary; } }
        internal Bitmap CreateWallpaperSlice(Control control)
        {
            EnsureWallpaperCanvas();
            var slice = new Bitmap(Math.Max(1, control.ClientSize.Width), Math.Max(1, control.ClientSize.Height), PixelFormat.Format32bppRgb);
            using (var graphics = Graphics.FromImage(slice)) PaintWallpaperRegion(graphics, control, new Rectangle(Point.Empty, slice.Size));
            return slice;
        }

        internal void PaintWallpaperRegion(Graphics graphics, Control control, Rectangle region)
        {
            EnsureWallpaperCanvas();
            Point origin = PointToClient(control.PointToScreen(Point.Empty));
            var source = new Rectangle(origin.X + region.X, origin.Y + region.Y, region.Width, region.Height);
            graphics.DrawImage(wallpaperCanvas, region, source, GraphicsUnit.Pixel);
        }

        private void RefreshWallpaperLists(Control root)
        {
            foreach (Control child in root.Controls) RefreshWallpaperLists(child);
            var table = root as CachedSongTable;
            if (table != null) table.RefreshWallpaper();
            var list = root as WallpaperListView;
            if (list != null) list.RefreshWallpaper();
            var text = root as WallpaperRichTextBox;
            if (text != null) text.RefreshWallpaper();
        }

        protected override void OnResize(EventArgs e)
        {
            base.OnResize(e);
            // Child layout may still be changing. Their next paint refreshes
            // the final background slice once, rather than on every size event.
            if (wallpaperBlurred != null) Invalidate(true);
        }

        protected override void WndProc(ref Message message)
        {
            // WM_SIZE can synchronously paint native headers during child
            // layout. Coalesce those requests before entering base.WndProc.
            if (message.Msg == 0x5 && wallpaperBlurred != null && wallpaperCanvas != null &&
                !wallpaperLayoutPending && !Disposing && !IsDisposed && IsHandleCreated) {
                wallpaperLayoutPending = true;
                BeginInvoke((Action)delegate {
                    wallpaperLayoutPending = false;
                    if (Disposing || IsDisposed || !IsHandleCreated || wallpaperBlurred == null) return;
                    RefreshWallpaperLists(this);
                    Invalidate(true);
                });
            }
            base.WndProc(ref message);
        }

        internal static string NormalizeWallpaperFit(string mode)
        {
            return Array.IndexOf(WallpaperFits, mode) >= 0 ? mode : "Fill";
        }

        internal static Rectangle WallpaperRectangle(Size viewport, Size image, string mode)
        {
            if (viewport.Width <= 0 || viewport.Height <= 0 || image.Width <= 0 || image.Height <= 0) return Rectangle.Empty;
            mode = NormalizeWallpaperFit(mode);
            if (mode == "Stretch") return new Rectangle(Point.Empty, viewport);
            if (mode == "Center" || mode == "Tile")
                return new Rectangle(mode == "Tile" ? Point.Empty : new Point((viewport.Width - image.Width) / 2, (viewport.Height - image.Height) / 2), image);
            double scale = mode == "Fit" ? Math.Min((double)viewport.Width / image.Width, (double)viewport.Height / image.Height)
                : Math.Max((double)viewport.Width / image.Width, (double)viewport.Height / image.Height);
            int width = Math.Max(1, (int)(mode == "Fit" ? Math.Floor(image.Width * scale) : Math.Ceiling(image.Width * scale)));
            int height = Math.Max(1, (int)(mode == "Fit" ? Math.Floor(image.Height * scale) : Math.Ceiling(image.Height * scale)));
            return new Rectangle((viewport.Width - width) / 2, (viewport.Height - height) / 2, width, height);
        }

        internal static void DrawWallpaper(Graphics graphics, Bitmap image, Size originalSize, Rectangle bounds, string mode)
        {
            if (bounds.Width <= 0 || bounds.Height <= 0) return;
            var state = graphics.Save();
            try {
                graphics.SetClip(bounds, CombineMode.Intersect);
                graphics.InterpolationMode = InterpolationMode.HighQualityBicubic;
                if (NormalizeWallpaperFit(mode) == "Tile") {
                    using (var tiles = new TextureBrush(image, WrapMode.Tile)) {
                        using (var transform = new Matrix((float)originalSize.Width / image.Width, 0, 0,
                            (float)originalSize.Height / image.Height, bounds.Left, bounds.Top)) tiles.Transform = transform;
                        graphics.FillRectangle(tiles, bounds);
                    }
                }
                else {
                    var rectangle = WallpaperRectangle(bounds.Size, originalSize, mode);
                    rectangle.Offset(bounds.Location);
                    using (var attributes = new ImageAttributes()) {
                        attributes.SetWrapMode(WrapMode.TileFlipXY);
                        graphics.DrawImage(image, rectangle, 0, 0, image.Width, image.Height, GraphicsUnit.Pixel, attributes);
                    }
                }
            }
            finally { graphics.Restore(state); }
        }

        private void ClearWallpaper()
        {
            if (wallpaperSource != null) { wallpaperSource.Dispose(); wallpaperSource = null; }
            appearance.HasWallpaper = false;
            RebuildWallpaper();
        }

        private void ResetCustomAppearance()
        {
            appearance = new AppearancePreferences { FontFamily = "Microsoft YaHei UI", Blur = 12 };
            updatingAppearance = true;
            blurSlider.Value = 12;
            wallpaperFitSelector.SelectedIndex = 0;
            updatingAppearance = false;
            ClearWallpaper();
            ApplyUiFonts(this);
            if (logWindow != null) ApplyUiFonts(logWindow);
            if (taskMessagesWindow != null) ApplyUiFonts(taskMessagesWindow);
            SetTheme(theme == ThemePalette.Light);
            SaveCustomAppearance("已恢复默认按钮颜色、背景和字体，保留深浅主题选择。");
        }

        internal static Bitmap BlurBitmap(Bitmap original, int radius)
        {
            var bitmap = original.Clone(new Rectangle(Point.Empty, original.Size), PixelFormat.Format24bppRgb);
            if (radius == 0) return bitmap;
            var data = bitmap.LockBits(new Rectangle(Point.Empty, bitmap.Size), ImageLockMode.ReadWrite, PixelFormat.Format24bppRgb);
            try {
                int stride = data.Stride, width = bitmap.Width, height = bitmap.Height;
                var pixels = new byte[stride * height];
                var scratch = new byte[pixels.Length];
                Marshal.Copy(data.Scan0, pixels, 0, pixels.Length);
                // Three separable box passes approximate a Gaussian blur in O(pixels).
                for (int pass = 0; pass < 3; pass++) {
                    BlurPass(pixels, scratch, width, height, stride, radius, true);
                    BlurPass(scratch, pixels, width, height, stride, radius, false);
                }
                Marshal.Copy(pixels, 0, data.Scan0, pixels.Length);
            }
            finally { bitmap.UnlockBits(data); }
            return bitmap;
        }

        private static void BlurPass(byte[] input, byte[] output, int width, int height, int stride, int radius, bool horizontal)
        {
            int lines = horizontal ? height : width, length = horizontal ? width : height, window = radius * 2 + 1;
            for (int line = 0; line < lines; line++) {
                int start = horizontal ? line * stride : line * 3, step = horizontal ? 3 : stride;
                for (int channel = 0; channel < 3; channel++) {
                    int sum = 0;
                    for (int k = -radius; k <= radius; k++) sum += input[start + Math.Max(0, Math.Min(length - 1, k)) * step + channel];
                    for (int position = 0; position < length; position++) {
                        output[start + position * step + channel] = (byte)(sum / window);
                        sum -= input[start + Math.Max(0, position - radius) * step + channel];
                        sum += input[start + Math.Min(length - 1, position + radius + 1) * step + channel];
                    }
                }
            }
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing) {
                var source = wallpaperSource; wallpaperSource = null;
                var blurred = wallpaperBlurred; wallpaperBlurred = null;
                var canvas = wallpaperCanvas; wallpaperCanvas = null;
                if (source != null) source.Dispose();
                if (blurred != null) blurred.Dispose();
                if (canvas != null) canvas.Dispose();
            }
            base.Dispose(disposing);
            if (disposing) foreach (var font in uiFonts.Values) font.Dispose();
        }
    }
}
