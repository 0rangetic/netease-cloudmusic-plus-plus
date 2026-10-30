using System;
using System.Globalization;
using System.IO;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Threading;
using ModernWpf;

namespace NeteaseToolbox.Wpf
{
    public partial class AppearanceDialog : Window
    {
        private readonly AppearanceBackdropSettings settings;
        private readonly DispatcherTimer blurTimer;
        private readonly DispatcherTimer maskTimer;
        private bool isLight;
        private bool initializing;
        private bool updatingAccentText;

        [DllImport("dwmapi.dll")]
        private static extern int DwmSetWindowAttribute(IntPtr window, int attribute, ref int value, int size);

        internal event EventHandler SettingsChanged;

        internal AppearanceBackdropSettings Settings { get { return settings; } }

        public AppearanceDialog() : this(false) { }

        internal AppearanceDialog(bool isLight)
        {
            this.isLight = isLight;
            InitializeComponent();
            ThemeManager.SetRequestedTheme(this, isLight ? ElementTheme.Light : ElementTheme.Dark);
            UpdateDialogSurface();

            settings = AppearanceBackdrop.LoadSettings();
            if (string.IsNullOrEmpty(settings.ThemeMode))
                settings.ThemeMode = isLight ? "light" : "dark";
            blurTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(200) };
            blurTimer.Tick += OnBlurTimerTick;
            maskTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(200) };
            maskTimer.Tick += OnMaskTimerTick;

            initializing = true;
            try
            {
                int blur = Math.Max((int)BlurSlider.Minimum, Math.Min((int)BlurSlider.Maximum, settings.Blur));
                BlurSlider.Value = blur;
                BlurValueText.Text = blur.ToString();
                int mask = Math.Max((int)MaskSlider.Minimum, Math.Min((int)MaskSlider.Maximum, settings.MaskOpacity));
                MaskSlider.Value = mask;
                MaskValueText.Text = mask.ToString();
                FitSelector.SelectedIndex = Math.Max(0,
                    Array.IndexOf(AppearanceBackdrop.FitModes, AppearanceBackdrop.NormalizeFit(settings.WallpaperFit)));
                ThemeModeSelector.SelectedIndex = settings.ThemeMode == "auto" ? 0 : settings.ThemeMode == "light" ? 1 : 2;
                GlassToggle.IsChecked = settings.GlassEnabled;
                AccentFollowSystem.IsChecked = settings.AccentMode != "custom";
            }
            finally { initializing = false; }

            UpdateWash();
            RefreshPreview();
            UpdateAccentPreview();
        }

        private void UpdateWash()
        {
            var wash = new SolidColorBrush(AppearanceBackdrop.MaskColor(isLight, settings.MaskOpacity));
            wash.Freeze();
            PreviewWash.Background = wash;

            var placeholderColor = isLight ? Color.FromRgb(80, 86, 94) : Color.FromRgb(200, 205, 212);
            var placeholderBrush = new SolidColorBrush(placeholderColor);
            placeholderBrush.Freeze();
            PreviewPlaceholder.Foreground = placeholderBrush;
        }

        private void RefreshPreview()
        {
            PreviewSurface.Background = null;
            if (!settings.HasWallpaper)
            {
                PreviewPlaceholder.Text = "未选择背景图片";
                PreviewPlaceholder.Visibility = Visibility.Visible;
                StatusText.Text = "当前未使用背景图片。";
                RemoveImageButton.IsEnabled = false;
                return;
            }

            RemoveImageButton.IsEnabled = true;
            try
            {
                int previewBlur = AppearanceBackdrop.NormalizeBlur(settings.Blur);
                var source = AppearanceBackdrop.LoadWallpaper(previewBlur);
                if (source == null)
                {
                    PreviewPlaceholder.Text = "背景图片不可用";
                    PreviewPlaceholder.Visibility = Visibility.Visible;
                    StatusText.Text = "背景图片文件缺失或已损坏，请重新选择。";
                    return;
                }

                PreviewSurface.Background = AppearanceBackdrop.CreateBrush(source, settings.WallpaperFit);
                PreviewPlaceholder.Visibility = Visibility.Collapsed;
                StatusText.Text = AppearanceBackdrop.DescribeWallpaper(settings);
            }
            catch (Exception ex)
            {
                PreviewPlaceholder.Text = "背景图片加载失败";
                PreviewPlaceholder.Visibility = Visibility.Visible;
                StatusText.Text = "背景图片加载失败。";
                ShowError("背景图片加载失败：" + ex.Message);
            }
        }

        private void OnChooseImageClick(object sender, RoutedEventArgs e)
        {
            var dialog = new Microsoft.Win32.OpenFileDialog
            {
                Title = "选择背景图片",
                Filter = "图片文件|*.jpg;*.jpeg;*.png;*.bmp|所有文件|*.*",
                CheckFileExists = true
            };
            if (dialog.ShowDialog(this) != true) return;

            try
            {
                ClearError();
                InstallWallpaper(dialog.FileName);
            }
            catch (Exception ex)
            {
                ShowError("无法使用此图片：" + ex.Message);
            }
        }

        private void InstallWallpaper(string path)
        {
            int width, height;
            try
            {
                using (var stream = File.OpenRead(path))
                using (var original = System.Drawing.Image.FromStream(stream, false, false))
                {
                    width = original.Width;
                    height = original.Height;
                }
            }
            catch (Exception ex)
            {
                throw new InvalidDataException("无法读取图片：" + ex.Message, ex);
            }
            if ((long)width * height > 40000000)
            {
                throw new InvalidDataException("图片超过 4000 万像素（" + width + "×" + height + "），请先缩小后再选择。");
            }

            string target = AppearanceBackdrop.WallpaperPath;
            string temporary = target + ".tmp";
            using (var source = ToolboxForm.ReadWallpaper(path))
            {
                Directory.CreateDirectory(Path.GetDirectoryName(target));
                try
                {
                    source.Save(temporary, System.Drawing.Imaging.ImageFormat.Png);
                    if (File.Exists(target)) File.Replace(temporary, target, null);
                    else File.Move(temporary, target);
                }
                finally
                {
                    try { if (File.Exists(temporary)) File.Delete(temporary); } catch { }
                }
            }

            settings.HasWallpaper = true;
            settings.WallpaperWidth = width;
            settings.WallpaperHeight = height;
            string saveError = AppearanceBackdrop.SaveSettings(settings);

            RefreshPreview();
            NotifyChanged();
            if (saveError != null) ShowError("背景已更新，但偏好保存失败：" + saveError);
        }

        private void OnRemoveImageClick(object sender, RoutedEventArgs e)
        {
            settings.HasWallpaper = false;
            string saveError = AppearanceBackdrop.SaveSettings(settings);
            RefreshPreview();
            NotifyChanged();
            if (saveError != null) ShowError("背景已移除，但偏好保存失败：" + saveError);
            else ClearError();
        }

        private void OnBlurValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
        {
            if (BlurValueText != null)
            {
                BlurValueText.Text = ((int)Math.Round(e.NewValue)).ToString();
            }
            if (initializing || blurTimer == null) return;
            blurTimer.Stop();
            blurTimer.Start();
        }

        private void OnBlurTimerTick(object sender, EventArgs e)
        {
            blurTimer.Stop();
            if (initializing) return;
            int value = (int)Math.Round(BlurSlider.Value);
            if (value == settings.Blur) return;

            settings.Blur = value;
            RefreshPreview();
            NotifyChanged();
            string saveError = AppearanceBackdrop.SaveSettings(settings);
            if (saveError != null) ShowError("虚化程度已应用，但偏好保存失败：" + saveError);
            else ClearError();
        }

        private void OnMaskValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
        {
            if (MaskValueText != null)
            {
                MaskValueText.Text = ((int)Math.Round(e.NewValue)).ToString();
            }
            if (initializing || maskTimer == null) return;
            int value = (int)Math.Round(MaskSlider.Value);
            if (value == settings.MaskOpacity) return;

            settings.MaskOpacity = value;
            UpdateWash();
            NotifyChanged();
            maskTimer.Stop();
            maskTimer.Start();
        }

        private void OnMaskTimerTick(object sender, EventArgs e)
        {
            maskTimer.Stop();
            string saveError = AppearanceBackdrop.SaveSettings(settings);
            if (saveError != null) ShowError("蒙版深浅已应用，但偏好保存失败：" + saveError);
            else ClearError();
        }

        private void OnFitChanged(object sender, SelectionChangedEventArgs e)
        {
            if (initializing || FitSelector.SelectedIndex < 0) return;
            string fit = AppearanceBackdrop.FitModes[FitSelector.SelectedIndex];
            if (fit == settings.WallpaperFit) return;

            settings.WallpaperFit = fit;
            RefreshPreview();
            NotifyChanged();
            string saveError = AppearanceBackdrop.SaveSettings(settings);
            if (saveError != null) ShowError("适应模式已切换，但偏好保存失败：" + saveError);
            else ClearError();
        }

        private void OnThemeModeChanged(object sender, SelectionChangedEventArgs e)
        {
            if (initializing || ThemeModeSelector == null) return;
            var item = ThemeModeSelector.SelectedItem as ComboBoxItem;
            string mode = item != null && item.Tag != null ? item.Tag.ToString() : "light";
            mode = AppearanceBackdrop.NormalizeThemeMode(mode);
            if (string.IsNullOrEmpty(mode)) mode = "light";
            if (mode == settings.ThemeMode) return;

            settings.ThemeMode = mode;
            bool light = mode == "light" || (mode == "auto" && SystemAppearance.IsSystemLight());
            ApplyDialogTheme(light);
            string saveError = AppearanceBackdrop.SaveSettings(settings);
            if (saveError != null) ShowError("主题模式已应用，但偏好保存失败：" + saveError);
            else ClearError();
            NotifyChanged();
        }

        private void OnGlassToggleChanged(object sender, RoutedEventArgs e)
        {
            if (initializing || GlassToggle == null) return;
            settings.GlassEnabled = GlassToggle.IsChecked == true;
            RefreshPreview();
            string saveError = AppearanceBackdrop.SaveSettings(settings);
            if (saveError != null) ShowError("玻璃设置已应用，但偏好保存失败：" + saveError);
            else ClearError();
            NotifyChanged();
        }

        private void OnAccentFollowChanged(object sender, RoutedEventArgs e)
        {
            if (initializing || AccentFollowSystem == null) return;
            bool follow = AccentFollowSystem.IsChecked == true;
            settings.AccentMode = follow ? "system" : "custom";
            if (!follow && settings.AccentColor == 0)
                settings.AccentColor = SystemAppearance.AccentColor().ToArgb();
            UpdateAccentPreview();
            string saveError = AppearanceBackdrop.SaveSettings(settings);
            if (saveError != null) ShowError("主题色已应用，但偏好保存失败：" + saveError);
            else ClearError();
            NotifyChanged();
        }

        private void OnChooseAccentClick(object sender, RoutedEventArgs e)
        {
            if (settings == null || AppearanceBackdrop.NormalizeAccentMode(settings.AccentMode) != "custom") return;
            using (var dialog = new System.Windows.Forms.ColorDialog
            {
                FullOpen = true,
                Color = CurrentAccent(),
                AnyColor = true
            })
            {
                if (dialog.ShowDialog() != System.Windows.Forms.DialogResult.OK) return;
                settings.AccentColor = dialog.Color.ToArgb();
            }

            UpdateAccentPreview();
            string saveError = AppearanceBackdrop.SaveSettings(settings);
            if (saveError != null) ShowError("主题色已应用，但偏好保存失败：" + saveError);
            else ClearError();
            NotifyChanged();
        }

        private void OnAccentHexChanged(object sender, TextChangedEventArgs e)
        {
            if (initializing || updatingAccentText || settings == null) return;
            if (AppearanceBackdrop.NormalizeAccentMode(settings.AccentMode) != "custom") return;

            int argb;
            if (!TryParseAccentHex(AccentHexBox.Text, out argb))
            {
                MarkAccentInputError();
                return;
            }

            settings.AccentColor = argb;
            ClearAccentInputError();
            UpdateAccentSwatch();
            string saveError = AppearanceBackdrop.SaveSettings(settings);
            if (saveError != null) ShowError("主题色已应用，但偏好保存失败：" + saveError);
            else ClearError();
            NotifyChanged();
        }

        private void OnAccentHexLostFocus(object sender, RoutedEventArgs e)
        {
            if (initializing || updatingAccentText || settings == null) return;
            if (AppearanceBackdrop.NormalizeAccentMode(settings.AccentMode) != "custom") return;
            UpdateAccentPreview();
        }

        private void OnAccentHexKeyDown(object sender, System.Windows.Input.KeyEventArgs e)
        {
            if (e.Key != System.Windows.Input.Key.Enter) return;
            e.Handled = true;
            int argb;
            if (TryParseAccentHex(AccentHexBox.Text, out argb))
            {
                settings.AccentColor = argb;
                UpdateAccentPreview();
                string saveError = AppearanceBackdrop.SaveSettings(settings);
                if (saveError != null) ShowError("主题色已应用，但偏好保存失败：" + saveError);
                else ClearError();
                NotifyChanged();
            }
            else
            {
                MarkAccentInputError();
            }
            AccentHexBox.SelectAll();
        }

        private System.Drawing.Color CurrentAccent()
        {
            bool custom = settings != null
                && AppearanceBackdrop.NormalizeAccentMode(settings.AccentMode) == "custom";
            if (custom)
            {
                var color = System.Drawing.Color.FromArgb(settings.AccentColor);
                return System.Drawing.Color.FromArgb(255, color.R, color.G, color.B);
            }
            return SystemAppearance.AccentColor();
        }

        private static string FormatAccent(System.Drawing.Color color)
        {
            return "#" + color.R.ToString("X2") + color.G.ToString("X2") + color.B.ToString("X2");
        }

        private static bool TryParseAccentHex(string text, out int argb)
        {
            argb = 0;
            if (string.IsNullOrWhiteSpace(text)) return false;
            string value = text.Trim();
            if (value.StartsWith("#", StringComparison.Ordinal)) value = value.Substring(1);
            if (value.Length == 3)
            {
                value = new string(new[] { value[0], value[0], value[1], value[1], value[2], value[2] });
            }
            if (value.Length != 6) return false;

            int rgb;
            if (!int.TryParse(value, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out rgb)) return false;
            argb = unchecked((int)(0xFF000000u | (uint)rgb));
            return true;
        }

        private void UpdateAccentPreview()
        {
            UpdateAccentSwatch();
            if (AccentHexBox == null) return;
            updatingAccentText = true;
            try { AccentHexBox.Text = FormatAccent(CurrentAccent()); }
            finally { updatingAccentText = false; }
            ClearAccentInputError();
        }

        private void UpdateAccentSwatch()
        {
            if (AccentSwatch == null || AccentColorButton == null || AccentHexBox == null) return;
            bool custom = settings != null
                && AppearanceBackdrop.NormalizeAccentMode(settings.AccentMode) == "custom";
            var accent = CurrentAccent();
            var brush = new SolidColorBrush(System.Windows.Media.Color.FromArgb(accent.A, accent.R, accent.G, accent.B));
            brush.Freeze();
            AccentSwatch.Background = brush;
            AccentColorButton.IsEnabled = custom;
            AccentHexBox.IsEnabled = custom;
        }

        private void MarkAccentInputError()
        {
            if (AccentHexBox == null) return;
            AccentHexBox.BorderBrush = Brushes.IndianRed;
            AccentHexBox.Foreground = Brushes.IndianRed;
            AccentHexBox.ToolTip = "色号格式不正确；请输入 #RRGGBB 或 RRGGBB。";
            ShowError("色号格式不正确；请输入 #RRGGBB 或 RRGGBB。");
        }

        private void ClearAccentInputError()
        {
            if (AccentHexBox == null) return;
            AccentHexBox.ClearValue(Control.BorderBrushProperty);
            AccentHexBox.ClearValue(Control.ForegroundProperty);
            AccentHexBox.ToolTip = "输入 #RRGGBB 或 RRGGBB（也支持 #RGB）。";
            ClearError();
        }

        private void ApplyDialogTheme(bool light)
        {
            if (light == isLight) return;
            isLight = light;
            ThemeManager.SetRequestedTheme(this, light ? ElementTheme.Light : ElementTheme.Dark);
            UpdateDialogSurface();
            UpdateWash();
            RefreshPreview();
        }

        private void UpdateDialogSurface()
        {
            var brush = new SolidColorBrush(isLight ? Color.FromRgb(244, 246, 250) : Color.FromRgb(20, 24, 33));
            brush.Freeze();
            Background = brush;
            UpdateDialogChrome();
        }

        private void UpdateDialogChrome()
        {
            try
            {
                IntPtr handle = new WindowInteropHelper(this).Handle;
                if (handle == IntPtr.Zero) return;
                int dark = isLight ? 0 : 1;
                if (DwmSetWindowAttribute(handle, 20, ref dark, 4) != 0)
                    DwmSetWindowAttribute(handle, 19, ref dark, 4);
            }
            catch { }
        }

        protected override void OnSourceInitialized(EventArgs e)
        {
            base.OnSourceInitialized(e);
            UpdateDialogChrome();
        }

        internal void RefreshFromSystem()
        {
            if (settings == null) return;
            if (settings.ThemeMode == "auto")
                ApplyDialogTheme(SystemAppearance.IsSystemLight());
            if (AppearanceBackdrop.NormalizeAccentMode(settings.AccentMode) != "custom")
                UpdateAccentPreview();
        }

        private void NotifyChanged()
        {
            var handler = SettingsChanged;
            if (handler != null) handler(this, EventArgs.Empty);
        }

        private void ShowError(string message)
        {
            ErrorText.Text = message;
            ErrorText.Visibility = Visibility.Visible;
        }

        private void ClearError()
        {
            ErrorText.Text = string.Empty;
            ErrorText.Visibility = Visibility.Collapsed;
        }

        private void OnCloseClick(object sender, RoutedEventArgs e)
        {
            Close();
        }

        protected override void OnClosed(EventArgs e)
        {
            blurTimer.Stop();
            maskTimer.Stop();
            PreviewSurface.Background = null;
            PreviewWash.Background = null;
            base.OnClosed(e);
        }
    }
}
