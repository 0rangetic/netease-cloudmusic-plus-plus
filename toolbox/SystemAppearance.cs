using System;
using System.Drawing;
using Microsoft.Win32;
using System.Windows;

namespace NeteaseToolbox.Wpf
{
    internal static class SystemAppearance
    {
        private const string PersonalizeKey = @"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize";
        private const string DwmKey = @"Software\Microsoft\Windows\DWM";
        private const string ExplorerAccentKey = @"Software\Microsoft\Windows\CurrentVersion\Explorer\Accent";

        internal static bool IsSystemLight()
        {
            try
            {
                using (var key = Registry.CurrentUser.OpenSubKey(PersonalizeKey))
                {
                    object value = key == null ? null : key.GetValue("AppsUseLightTheme");
                    return value == null || Convert.ToInt32(value) != 0;
                }
            }
            catch
            {
                return true;
            }
        }

        internal static bool IsTransparencyEnabled()
        {
            try
            {
                using (var key = Registry.CurrentUser.OpenSubKey(PersonalizeKey))
                {
                    object value = key == null ? null : key.GetValue("EnableTransparency");
                    return value == null || Convert.ToInt32(value) != 0;
                }
            }
            catch
            {
                return true;
            }
        }

        internal static bool IsHighContrast()
        {
            try { return SystemParameters.HighContrast; }
            catch { return false; }
        }

        internal static Color EffectiveAccent(AppearanceBackdropSettings settings)
        {
            if (settings != null && AppearanceBackdrop.NormalizeAccentMode(settings.AccentMode) == "custom")
            {
                var custom = Color.FromArgb(settings.AccentColor);
                return Color.FromArgb(255, custom.R, custom.G, custom.B);
            }
            return AccentColor();
        }

        internal static Color AccentColor()
        {
            int raw;
            if (TryReadDword(DwmKey, "AccentColor", out raw) ||
                TryReadDword(ExplorerAccentKey, "AccentColorMenu", out raw))
            {
                var accent = Color.FromArgb(255, (byte)(raw & 0xFF), (byte)((raw >> 8) & 0xFF), (byte)((raw >> 16) & 0xFF));
                if (accent.R + accent.G + accent.B > 0) return accent;
            }

            if (TryReadDword(DwmKey, "ColorizationColor", out raw))
            {
                var accent = Color.FromArgb(255, (byte)((raw >> 16) & 0xFF), (byte)((raw >> 8) & 0xFF), (byte)(raw & 0xFF));
                if (accent.R + accent.G + accent.B > 0) return accent;
            }

            return Color.FromArgb(0, 120, 212);
        }

        private static bool TryReadDword(string subKey, string name, out int value)
        {
            value = 0;
            try
            {
                using (var key = Registry.CurrentUser.OpenSubKey(subKey))
                {
                    object raw = key == null ? null : key.GetValue(name);
                    if (raw == null) return false;
                    value = Convert.ToInt32(raw);
                    return true;
                }
            }
            catch
            {
                return false;
            }
        }
    }

    internal static class GlassPolicy
    {
        // Windows“透明效果”暂不参与判断；要改为跟随系统，把这里改成 true。
        private const bool FollowSystemTransparency = false;

        internal static bool IsGlassEffective(bool userEnabled)
        {
            return userEnabled && !SystemAppearance.IsHighContrast()
                && (!FollowSystemTransparency || SystemAppearance.IsTransparencyEnabled());
        }
    }
}
