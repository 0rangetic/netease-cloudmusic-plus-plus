using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.IO;
using System.Linq;
using System.Net;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Forms.Integration;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using Microsoft.Win32;
using ModernWpf;
using ModernWpf.Controls;

namespace NeteaseToolbox.Wpf
{
    public partial class MainWindow : Window
    {
        private readonly List<string> taskMessages = new List<string>();
        private readonly List<string> runtimeLogs = new List<string>();
        private ProfileDashboardView profileView;
        private DailySigninView dailySigninView;
        private SettingsView settingsView;
        private GenreView genreView;
        private MemoryView memoryView;
        private AudioForwardView audioForwardView;
        private MultiListenView multiListenView;
        private ConvertView convertView;
        private ToolboxForm legacyForm;
        private WindowsFormsHost formHost;
        private bool syncingTheme;
        private AppearanceBackdropSettings backdropSettings;
        private AppearanceDialog appearanceDialog;
        private BitmapSource backdropSource;
        private int backdropSourceBlur = -1;
        private long backdropSourceStamp = -1;
        private string backdropBrushFit;
        private readonly DispatcherTimer appearanceRefreshTimer;
        private bool appearanceWatcherAttached;
        private bool isLightTheme;
        private bool glassActive;
        private string themeMode;
        private string accountAvatarKey;
        private HwndSource windowSource;

        [DllImport("dwmapi.dll")]
        private static extern int DwmSetWindowAttribute(IntPtr window, int attribute, ref int value, int size);

        public MainWindow() : this(null) { }

        internal MainWindow(ToolboxForm legacyForm)
        {
            this.legacyForm = legacyForm ?? ToolboxForm.Instance;
            InitializeComponent();

            backdropSettings = AppearanceBackdrop.LoadSettings();
            themeMode = backdropSettings.ThemeMode;
            if (string.IsNullOrEmpty(themeMode))
            {
                themeMode = this.legacyForm != null && this.legacyForm.IsLightTheme ? "light" : "dark";
                backdropSettings.ThemeMode = themeMode;
                AppearanceBackdrop.SaveSettings(backdropSettings);
            }

            appearanceRefreshTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(250) };
            appearanceRefreshTimer.Tick += OnAppearanceRefreshTick;

            ApplyEffectiveTheme(ResolveIsLight(), false);
            EnsureLegacyFormInitialized();
            Loaded += MainWindow_Loaded;
        }

        private bool ResolveIsLight()
        {
            string mode = backdropSettings != null ? backdropSettings.ThemeMode : themeMode;
            if (mode == "light") return true;
            if (mode == "dark") return false;
            if (mode == "auto") return SystemAppearance.IsSystemLight();
            return legacyForm != null && legacyForm.IsLightTheme;
        }

        private void ApplyEffectiveTheme(bool isLight, bool persistManual)
        {
            isLightTheme = isLight;
            syncingTheme = true;
            try
            {
                ThemeManager.SetRequestedTheme(this, isLight ? ElementTheme.Light : ElementTheme.Dark);
                if (legacyForm != null) legacyForm.SetTheme(isLight);
                if (persistManual && legacyForm != null) legacyForm.PersistTheme(isLight);
                UpdateHostThemeBrushes(isLight);
                ApplyGlassResources(isLight);
                ApplyAccent();
                UpdateWindowChrome(isLight);
                if (IsLoaded) RefreshBackdrop();
            }
            finally { syncingTheme = false; }
        }

        private void ApplyAccent()
        {
            System.Drawing.Color accent = SystemAppearance.EffectiveAccent(backdropSettings);
            try
            {
                ThemeManager.Current.AccentColor = Color.FromArgb(accent.A, accent.R, accent.G, accent.B);
            }
            catch { }
            legacyForm?.ApplySystemAccent(accent);
        }

        private void UpdateHostThemeBrushes(bool isLight)
        {
            var bgColor = isLight ? Color.FromRgb(244, 246, 250) : Color.FromRgb(20, 24, 33);
            var bgBrush = new SolidColorBrush(bgColor);
            bgBrush.Freeze();
            Background = bgBrush;
            if (RootGrid != null) RootGrid.Background = bgBrush;
            if (ContentBorder != null) ContentBorder.Background = Brushes.Transparent;
            if (formHost != null) formHost.Background = bgBrush;
            UpdateBackdropWash(isLight);
        }

        private void UpdateBackdropWash(bool isLight)
        {
            if (BackdropWash == null) return;
            int opacity = backdropSettings != null ? backdropSettings.MaskOpacity : AppearanceBackdrop.DefaultMaskOpacity;
            var brush = new SolidColorBrush(AppearanceBackdrop.MaskColor(isLight, opacity));
            brush.Freeze();
            BackdropWash.Background = brush;
        }

        private void ApplyBackdrop()
        {
            if (backdropSettings == null) backdropSettings = AppearanceBackdrop.LoadSettings();
            RefreshBackdrop();
        }

        private void RefreshBackdrop()
        {
            if (BackdropLayer == null) return;
            try
            {
                bool highContrast = SystemAppearance.IsHighContrast();
                bool hasWallpaper = !highContrast && backdropSettings != null && backdropSettings.HasWallpaper;
                if (!hasWallpaper)
                {
                    if (BackdropSurface != null) BackdropSurface.Background = null;
                    if (BackdropWash != null) BackdropWash.Background = null;
                    if (AmbientBase != null) AmbientBase.Background = glassActive ? GlassBrushes.AmbientBase(isLightTheme) : null;
                    if (AmbientGlow != null) AmbientGlow.Background = glassActive ? GlassBrushes.AmbientGlow(isLightTheme) : null;
                    return;
                }

                int blur = AppearanceBackdrop.NormalizeBlur(backdropSettings.Blur);
                long stamp = AppearanceBackdrop.WallpaperStamp();
                if (backdropSource == null || backdropSourceBlur != blur || backdropSourceStamp != stamp)
                {
                    backdropSource = AppearanceBackdrop.LoadWallpaper(blur);
                    backdropSourceBlur = blur;
                    backdropSourceStamp = stamp;
                    backdropBrushFit = null;
                }
                if (backdropSource == null)
                {
                    if (BackdropSurface != null) BackdropSurface.Background = null;
                    if (BackdropWash != null) BackdropWash.Background = null;
                    return;
                }

                string fit = AppearanceBackdrop.NormalizeFit(backdropSettings.WallpaperFit);
                if (backdropBrushFit != fit || BackdropSurface.Background == null)
                {
                    BackdropSurface.Background = AppearanceBackdrop.CreateBrush(backdropSource, fit);
                    backdropBrushFit = fit;
                }
                if (AmbientBase != null) AmbientBase.Background = null;
                if (AmbientGlow != null) AmbientGlow.Background = null;
                UpdateBackdropWash(isLightTheme);
            }
            catch
            {
                ClearBackdrop();
            }
        }

        private void ClearBackdrop()
        {
            if (BackdropSurface != null) BackdropSurface.Background = null;
            if (AmbientBase != null) AmbientBase.Background = null;
            if (AmbientGlow != null) AmbientGlow.Background = null;
            if (BackdropWash != null) BackdropWash.Background = null;
            backdropBrushFit = null;
            backdropSourceBlur = -1;
            backdropSourceStamp = -1;
        }

        private void ApplyGlassResources(bool isLight)
        {
            bool userEnabled = backdropSettings == null || backdropSettings.GlassEnabled;
            bool glass = GlassPolicy.IsGlassEffective(userEnabled);
            glassActive = glass;

            Brush shell = glass ? GlassBrushes.Shell(isLight) : (TryFindResource("SystemControlBackgroundAltHighBrush") as Brush) ?? Brushes.Transparent;
            Brush border = glass ? GlassBrushes.Border(isLight) : (TryFindResource("SystemControlStrokeColorCardStrokeBrush") as Brush) ?? Brushes.Transparent;

            if (TopBar != null)
            {
                TopBar.Background = shell;
                TopBar.BorderBrush = border;
            }
            if (BottomBar != null)
            {
                BottomBar.Background = shell;
                BottomBar.BorderBrush = border;
            }
            if (NavView != null)
            {
                NavView.Background = Brushes.Transparent;
                NavView.Resources["NavigationViewExpandedPaneBackground"] = shell;
                NavView.Resources["NavigationViewDefaultPaneBackground"] = shell;
                NavView.Resources["NavigationViewTopPaneBackground"] = shell;
                NavView.Resources["NavigationViewContentBackground"] = Brushes.Transparent;
                NavView.Resources["NavigationViewContentGridBorderBrush"] = Brushes.Transparent;
            }

            Resources["ToolboxGlassCardBrush"] = glass
                ? GlassBrushes.Card(isLight)
                : ((TryFindResource("SystemControlBackgroundAltHighBrush") as Brush) ?? Brushes.Transparent);
            Resources["ToolboxGlassCardBorderBrush"] = border;
            if (ContentBorder != null) ContentBorder.Background = Brushes.Transparent;
            UpdateBackdropWash(isLight);
        }

        private void UpdateWindowChrome(bool isLight)
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
            UpdateWindowChrome(isLightTheme);
            windowSource = PresentationSource.FromVisual(this) as HwndSource;
            if (windowSource != null) windowSource.AddHook(WindowMessageHook);
        }

        private IntPtr WindowMessageHook(IntPtr window, int message, IntPtr wParam, IntPtr lParam, ref bool handled)
        {
            const int WmSettingChange = 0x001A;
            const int WmDwmCompositionChanged = 0x031E;
            if (message == WmSettingChange || message == WmDwmCompositionChanged)
                QueueSystemAppearanceRefresh();
            return IntPtr.Zero;
        }

        protected override void OnClosed(EventArgs e)
        {
            if (windowSource != null)
            {
                windowSource.RemoveHook(WindowMessageHook);
                windowSource = null;
            }
            base.OnClosed(e);
        }

        private void AttachSystemAppearanceWatcher()
        {
            if (appearanceWatcherAttached) return;
            SystemEvents.UserPreferenceChanged += OnUserPreferenceChanged;
            SystemParameters.StaticPropertyChanged += OnSystemParametersChanged;
            appearanceWatcherAttached = true;
        }

        private void DetachSystemAppearanceWatcher()
        {
            if (!appearanceWatcherAttached) return;
            SystemEvents.UserPreferenceChanged -= OnUserPreferenceChanged;
            SystemParameters.StaticPropertyChanged -= OnSystemParametersChanged;
            appearanceWatcherAttached = false;
            if (appearanceRefreshTimer != null) appearanceRefreshTimer.Stop();
        }

        private void OnUserPreferenceChanged(object sender, UserPreferenceChangedEventArgs e)
        {
            QueueSystemAppearanceRefresh();
        }

        private void OnSystemParametersChanged(object sender, PropertyChangedEventArgs e)
        {
            if (string.IsNullOrEmpty(e.PropertyName) || e.PropertyName == "HighContrast")
                QueueSystemAppearanceRefresh();
        }

        private void QueueSystemAppearanceRefresh()
        {
            if (!Dispatcher.CheckAccess())
            {
                Dispatcher.BeginInvoke((Action)QueueSystemAppearanceRefresh);
                return;
            }
            appearanceRefreshTimer.Stop();
            appearanceRefreshTimer.Start();
        }

        private void OnAppearanceRefreshTick(object sender, EventArgs e)
        {
            appearanceRefreshTimer.Stop();
            if (!IsLoaded) return;

            if (backdropSettings != null && backdropSettings.ThemeMode == "auto")
            {
                bool light = SystemAppearance.IsSystemLight();
                if (light != isLightTheme) ApplyEffectiveTheme(light, false);
            }

            ApplyAccent();
            ApplyGlassResources(isLightTheme);
            RefreshBackdrop();
            UpdateWindowChrome(isLightTheme);
            if (appearanceDialog != null) appearanceDialog.RefreshFromSystem();
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
                        themeMode = light ? "light" : "dark";
                        if (backdropSettings != null)
                        {
                            backdropSettings.ThemeMode = themeMode;
                            AppearanceBackdrop.SaveSettings(backdropSettings);
                        }
                        ApplyEffectiveTheme(light, false);
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
            ApplyAccent();
            AttachSystemAppearanceWatcher();
            ApplyGlassResources(isLightTheme);
            ApplyBackdrop();
            UpdateWindowChrome(isLightTheme);
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
                    string userId = string.Empty;
                    string avatarUrl = string.Empty;
                    try
                    {
                        var session = NeteaseAuth.Load();
                        nickname = NeteaseAuth.Text(session, "nickname");
                        userId = NeteaseAuth.Text(session, "userId");
                        avatarUrl = NeteaseAuth.Text(session, "avatarUrl");
                    }
                    catch { }
                    AccountNicknameText.Text = !string.IsNullOrWhiteSpace(nickname) ? nickname : "网易云账号已登录";
                    AccountStatusText.Text = "已登录 · 点击切换或管理";
                    _ = LoadAccountAvatarAsync(userId, avatarUrl);
                }
                else
                {
                    AccountNicknameText.Text = "未登录网易云";
                    AccountStatusText.Text = "点击在此扫码登录";
                    ClearAccountAvatar();
                }
            }
            catch
            {
                AccountNicknameText.Text = "网易云账号";
                AccountStatusText.Text = "就绪";
                ClearAccountAvatar();
            }
        }

        private void ClearAccountAvatar()
        {
            accountAvatarKey = null;
            if (AccountAvatarBrush != null) AccountAvatarBrush.ImageSource = null;
            if (AccountAvatarImage != null) AccountAvatarImage.Visibility = Visibility.Collapsed;
            if (AccountAvatarPlaceholder != null) AccountAvatarPlaceholder.Visibility = Visibility.Visible;
        }

        private async Task LoadAccountAvatarAsync(string userId, string avatarUrl)
        {
            Uri url;
            if (string.IsNullOrWhiteSpace(userId) || string.IsNullOrWhiteSpace(avatarUrl) ||
                !Uri.TryCreate(avatarUrl, UriKind.Absolute, out url) ||
                (url.Scheme != Uri.UriSchemeHttps && url.Scheme != Uri.UriSchemeHttp) ||
                !url.Host.EndsWith(".music.126.net", StringComparison.OrdinalIgnoreCase))
            {
                ClearAccountAvatar();
                return;
            }

            string key = userId + "|" + avatarUrl;
            if (string.Equals(key, accountAvatarKey, StringComparison.Ordinal)) return;
            accountAvatarKey = key;

            string cacheFolder = Path.GetDirectoryName(NeteaseAuth.SessionPath);
            string cacheFile = Path.Combine(cacheFolder, "avatar-" + userId + "-" + ShortHash(avatarUrl) + ".img");
            byte[] bytes = null;
            try { if (File.Exists(cacheFile)) bytes = File.ReadAllBytes(cacheFile); }
            catch { }

            if (bytes == null)
            {
                try { bytes = await Task.Run(() => DownloadAvatar(new UriBuilder(url) { Scheme = "https", Port = -1 }.Uri)); }
                catch { }
                if (bytes == null)
                {
                    if (string.Equals(accountAvatarKey, key, StringComparison.Ordinal)) accountAvatarKey = null;
                    return;
                }
                try
                {
                    Directory.CreateDirectory(cacheFolder);
                    byte[] payload = bytes;
                    await Task.Run(() => File.WriteAllBytes(cacheFile, payload));
                }
                catch { }
            }

            BitmapImage image = DecodeAvatar(bytes);
            if (image == null)
            {
                try { File.Delete(cacheFile); }
                catch { }
                if (string.Equals(accountAvatarKey, key, StringComparison.Ordinal)) accountAvatarKey = null;
                return;
            }

            string currentUserId = string.Empty;
            try { currentUserId = NeteaseAuth.Text(NeteaseAuth.Load(), "userId"); }
            catch { }
            if (currentUserId != userId || !string.Equals(accountAvatarKey, key, StringComparison.Ordinal)) return;

            AccountAvatarBrush.ImageSource = image;
            AccountAvatarPlaceholder.Visibility = Visibility.Collapsed;
            AccountAvatarImage.Visibility = Visibility.Visible;
        }

        private static byte[] DownloadAvatar(Uri url)
        {
            var request = (HttpWebRequest)WebRequest.Create(url);
            request.Proxy = null;
            request.Timeout = 10000;
            request.ReadWriteTimeout = 10000;
            using (var response = request.GetResponse())
            using (var stream = response.GetResponseStream())
            using (var buffer = new MemoryStream())
            {
                if (stream == null) return null;
                stream.CopyTo(buffer);
                return buffer.ToArray();
            }
        }

        private static BitmapImage DecodeAvatar(byte[] bytes)
        {
            try
            {
                using (var stream = new MemoryStream(bytes))
                {
                    var image = new BitmapImage();
                    image.BeginInit();
                    image.CacheOption = BitmapCacheOption.OnLoad;
                    image.StreamSource = stream;
                    image.EndInit();
                    image.Freeze();
                    return image;
                }
            }
            catch { return null; }
        }

        private static string ShortHash(string value)
        {
            using (var sha = SHA1.Create())
                return BitConverter.ToString(sha.ComputeHash(Encoding.UTF8.GetBytes(value))).Replace("-", "").Substring(0, 8).ToLowerInvariant();
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

        private void ShowWpfView(UserControl view)
        {
            if (!ReferenceEquals(ContentArea.Content, view)) ContentArea.Content = view;
            if (WinFormsArea != null) WinFormsArea.Visibility = Visibility.Hidden;
            ContentArea.Visibility = Visibility.Visible;
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

            if (tag == "DailySignin")
            {
                EnsureLegacyFormInitialized();
                if (dailySigninView == null)
                {
                    dailySigninView = new DailySigninView(legacyForm ?? ToolboxForm.Instance);
                }
                if (!ReferenceEquals(ContentArea.Content, dailySigninView))
                {
                    ContentArea.Content = dailySigninView;
                }
                if (WinFormsArea != null) WinFormsArea.Visibility = Visibility.Hidden;
                ContentArea.Visibility = Visibility.Visible;
                return;
            }

            if (tag == "Settings")
            {
                EnsureLegacyFormInitialized();
                if (settingsView == null)
                {
                    settingsView = new SettingsView(legacyForm ?? ToolboxForm.Instance);
                }
                if (!ReferenceEquals(ContentArea.Content, settingsView))
                {
                    ContentArea.Content = settingsView;
                }
                if (WinFormsArea != null) WinFormsArea.Visibility = Visibility.Hidden;
                ContentArea.Visibility = Visibility.Visible;
                return;
            }

            if (tag == "Convert")
            {
                EnsureLegacyFormInitialized();
                if (convertView == null) convertView = new ConvertView(legacyForm ?? ToolboxForm.Instance);
                ShowWpfView(convertView);
                return;
            }

            if (tag == "Genre")
            {
                EnsureLegacyFormInitialized();
                if (genreView == null) genreView = new GenreView(legacyForm ?? ToolboxForm.Instance);
                ShowWpfView(genreView);
                return;
            }

            if (tag == "Memory")
            {
                EnsureLegacyFormInitialized();
                if (memoryView == null) memoryView = new MemoryView(legacyForm ?? ToolboxForm.Instance);
                ShowWpfView(memoryView);
                return;
            }

            if (tag == "AudioForward")
            {
                EnsureLegacyFormInitialized();
                if (audioForwardView == null) audioForwardView = new AudioForwardView(legacyForm ?? ToolboxForm.Instance);
                ShowWpfView(audioForwardView);
                return;
            }

            if (tag == "MultiListen")
            {
                EnsureLegacyFormInitialized();
                if (multiListenView == null) multiListenView = new MultiListenView(legacyForm ?? ToolboxForm.Instance);
                ShowWpfView(multiListenView);
                multiListenView.OnShown();
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
            bool nextLight = !isLightTheme;
            themeMode = nextLight ? "light" : "dark";
            if (backdropSettings != null)
            {
                backdropSettings.ThemeMode = themeMode;
                string saveError = AppearanceBackdrop.SaveSettings(backdropSettings);
                if (saveError != null) ReportTask("界面主题", "部分失败", "主题已切换，但偏好保存失败：" + saveError);
            }
            ApplyEffectiveTheme(nextLight, true);
            RefreshBackdrop();
            if (legacyForm == null)
                ReportTask("界面主题", "完成", "已切换为" + (nextLight ? "浅色模式" : "深色模式") + "。");
        }

        private void OnAppearanceClick(object sender, RoutedEventArgs e)
        {
            if (appearanceDialog != null)
            {
                appearanceDialog.Activate();
                return;
            }

            var dialog = new AppearanceDialog(isLightTheme) { Owner = this };
            dialog.SettingsChanged += OnAppearanceSettingsChanged;
            appearanceDialog = dialog;
            try
            {
                dialog.ShowDialog();
            }
            finally
            {
                dialog.SettingsChanged -= OnAppearanceSettingsChanged;
                backdropSettings = dialog.Settings;
                themeMode = backdropSettings.ThemeMode;
                appearanceDialog = null;
                ApplyAccent();
                ApplyGlassResources(isLightTheme);
                RefreshBackdrop();
            }
        }

        private void OnAppearanceSettingsChanged(object sender, EventArgs e)
        {
            var dialog = sender as AppearanceDialog;
            if (dialog == null) return;

            string previousMode = themeMode;
            backdropSettings = dialog.Settings;
            themeMode = backdropSettings.ThemeMode;
            bool light = ResolveIsLight();
            bool modeChanged = !string.Equals(previousMode, themeMode, StringComparison.OrdinalIgnoreCase);
            if (light != isLightTheme || modeChanged)
                ApplyEffectiveTheme(light, themeMode != "auto");
            ApplyAccent();
            ApplyGlassResources(light);
            RefreshBackdrop();
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
            if (appearanceDialog != null)
            {
                appearanceDialog.Close();
                appearanceDialog = null;
            }
            DetachSystemAppearanceWatcher();
            ClearBackdrop();
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
