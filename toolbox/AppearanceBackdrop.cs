using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;
using System.Web.Script.Serialization;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace NeteaseToolbox.Wpf
{
    internal sealed class AppearanceBackdropSettings
    {
        public int Blur { get; set; }
        public int MaskOpacity { get; set; }
        public bool HasWallpaper { get; set; }
        public string WallpaperFit { get; set; }
        public int WallpaperWidth { get; set; }
        public int WallpaperHeight { get; set; }
        public string ThemeMode { get; set; }
        public bool GlassEnabled { get; set; }
        public string AccentMode { get; set; }
        public int AccentColor { get; set; }
    }

    internal static class AppearanceBackdrop
    {
        internal const int DefaultBlur = 12;
        internal const int DefaultMaskOpacity = 82;
        internal const int DefaultAccentColor = unchecked((int)0xFF0078D4);
        internal const string DefaultFontFamily = "Microsoft YaHei UI";
        internal static readonly string[] FitModes = { "Fill", "Fit", "Stretch", "Tile", "Center" };

        private static string AppearanceFolder
        {
            get
            {
                return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "NeteaseToolbox");
            }
        }

        internal static string ConfigPath
        {
            get { return Path.Combine(AppearanceFolder, "appearance-custom.json"); }
        }

        internal static string WallpaperPath
        {
            get { return Path.Combine(AppearanceFolder, "appearance-background.png"); }
        }

        internal static string NormalizeFit(string mode)
        {
            return Array.IndexOf(FitModes, mode) >= 0 ? mode : "Fill";
        }

        internal static string NormalizeThemeMode(string mode)
        {
            if (string.Equals(mode, "auto", StringComparison.OrdinalIgnoreCase)) return "auto";
            if (string.Equals(mode, "light", StringComparison.OrdinalIgnoreCase)) return "light";
            if (string.Equals(mode, "dark", StringComparison.OrdinalIgnoreCase)) return "dark";
            return "";
        }

        internal static int NormalizeBlur(int blur)
        {
            return Math.Max(0, Math.Min(30, blur));
        }

        internal static string NormalizeAccentMode(string mode)
        {
            return string.Equals(mode, "custom", StringComparison.OrdinalIgnoreCase) ? "custom" : "system";
        }

        internal static int MaskAlpha(int percent)
        {
            return (int)Math.Round(255.0 * Math.Max(0, Math.Min(100, percent)) / 100.0);
        }

        internal static Color MaskColor(bool isLight, int percent)
        {
            Color background = isLight ? Color.FromRgb(244, 246, 250) : Color.FromRgb(20, 24, 33);
            return Color.FromArgb((byte)MaskAlpha(percent), background.R, background.G, background.B);
        }

        internal static long WallpaperStamp()
        {
            try
            {
                var info = new FileInfo(WallpaperPath);
                return info.Exists ? info.LastWriteTimeUtc.Ticks ^ info.Length : 0;
            }
            catch
            {
                return 0;
            }
        }

        internal static AppearanceBackdropSettings LoadSettings()
        {
            var settings = new AppearanceBackdropSettings
            {
                Blur = DefaultBlur,
                MaskOpacity = DefaultMaskOpacity,
                HasWallpaper = false,
                WallpaperFit = "Fill",
                WallpaperWidth = 0,
                WallpaperHeight = 0,
                ThemeMode = "",
                GlassEnabled = true,
                AccentMode = "system",
                AccentColor = DefaultAccentColor
            };

            Dictionary<string, object> data = ReadRawConfig();
            if (data == null) return settings;

            settings.Blur = Math.Max(0, Math.Min(30, IntValue(data, "Blur", DefaultBlur)));
            settings.MaskOpacity = Math.Max(0, Math.Min(100, IntValue(data, "MaskOpacity", DefaultMaskOpacity)));
            settings.HasWallpaper = BoolValue(data, "HasWallpaper", false);
            settings.WallpaperFit = NormalizeFit(StringValue(data, "WallpaperFit", "Fill"));
            settings.WallpaperWidth = Math.Max(0, IntValue(data, "WallpaperWidth", 0));
            settings.WallpaperHeight = Math.Max(0, IntValue(data, "WallpaperHeight", 0));
            settings.ThemeMode = NormalizeThemeMode(StringValue(data, "ThemeMode", ""));
            settings.GlassEnabled = BoolValue(data, "GlassEnabled", true);
            settings.AccentMode = NormalizeAccentMode(StringValue(data, "AccentMode", "system"));
            settings.AccentColor = IntValue(data, "AccentColor", DefaultAccentColor);
            return settings;
        }

        internal static string SaveSettings(AppearanceBackdropSettings settings)
        {
            if (settings == null) return "外观设置为空。";
            string temporary = ConfigPath + ".tmp";
            try
            {
                Dictionary<string, object> data = ReadRawConfig();
                if (data == null) data = new Dictionary<string, object>();
                if (!data.ContainsKey("FontFamily")) data["FontFamily"] = DefaultFontFamily;
                data["Blur"] = Math.Max(0, Math.Min(30, settings.Blur));
                data["MaskOpacity"] = Math.Max(0, Math.Min(100, settings.MaskOpacity));
                data["HasWallpaper"] = settings.HasWallpaper;
                data["WallpaperFit"] = NormalizeFit(settings.WallpaperFit);
                data["WallpaperWidth"] = Math.Max(0, settings.WallpaperWidth);
                data["WallpaperHeight"] = Math.Max(0, settings.WallpaperHeight);
                string themeMode = NormalizeThemeMode(settings.ThemeMode);
                if (themeMode.Length > 0) data["ThemeMode"] = themeMode;
                else if (data.ContainsKey("ThemeMode")) data.Remove("ThemeMode");
                data["GlassEnabled"] = settings.GlassEnabled;
                data["AccentMode"] = NormalizeAccentMode(settings.AccentMode);
                data["AccentColor"] = settings.AccentColor;

                Directory.CreateDirectory(AppearanceFolder);
                File.WriteAllText(temporary, new JavaScriptSerializer().Serialize(data), Encoding.UTF8);
                if (File.Exists(ConfigPath)) File.Replace(temporary, ConfigPath, null);
                else File.Move(temporary, ConfigPath);
                return null;
            }
            catch (Exception ex)
            {
                try { if (File.Exists(temporary)) File.Delete(temporary); } catch { }
                return ex.Message;
            }
        }

        internal static BitmapSource LoadWallpaper(int blur)
        {
            try
            {
                string path = WallpaperPath;
                if (!File.Exists(path)) return null;
                using (System.Drawing.Bitmap source = ToolboxForm.ReadWallpaper(path))
                using (System.Drawing.Bitmap blurred = ToolboxForm.BlurBitmap(source, Math.Max(0, Math.Min(30, blur))))
                using (var stream = new MemoryStream())
                {
                    blurred.Save(stream, System.Drawing.Imaging.ImageFormat.Bmp);
                    stream.Position = 0;
                    var decoder = new BmpBitmapDecoder(stream, BitmapCreateOptions.PreservePixelFormat, BitmapCacheOption.OnLoad);
                    if (decoder.Frames.Count == 0) return null;
                    BitmapSource frame = decoder.Frames[0];
                    frame.Freeze();
                    return frame;
                }
            }
            catch
            {
                return null;
            }
        }

        internal static ImageBrush CreateBrush(BitmapSource source, string fit)
        {
            if (source == null) return null;
            var brush = new ImageBrush(source);
            switch (NormalizeFit(fit))
            {
                case "Fit":
                    brush.Stretch = Stretch.Uniform;
                    break;
                case "Stretch":
                    brush.Stretch = Stretch.Fill;
                    break;
                case "Tile":
                    brush.Stretch = Stretch.None;
                    brush.TileMode = TileMode.Tile;
                    brush.ViewportUnits = BrushMappingMode.Absolute;
                    brush.Viewport = new Rect(0, 0, source.PixelWidth, source.PixelHeight);
                    break;
                case "Center":
                    brush.Stretch = Stretch.None;
                    brush.AlignmentX = AlignmentX.Center;
                    brush.AlignmentY = AlignmentY.Center;
                    break;
                default:
                    brush.Stretch = Stretch.UniformToFill;
                    break;
            }
            brush.Freeze();
            return brush;
        }

        internal static string DescribeWallpaper(AppearanceBackdropSettings settings)
        {
            if (settings == null || !settings.HasWallpaper) return "当前未使用背景图片。";
            if (!File.Exists(WallpaperPath)) return "背景图片文件缺失，请重新选择。";
            string size = settings.WallpaperWidth > 0 && settings.WallpaperHeight > 0
                ? settings.WallpaperWidth + "×" + settings.WallpaperHeight
                : "尺寸未知";
            string scale = settings.WallpaperWidth > 1280 || settings.WallpaperHeight > 1280
                ? "已缩放到最长边 1280"
                : "按原始尺寸";
            return "背景图片：appearance-background.png · 原始 " + size + " · " + scale;
        }

        private static Dictionary<string, object> ReadRawConfig()
        {
            try
            {
                if (!File.Exists(ConfigPath)) return null;
                string text = File.ReadAllText(ConfigPath, Encoding.UTF8);
                if (string.IsNullOrWhiteSpace(text)) return null;
                return new JavaScriptSerializer().Deserialize<Dictionary<string, object>>(text);
            }
            catch
            {
                return null;
            }
        }

        private static int IntValue(Dictionary<string, object> data, string key, int fallback)
        {
            object value;
            if (data == null || !data.TryGetValue(key, out value) || value == null) return fallback;
            try { return Convert.ToInt32(value, CultureInfo.InvariantCulture); }
            catch { return fallback; }
        }

        private static bool BoolValue(Dictionary<string, object> data, string key, bool fallback)
        {
            object value;
            if (data == null || !data.TryGetValue(key, out value) || value == null) return fallback;
            try { return Convert.ToBoolean(value, CultureInfo.InvariantCulture); }
            catch { return fallback; }
        }

        private static string StringValue(Dictionary<string, object> data, string key, string fallback)
        {
            object value;
            if (data == null || !data.TryGetValue(key, out value) || value == null) return fallback;
            string text = value as string;
            return string.IsNullOrWhiteSpace(text) ? fallback : text;
        }
    }
}
