using System.Windows;
using System.Windows.Media;

namespace NeteaseToolbox.Wpf
{
    internal static class GlassBrushes
    {
        internal static Brush Shell(bool light)
        {
            var brush = new LinearGradientBrush
            {
                StartPoint = new Point(0, 0),
                EndPoint = new Point(0, 1)
            };
            if (light)
            {
                brush.GradientStops.Add(new GradientStop(Color.FromArgb(0xD6, 255, 255, 255), 0));
                brush.GradientStops.Add(new GradientStop(Color.FromArgb(0xC2, 240, 244, 250), 1));
            }
            else
            {
                brush.GradientStops.Add(new GradientStop(Color.FromArgb(0xB4, 42, 51, 70), 0));
                brush.GradientStops.Add(new GradientStop(Color.FromArgb(0x9C, 23, 29, 41), 1));
            }
            brush.Freeze();
            return brush;
        }

        internal static Brush Card(bool light)
        {
            var brush = new LinearGradientBrush
            {
                StartPoint = new Point(0, 0),
                EndPoint = new Point(0, 1)
            };
            if (light)
            {
                brush.GradientStops.Add(new GradientStop(Color.FromArgb(0xE8, 255, 255, 255), 0));
                brush.GradientStops.Add(new GradientStop(Color.FromArgb(0xD6, 255, 255, 255), 1));
            }
            else
            {
                brush.GradientStops.Add(new GradientStop(Color.FromArgb(0xCC, 42, 50, 69), 0));
                brush.GradientStops.Add(new GradientStop(Color.FromArgb(0xB4, 27, 33, 48), 1));
            }
            brush.Freeze();
            return brush;
        }

        internal static Brush Border(bool light)
        {
            var brush = new SolidColorBrush(light
                ? Color.FromArgb(0x1F, 0, 0, 0)
                : Color.FromArgb(0x30, 255, 255, 255));
            brush.Freeze();
            return brush;
        }

        internal static Brush AmbientBase(bool light)
        {
            var brush = new LinearGradientBrush
            {
                StartPoint = new Point(0, 0),
                EndPoint = new Point(1, 1)
            };
            if (light)
            {
                brush.GradientStops.Add(new GradientStop(Color.FromRgb(247, 250, 255), 0));
                brush.GradientStops.Add(new GradientStop(Color.FromRgb(235, 241, 249), 0.55));
                brush.GradientStops.Add(new GradientStop(Color.FromRgb(226, 234, 245), 1));
            }
            else
            {
                brush.GradientStops.Add(new GradientStop(Color.FromRgb(16, 21, 31), 0));
                brush.GradientStops.Add(new GradientStop(Color.FromRgb(20, 24, 33), 0.55));
                brush.GradientStops.Add(new GradientStop(Color.FromRgb(15, 19, 27), 1));
            }
            brush.Freeze();
            return brush;
        }

        internal static Brush AmbientGlow(bool light)
        {
            var brush = new RadialGradientBrush
            {
                Center = new Point(0.78, 0.08),
                GradientOrigin = new Point(0.78, 0.08),
                RadiusX = 0.95,
                RadiusY = 0.95
            };
            brush.GradientStops.Add(new GradientStop(
                light ? Color.FromArgb(0x55, 221, 232, 247) : Color.FromArgb(0x44, 58, 85, 126), 0));
            brush.GradientStops.Add(new GradientStop(
                light ? Color.FromArgb(0x00, 244, 246, 250) : Color.FromArgb(0x00, 20, 24, 33), 1));
            brush.Freeze();
            return brush;
        }
    }
}
