using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Forms.Integration;
using System.Windows.Media;
using System.Windows.Threading;
using ModernWpf;
using ModernWpf.Controls;

namespace NeteaseToolbox.Wpf
{
    public partial class MainWindow : Window
    {
        private readonly List<string> taskMessages = new List<string>();
        private readonly List<string> runtimeLogs = new List<string>();
        private ProfileDashboardView profileView;
        private ToolboxForm legacyForm;
        private WindowsFormsHost formHost;
        private bool syncingTheme;

        public MainWindow() : this(null) { }

        internal MainWindow(ToolboxForm legacyForm)
        {
            this.legacyForm = legacyForm ?? ToolboxForm.Instance;
            InitializeComponent();
            bool isLight = this.legacyForm != null && this.legacyForm.IsLightTheme;
            ThemeManager.SetRequestedTheme(this, isLight ? ElementTheme.Light : ElementTheme.Dark);
            UpdateHostThemeBrushes(isLight);

            EnsureLegacyFormInitialized();
            Loaded += MainWindow_Loaded;
        }

        private void UpdateHostThemeBrushes(bool isLight)
        {
            var bgColor = isLight ? Color.FromRgb(244, 246, 250) : Color.FromRgb(20, 24, 33);
            var bgBrush = new SolidColorBrush(bgColor);
            if (ContentBorder != null) ContentBorder.Background = bgBrush;
            if (formHost != null) formHost.Background = bgBrush;
        }

        private void EnsureLegacyFormInitialized()
        {
            var target = legacyForm ?? ToolboxForm.Instance;
            if (target != null && formHost == null)
            {
                legacyForm = target;
                ToolboxForm.TaskReported += (task, phase, detail) => ReportTask(task, phase, detail);
                target.LyricStatusChanged += (on, text) =>
                {
                    Dispatcher.BeginInvoke((Action)(() =>
                    {
                        LyricStatusDot.Fill = new SolidColorBrush(on ? Color.FromRgb(42, 157, 143) : Color.FromRgb(108, 117, 125));
                        LyricStatusText.Text = text;
                    }));
                };
                target.AccountStateChanged += () =>
                {
                    Dispatcher.BeginInvoke((Action)CheckLoginState);
                };
                target.ThemeChanged += light =>
                {
                    if (syncingTheme) return;
                    Dispatcher.BeginInvoke((Action)(() =>
                    {
                        syncingTheme = true;
                        try
                        {
                            ThemeManager.SetRequestedTheme(this, light ? ElementTheme.Light : ElementTheme.Dark);
                            UpdateHostThemeBrushes(light);
                        }
                        finally { syncingTheme = false; }
                    }));
                };

                target.HideShellForWpf();
                var hostControl = target.GetPageHost() ?? (System.Windows.Forms.Control)target;
                bool isLight = target.IsLightTheme;
                hostControl.BackColor = isLight
                    ? System.Drawing.Color.FromArgb(244, 246, 250)
                    : System.Drawing.Color.FromArgb(20, 24, 33);

                formHost = new WindowsFormsHost
                {
                    Child = hostControl,
                    Background = new SolidColorBrush(isLight ? Color.FromRgb(244, 246, 250) : Color.FromRgb(20, 24, 33))
                };
                formHost.PropertyMap.Remove("Background");
                if (WinFormsArea != null) WinFormsArea.Content = formHost;
                UpdateHostThemeBrushes(isLight);
            }
        }

        private void MainWindow_Loaded(object sender, RoutedEventArgs e)
        {
            try
            {
                var ver = System.Reflection.Assembly.GetExecutingAssembly().GetName().Version;
                if (ver != null) AppVersionText.Text = "v" + ver.ToString();
            }
            catch { }

            ReportTask("工具箱初始化", "完成", "网易云++ WPF 原生现代架构已就绪。");
            CheckLoginState();
            EnsureLegacyFormInitialized();
            NavigateTo("Profile");
            // Startup scanning can run for a while; keep it off the first frame
            // so the shell is never shown half-painted (transparent background).
            Dispatcher.BeginInvoke(new Action(() => legacyForm?.RunStartupTasks()), DispatcherPriority.ApplicationIdle);
        }

        public void ReportTask(string taskName, string status, string detail)
        {
            Dispatcher.Invoke(() =>
            {
                string timestamp = DateTime.Now.ToString("HH:mm:ss");
                string logLine = $"[{timestamp}] [{taskName}] {status}: {detail}";
                taskMessages.Add(logLine);
                runtimeLogs.Add(logLine);

                TaskStatusText.Text = $"{taskName}: {status} · {detail}";

                if (status == "完成")
                {
                    TaskStatusDot.Fill = new SolidColorBrush(Color.FromRgb(42, 157, 143)); // 绿色
                }
                else if (status == "开始" || status == "进行中")
                {
                    TaskStatusDot.Fill = new SolidColorBrush(Color.FromRgb(233, 196, 106)); // 黄色
                }
                else if (status == "部分失败")
                {
                    TaskStatusDot.Fill = new SolidColorBrush(Color.FromRgb(244, 162, 97)); // 橙色
                }
                else // 失败
                {
                    TaskStatusDot.Fill = new SolidColorBrush(Color.FromRgb(231, 111, 81)); // 红色
                }
            });
        }

        private void CheckLoginState()
        {
            try
            {
                string sessionFile = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                    @"NeteaseToolbox\Auth\session.dat");

                if (File.Exists(sessionFile))
                {
                    string nickname = null;
                    try
                    {
                        var session = NeteaseAuth.Load();
                        nickname = NeteaseAuth.Text(session, "nickname");
                    }
                    catch { }
                    AccountNicknameText.Text = !string.IsNullOrWhiteSpace(nickname) ? nickname : "网易云账号已登录";
                    AccountStatusText.Text = "已登录 · 点击切换或管理";
                }
                else
                {
                    AccountNicknameText.Text = "未登录网易云";
                    AccountStatusText.Text = "点击在此扫码登录";
                }
            }
            catch
            {
                AccountNicknameText.Text = "网易云账号";
                AccountStatusText.Text = "就绪";
            }
        }

        private void OnNavSelectionChanged(ModernWpf.Controls.NavigationView sender, ModernWpf.Controls.NavigationViewSelectionChangedEventArgs args)
        {
            if (args.SelectedItem is ModernWpf.Controls.NavigationViewItem item && item.Tag != null)
            {
                string tag = item.Tag.ToString();
                if (tag == "Account") return;
                NavigateTo(tag);
            }
        }

        private void OnNavItemInvoked(ModernWpf.Controls.NavigationView sender, ModernWpf.Controls.NavigationViewItemInvokedEventArgs args)
        {
            if (args.InvokedItemContainer is ModernWpf.Controls.NavigationViewItem item && item.Tag != null)
            {
                string tag = item.Tag.ToString();
                if (tag == "Account")
                {
                    var target = legacyForm ?? ToolboxForm.Instance;
                    if (target != null)
                    {
                        target.OpenLoginDialog();
                        CheckLoginState();
                    }
                    return;
                }
                NavigateTo(tag);
            }
        }

        internal void NavigateTo(string tag)
        {
            if (string.IsNullOrEmpty(tag) || tag == "Account") return;

            if (tag == "Profile")
            {
                if (profileView == null)
                {
                    profileView = new ProfileDashboardView
                    {
                        ReportTaskAction = (task, status, detail) => ReportTask(task, status, detail)
                    };
                }
                if (!ReferenceEquals(ContentArea.Content, profileView))
                {
                    ContentArea.Content = profileView;
                }
                if (WinFormsArea != null) WinFormsArea.Visibility = Visibility.Hidden;
                ContentArea.Visibility = Visibility.Visible;
                return;
            }

            EnsureLegacyFormInitialized();

            var target = legacyForm ?? ToolboxForm.Instance;
            if (target != null && formHost != null)
            {
                int pageIndex = 0;
                switch (tag)
                {
                    case "Convert": pageIndex = 0; break;
                    case "Genre": pageIndex = 1; break;
                    case "Memory": pageIndex = 3; break;
                    case "Settings": pageIndex = 4; break;
                    case "AudioForward": pageIndex = 5; break;
                    case "DailySignin": pageIndex = 6; break;
                    case "MultiListen": pageIndex = 7; break;
                    default: pageIndex = 0; break;
                }

                if (WinFormsArea != null && !ReferenceEquals(WinFormsArea.Content, formHost))
                {
                    WinFormsArea.Content = formHost;
                }
                ContentArea.Visibility = Visibility.Hidden;
                if (WinFormsArea != null) WinFormsArea.Visibility = Visibility.Visible;
                target.SwitchToPage(pageIndex);
            }
        }

        private void OnLyricToggleClick(object sender, RoutedEventArgs e)
        {
            var target = legacyForm ?? ToolboxForm.Instance;
            if (target != null)
            {
                target.ToggleLyrics();
            }
            else
            {
                ReportTask("桌面歌词", "完成", "桌面三行歌词补丁已连接。");
            }
        }

        private void OnThemeToggleClick(object sender, RoutedEventArgs e)
        {
            var currentTheme = ThemeManager.GetActualTheme(this);
            var nextTheme = currentTheme == ElementTheme.Dark ? ElementTheme.Light : ElementTheme.Dark;
            bool isLight = nextTheme == ElementTheme.Light;
            syncingTheme = true;
            try
            {
                ThemeManager.SetRequestedTheme(this, nextTheme);
                UpdateHostThemeBrushes(isLight);
                var target = legacyForm ?? ToolboxForm.Instance;
                if (target != null)
                {
                    target.SetTheme(isLight);
                    target.PersistTheme(isLight);
                }
                else
                {
                    ReportTask("主题切换", "完成", $"界面主题已切换为: {(nextTheme == ElementTheme.Dark ? "深色模式" : "浅色模式")}");
                }
            }
            finally { syncingTheme = false; }
        }

        protected override void OnClosing(System.ComponentModel.CancelEventArgs e)
        {
            var target = legacyForm ?? ToolboxForm.Instance;
            if (target != null && !target.AllowExit)
            {
                e.Cancel = true;
                Hide();
                return;
            }
            base.OnClosing(e);
        }

        private async void OnTaskMessagesClick(object sender, RoutedEventArgs e)
        {
            var content = new TextBox
            {
                Text = string.Join(Environment.NewLine, taskMessages.Skip(Math.Max(0, taskMessages.Count - 50))),
                IsReadOnly = true,
                TextWrapping = TextWrapping.Wrap,
                MaxHeight = 400,
                Width = 550,
                VerticalScrollBarVisibility = ScrollBarVisibility.Auto
            };

            var dialog = new ContentDialog
            {
                Title = "📋 最近任务消息反馈",
                Content = content,
                CloseButtonText = "关闭",
                DefaultButton = ContentDialogButton.Close
            };

            await dialog.ShowAsync();
        }

        private async void OnRuntimeLogClick(object sender, RoutedEventArgs e)
        {
            var target = legacyForm ?? ToolboxForm.Instance;
            if (target != null)
            {
                target.ShowLogWindow();
                return;
            }

            var content = new TextBox
            {
                Text = string.Join(Environment.NewLine, runtimeLogs.Skip(Math.Max(0, runtimeLogs.Count - 100))),
                IsReadOnly = true,
                TextWrapping = TextWrapping.Wrap,
                MaxHeight = 450,
                Width = 600,
                VerticalScrollBarVisibility = ScrollBarVisibility.Auto
            };

            var dialog = new ContentDialog
            {
                Title = "📜 运行详细日志",
                Content = content,
                CloseButtonText = "关闭",
                DefaultButton = ContentDialogButton.Close
            };

            await dialog.ShowAsync();
        }
    }
}
