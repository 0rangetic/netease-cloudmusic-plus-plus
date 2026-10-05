using System;
using System.Drawing;
using System.IO;
using System.Text;
using System.Windows.Forms;

namespace NeteaseToolbox
{
    internal sealed class ThemePalette
    {
        public readonly Color Background, Panel, Ink, Muted, Accent, Border, Secondary,
            ButtonInk, Danger, Success, Error, LogBackground;
        public static readonly ThemePalette Dark = new ThemePalette(false);
        public static readonly ThemePalette Light = new ThemePalette(true);

        private ThemePalette(bool light)
        {
            Background = light ? Color.FromArgb(244, 246, 250) : Color.FromArgb(20, 24, 33);
            Panel = light ? Color.White : Color.FromArgb(31, 37, 49);
            Ink = light ? Color.FromArgb(31, 41, 55) : Color.FromArgb(236, 240, 247);
            Muted = light ? Color.FromArgb(91, 105, 124) : Color.FromArgb(156, 167, 184);
            Accent = light ? Color.FromArgb(34, 103, 205) : Color.FromArgb(76, 149, 245);
            Border = light ? Color.FromArgb(203, 212, 225) : Color.FromArgb(67, 77, 94);
            Secondary = light ? Color.FromArgb(229, 237, 248) : Color.FromArgb(48, 57, 72);
            ButtonInk = light ? Ink : Color.White;
            Danger = Color.FromArgb(176, 73, 65);
            Success = light ? Color.FromArgb(24, 117, 73) : Color.FromArgb(119, 213, 164);
            Error = light ? Color.FromArgb(178, 53, 43) : Color.FromArgb(255, 139, 124);
            LogBackground = light ? Panel : Color.FromArgb(25, 30, 41);
        }

        public Color Map(Color value, ThemePalette target, bool foreground)
        {
            // Foreground and background have separate mappings: white is both the
            // light panel surface and the dark secondary-button text color.
            Color[] source = foreground ? new[] { Ink, Muted, Success, Error, ButtonInk }
                : new[] { Background, Panel, Secondary, Accent, Border, LogBackground };
            Color[] destination = foreground ? new[] { target.Ink, target.Muted, target.Success, target.Error, target.ButtonInk }
                : new[] { target.Background, target.Panel, target.Secondary, target.Accent, target.Border, target.LogBackground };
            for (int i = 0; i < source.Length; i++)
                if (value.ToArgb() == source[i].ToArgb()) return destination[i];
            return value;
        }
    }

    internal sealed partial class ToolboxForm
    {
        private ThemePalette theme = ThemePalette.Dark;
        private readonly string themeSettingPath = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            @"NeteaseToolbox\appearance.txt");
        private ComboBox themeSelector;
        private bool loadingTheme;

        private Control BuildThemeSelector()
        {
            var row = new FlowLayoutPanel {
                Name = "AppearanceSettings", Location = new Point(28, 76),
                Size = new Size(440, 36), WrapContents = false,
                BackColor = Color.Transparent
            };
            var caption = Label("界面主题", 10, true, ink);
            caption.Margin = new Padding(0, 6, 16, 0);
            row.Controls.Add(caption);
            themeSelector = new ComboBox {
                Name = "ThemeSelector", DropDownStyle = ComboBoxStyle.DropDownList,
                Width = 156, Margin = new Padding(0, 2, 0, 0),
                BackColor = panel, ForeColor = ink
            };
            themeSelector.Items.AddRange(new object[] { "深色模式", "浅色模式" });
            themeSelector.SelectedIndex = 0;
            themeSelector.SelectedIndexChanged += delegate {
                if (loadingTheme) return;
                bool light = themeSelector.SelectedIndex == 1;
                SetTheme(light);
                try {
                    Directory.CreateDirectory(Path.GetDirectoryName(themeSettingPath));
                    File.WriteAllText(themeSettingPath, light ? "light" : "dark", Encoding.UTF8);
                    ReportTask("界面主题", "完成", "已切换为" + (light ? "浅色模式" : "深色模式") + "，下次启动继续使用。");
                }
                catch (Exception ex) {
                    ReportTask("界面主题", "部分失败", "主题已切换，但无法保存下次启动的偏好：" + ex.Message);
                }
            };
            row.Controls.Add(themeSelector);
            return row;
        }

        private void LoadTheme()
        {
            loadingTheme = true;
            try {
                bool light = File.Exists(themeSettingPath)
                    && File.ReadAllText(themeSettingPath, Encoding.UTF8).Trim() == "light";
                SetTheme(light);
                themeSelector.SelectedIndex = light ? 1 : 0;
            }
            catch (Exception ex) {
                SetTheme(false);
                themeSelector.SelectedIndex = 0;
                string detail = "无法读取主题偏好，使用深色模式：" + ex.Message;
                if (IsHandleCreated) ReportTask("界面主题", "失败", detail);
                else {
                    EventHandler report = null;
                    report = delegate { HandleCreated -= report; ReportTask("界面主题", "失败", detail); };
                    HandleCreated += report;
                }
            }
            finally { loadingTheme = false; }
        }

        private void SetTheme(bool light)
        {
            var previous = theme;
            theme = light ? ThemePalette.Light : ThemePalette.Dark;
            SuspendLayout();
            try {
                RecolorControls(this, previous);
                if (logWindow != null && !logWindow.IsDisposed) RecolorControls(logWindow, previous);
                if (taskMessagesWindow != null && !taskMessagesWindow.IsDisposed) RecolorControls(taskMessagesWindow, previous);
            }
            finally { ResumeLayout(true); }
            ApplyWallpaperSurfaces(this);
            Invalidate(true);
        }

        private void RecolorControls(Control control, ThemePalette previous)
        {
            // Visit children first so inherited colors still come from the old palette.
            foreach (Control child in control.Controls) RecolorControls(child, previous);
            control.BackColor = previous.Map(control.BackColor, theme, false);
            control.ForeColor = previous.Map(control.ForeColor, theme, true);
            var input = control is TextBoxBase || control is ComboBox || control is ListView || control is ListBox || control is CachedSongTable;
            if (input) {
                control.BackColor = theme.Panel;
                control.ForeColor = control == memoryLifeRecallPath ? theme.Muted : theme.Ink;
            }
            var button = control as Button;
            if (button != null) {
                ButtonRole role;
                if (Enum.TryParse<ButtonRole>(button.AccessibleDescription, out role)) {
                    button.FlatAppearance.BorderColor = theme.Border;
                    button.FlatAppearance.MouseOverBackColor = role == ButtonRole.Primary ? theme.Accent
                        : role == ButtonRole.Danger ? theme.Danger : theme.Secondary;
                    button.FlatAppearance.MouseDownBackColor = button.FlatAppearance.MouseOverBackColor;
                    if (role == ButtonRole.Primary || role == ButtonRole.Danger) button.ForeColor = Color.White;
                    else if (role == ButtonRole.Navigation)
                        button.ForeColor = button.BackColor.ToArgb() == theme.Secondary.ToArgb() ? theme.ButtonInk : theme.Muted;
                    else if (button.ForeColor.ToArgb() != theme.Error.ToArgb()) button.ForeColor = theme.ButtonInk;
                    ApplyButtonAppearance(button, role);
                }
            }
            var tabs = control as TabControl;
            if (tabs != null) {
                tabs.DrawMode = TabDrawMode.OwnerDrawFixed;
                tabs.DrawItem -= DrawThemeTab;
                tabs.DrawItem += DrawThemeTab;
            }
            control.Invalidate();
        }

        private void PrepareThemedDialog(Control dialog)
        {
            // Dynamically-created windows already use the current palette; this
            // also styles native-default text inputs and buttons consistently.
            RecolorControls(dialog, theme);
            ApplyUiFonts(dialog);
        }

        private void DrawThemeTab(object sender, DrawItemEventArgs e)
        {
            var tabs = (TabControl)sender;
            bool selected = tabs.SelectedIndex == e.Index;
            using (var brush = new SolidBrush(selected ? theme.Secondary : theme.Panel))
                e.Graphics.FillRectangle(brush, e.Bounds);
            TextRenderer.DrawText(e.Graphics, tabs.TabPages[e.Index].Text, tabs.Font, e.Bounds,
                selected ? theme.Ink : theme.Muted, TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter);
            if ((e.State & DrawItemState.Focus) != 0) ControlPaint.DrawFocusRectangle(e.Graphics, e.Bounds);
        }
    }
}
