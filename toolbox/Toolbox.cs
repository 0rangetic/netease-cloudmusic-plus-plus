using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Management;
using Microsoft.Win32;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;
using System.Web.Script.Serialization;

[assembly: AssemblyTitle("网易云工具箱")]
[assembly: AssemblyDescription("网易云账号播放记录、音乐记忆检索、转换、曲风分析、听歌画像、多人一起听、桌面歌词与音频转麦克风工具箱")]
[assembly: AssemblyProduct("网易云工具箱")]
[assembly: AssemblyCompany("")]
[assembly: AssemblyVersion("2.13.0.4")]
[assembly: AssemblyFileVersion("2.13.0.4")]
[assembly: AssemblyInformationalVersion("2.13.0.4")]

namespace NeteaseToolbox
{
    internal static class Program
    {
        private static Mutex instanceMutex;
        private static EventWaitHandle showWindowEvent;

        [DllImport("user32.dll", CharSet = CharSet.Unicode)]
        private static extern IntPtr FindWindow(string className, string windowName);
        [DllImport("user32.dll")]
        private static extern bool ShowWindow(IntPtr window, int command);
        [DllImport("user32.dll")]
        private static extern bool SetForegroundWindow(IntPtr window);

        [STAThread]
        private static void Main(string[] args)
        {
            if (args.Any(arg => string.Equals(arg, "--daily-signin", StringComparison.OrdinalIgnoreCase))) {
                try {
                    var result = DailySigninService.Run(args.Any(arg => string.Equals(arg, "--scheduled", StringComparison.OrdinalIgnoreCase)), null);
                    Environment.ExitCode = result.Skipped || result.Phase == "完成" ? 0 : 1;
                    string error = Path.Combine(DailySigninService.Root, "daily-signin-runner-error.json");
                    if (File.Exists(error)) File.Delete(error);
                } catch (Exception ex) {
                    Environment.ExitCode = 1;
                    try { DailySigninService.SaveJson(Path.Combine(DailySigninService.Root, "daily-signin-runner-error.json"), new SigninEvent { Time = DateTimeOffset.Now.ToOffset(DailySigninService.ChinaOffset).ToString("yyyy-MM-dd HH:mm:ss"), Task = "每日签到", Phase = "失败", Detail = DailySigninService.SafeError(ex) }); } catch { }
                }
                return;
            }
            bool bg = args.Any(arg => string.Equals(arg, "--background", StringComparison.OrdinalIgnoreCase));
            bool created;
            instanceMutex = new Mutex(true, @"Local\NeteaseToolboxPlaybackCapture", out created);
            if (!created)
            {
                if (!bg)
                {
                    try
                    {
                        using (var evt = EventWaitHandle.OpenExisting(@"Local\NeteaseToolboxShowMainWindow"))
                        {
                            evt.Set();
                        }
                    }
                    catch { }
                    var existing = FindWindow(null, "网易云工具箱");
                    if (existing != IntPtr.Zero) { ShowWindow(existing, 9); SetForegroundWindow(existing); }
                }
                return;
            }
            try
            {
                showWindowEvent = new EventWaitHandle(false, EventResetMode.AutoReset, @"Local\NeteaseToolboxShowMainWindow");
            }
            catch { }
            Application.EnableVisualStyles();
            Application.SetCompatibleTextRenderingDefault(false);
            var toolboxForm = new ToolboxForm(bg, true);
            toolboxForm.HideShellForWpf();
            var app = new NeteaseToolbox.Wpf.App();
            app.InitializeComponent();
            var mainWindow = new NeteaseToolbox.Wpf.MainWindow(toolboxForm);
            if (showWindowEvent != null)
            {
                ThreadPool.RegisterWaitForSingleObject(showWindowEvent, delegate {
                    try
                    {
                        app.Dispatcher.BeginInvoke((Action)(() => {
                            if (!mainWindow.IsVisible) mainWindow.Show();
                            if (mainWindow.WindowState == System.Windows.WindowState.Minimized)
                                mainWindow.WindowState = System.Windows.WindowState.Normal;
                            mainWindow.Activate();
                            mainWindow.Topmost = true;
                            mainWindow.Topmost = false;
                            mainWindow.Focus();
                        }));
                    }
                    catch { }
                }, null, -1, false);
            }
            if (!bg) {
                app.Run(mainWindow);
            } else {
                mainWindow.Hide();
                toolboxForm.RunStartupTasks();
                app.Run();
            }
            GC.KeepAlive(instanceMutex);
            GC.KeepAlive(showWindowEvent);
        }
    }

    internal sealed partial class ToolboxForm : Form
    {
        [DllImport("user32.dll", EntryPoint = "SendMessageW")]
        private static extern IntPtr SendUiMessage(IntPtr window, int message, IntPtr wParam, IntPtr lParam);

        private string MusicRoot { get { return string.IsNullOrEmpty(InputRoot) ? AppDomain.CurrentDomain.BaseDirectory : InputRoot; } }
        private string InputRoot = "";
        private readonly string inputSettingPath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "input-folder.txt");
        private readonly Button chooseInputButton = new Button();
        private static string ResolvePortablePath(string value)
        {
            return Path.GetFullPath(Path.IsPathRooted(value) ? value : Path.Combine(AppDomain.CurrentDomain.BaseDirectory, value));
        }
        private static string SavePortablePath(string value)
        {
            var basePath = AppDomain.CurrentDomain.BaseDirectory.TrimEnd('\\') + "\\";
            if (value.TrimEnd('\\').Equals(basePath.TrimEnd('\\'), StringComparison.OrdinalIgnoreCase)) return ".";
            return value.StartsWith(basePath, StringComparison.OrdinalIgnoreCase) ? value.Substring(basePath.Length) : value;
        }
        private void ConfigureInputWatcher()
        {
            if (watcher != null) { watcher.Dispose(); watcher = null; }
            if (!Directory.Exists(InputRoot)) return;
            watcher = new FileSystemWatcher(InputRoot, "*.ncm");
            watcher.IncludeSubdirectories = true;
            watcher.NotifyFilter = NotifyFilters.FileName | NotifyFilters.LastWrite | NotifyFilters.Size;
            watcher.Created += delegate { QueueScan(); };
            watcher.Changed += delegate { QueueScan(); };
            watcher.Deleted += delegate { QueueScan(); };
            watcher.Renamed += delegate { QueueScan(); };
            watcher.EnableRaisingEvents = true;
        }
        private void ChooseInputFolder()
        {
            if (converting || deduplicating || extractingCover || analyzingGenres) return;
            using (var dialog = new FolderBrowserDialog())
            {
                dialog.Description = "选择包含 NCM 歌曲的目录（包括子目录）";
                dialog.SelectedPath = InputRoot;
                if (dialog.ShowDialog(DialogOwner) != DialogResult.OK) return;
                try
                {
                    var selected = Path.GetFullPath(dialog.SelectedPath);
                    File.WriteAllText(inputSettingPath, SavePortablePath(selected), new UTF8Encoding(false));
                    InputRoot = selected;
                    ConfigureInputWatcher();
                    tips.SetToolTip(chooseInputButton, InputRoot);
                    ScanSongs(true);
                }
                catch (Exception ex) { ReportTask("选择音乐目录", "失败", ex.Message); }
            }
        }
        private readonly string scriptPath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "convert.ps1");
        private readonly string genreScriptPath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "genre_analysis.py");
        private readonly string profileScriptPath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "profile_fetch.py");
        private readonly string profileAnalysisScriptPath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "profile_analysis.py");
        private readonly string portraitAiScriptPath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "portrait_ai.py");
        private readonly string memoryRecallScriptPath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "memory_recall.py");
        private readonly string chartReportSettingPath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "chart-report-project.txt");
        private string profileDatabasePath { get { return Path.Combine(NeteaseAuth.AccountFolder("Profile"), "netease-profile.json"); } }
        private readonly string outputSettingPath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "output-folder.txt");
        private string playbackHistoryPath { get { return Path.Combine(NeteaseAuth.AccountFolder("Playback"), "play-history.csv"); } }
        private readonly string genreDatabasePath = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), @"NeteaseToolbox\GenreAnalysis\analysis.sqlite3");
        private string pythonPath { get { return FindGenrePython(); } }
        private string launcherPath { get { return Path.Combine(LyricFolder(), "launcher.py"); } }
        private Color background { get { return theme.Background; } }
        private Color panel { get { return theme.Panel; } }
        private Color ink { get { return theme.Ink; } }
        private Color muted { get { return theme.Muted; } }
        private Color accent { get { return theme.Accent; } }
        private readonly Label summary = new Label();
        private readonly Label outputStatus = new Label();
        private readonly Label settingsOutputStatus = new Label();
        private readonly Label lyricStatus = new Label();
        private readonly Button lyricButton = new Button();
        private readonly Button convertButton = new Button();
        private readonly Button deduplicateButton = new Button();
        private readonly Button scanButton = new Button();
        private readonly Button folderButton = new Button();
        private readonly Button chooseOutputButton = new Button();
        private readonly Button resetOutputButton = new Button();
        private readonly Button logButton = new Button();
        private readonly Button playbackHistoryButton = new Button();
        private readonly Label taskStatus = new Label();
        private readonly Button taskMessagesButton = new Button();
        private readonly ListBox taskMessages = new ListBox();
        private Form taskMessagesWindow;
        private readonly ListView songs = new WallpaperListView();
        private readonly ListView convertedSongs = new WallpaperListView();
        private readonly TabControl songTabs = new ThemedTabControl();
        private readonly Button convertSelectedButton = new Button();
        private readonly Button extractCoverButton = new Button();
        private readonly Button analyzeGenresButton = new Button();
        private readonly Button exportGenresButton = new Button();
        private readonly CachedSongTable genreSongs = new CachedSongTable();
        private readonly ListView profileSongs = new WallpaperListView();
        private readonly Button refreshProfileButton = new Button();
        private readonly Label profileStatus = new Label();
        private readonly Button allPortraitButton = new Button();
        private readonly RichTextBox allPortraitText = new WallpaperRichTextBox();
        private readonly RichTextBox recentPortraitText = new WallpaperRichTextBox();
        private readonly ListView recentGenreComparison = new WallpaperListView();
        private readonly WallpaperRichTextBox memoryQuery = new WallpaperRichTextBox { DetectUrls = false };
        private readonly CheckBox memoryLimitDates = new CheckBox();
        private readonly DateTimePicker memoryStartDate = new DateTimePicker();
        private readonly DateTimePicker memoryEndDate = new DateTimePicker();
        private readonly CheckBox memoryUseAi = new CheckBox();
        private readonly Button memorySearchButton = new Button();
        private readonly Label memorySourceStatus = new Label();
        private readonly ListView memoryResults = new WallpaperListView();
        private readonly WallpaperRichTextBox memoryEvidence = new WallpaperRichTextBox { UseUniformFont = true };
        private readonly Dictionary<string, ListViewItem> genreRows = new Dictionary<string, ListViewItem>(StringComparer.OrdinalIgnoreCase);
        private readonly List<Button> navigationButtons = new List<Button>();
        private readonly List<Control> navigationPages = new List<Control>();
        private WallpaperPanel pageHost;
        private WallpaperPanel settingsPage;
        private readonly TextBox log = new TextBox();
        private Form logWindow;
        private readonly ToolTip tips = new ToolTip();
        private readonly System.Windows.Forms.Timer scanTimer = new System.Windows.Forms.Timer();
        private readonly System.Windows.Forms.Timer statusTimer = new System.Windows.Forms.Timer();
        private readonly bool startInBackground;
        private PlaybackRecorder playbackRecorder;
        private NotifyIcon trayIcon;
        private bool allowExit;
        private FileSystemWatcher watcher;
        private readonly List<string> pending = new List<string>();
        private string latestOutputPath;
        private string outputRoot;
        private bool converting;
        private bool deduplicating;
        private bool extractingCover;
        private bool analyzingGenres;
        private bool loadingGenreResults;
        private bool loadingProfile;
        private bool loadingPortrait;
        private bool searchingMemory;
        private bool scanQueued;

        internal static ToolboxForm Instance { get; private set; }
        public static event Action<string, string, string> TaskReported;
        internal event Action<bool, string> LyricStatusChanged;
        internal bool IsWpfHosted { get; private set; }
        internal bool AllowExit { get { return allowExit; } }
        private bool startupTasksInitialized;
        private sealed class HwndWin32Window : IWin32Window
        {
            public IntPtr Handle { get; private set; }
            public HwndWin32Window(IntPtr handle) { Handle = handle; }
        }
        internal IWin32Window DialogOwner
        {
            get
            {
                if (!IsWpfHosted && TopLevel) return this;
                var wpfWindow = System.Windows.Application.Current != null ? System.Windows.Application.Current.MainWindow : null;
                if (wpfWindow != null)
                {
                    var handle = new System.Windows.Interop.WindowInteropHelper(wpfWindow).Handle;
                    if (handle != IntPtr.Zero) return new HwndWin32Window(handle);
                }
                return null;
            }
        }
        internal Control GetPageHost() { return pageHost; }
        internal void SwitchToPage(int index) { NavigateTo(index); }

        internal void OpenLoginDialog() { ShowNeteaseLogin(); }
        internal void InvokeUi(Action action)
        {
            if (IsDisposed || action == null) return;
            if (IsWpfHosted && System.Windows.Application.Current != null)
            {
                var dispatcher = System.Windows.Application.Current.Dispatcher;
                if (dispatcher.CheckAccess()) action();
                else dispatcher.BeginInvoke(action);
                return;
            }
            Control target = pageHost != null && pageHost.IsHandleCreated ? (Control)pageHost : this;
            if (!target.IsHandleCreated) return;
            if (target.InvokeRequired) target.BeginInvoke(action);
            else action();
        }
        internal void InvokeUiAsync(Action action)
        {
            if (IsDisposed || action == null) return;
            if (IsWpfHosted && System.Windows.Application.Current != null)
            {
                System.Windows.Application.Current.Dispatcher.BeginInvoke(action);
                return;
            }
            Control target = pageHost != null && pageHost.IsHandleCreated ? (Control)pageHost : this;
            if (!target.IsHandleCreated) return;
            try { target.BeginInvoke(action); } catch (InvalidOperationException) { }
        }
        internal void HideShellForWpf()
        {
            IsWpfHosted = true;
            TopLevel = false;
            FormBorderStyle = FormBorderStyle.None;
            DoubleBuffered = true;
            SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer, true);
            ClearWallpaper();
            SetTheme(theme == ThemePalette.Light);
            if (pageHost != null)
            {
                pageHost.BackColor = background;
                Controls.Clear();
                pageHost.Dock = DockStyle.Fill;
                Controls.Add(pageHost);
                pageHost.BringToFront();
                ApplyOpaqueSurfaces(pageHost);
            }
        }

        public ToolboxForm(bool startInBackground) : this(startInBackground, false)
        {
        }

        private static readonly Icon AppIcon = LoadAppIcon();

        private static Icon LoadAppIcon()
        {
            try
            {
                using (var stream = typeof(ToolboxForm).Assembly.GetManifestResourceStream("NeteaseToolbox.AppIcon.ico"))
                {
                    if (stream != null) return new Icon(stream);
                }
            }
            catch { }
            return SystemIcons.Application;
        }

        public ToolboxForm(bool startInBackground, bool wpfHosted)
        {
            Instance = this;
            this.IsWpfHosted = wpfHosted;
            this.startInBackground = startInBackground;
            if (wpfHosted)
            {
                TopLevel = false;
                FormBorderStyle = FormBorderStyle.None;
            }
            Text = "网易云工具箱";
            AutoScaleMode = AutoScaleMode.Dpi;
            AutoScaleDimensions = new SizeF(96F, 96F);
            MinimumSize = new Size(780, 600);
            Size = new Size(960, 720);
            StartPosition = FormStartPosition.CenterScreen;
            BackColor = background;
            ForeColor = ink;
            Font = UiFont(9F);
            Icon = AppIcon;
            try
            {
                if (File.Exists(outputSettingPath))
                {
                    var saved = File.ReadAllText(outputSettingPath, Encoding.UTF8).Trim();
                    if (saved.Length > 0) outputRoot = ResolvePortablePath(saved);
                }
            }
            catch { outputRoot = null; }
            try
            {
                if (File.Exists(inputSettingPath)) InputRoot = ResolvePortablePath(File.ReadAllText(inputSettingPath, Encoding.UTF8).Trim());
            }
            catch { InputRoot = ""; }
            if (!Directory.Exists(InputRoot))
            {
                var nearby = Path.GetFullPath(Path.Combine(AppDomain.CurrentDomain.BaseDirectory, @"..\download\VipSongsDownload"));
                InputRoot = Directory.Exists(nearby) ? nearby : "";
            }
            BuildUi();
            LoadTheme();
            LoadCustomAppearance();
            ConfigureTrayIcon();
            UpdateOutputStatus();

            scanTimer.Interval = 1200;
            scanTimer.Tick += delegate { scanTimer.Stop(); if (scanQueued) ScanSongs(); };
            statusTimer.Interval = 4000;
            statusTimer.Tick += delegate { UpdateLyricStatus(); };
            statusTimer.Start();
            ConfigureInputWatcher();
            Shown += delegate
            {
                RunStartupTasks();
                if (startInBackground && !IsWpfHosted)
                    BeginInvoke((Action)(() => Hide()));
            };
        }

        internal async void RunStartupTasks()
        {
            if (startupTasksInitialized || IsDisposed) return;
            startupTasksInitialized = true;
            // Populate the forwarding device list in the background so auto-start can find the
            // saved output without enumerating audio endpoints before the first frame.
            RefreshAudioChoices(false);
            StartPhoneSyncServer(true);
            UpdateLyricStatus();
            await StartLocalPlayback();
            try { EnsureAutoStart(); }
            catch (Exception ex) { ReportTask("播放记录", "失败", "登录启动项注册失败：" + ex.Message); }
            await CheckNeteaseSession();
            try
            {
                var dedupeNotice = await Task.Run(() => DedupeQuarantine.MarkInterruptedOperations());
                if (!string.IsNullOrWhiteSpace(dedupeNotice))
                    ReportTask("工具箱回收站", "部分失败", dedupeNotice);
            }
            catch (Exception ex) { ReportTask("工具箱回收站", "失败", "检查未完成操作失败：" + ex.Message); }
            if (File.Exists(NeteaseAuth.SessionPath)) {
                playbackRecorder = new PlaybackRecorder(playbackHistoryPath, ReportPlaybackMessage);
                try { await playbackRecorder.StartAsync(); }
                catch (Exception ex) { ReportTask("播放记录同步", "失败", ex.Message); }
            }
            if (!startInBackground)
            {
                ScanSongs();
                StartLyrics(true);
            }
            await InitializeDailySignin();
            if (!IsWpfHosted)
            {
                await RefreshPortrait("all");
                await RefreshPortrait("recent");
            }
        }

        private Label Label(string value, int size, bool bold, Color color)
        {
            return new Label {
                Text = value, AutoSize = true, ForeColor = color,
                Font = UiFont(size, bold ? FontStyle.Bold : FontStyle.Regular),
                BackColor = Color.Transparent
            };
        }

        private Button Button(string value, bool primary)
        {
            var button = new Button { Text = value, AutoSize = false, Height = 38 };
            StyleButton(button, primary ? ButtonRole.Primary : ButtonRole.Secondary);
            return button;
        }

        private enum ButtonRole { Primary, Secondary, Navigation, Danger, Ghost }

        private void StyleButton(Button button, ButtonRole role)
        {
            button.FlatStyle = FlatStyle.Flat;
            button.FlatAppearance.BorderSize = role == ButtonRole.Secondary ? 1 : 0;
            button.FlatAppearance.BorderColor = theme.Border;
            button.Cursor = Cursors.Hand;
            button.Font = UiFont(9F,
                role == ButtonRole.Primary || role == ButtonRole.Danger ? FontStyle.Bold : FontStyle.Regular);
            button.ForeColor = role == ButtonRole.Navigation ? muted
                : role == ButtonRole.Primary || role == ButtonRole.Danger ? Color.White : theme.ButtonInk;
            button.BackColor = role == ButtonRole.Primary ? accent
                : role == ButtonRole.Danger ? theme.Danger
                : role == ButtonRole.Navigation ? panel
                : role == ButtonRole.Ghost ? background
                : theme.Secondary;
            button.UseVisualStyleBackColor = false;
            button.AccessibleDescription = role.ToString();
            ApplyButtonAppearance(button, role);
        }

        private Button NavigationButton(string name, string caption, int index)
        {
            var button = new Button {
                Name = name,
                Text = caption,
                Tag = index,
                Width = 138,
                Height = 42,
                TextAlign = ContentAlignment.MiddleLeft,
                Padding = new Padding(16, 0, 0, 0)
            };
            StyleButton(button, ButtonRole.Navigation);
            button.Click += delegate(object sender, EventArgs e) {
                NavigateTo(Convert.ToInt32(((Button)sender).Tag));
            };
            navigationButtons.Add(button);
            return button;
        }

        private void NavigateTo(int index)
        {
            selectedNavigationIndex = index;
            var host = navigationPages.Count == 0 ? null : navigationPages[0].Parent;
            if (host != null) host.SuspendLayout();
            bool freeze = host != null && host.IsHandleCreated;
            if (freeze) SendUiMessage(host.Handle, 0xB, IntPtr.Zero, IntPtr.Zero);
            try {
                navigationPages[index].BringToFront();
                navigationPages[index].Visible = true;
                for (int i = 0; i < navigationPages.Count; i++)
                {
                    if (i != index) navigationPages[i].Visible = false;
                    navigationButtons[i].BackColor = i == index ? theme.Secondary : panel;
                    navigationButtons[i].ForeColor = i == index ? theme.ButtonInk : muted;
                    navigationButtons[i].Font = UiFont(9F,
                        i == index ? FontStyle.Bold : FontStyle.Regular);
                    ApplyButtonAppearance(navigationButtons[i], ButtonRole.Navigation, i == index);
                }
            }
            finally {
                if (host != null) host.ResumeLayout(true);
                if (freeze) { SendUiMessage(host.Handle, 0xB, new IntPtr(1), IntPtr.Zero); host.Invalidate(true); }
            }
            if (index == 0) UpdateConversionSelectionButtons();
            if (index == 7) OnMultiListenPageOpened();
        }

        private void BuildUi()
        {
            Size = new Size(1040, 740);
            var root = new TableLayoutPanel { Name = "AppShell", Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 3 };
            root.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            root.RowStyles.Add(new RowStyle(SizeType.Absolute, 108));
            root.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
            root.RowStyles.Add(new RowStyle(SizeType.Absolute, 46));
            Controls.Add(root);

            var topbar = new WallpaperPanel { Name = "TopBar", Dock = DockStyle.Fill, BackColor = background };
            var title = Label("网易云工具箱", 16, true, ink);
            title.Name = "AppTitle";
            title.Location = new Point(22, 7);
            topbar.Controls.Add(title);
            var version = Label("v" + Assembly.GetExecutingAssembly().GetName().Version, 8, false, muted);
            version.Name = "VersionLabel";
            version.Location = new Point(24, 52);
            topbar.Controls.Add(version);
            topbar.Layout += delegate { version.Top = title.Bottom + 2; };
            lyricStatus.AutoSize = true;
            lyricStatus.ForeColor = muted;
            lyricStatus.Name = "LyricStatus";
            lyricStatus.Text = "正在检查…";
            lyricStatus.Location = new Point(560, 45);
            topbar.Controls.Add(lyricStatus);
            lyricButton.Name = "LyricToggleButton";
            lyricButton.Text = "开启";
            lyricButton.Size = new Size(82, 36);
            lyricButton.Location = new Point(706, 35);
            StyleButton(lyricButton, ButtonRole.Secondary);
            lyricButton.Click += delegate { ToggleLyrics(); };
            topbar.Controls.Add(lyricButton);
            logButton.Name = "LogButton";
            logButton.Text = "运行日志";
            logButton.Size = new Size(96, 36);
            logButton.Location = new Point(844, 35);
            StyleButton(logButton, ButtonRole.Ghost);
            logButton.Click += delegate { ShowLogWindow(); };
            topbar.Controls.Add(logButton);
            playbackHistoryButton.Name = "PlaybackHistoryButton";
            playbackHistoryButton.Text = "播放记录";
            playbackHistoryButton.Size = new Size(96, 36);
            playbackHistoryButton.Location = new Point(0, 35);
            StyleButton(playbackHistoryButton, ButtonRole.Secondary);
            playbackHistoryButton.Click += delegate { OpenPlaybackHistory(); };
            tips.SetToolTip(playbackHistoryButton, "自动记录当前播放歌曲；点击打开本地记录文件夹");
            topbar.Controls.Add(playbackHistoryButton);
            topbar.Resize += delegate {
                int buttonTop = Math.Max(0, (topbar.ClientSize.Height - logButton.Height) / 2);
                lyricButton.Top = buttonTop;
                playbackHistoryButton.Top = buttonTop;
                logButton.Top = buttonTop;
                lyricStatus.Top = Math.Max(0, (topbar.ClientSize.Height - lyricStatus.PreferredHeight) / 2);
                logButton.Left = topbar.ClientSize.Width - logButton.Width - 20;
                playbackHistoryButton.Left = logButton.Left - playbackHistoryButton.Width - 8;
                lyricButton.Left = playbackHistoryButton.Left - lyricButton.Width - 8;
                lyricStatus.Left = Math.Max(260, lyricButton.Left - lyricStatus.PreferredWidth - 18);
            };
            root.Controls.Add(topbar, 0, 0);

            var body = new TableLayoutPanel { Name = "AppBody", Dock = DockStyle.Fill, ColumnCount = 2, RowCount = 1 };
            body.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 170));
            body.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            body.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
            root.Controls.Add(body, 0, 1);

            var sidebar = new WallpaperPanel { Name = "Sidebar", Dock = DockStyle.Fill, BackColor = panel, AutoScroll = true };
            var navigationTitle = Label("功能", 9, true, muted);
            navigationTitle.Location = new Point(18, 20);
            sidebar.Controls.Add(navigationTitle);
            var conversionNavigation = NavigationButton("ConversionNavigation", "音乐转换", 0);
            var genreNavigation = NavigationButton("GenreNavigation", "曲风分析", 1);
            var profileNavigation = NavigationButton("ProfileNavigation", "听歌画像", 2);
            var memoryNavigation = NavigationButton("MemoryRecallNavigation", "音乐记忆", 3);
            var settingsNavigation = NavigationButton("SettingsNavigation", "设置与维护", 4);
            var audioNavigation = NavigationButton("AudioForwardingNavigation", "音频转麦克风", 5);
            var signinNavigation = NavigationButton("DailySigninNavigation", "每日签到", 6);
            var multiListenNavigation = NavigationButton("MultiListenNavigation", "多人一起听", 7);
            conversionNavigation.Location = new Point(16, 48);
            genreNavigation.Location = new Point(16, 96);
            profileNavigation.Location = new Point(16, 144);
            memoryNavigation.Location = new Point(16, 192);
            settingsNavigation.Location = new Point(16, 240);
            sidebar.Controls.Add(conversionNavigation);
            sidebar.Controls.Add(genreNavigation);
            sidebar.Controls.Add(profileNavigation);
            sidebar.Controls.Add(memoryNavigation);
            sidebar.Controls.Add(settingsNavigation);
            audioNavigation.Location = new Point(16, 288);
            sidebar.Controls.Add(audioNavigation);
            signinNavigation.Location = new Point(16, 336);
            sidebar.Controls.Add(signinNavigation);
            multiListenNavigation.Location = new Point(16, 384);
            sidebar.Controls.Add(multiListenNavigation);
            BuildAccountUi(sidebar);
            sidebar.Layout += delegate {
                int width = Math.Max(110, Math.Min(138, sidebar.ClientSize.Width - 32));
                foreach (var button in navigationButtons) if (button.Width != width) button.Width = width;
                if (accountButton.Width != width) accountButton.Width = width;
                accountStatus.Width = Math.Max(106, sidebar.ClientSize.Width - 36);
                LayoutAccountUi();
            };
            body.Controls.Add(sidebar, 0, 0);

            pageHost = new WallpaperPanel { Name = "PageHost", Dock = DockStyle.Fill, BackColor = background };
            body.Controls.Add(pageHost, 1, 0);

            var conversionPage = new TableLayoutPanel { Name = "ConversionPage", Dock = DockStyle.Fill, Padding = new Padding(24, 18, 24, 18), ColumnCount = 1, RowCount = 3 };
            conversionPage.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            conversionPage.RowStyles.Add(new RowStyle(SizeType.Absolute, 88));
            conversionPage.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
            conversionPage.RowStyles.Add(new RowStyle(SizeType.Absolute, 62));
            var songHeader = new WallpaperPanel { Name = "ConversionHeader", Dock = DockStyle.Fill };
            var songTitle = Label("本地歌曲", 13, true, ink);
            songTitle.Location = new Point(3, 4);
            songHeader.Controls.Add(songTitle);
            summary.AutoSize = true;
            summary.ForeColor = muted;
            summary.Location = new Point(4, 44);
            summary.Text = "正在扫描…";
            songHeader.Controls.Add(summary);
            songHeader.Layout += delegate { summary.Top = songTitle.Bottom + 4; };
            scanButton.Name = "ScanButton";
            scanButton.Text = "重新扫描";
            scanButton.Size = new Size(104, 38);
            scanButton.Location = new Point(510, 8);
            StyleButton(scanButton, ButtonRole.Secondary);
            scanButton.Click += delegate { ScanSongs(true); };
            songHeader.Controls.Add(scanButton);
            chooseInputButton.Name = "ChooseInputButton";
            chooseInputButton.Text = "选择音乐目录";
            chooseInputButton.Size = new Size(132, 38);
            chooseInputButton.Top = 8;
            StyleButton(chooseInputButton, ButtonRole.Secondary);
            chooseInputButton.Click += delegate { ChooseInputFolder(); };
            songHeader.Controls.Add(chooseInputButton);
            tips.SetToolTip(chooseInputButton, InputRoot);
            convertButton.Name = "ConvertButton";
            convertButton.Text = "转换全部新歌";
            convertButton.Size = new Size(154, 40);
            convertButton.Location = new Point(624, 7);
            StyleButton(convertButton, ButtonRole.Primary);
            convertButton.Click += async delegate { await ConvertFiles(pending.ToArray()); };
            songHeader.Controls.Add(convertButton);
            songHeader.Resize += delegate {
                convertButton.Left = songHeader.ClientSize.Width - convertButton.Width;
                scanButton.Left = convertButton.Left - scanButton.Width - 10;
                chooseInputButton.Left = scanButton.Left - chooseInputButton.Width - 10;
            };
            conversionPage.Controls.Add(songHeader, 0, 0);

            ConfigureSongList(songs, "所在目录");
            ConfigureSongList(convertedSongs, "输出目录");
            songs.Name = "PendingSongList";
            convertedSongs.Name = "ConvertedSongList";
            songs.SelectedIndexChanged += delegate { UpdateConversionSelectionButtons(); };
            convertedSongs.SelectedIndexChanged += delegate { UpdateConversionSelectionButtons(); };
            songTabs.Name = "ConversionTabs";
            songTabs.Dock = DockStyle.Fill;
            songTabs.SelectedIndexChanged += delegate { UpdateConversionSelectionButtons(); };
            var pendingTab = new TabPage("待转换歌曲") { Name = "PendingSongsTab", BackColor = panel };
            var convertedTab = new TabPage("已转换歌曲") { Name = "ConvertedSongsTab", BackColor = panel };
            pendingTab.Controls.Add(songs);
            convertedTab.Controls.Add(convertedSongs);
            songTabs.TabPages.Add(pendingTab);
            songTabs.TabPages.Add(convertedTab);
            conversionPage.Controls.Add(songTabs, 0, 1);

            var conversionFooter = new WallpaperPanel { Name = "ConversionFooter", Dock = DockStyle.Fill };
            outputStatus.AutoSize = false;
            outputStatus.ForeColor = muted;
            outputStatus.Location = new Point(4, 20);
            outputStatus.Size = new Size(460, 22);
            outputStatus.AutoEllipsis = true;
            conversionFooter.Controls.Add(outputStatus);
            convertSelectedButton.Name = "ConvertSelectedButton";
            convertSelectedButton.Text = "转换选中歌曲";
            convertSelectedButton.Size = new Size(138, 40);
            convertSelectedButton.Location = new Point(640, 9);
            StyleButton(convertSelectedButton, ButtonRole.Primary);
            convertSelectedButton.Click += async delegate { await ConvertSelectedSongs(); };
            conversionFooter.Controls.Add(convertSelectedButton);
            folderButton.Text = "打开最新输出";
            folderButton.Name = "OpenOutputButton";
            folderButton.Size = new Size(128, 38);
            folderButton.Location = new Point(510, 10);
            StyleButton(folderButton, ButtonRole.Secondary);
            folderButton.Click += delegate {
                if (latestOutputPath != null && File.Exists(latestOutputPath))
                {
                    Process.Start("explorer.exe", "/select," + Quoted(latestOutputPath));
                    WriteLog("已打开转换结果：" + latestOutputPath);
                }
            };
            conversionFooter.Controls.Add(folderButton);
            extractCoverButton.Name = "ExtractCoverButton";
            extractCoverButton.Text = "提取选中封面";
            extractCoverButton.Size = new Size(132, 38);
            extractCoverButton.Location = new Point(648, 10);
            StyleButton(extractCoverButton, ButtonRole.Secondary);
            extractCoverButton.Click += async delegate { await ExtractSelectedCover(); };
            conversionFooter.Controls.Add(extractCoverButton);
            conversionFooter.Resize += delegate {
                convertSelectedButton.Left = conversionFooter.ClientSize.Width - convertSelectedButton.Width;
                extractCoverButton.Left = convertSelectedButton.Left - extractCoverButton.Width - 10;
                folderButton.Left = extractCoverButton.Left - folderButton.Width - 10;
                outputStatus.Width = Math.Max(180, folderButton.Left - 18);
            };
            conversionPage.Controls.Add(conversionFooter, 0, 2);

            var genrePage = new TableLayoutPanel { Name = "GenrePage", Dock = DockStyle.Fill, Padding = new Padding(24, 18, 24, 18), ColumnCount = 1, RowCount = 3 };
            genrePage.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            genrePage.RowStyles.Add(new RowStyle(SizeType.Absolute, 88));
            genrePage.RowStyles.Add(new RowStyle(SizeType.Absolute, 54));
            genrePage.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
            var genreHeader = new WallpaperPanel { Name = "GenreHeader", Dock = DockStyle.Fill };
            var genreTitle = Label("曲风分析", 13, true, ink);
            genreTitle.Location = new Point(3, 4);
            genreHeader.Controls.Add(genreTitle);
            var genreDescription = Label("读取歌曲百科标签；双击歌曲可以手动修正。", 9, false, muted);
            genreDescription.Location = new Point(4, 44);
            genreHeader.Controls.Add(genreDescription);
            genreHeader.Layout += delegate { genreDescription.Top = genreTitle.Bottom + 4; };
            analyzeGenresButton.Text = "分析曲风";
            analyzeGenresButton.Name = "AnalyzeGenresButton";
            analyzeGenresButton.Size = new Size(112, 40);
            analyzeGenresButton.Location = new Point(640, 7);
            StyleButton(analyzeGenresButton, ButtonRole.Primary);
            analyzeGenresButton.Click += async delegate { await AnalyzeGenres(); };
            genreHeader.Controls.Add(analyzeGenresButton);
            exportGenresButton.Text = "导出 CSV";
            exportGenresButton.Name = "ExportGenresButton";
            exportGenresButton.Size = new Size(96, 40);
            exportGenresButton.Location = new Point(762, 7);
            StyleButton(exportGenresButton, ButtonRole.Secondary);
            exportGenresButton.Click += async delegate { await ExportGenres(); };
            genreHeader.Controls.Add(exportGenresButton);
            genreHeader.Resize += delegate {
                exportGenresButton.Left = genreHeader.ClientSize.Width - exportGenresButton.Width;
                analyzeGenresButton.Left = exportGenresButton.Left - analyzeGenresButton.Width - 10;
            };
            genrePage.Controls.Add(genreHeader, 0, 0);
            var genreToolbar = new WallpaperPanel { Name = "GenreToolbar", Dock = DockStyle.Fill };
            var genreToolbarHint = Label("按情绪或曲风筛选红心歌曲，确认后创建新歌单。", 9, false, muted);
            genreToolbarHint.Name = "TagPlaylistHint";
            genreToolbarHint.Location = new Point(4, 16);
            genreToolbar.Controls.Add(genreToolbarHint);
            ConfigureTagPlaylistButton(genreToolbar);
            genrePage.Controls.Add(genreToolbar, 0, 1);
            ConfigureGenreList();
            genreSongs.Name = "GenreSongList";
            genrePage.Controls.Add(genreSongs, 0, 2);

            var profilePage = new WallpaperPanel { Name = "ProfilePage", Dock = DockStyle.Fill, Padding = new Padding(24, 18, 24, 18) };
            ConfigureProfileTab(profilePage);
            var memoryPage = BuildMemoryRecallPage();

            settingsPage = new WallpaperPanel { Name = "SettingsPage", Dock = DockStyle.Fill, AutoScroll = true, Padding = new Padding(28, 22, 28, 22) };
            var settingsTitle = Label("设置与维护", 13, true, ink);
            settingsTitle.Location = new Point(28, 22);
            settingsPage.Controls.Add(settingsTitle);
            var settingsDescription = Label("集中管理低频设置和可能影响文件的维护操作。", 9, false, muted);
            settingsDescription.Location = new Point(29, 54);
            settingsPage.Controls.Add(settingsDescription);
            var themeRow = BuildThemeSelector();
            settingsPage.Controls.Add(themeRow);
            var appearanceRow = BuildCustomAppearance();
            if (IsWpfHosted) appearanceRow.Visible = false;
            settingsPage.Controls.Add(appearanceRow);
            var outputCard = new WallpaperPanel { Name = "OutputSettingsCard", BackColor = panel, Location = new Point(28, 90), Size = new Size(760, 128), Anchor = AnchorStyles.Left | AnchorStyles.Right | AnchorStyles.Top };
            var outputTitle = Label("转换输出目录", 11, true, ink);
            outputTitle.Location = new Point(18, 16);
            outputCard.Controls.Add(outputTitle);
            settingsOutputStatus.AutoSize = false;
            settingsOutputStatus.ForeColor = muted;
            settingsOutputStatus.Location = new Point(18, 47);
            settingsOutputStatus.Size = new Size(500, 24);
            settingsOutputStatus.AutoEllipsis = true;
            outputCard.Controls.Add(settingsOutputStatus);
            chooseOutputButton.Name = "ChooseOutputButton";
            chooseOutputButton.Text = "选择输出目录";
            chooseOutputButton.Size = new Size(132, 38);
            StyleButton(chooseOutputButton, ButtonRole.Secondary);
            chooseOutputButton.Click += delegate { ChooseOutputFolder(); };
            outputCard.Controls.Add(chooseOutputButton);
            resetOutputButton.Name = "ResetOutputButton";
            resetOutputButton.Text = "恢复默认";
            resetOutputButton.Size = new Size(100, 38);
            StyleButton(resetOutputButton, ButtonRole.Ghost);
            resetOutputButton.Click += delegate { ResetOutputFolder(); };
            outputCard.Controls.Add(resetOutputButton);
            outputCard.Resize += delegate {
                settingsOutputStatus.Width = Math.Max(180, outputCard.ClientSize.Width - 36);
            };
            chooseOutputButton.Location = new Point(18, 76);
            resetOutputButton.Location = new Point(160, 76);
            settingsOutputStatus.Width = Math.Max(180, outputCard.ClientSize.Width - 36);
            settingsPage.Controls.Add(outputCard);
            var maintenanceCard = new WallpaperPanel { Name = "MaintenanceCard", BackColor = panel, Location = new Point(28, 234), Size = new Size(760, 126), Anchor = AnchorStyles.Left | AnchorStyles.Right | AnchorStyles.Top };
            var maintenanceTitle = Label("歌曲维护", 11, true, ink);
            maintenanceTitle.Location = new Point(18, 16);
            maintenanceCard.Controls.Add(maintenanceTitle);
            var maintenanceDescription = Label("扫描重复歌曲，先预览再决定；确认后移入工具箱回收站，可恢复。", 9, false, muted);
            maintenanceDescription.Location = new Point(18, 47);
            maintenanceCard.Controls.Add(maintenanceDescription);
            deduplicateButton.Name = "DeduplicateButton";
            deduplicateButton.Text = "曲库安全去重";
            deduplicateButton.Size = new Size(180, 40);
            deduplicateButton.Location = new Point(18, 76);
            StyleButton(deduplicateButton, ButtonRole.Danger);
            deduplicateButton.Click += delegate { OpenSafeDedupe(); };
            tips.SetToolTip(deduplicateButton, "扫描、预览并确认后才把选中的重复文件移入工具箱回收站。");
            maintenanceCard.Controls.Add(deduplicateButton);
            var trashButton = new Button { Name = "TrashButton", Text = "工具箱回收站", Size = new Size(160, 40), Location = new Point(210, 76) };
            StyleButton(trashButton, ButtonRole.Secondary);
            trashButton.Click += delegate { OpenTrash(); };
            tips.SetToolTip(trashButton, "查看、恢复或永久清理由安全去重移入隔离目录的文件。");
            maintenanceCard.Controls.Add(trashButton);
            settingsPage.Controls.Add(maintenanceCard);
            var aiSettingsButton = new Button { Name = "PortraitAiSettingsButton", Text = "AI 画像接口与提示词", Size = new Size(190, 40) };
            StyleButton(aiSettingsButton, ButtonRole.Secondary);
            aiSettingsButton.Click += delegate { ShowPortraitAiSettings(); };
            settingsPage.Controls.Add(aiSettingsButton);
            var portableButtons = new FlowLayoutPanel { Name = "PortableSettings", Height = 52, Width = 740 };
            var clientFolderButton = Button("网易云安装目录", false);
            clientFolderButton.Name = "ChooseClientFolderButton";
            clientFolderButton.Width = 160;
            clientFolderButton.Click += delegate { ChooseClientFolder(); };
            var environmentButton = Button("检查运行环境", false);
            environmentButton.Name = "EnvironmentCheckButton";
            environmentButton.Width = 150;
            environmentButton.Click += async delegate { await CheckPortableEnvironment(); };
            portableButtons.Controls.Add(clientFolderButton);
            portableButtons.Controls.Add(environmentButton);
            var setupEnvironmentButton = Button("配置登录运行环境", false);
            setupEnvironmentButton.Width = 170;
            setupEnvironmentButton.Name = "SetupEnvironmentButton";
            setupEnvironmentButton.Click += async delegate { await SetupPortableEnvironment(); };
            portableButtons.Controls.Add(setupEnvironmentButton);
            settingsPage.Controls.Add(portableButtons);
            settingsPage.Layout += delegate {
                settingsDescription.Top = settingsTitle.Bottom + 4;
                themeRow.Top = settingsDescription.Bottom + 12;
                appearanceRow.Top = themeRow.Bottom + 8;
                outputCard.Top = (appearanceRow.Visible ? appearanceRow.Bottom : themeRow.Bottom) + 12;
                maintenanceCard.Top = outputCard.Bottom + 16;
                aiSettingsButton.Location = new Point(28, maintenanceCard.Bottom + 18);
                portableButtons.Location = new Point(28, aiSettingsButton.Bottom + 12);
            };

            navigationPages.Add(conversionPage);
            navigationPages.Add(genrePage);
            navigationPages.Add(profilePage);
            navigationPages.Add(memoryPage);
            navigationPages.Add(settingsPage);
            var audioPage = BuildAudioForwardingPage();
            navigationPages.Add(audioPage);
            pageHost.Controls.Add(audioPage);
            var signinPage = BuildDailySigninPage();
            navigationPages.Add(signinPage);
            pageHost.Controls.Add(signinPage);
            multiListenPage = BuildMultiListenPage();
            navigationPages.Add(multiListenPage);
            pageHost.Controls.Add(multiListenPage);
            pageHost.Controls.Add(settingsPage);
            pageHost.Controls.Add(memoryPage);
            pageHost.Controls.Add(profilePage);
            pageHost.Controls.Add(genrePage);
            pageHost.Controls.Add(conversionPage);
            NavigateTo(0);

            var taskBar = new WallpaperPanel { Name = "TaskStatusBar", Dock = DockStyle.Fill, BackColor = panel };
            taskStatus.Name = "TaskStatus";
            taskStatus.Text = "任务状态：就绪";
            taskStatus.ForeColor = muted;
            taskStatus.AutoEllipsis = true;
            taskStatus.AutoSize = false;
            taskStatus.Location = new Point(22, 13);
            taskStatus.Height = 24;
            taskBar.Controls.Add(taskStatus);
            taskMessagesButton.Name = "TaskMessagesButton";
            taskMessagesButton.Text = "任务消息";
            taskMessagesButton.Size = new Size(104, 32);
            StyleButton(taskMessagesButton, ButtonRole.Ghost);
            taskMessagesButton.Location = new Point(850, 7);
            taskMessagesButton.Click += delegate { ShowTaskMessagesWindow(); };
            taskBar.Controls.Add(taskMessagesButton);
            taskBar.Resize += delegate {
                taskMessagesButton.Left = taskBar.ClientSize.Width - taskMessagesButton.Width - 20;
                taskStatus.Width = Math.Max(180, taskMessagesButton.Left - taskStatus.Left - 12);
            };
            root.Controls.Add(taskBar, 0, 2);
        }

        private Control BuildMemoryRecallPage()
        {
            var page = new TableLayoutPanel {
                Name = "MemoryRecallPage", Dock = DockStyle.Fill, Padding = new Padding(24, 18, 24, 18),
                ColumnCount = 1, RowCount = 3
            };
            page.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            page.RowStyles.Add(new RowStyle(SizeType.Absolute, 60));
            page.RowStyles.Add(new RowStyle(SizeType.Absolute, 184));
            page.RowStyles.Add(new RowStyle(SizeType.Percent, 100));

            var header = new WallpaperPanel { Name = "MemoryRecallHeader", Dock = DockStyle.Fill };
            var title = Label("音乐记忆考古", 13, true, ink);
            title.Location = new Point(3, 0);
            header.Controls.Add(title);
            memorySourceStatus.Name = "MemoryRecallSourceStatus";
            memorySourceStatus.AutoEllipsis = true;
            memorySourceStatus.ForeColor = muted;
            memorySourceStatus.Location = new Point(4, 32);
            memorySourceStatus.Size = new Size(650, 22);
            memorySourceStatus.Text = "本机读取播放记录和歌词，按播放时间检索；不读取位置或照片数据。";
            header.Controls.Add(memorySourceStatus);
            page.Controls.Add(header, 0, 0);

            var searchPanel = new WallpaperPanel { Name = "MemoryRecallSearchPanel", Dock = DockStyle.Fill };
            memoryQuery.Name = "MemoryRecallQuery";
            memoryQuery.Multiline = true;
            memoryQuery.ScrollBars = RichTextBoxScrollBars.Vertical;
            memoryQuery.Location = new Point(0, 0);
            memoryQuery.Size = new Size(620, 58);
            memoryQuery.Anchor = AnchorStyles.Left | AnchorStyles.Top | AnchorStyles.Right;
            memoryQuery.Font = UiFont(10F);
            memoryQuery.BackColor = panel;
            memoryQuery.ForeColor = ink;
            memoryQuery.BorderStyle = BorderStyle.None;
            memoryQuery.AccessibleName = "音乐记忆检索描述";
            tips.SetToolTip(memoryQuery, "可输入日期、时段、歌名、艺人、专辑或记得的歌词片段。");
            memoryQuery.KeyDown += async delegate(object sender, KeyEventArgs e) {
                if (e.Control && e.KeyCode == Keys.Enter) { e.SuppressKeyPress = true; await SearchMemoryRecall(); }
            };
            searchPanel.Controls.Add(memoryQuery);
            var hint = Label("例如：9月27日中午、昨晚手机上听的歌，或记得的歌词。", 8, false, muted);
            hint.Location = new Point(3, 61);
            searchPanel.Controls.Add(hint);

            memoryLimitDates.Name = "MemoryRecallLimitDates";
            memoryLimitDates.Text = "限定日期";
            memoryLimitDates.AutoSize = true;
            memoryLimitDates.ForeColor = ink;
            memoryLimitDates.Margin = new Padding(0, 5, 12, 0);
            memoryLimitDates.CheckedChanged += delegate {
                memoryStartDate.Enabled = memoryLimitDates.Checked;
                memoryEndDate.Enabled = memoryLimitDates.Checked;
            };
            memoryStartDate.Name = "MemoryRecallStartDate";
            memoryStartDate.Format = DateTimePickerFormat.Short;
            memoryStartDate.Enabled = false;
            memoryStartDate.Width = 140;
            memoryStartDate.Margin = new Padding(0, 2, 10, 0);
            var toLabel = Label("至", 9, false, muted);
            toLabel.Margin = new Padding(0, 6, 10, 0);
            memoryEndDate.Name = "MemoryRecallEndDate";
            memoryEndDate.Format = DateTimePickerFormat.Short;
            memoryEndDate.Enabled = false;
            memoryEndDate.Width = 140;
            memoryEndDate.Margin = new Padding(0, 2, 0, 0);
            var dateRangeRow = new FlowLayoutPanel {
                Name = "MemoryRecallDateRangeRow",
                Location = new Point(2, 83),
                Size = new Size(510, 36),
                FlowDirection = FlowDirection.LeftToRight,
                WrapContents = false,
                BackColor = Color.Transparent,
                Anchor = AnchorStyles.Left | AnchorStyles.Top | AnchorStyles.Right
            };
            dateRangeRow.Controls.Add(memoryLimitDates);
            dateRangeRow.Controls.Add(memoryStartDate);
            dateRangeRow.Controls.Add(toLabel);
            dateRangeRow.Controls.Add(memoryEndDate);
            searchPanel.Controls.Add(dateRangeRow);

            memoryUseAi.Name = "MemoryRecallUseAi";
            memoryUseAi.Text = "云端 AI 排序（发送检索描述、歌曲信息和短歌词）";
            memoryUseAi.AutoSize = true;
            memoryUseAi.ForeColor = muted;
            memoryUseAi.Location = new Point(2, 122);
            tips.SetToolTip(memoryUseAi, "默认关闭。仅勾选后才会把本次描述和有限候选发给 AI 画像设置中的服务；不会发送照片、精确坐标或运动数据。");
            searchPanel.Controls.Add(memoryUseAi);

            memorySearchButton.Name = "MemoryRecallSearchButton";
            memorySearchButton.Text = "开始检索";
            memorySearchButton.Size = new Size(116, 38);
            memorySearchButton.Location = new Point(680, 118);
            memorySearchButton.Anchor = AnchorStyles.Right | AnchorStyles.Top;
            StyleButton(memorySearchButton, ButtonRole.Primary);
            memorySearchButton.Click += async delegate { await SearchMemoryRecall(); };
            searchPanel.Controls.Add(memorySearchButton);
            searchPanel.Resize += delegate {
                memoryQuery.Width = Math.Max(220, searchPanel.ClientSize.Width - 4);
                memorySearchButton.Left = searchPanel.ClientSize.Width - memorySearchButton.Width;
            };
            page.Controls.Add(searchPanel, 0, 1);

            var split = new SplitContainer {
                Name = "MemoryRecallResultsSplit", Size = new Size(800, 320), Dock = DockStyle.Fill, Orientation = Orientation.Horizontal,
                SplitterDistance = 190, BackColor = background, Panel1MinSize = 80, Panel2MinSize = 60
            };
            memoryResults.Name = "MemoryRecallResults";
            memoryResults.Dock = DockStyle.Fill;
            memoryResults.View = View.Details;
            memoryResults.FullRowSelect = true;
            memoryResults.MultiSelect = false;
            memoryResults.HideSelection = false;
            memoryResults.BackColor = panel;
            memoryResults.ForeColor = ink;
            memoryResults.BorderStyle = BorderStyle.None;
            memoryResults.Columns.Add("播放时间", 142);
            memoryResults.Columns.Add("歌曲", 210);
            memoryResults.Columns.Add("艺人", 150);
            memoryResults.Columns.Add("本机证据", 260);
            memoryResults.SelectedIndexChanged += delegate { ShowMemoryEvidence(); };
            ConfigureSongNavigation(memoryResults);
            split.Panel1.Controls.Add(memoryResults);
            memoryEvidence.Name = "MemoryRecallEvidence";
            memoryEvidence.Dock = DockStyle.Fill;
            memoryEvidence.ReadOnly = true;
            memoryEvidence.Font = UiFont(10F);
            memoryEvidence.DetectUrls = false;
            memoryEvidence.ScrollBars = RichTextBoxScrollBars.Vertical;
            memoryEvidence.BackColor = panel;
            memoryEvidence.ForeColor = ink;
            memoryEvidence.BorderStyle = BorderStyle.None;
            memoryEvidence.Text = "检索结果会在这里显示播放时间和歌词命中。";
            split.Panel2.Controls.Add(memoryEvidence);
            page.Controls.Add(split, 0, 2);
            return page;
        }

        private async Task SearchMemoryRecall()
        {
            if (searchingMemory) return;
            if (string.IsNullOrWhiteSpace(memoryQuery.Text) && !memoryLimitDates.Checked)
            {
                memoryEvidence.Text = "输入日期、时段、歌名、艺人或记得的歌词片段，也可以勾选日期范围后浏览那段时间的播放记录。";
                memoryQuery.Focus();
                return;
            }
            if (memoryLimitDates.Checked && memoryEndDate.Value.Date < memoryStartDate.Value.Date)
            {
                memoryEvidence.Text = "日期范围无效：结束日期早于开始日期。";
                return;
            }

            searchingMemory = true;
            memorySearchButton.Enabled = false;
            memorySourceStatus.Text = "正在本机读取播放记录和歌词…";
            ReportTask("音乐记忆考古", "开始", "正在检索本机播放记录及歌词。");
            try
            {
                var serializer = new JavaScriptSerializer { MaxJsonLength = 8 * 1024 * 1024 };
                var request = new Dictionary<string, object> {
                    { "query", memoryQuery.Text },
                    { "playbackPath", playbackHistoryPath },
                    { "musicRoots", new[] { InputRoot, outputRoot }.Where(path => !string.IsNullOrEmpty(path)).Distinct().ToArray() },
                    { "limit", 40 }
                };
                if (memoryLimitDates.Checked)
                {
                    request["startDate"] = memoryStartDate.Value.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
                    request["endDate"] = memoryEndDate.Value.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
                }
                string output = await Task.Run(() => RunMemoryRecallCommand(serializer.Serialize(request)));
                var response = serializer.Deserialize<Dictionary<string, object>>(output);
                if (response == null) throw new InvalidOperationException("本机检索程序返回了空结果。");
                if (response.ContainsKey("error")) throw new InvalidOperationException(Convert.ToString(response["error"]));

                var results = response.ContainsKey("results") ? response["results"] as System.Collections.ArrayList : null;
                if (results == null) results = new System.Collections.ArrayList();
                UpdateMemoryRecallSummary(response);
                PopulateMemoryResults(results);

                bool aiFailed = false;
                bool aiSucceeded = false;
                string aiFailure = null;
                if (memoryUseAi.Checked && results.Count > 0)
                {
                    memorySourceStatus.Text += "  正在请求已配置模型排序候选…";
                    try
                    {
                        var candidates = new List<Dictionary<string, object>>();
                        foreach (Dictionary<string, object> item in results)
                        {
                            if (candidates.Count >= 20) break;
                            candidates.Add(new Dictionary<string, object> {
                                { "key", RecallValue(item, "key") },
                                { "title", RecallValue(item, "title") },
                                { "artist", RecallValue(item, "artist") },
                                { "lyricExcerpt", RecallValue(item, "lyricExcerpt") }
                            });
                        }
                        string rankOutput = await Task.Run(() => RunPortraitAiSettingsCommand("rank-memory", serializer.Serialize(new {
                            consent = true, question = memoryQuery.Text, candidates = candidates
                        })));
                        var rankResponse = serializer.Deserialize<Dictionary<string, object>>(rankOutput);
                        var orderedKeys = rankResponse != null && rankResponse.ContainsKey("keys")
                            ? rankResponse["keys"] as System.Collections.ArrayList : null;
                        if (orderedKeys == null) throw new InvalidOperationException("模型没有返回有效的候选顺序。");
                        var rank = new Dictionary<string, int>(StringComparer.Ordinal);
                        for (int i = 0; i < orderedKeys.Count; i++)
                        {
                            var key = Convert.ToString(orderedKeys[i]);
                            if (!rank.ContainsKey(key)) rank[key] = i;
                        }
                        var sorted = results.Cast<Dictionary<string, object>>()
                            .OrderBy(item => rank.ContainsKey(RecallValue(item, "key")) ? rank[RecallValue(item, "key")] : int.MaxValue)
                            .ThenByDescending(item => Convert.ToInt32(item.ContainsKey("score") ? item["score"] : 0))
                            .ThenByDescending(item => RecallValue(item, "playedAt"))
                            .ToList();
                        results = new System.Collections.ArrayList(sorted);
                        PopulateMemoryResults(results);
                        aiSucceeded = true;
                    }
                    catch (Exception ex)
                    {
                        aiFailed = true;
                        aiFailure = ex.Message;
                    }
                }

                var warnings = response.ContainsKey("warnings") ? response["warnings"] as System.Collections.ArrayList : null;
                int warningCount = warnings == null ? 0 : warnings.Count;
                if (aiFailed)
                {
                    memorySourceStatus.Text += "  AI 排序失败，本机结果仍可用。";
                    ReportTask("音乐记忆考古", "部分失败", string.Format("本机检索完成，找到 {0} 条候选；AI 排序失败：{1}", results.Count, aiFailure));
                }
                else if (warningCount > 0)
                {
                    var warningText = string.Join("；", warnings.Cast<object>().Select(value => Convert.ToString(value)).Take(3));
                    memoryEvidence.Text = memoryEvidence.Text + "\r\n\r\n数据源提示：" + warningText;
                    ReportTask("音乐记忆考古", "部分失败", string.Format("找到 {0} 条候选；有 {1} 条数据源提示，请查看结果说明。", results.Count, warningCount));
                }
                else
                {
                    ReportTask("音乐记忆考古", "完成", string.Format("本机检索完成，找到 {0} 条候选{1}。", results.Count,
                        aiSucceeded ? "；已按模型语义调整顺序" : ""));
                }
            }
            catch (Exception ex)
            {
                memorySourceStatus.Text = "检索失败：" + ex.Message;
                memoryEvidence.Text = "检索失败：" + ex.Message;
                ReportTask("音乐记忆考古", "失败", ex.Message);
            }
            finally
            {
                searchingMemory = false;
                memorySearchButton.Enabled = true;
            }
        }

        private string RunMemoryRecallCommand(string requestJson)
        {
            if (!File.Exists(memoryRecallScriptPath))
                throw new FileNotFoundException("找不到音乐记忆检索脚本。", memoryRecallScriptPath);
            var info = new ProcessStartInfo(FindGenrePython()) {
                Arguments = Quoted(memoryRecallScriptPath) + " search",
                WorkingDirectory = AppDomain.CurrentDomain.BaseDirectory,
                UseShellExecute = false, CreateNoWindow = true,
                RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true,
                StandardOutputEncoding = Encoding.UTF8, StandardErrorEncoding = Encoding.UTF8
            };
            using (var process = new Process { StartInfo = info })
            {
                process.Start();
                process.StandardInput.Write(Convert.ToBase64String(Encoding.UTF8.GetBytes(requestJson)));
                process.StandardInput.Close();
                string output = process.StandardOutput.ReadToEnd();
                string error = process.StandardError.ReadToEnd();
                process.WaitForExit();
                if (process.ExitCode != 0)
                    throw new InvalidOperationException("音乐记忆检索程序失败。" + (string.IsNullOrWhiteSpace(error) ? "" : " " + error.Trim().Substring(0, Math.Min(300, error.Trim().Length))));
                return output.Trim();
            }
        }

        private void UpdateMemoryRecallSummary(Dictionary<string, object> response)
        {
            var summary = response.ContainsKey("summary") ? response["summary"] as Dictionary<string, object> : null;
            if (summary == null) return;
            string from = RecallValue(summary, "playFrom"), to = RecallValue(summary, "playTo");
            string range = string.IsNullOrWhiteSpace(from) ? "暂无播放记录" : from == to ? from : from + " 至 " + to;
            memorySourceStatus.Text = string.Format(CultureInfo.InvariantCulture,
                "播放记录 {0} 条（{1}）；本地歌词匹配 {2}/{0} 首。",
                RecallValue(summary, "playCount"), range, RecallValue(summary, "lyricsMatchedSongs"));
            tips.SetToolTip(memorySourceStatus, "播放记录由网易云最近播放接口同步；接口不支持翻页且按间隔轮询，记录缺失不代表当时没有听歌。");
        }

        private void PopulateMemoryResults(System.Collections.ArrayList results)
        {
            memoryResults.BeginUpdate();
            memoryResults.Items.Clear();
            foreach (var value in results)
            {
                var item = value as Dictionary<string, object>;
                if (item == null) continue;
                var row = new ListViewItem(RecallValue(item, "playedText"));
                row.SubItems.Add(RecallValue(item, "title"));
                row.SubItems.Add(RecallValue(item, "artist"));
                var evidence = item.ContainsKey("evidence") ? item["evidence"] as System.Collections.ArrayList : null;
                row.SubItems.Add(evidence == null ? "" : string.Join("；", evidence.Cast<object>().Select(valueText => Convert.ToString(valueText))));
                row.Tag = item;
                memoryResults.Items.Add(row);
            }
            memoryResults.EndUpdate();
            if (memoryResults.Items.Count > 0)
            {
                memoryResults.Items[0].Selected = true;
                memoryResults.Items[0].Focused = true;
                memoryResults.EnsureVisible(0);
            }
            else
            {
                memoryEvidence.Text = "没有找到匹配项。可以补充日期/时段、歌曲名、艺人名或一段本地歌词。播放记录有接口采集窗口，未记录不等于没听过。";
            }
        }

        private void ShowMemoryEvidence()
        {
            if (memoryResults.SelectedItems.Count == 0) return;
            var item = memoryResults.SelectedItems[0].Tag as Dictionary<string, object>;
            if (item == null) return;
            var detail = new StringBuilder();
            detail.AppendLine(RecallValue(item, "title") + " — " + RecallValue(item, "artist"));
            if (!string.IsNullOrWhiteSpace(RecallValue(item, "album"))) detail.AppendLine("专辑：" + RecallValue(item, "album"));
            detail.AppendLine("播放时间：" + RecallValue(item, "playedText"));
            var device = string.Join(" / ", new[] { RecallValue(item, "deviceSystem"), RecallValue(item, "deviceName") }.Where(value => !string.IsNullOrWhiteSpace(value)));
            if (!string.IsNullOrWhiteSpace(device)) detail.AppendLine("设备：" + device);
            detail.AppendLine();
            detail.AppendLine("本机检索依据");
            var evidence = item.ContainsKey("evidence") ? item["evidence"] as System.Collections.ArrayList : null;
            if (evidence != null) foreach (var line in evidence) detail.AppendLine("• " + Convert.ToString(line));
            if (!string.IsNullOrWhiteSpace(RecallValue(item, "lyricExcerpt")))
            {
                detail.AppendLine();
                detail.AppendLine("歌词片段");
                detail.AppendLine(RecallValue(item, "lyricExcerpt"));
            }
            memoryEvidence.Text = detail.ToString();
        }

        private static string RecallValue(Dictionary<string, object> value, string key)
        {
            object item;
            return value != null && value.TryGetValue(key, out item) && item != null
                ? Convert.ToString(item, CultureInfo.InvariantCulture) : "";
        }

        private void ConfigureProfileTab(Control tab)
        {
            var header = new WallpaperPanel { Name = "ProfileHeader", Dock = DockStyle.Fill };
            var profileTitle = Label("听歌画像", 13, true, ink);
            profileTitle.Location = new Point(3, 4);
            header.Controls.Add(profileTitle);
            refreshProfileButton.Text = "刷新网易云数据";
            refreshProfileButton.Name = "RefreshProfileButton";
            refreshProfileButton.Width = 150;
            refreshProfileButton.Height = 40;
            StyleButton(refreshProfileButton, ButtonRole.Primary);
            refreshProfileButton.Click += async delegate { await RefreshProfile(); };
            header.Controls.Add(refreshProfileButton);
            allPortraitButton.Name = "RefreshAllPortraitsButton";
            allPortraitButton.Text = "刷新全部画像";
            allPortraitButton.Size = new Size(150, 40);
            StyleButton(allPortraitButton, ButtonRole.Secondary);
            allPortraitButton.Click += async delegate { await RefreshAllPortraits(); };
            header.Controls.Add(allPortraitButton);
            profileStatus.AutoSize = false;
            profileStatus.Text = "点击刷新，从网易云读取听歌次数和红心时间。";
            profileStatus.ForeColor = muted;
            profileStatus.Location = new Point(4, 44);
            profileStatus.Size = new Size(620, 24);
            profileStatus.Anchor = AnchorStyles.Left | AnchorStyles.Right | AnchorStyles.Top;
            header.Controls.Add(profileStatus);
            header.Resize += delegate {
                refreshProfileButton.Left = header.ClientSize.Width - refreshProfileButton.Width;
                refreshProfileButton.Top = 7;
                allPortraitButton.Left = refreshProfileButton.Left - allPortraitButton.Width - 10;
                allPortraitButton.Top = 7;
                profileStatus.Top = profileTitle.Bottom + 4;
                profileStatus.Width = Math.Max(220, allPortraitButton.Left - 18);
            };
            profileSongs.Dock = DockStyle.Fill;
            profileSongs.View = View.Details;
            profileSongs.FullRowSelect = true;
            profileSongs.HideSelection = false;
            profileSongs.BackColor = panel;
            profileSongs.ForeColor = ink;
            profileSongs.BorderStyle = BorderStyle.None;
            profileSongs.Columns.Add("歌曲", 230);
            profileSongs.Columns.Add("歌手", 170);
            profileSongs.Columns.Add("专辑", 190);
            profileSongs.Columns.Add("累计播放", 85);
            profileSongs.Columns.Add("首次收听", 145);
            profileSongs.Columns.Add("红心", 55);
            profileSongs.Columns.Add("红心时间", 145);
            profileSongs.Columns.Add("歌曲 ID", 90);
            ConfigureSongNavigation(profileSongs);
            var portraitTabs = new ThemedTabControl { Name = "ProfileTabs", Dock = DockStyle.Fill };
            var allTab = new TabPage("所有画像") { BackColor = panel };
            var recentTab = new TabPage("近30天画像") { BackColor = panel };
            var detailsTab = new TabPage("歌曲明细") { BackColor = panel };
            ConfigurePortraitText(allPortraitText);
            ConfigurePortraitText(recentPortraitText);
            allTab.Controls.Add(allPortraitText);
            recentGenreComparison.Dock = DockStyle.Fill;
            recentGenreComparison.View = View.Details;
            recentGenreComparison.FullRowSelect = true;
            recentGenreComparison.BackColor = panel;
            recentGenreComparison.ForeColor = ink;
            recentGenreComparison.BorderStyle = BorderStyle.None;
            recentGenreComparison.Columns.Add("曲风", 220);
            recentGenreComparison.Columns.Add("近期歌曲", 85);
            recentGenreComparison.Columns.Add("近期占比", 85);
            recentGenreComparison.Columns.Add("全局歌曲", 85);
            recentGenreComparison.Columns.Add("全局占比", 85);
            recentGenreComparison.Columns.Add("差值", 110);
            var recentSplit = new SplitContainer { Dock = DockStyle.Fill, Orientation = Orientation.Horizontal, SplitterDistance = 210 };
            recentSplit.Panel1.Controls.Add(recentPortraitText);
            recentSplit.Panel2.Controls.Add(recentGenreComparison);
            recentTab.Controls.Add(recentSplit);
            detailsTab.Controls.Add(profileSongs);
            portraitTabs.TabPages.Add(allTab);
            portraitTabs.TabPages.Add(recentTab);
            portraitTabs.TabPages.Add(detailsTab);
            portraitTabs.TabPages.Add(BuildLocalPlaybackTab());
            var profileLayout = new TableLayoutPanel { Name = "ProfileLayout", Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 2 };
            profileLayout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            profileLayout.RowStyles.Add(new RowStyle(SizeType.Absolute, 88));
            profileLayout.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
            profileLayout.Controls.Add(header, 0, 0);
            profileLayout.Controls.Add(portraitTabs, 0, 1);
            tab.Controls.Add(profileLayout);
            Shown += async delegate {
                await RefreshPortrait("all");
                await RefreshPortrait("recent");
            };
        }

        private void ConfigurePortraitText(RichTextBox box)
        {
            box.Dock = DockStyle.Fill;
            box.Multiline = true;
            box.ReadOnly = true;
            box.DetectUrls = true;
            box.ScrollBars = RichTextBoxScrollBars.Vertical;
            box.BackColor = panel;
            box.ForeColor = ink;
            box.BorderStyle = BorderStyle.None;
            box.Font = UiFont(10F);
            box.Text = "正在读取本地画像缓存…";
            box.LinkClicked += delegate(object sender, LinkClickedEventArgs e) {
                try {
                    Uri link;
                    if (!Uri.TryCreate(e.LinkText, UriKind.Absolute, out link) || !link.IsFile)
                        throw new InvalidOperationException("只支持打开本地图表报告链接。");
                    var path = link.LocalPath;
                    if (!File.Exists(path)) throw new FileNotFoundException("图表报告文件不存在。", path);
                    Process.Start(new ProcessStartInfo(path) { UseShellExecute = true });
                }
                catch (Exception ex) {
                    WriteLog("打开图表报告链接失败：" + ex.Message);
                    ReportTask("图表报告", "失败", "打开链接失败：" + ex.Message);
                }
            };
        }

        private void ConfigureGenreList()
        {
            genreSongs.Dock = DockStyle.Fill;
            genreSongs.BackColor = panel;
            genreSongs.ForeColor = ink;
            genreSongs.Columns.Add("歌曲", 250);
            genreSongs.Columns.Add("主曲风", 150);
            genreSongs.Columns.Add("其他曲风", 180);
            genreSongs.Columns.Add("情绪", 90);
            genreSongs.Columns.Add("来源", 65);
            genreSongs.Columns.Add("状态", 65);
            genreSongs.Columns.Add("来源 / 输出目录", 260);
            var genreMenu = new ContextMenuStrip();
            var editGenre = new ToolStripMenuItem("手动修正曲风");
            editGenre.Click += async delegate { await EditGenreResult(); };
            genreMenu.Items.Add(editGenre);
            genreSongs.ContextMenuStrip = genreMenu;
            ConfigureSongNavigation(genreSongs);
            genreSongs.FillLastColumn = true;
        }

        internal void ShowLogWindow()
        {
            if (logWindow == null)
            {
                logWindow = new Form {
                    Text = "运行日志",
                    Size = new Size(760, 420),
                    MinimumSize = new Size(480, 260),
                    StartPosition = FormStartPosition.CenterParent,
                    BackColor = theme.LogBackground,
                    ForeColor = ink,
                    Font = UiFont(9F)
                };
                log.Multiline = true;
                log.ReadOnly = true;
                log.ScrollBars = ScrollBars.Vertical;
                log.BackColor = logWindow.BackColor;
                log.ForeColor = ink;
                log.BorderStyle = BorderStyle.None;
                log.Font = UiFont(9F);
                log.Dock = DockStyle.Fill;
                logWindow.Padding = new Padding(16);
                logWindow.Controls.Add(log);
                logWindow.FormClosing += delegate(object sender, FormClosingEventArgs e) {
                    if (e.CloseReason == CloseReason.UserClosing) { e.Cancel = true; logWindow.Hide(); }
                };
            }
            PrepareThemedDialog(logWindow);
            if (!logWindow.Visible) { var owner = DialogOwner; if (owner != null) logWindow.Show(owner); else logWindow.Show(); }
            else { logWindow.WindowState = FormWindowState.Normal; logWindow.Activate(); }
        }

        private void ConfigureSongList(ListView list, string directoryHeading)
        {
            list.Dock = DockStyle.Fill;
            list.View = View.Details;
            list.FullRowSelect = true;
            list.HideSelection = false;
            list.MultiSelect = true;
            list.BackColor = panel;
            list.ForeColor = ink;
            list.BorderStyle = BorderStyle.None;
            list.Columns.Add("歌曲", 330);
            list.Columns.Add(directoryHeading, 450);
            ConfigureSongNavigation(list);
            list.Resize += delegate { list.Columns[1].Width = Math.Max(220, list.ClientSize.Width - list.Columns[0].Width - 8); };
        }

        private void ConfigureSongNavigation(ListView list)
        {
            ConfigureSongNavigation(list, list.GetItemAt, list == profileSongs, list == memoryResults);
        }

        private void ConfigureSongNavigation(CachedSongTable list)
        {
            ConfigureSongNavigation(list, list.GetItemAt, false, false);
        }

        private void ConfigureSongNavigation(Control list, Func<int, int, ListViewItem> hitTest, bool profile, bool memory)
        {
            tips.SetToolTip(list, "双击歌曲打开网易云播放页面；曲风修正请使用右键菜单。");
            list.MouseDoubleClick += async delegate(object sender, MouseEventArgs e) {
                if (e.Button != MouseButtons.Left) return;
                var item = hitTest(e.X, e.Y);
                if (item == null) return;
                string source = item.Tag as string;
                string id = null;
                if (profile && item.SubItems.Count > 7) id = item.SubItems[7].Text;
                if (memory)
                {
                    var result = item.Tag as Dictionary<string, object>;
                    if (result != null) id = RecallValue(result, "songId");
                }
                ReportTask("打开网易云", "开始", "正在打开所选歌曲的播放页面。");
                try
                {
                    var songId = await Task.Run(() => id != null ? SongNavigation.ValidateId(id)
                        : SongNavigation.ResolveSource(source, InputRoot));
                    bool hasClient;
                    using (var command = Registry.ClassesRoot.OpenSubKey(@"orpheus\shell\open\command"))
                        hasClient = command != null && !string.IsNullOrWhiteSpace(Convert.ToString(command.GetValue("")));
                    var url = hasClient ? SongNavigation.ClientUrl(songId) : "https://music.163.com/#/song?id=" + songId;
                    try { Process.Start(new ProcessStartInfo(url) { UseShellExecute = true }); }
                    catch
                    {
                        if (!hasClient) throw;
                        Process.Start(new ProcessStartInfo("https://music.163.com/#/song?id=" + songId) { UseShellExecute = true });
                        hasClient = false;
                    }
                    ReportTask("打开网易云", "完成", "已请求打开" + (hasClient ? "网易云客户端" : "网易云网页") + "播放页面，歌曲 ID：" + songId);
                }
                catch (Exception ex) { ReportTask("打开网易云", "失败", ex.Message); }
            };
        }

        private void UpdateConversionSelectionButtons()
        {
            var active = songTabs.SelectedIndex == 1 ? convertedSongs : songs;
            extractCoverButton.Enabled = !converting && !extractingCover && File.Exists(scriptPath) && active.SelectedItems.Count == 1;
            convertSelectedButton.Text = songTabs.SelectedIndex == 1 ? "重新转换选中" : "转换选中歌曲";
            convertSelectedButton.Enabled = !converting && !deduplicating && !extractingCover && File.Exists(scriptPath) && active.SelectedItems.Count > 0;
        }

        private void QueueScan()
        {
            InvokeUiAsync(() => {
                if (converting) return;
                scanQueued = true;
                scanTimer.Stop();
                scanTimer.Start();
            });
        }

        private void UpdateOutputStatus()
        {
            outputStatus.Text = outputRoot == null
                ? "输出位置：默认（每首歌曲目录下的 unlock 文件夹）"
                : "输出位置：" + outputRoot + "（保留原有子目录）";
            settingsOutputStatus.Text = outputStatus.Text;
            tips.SetToolTip(outputStatus, outputStatus.Text);
            tips.SetToolTip(settingsOutputStatus, settingsOutputStatus.Text);
            resetOutputButton.Enabled = outputRoot != null && !converting;
        }

        private void ChooseOutputFolder()
        {
            using (var dialog = new FolderBrowserDialog())
            {
                dialog.Description = "选择转换音频和歌词的保存文件夹";
                dialog.ShowNewFolderButton = true;
                dialog.SelectedPath = outputRoot ?? InputRoot;
                if (dialog.ShowDialog(DialogOwner) != DialogResult.OK) return;
                try
                {
                    var selected = Path.GetFullPath(dialog.SelectedPath);
                    File.WriteAllText(outputSettingPath, SavePortablePath(selected), new UTF8Encoding(false));
                    outputRoot = selected;
                    UpdateOutputStatus();
                    WriteLog("转换输出目录已设置为：" + outputRoot);
                    ReportTask("转换输出目录", "完成", settingsOutputStatus.Text);
                    ScanSongs();
                }
                catch (Exception ex)
                {
                    WriteLog("保存输出目录失败：" + ex.Message);
                    ReportTask("转换输出目录", "失败", ex.Message);
                }
            }
        }

        private void ResetOutputFolder()
        {
            try
            {
                if (File.Exists(outputSettingPath)) File.Delete(outputSettingPath);
                outputRoot = null;
                UpdateOutputStatus();
                WriteLog("转换输出目录已恢复默认。" );
                ReportTask("转换输出目录", "完成", settingsOutputStatus.Text);
                ScanSongs();
            }
            catch (Exception ex)
            {
                WriteLog("恢复默认目录失败：" + ex.Message);
                ReportTask("转换输出目录", "失败", ex.Message);
            }
        }

        private string CustomOutputDirectoryFor(string source)
        {
            if (outputRoot == null) return null;
            var sourceDirectory = Path.GetDirectoryName(source);
            var prefix = InputRoot.TrimEnd('\\') + "\\";
            if (sourceDirectory.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
                return Path.Combine(outputRoot, sourceDirectory.Substring(prefix.Length));
            return outputRoot;
        }

        private string OutputFor(string source)
        {
            var directories = new[] { Path.Combine(Path.GetDirectoryName(source), "unlock"), CustomOutputDirectoryFor(source) };
            var stem = Path.GetFileNameWithoutExtension(source);
            string latest = null;
            DateTime latestTime = DateTime.MinValue;
            foreach (var directory in directories)
            {
                if (directory == null || !Directory.Exists(directory)) continue;
                foreach (var extension in new[] { ".flac", ".mp3", ".m4a", ".wav", ".ogg" })
                {
                    var target = Path.Combine(directory, stem + extension);
                    if (!File.Exists(target)) continue;
                    var file = new FileInfo(target);
                    if (file.Length > 0 && file.LastWriteTimeUtc > latestTime)
                    {
                        latest = target;
                        latestTime = file.LastWriteTimeUtc;
                    }
                }
            }
            return latest;
        }

        private void ScanSongs(bool userInitiated = false)
        {
            scanQueued = false;
            if (converting || deduplicating || analyzingGenres) return;
            if (userInitiated) ReportTask("重新扫描", "开始", "正在检查本地歌曲。");
            pending.Clear();
            latestOutputPath = null;
            songs.BeginUpdate();
            convertedSongs.BeginUpdate();
            genreSongs.BeginUpdate();
            songs.Items.Clear();
            convertedSongs.Items.Clear();
            genreSongs.Items.Clear();
            genreRows.Clear();
            int converted = 0;
            DateTime latestOutputTime = DateTime.MinValue;
            try
            {
                if (!Directory.Exists(InputRoot)) throw new DirectoryNotFoundException("请点击“选择音乐目录”，选择本机的 NCM 歌曲文件夹。");
                var scanErrors = new List<string>();
                var files = DedupeFileSystem.EnumerateNcmFiles(InputRoot, true, scanErrors)
                    .OrderByDescending(file => file.LastWriteTimeUtc).ToArray();
                if (scanErrors.Count > 0) WriteLog("歌曲扫描跳过 " + scanErrors.Count + " 个项目：" + scanErrors[0]);
                foreach (var file in files)
                {
                    var output = OutputFor(file.FullName);
                    if (output != null)
                    {
                        converted++;
                        var completedItem = new ListViewItem(Path.GetFileNameWithoutExtension(file.Name));
                        completedItem.SubItems.Add(Path.GetDirectoryName(output));
                        completedItem.ToolTipText = output;
                        completedItem.Tag = file.FullName;
                        convertedSongs.Items.Add(completedItem);
                        var genreItem = new ListViewItem(Path.GetFileNameWithoutExtension(output));
                        genreItem.SubItems.Add("未分析");
                        genreItem.SubItems.Add("");
                        genreItem.SubItems.Add("");
                        genreItem.SubItems.Add("");
                        genreItem.SubItems.Add("未分析");
                        genreItem.SubItems.Add(Path.GetDirectoryName(output));
                        genreItem.Tag = output;
                        genreItem.ToolTipText = output;
                        genreSongs.Items.Add(genreItem);
                        genreRows[output] = genreItem;
                        var time = File.GetLastWriteTimeUtc(output);
                        if (time > latestOutputTime)
                        {
                            latestOutputPath = output;
                            latestOutputTime = time;
                        }
                        continue;
                    }
                    pending.Add(file.FullName);
                    var item = new ListViewItem(Path.GetFileNameWithoutExtension(file.Name));
                    item.SubItems.Add(file.DirectoryName);
                    item.ToolTipText = file.FullName;
                    item.Tag = file.FullName;
                    songs.Items.Add(item);
                }
                summary.Text = string.Format("待转换 {0} 首 · 已找到音频 {1} 首", pending.Count, converted);
                if (userInitiated) ReportTask("重新扫描", "完成", summary.Text);
            }
            catch (Exception ex)
            {
                summary.Text = "扫描失败：" + ex.Message;
                WriteLog(summary.Text);
                if (userInitiated) ReportTask("重新扫描", "失败", ex.Message);
            }
            finally { songs.EndUpdate(); convertedSongs.EndUpdate(); genreSongs.EndUpdate(); }
            convertButton.Enabled = pending.Count > 0 && File.Exists(scriptPath);
            folderButton.Enabled = latestOutputPath != null;
            analyzeGenresButton.Enabled = genreSongs.Items.Count > 0 && !analyzingGenres && File.Exists(genreScriptPath);
            exportGenresButton.Enabled = genreSongs.Items.Count > 0 && File.Exists(genreScriptPath);
            UpdateConversionSelectionButtons();
            if (!File.Exists(scriptPath)) WriteLog("找不到转换脚本：" + scriptPath);
            if (genreSongs.Items.Count > 0) LoadGenreResults();
        }

        private static string Quoted(string value)
        {
            return "\"" + value.Replace("\"", "\\\"") + "\"";
        }

        private string FindGenrePython()
        {
            var bundled = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, @"runtime\python\python.exe");
            if (File.Exists(bundled)) return bundled;
            var pythonRoot = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Programs", "Python");
            try
            {
                if (Directory.Exists(pythonRoot))
                {
                    string installed = null;
                    int bestMinor = 9;
                    foreach (var folder in Directory.GetDirectories(pythonRoot, "Python3*"))
                    {
                        var name = Path.GetFileName(folder);
                        int minor;
                        var executable = Path.Combine(folder, "python.exe");
                        if (name.StartsWith("Python3", StringComparison.OrdinalIgnoreCase)
                            && int.TryParse(name.Substring(7), out minor) && minor >= 10
                            && minor > bestMinor && File.Exists(executable))
                        {
                            installed = executable;
                            bestMinor = minor;
                        }
                    }
                    if (installed != null) return installed;
                }
            }
            catch { }
            return "python.exe";
        }

        private static string DecodeWorkerText(string value)
        {
            value = value.Replace('-', '+').Replace('_', '/');
            while (value.Length % 4 != 0) value += "=";
            return Encoding.UTF8.GetString(Convert.FromBase64String(value));
        }

        private void HandleGenreWorkerLine(string line)
        {
            if (string.IsNullOrEmpty(line)) return;
            var fields = line.Split('\t');
            try
            {
                if (fields[0] == "PROGRESS" && fields.Length >= 2)
                    WriteLog(DecodeWorkerText(fields[1]));
                else if (fields[0] == "RESULT" && fields.Length >= 7)
                {
                    var values = fields.Skip(1).Take(6).Select(DecodeWorkerText).ToArray();
                    InvokeUiAsync(() => ApplyGenreResult(values));
                }
                else if (fields[0] == "TRACK" && fields.Length >= 4)
                {
                    var values = fields.Skip(1).Take(3).Select(DecodeWorkerText).ToArray();
                    InvokeUi(() => ApplyLibraryTrack(values));
                }
                else if (fields[0] == "PLAYLIST_META" && fields.Length >= 2)
                    CaptureTagPlaylistMeta(DecodeWorkerText(fields[1]));
                else if (fields[0] == "PLAYLIST_SONG" && fields.Length >= 2)
                    CaptureTagPlaylistSong(DecodeWorkerText(fields[1]));
                else if (fields[0] == "ERROR" && fields.Length >= 3)
                    WriteLog("分析失败：" + DecodeWorkerText(fields[1]) + " · " + DecodeWorkerText(fields[2]));
                else if (fields[0] == "FATAL" && fields.Length >= 2)
                    WriteLog("曲风分析不可用：" + DecodeWorkerText(fields[1]));
                else WriteLog(line);
            }
            catch (Exception ex) { WriteLog("读取分析结果失败：" + ex.Message); }
        }

        private void ApplyGenreResult(string[] values)
        {
            if (values == null || values.Length < 6) return;
            ListViewItem item;
            if (!genreRows.TryGetValue(values[0], out item)) return;
            item.SubItems[1].Text = values[1];
            item.SubItems[2].Text = values[2];
            item.SubItems[3].Text = values[3];
            item.SubItems[4].Text = values[4];
            item.SubItems[5].Text = values[5];
            genreSongs.NotifyDataChanged(item);
        }

        private void ApplyLibraryTrack(string[] values)
        {
            if (values == null || values.Length < 3 || genreRows.ContainsKey(values[0])) return;
            var item = new ListViewItem(values[1]);
            item.SubItems.Add("未分析");
            item.SubItems.Add("");
            item.SubItems.Add("");
            item.SubItems.Add("");
            item.SubItems.Add("未分析");
            item.SubItems.Add(values[2]);
            item.Tag = values[0];
            item.ToolTipText = values[1] + " · " + values[2];
            genreSongs.Items.Add(item);
            genreRows[values[0]] = item;
        }

        private string[] GenreAudioPaths()
        {
            return genreSongs.Items.Cast<ListViewItem>()
                .Select(item => item.Tag as string)
                .Where(path => !string.IsNullOrEmpty(path) && (path.StartsWith("netease://song/", StringComparison.OrdinalIgnoreCase) || File.Exists(path)))
                .Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
        }

        private sealed class WorkerResult
        {
            public int ExitCode;
            public int CompletedCount;
            public int ErrorCount;
            public string FirstError;
        }

        private WorkerResult RunGenreWorker(string[] arguments)
        {
            var python = FindGenrePython();
            if (!File.Exists(genreScriptPath)) throw new FileNotFoundException("找不到曲风分析程序。", genreScriptPath);
            var result = new WorkerResult();
            var info = new ProcessStartInfo(python) {
                Arguments = Quoted(genreScriptPath) + " --db " + Quoted(genreDatabasePath) + " --input-root " + Quoted(InputRoot) + " " + string.Join(" ", arguments.Select(Quoted)),
                WorkingDirectory = AppDomain.CurrentDomain.BaseDirectory,
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                StandardOutputEncoding = Encoding.UTF8,
                StandardErrorEncoding = Encoding.UTF8
            };
            using (var process = new Process { StartInfo = info })
            {
                process.ErrorDataReceived += delegate(object sender, DataReceivedEventArgs e) {
                    if (e.Data != null) WriteLog(e.Data);
                };
                process.Start();
                process.BeginErrorReadLine();
                string line;
                while ((line = process.StandardOutput.ReadLine()) != null) {
                    var fields = line.Split('\t');
                    if (fields[0] == "ERROR" && fields.Length >= 3) {
                        result.ErrorCount++;
                        if (result.FirstError == null) result.FirstError = DecodeWorkerText(fields[1]) + " · " + DecodeWorkerText(fields[2]);
                    }
                    else if (fields[0] == "FATAL" && fields.Length >= 2) {
                        result.ErrorCount++;
                        if (result.FirstError == null) result.FirstError = DecodeWorkerText(fields[1]);
                    }
                    HandleGenreWorkerLine(line);
                }
                process.WaitForExit();
                result.ExitCode = process.ExitCode;
                if (process.ExitCode != 0) WriteLog("曲风分析进程退出码：" + process.ExitCode);
            }
            return result;
        }

        private async void LoadGenreResults()
        {
            if (loadingGenreResults || genreSongs.Items.Count == 0) return;
            loadingGenreResults = true;
            try
            {
                await Task.Run(() => RunGenreWorker(new[] { "--library-list" }));
                var args = new[] { "--list" }.Concat(GenreAudioPaths()).ToArray();
                await Task.Run(() => RunGenreWorker(args));
            }
            catch (Exception ex) { WriteLog("读取曲风记录失败：" + ex.Message); }
            finally { loadingGenreResults = false; }
        }

        private async Task AnalyzeGenres()
        {
            if (analyzingGenres || buildingTagPlaylist || converting || loadingGenreResults) return;
            var files = GenreAudioPaths();
            if (files.Length == 0) { ReportTask("曲风刷新", "失败", "当前没有可分析的歌曲。"); return; }
            analyzingGenres = true;
            analyzeGenresButton.Enabled = false;
            exportGenresButton.Enabled = false;
            tagPlaylistButton.Enabled = false;
            analyzeGenresButton.Text = "正在分析…";
            ReportTask("曲风刷新", "开始", "正在分析 " + files.Length + " 首歌曲。");
            WriteLog("开始从网易云歌曲百科读取 " + files.Length + " 首歌曲的曲风与情绪标签。");
            try
            {
                var result = await Task.Run(() => RunGenreWorker(new[] { "--clear-old" }.Concat(files).ToArray()));
                if (result.ExitCode != 0 || result.ErrorCount > 0)
                    ReportTask("曲风刷新", result.ErrorCount > 0 && result.ErrorCount < files.Length && result.ExitCode == 0 ? "部分失败" : "失败",
                        result.ErrorCount + " 首出错" + (result.FirstError == null ? "；退出码 " + result.ExitCode : "；" + result.FirstError));
                else ReportTask("曲风刷新", "完成", "已处理 " + files.Length + " 首歌曲。");
            }
            catch (Exception ex) { ReportTask("曲风刷新", "失败", ex.Message); }
            finally
            {
                analyzingGenres = false;
                analyzeGenresButton.Text = "分析曲风";
                analyzeGenresButton.Enabled = genreSongs.Items.Count > 0;
                exportGenresButton.Enabled = genreSongs.Items.Count > 0;
                tagPlaylistButton.Enabled = File.Exists(genreScriptPath) && !buildingTagPlaylist;
            }
        }

        private void HandleProfileLine(string line)
        {
            if (string.IsNullOrEmpty(line)) return;
            var fields = line.Split('\t');
            try
            {
                if (fields[0] == "META" && fields.Length >= 4)
                {
                    var values = fields.Skip(1).Take(3).Select(DecodeWorkerText).ToArray();
                    InvokeUiAsync(() => profileStatus.Text = values[0] + " 首歌曲，" + values[1] + " 首红心。完整 JSON：" + values[2]);
                }
                else if (fields[0] == "PROGRESS" && fields.Length >= 2)
                {
                    WriteLog(DecodeWorkerText(fields[1]));
                }
                else if (fields[0] == "SONG" && fields.Length >= 9)
                {
                    var values = fields.Skip(1).Take(8).Select(DecodeWorkerText).ToArray();
                    InvokeUiAsync(() => profileSongs.Items.Add(new ListViewItem(values)));
                }
                else if (fields[0] == "FATAL" && fields.Length >= 2)
                {
                    var message = DecodeWorkerText(fields[1]);
                    InvokeUiAsync(() => profileStatus.Text = "读取失败：" + message);
                    WriteLog("网易云画像数据读取失败：" + message);
                }
            }
            catch (Exception ex) { WriteLog("解析画像数据失败：" + ex.Message); }
        }

        private async Task<bool> RefreshProfile()
        {
            if (loadingProfile) return false;
            if (!File.Exists(profileScriptPath)) {
                profileStatus.Text = "找不到 profile_fetch.py。";
                ReportTask("网易云数据刷新", "失败", profileStatus.Text);
                return false;
            }
            var python = FindGenrePython();
            loadingProfile = true;
            refreshProfileButton.Enabled = false;
            refreshProfileButton.Text = "正在读取…";
            profileStatus.Text = "正在读取网易云听歌排行和我喜欢的音乐…";
            ReportTask("网易云数据刷新", "开始", "正在读取听歌排行和红心歌曲。");
            profileSongs.Items.Clear();
            try
            {
                var result = await Task.Run(() => {
                    var info = new ProcessStartInfo(python) {
                        Arguments = Quoted(profileScriptPath),
                        WorkingDirectory = AppDomain.CurrentDomain.BaseDirectory,
                        UseShellExecute = false,
                        CreateNoWindow = true,
                        RedirectStandardOutput = true,
                        RedirectStandardError = true,
                        StandardOutputEncoding = Encoding.UTF8,
                        StandardErrorEncoding = Encoding.UTF8
                    };
                    using (var process = new Process { StartInfo = info })
                    {
                        process.ErrorDataReceived += delegate(object sender, DataReceivedEventArgs e) { if (e.Data != null) WriteLog(e.Data); };
                        process.Start();
                        process.BeginErrorReadLine();
                        string line;
                        string fatal = null;
                        while ((line = process.StandardOutput.ReadLine()) != null) {
                            var fields = line.Split('\t');
                            if (fields[0] == "FATAL" && fields.Length >= 2) fatal = DecodeWorkerText(fields[1]);
                            HandleProfileLine(line);
                        }
                        process.WaitForExit();
                        if (process.ExitCode != 0) WriteLog("网易云画像读取进程退出码：" + process.ExitCode);
                        return new KeyValuePair<bool, string>(process.ExitCode == 0 && fatal == null && File.Exists(profileDatabasePath),
                            fatal ?? (process.ExitCode != 0 ? "进程退出码 " + process.ExitCode : "未生成网易云档案。"));
                    }
                });
                if (result.Key) ReportTask("网易云数据刷新", "完成", "听歌排行和红心歌曲已更新。");
                else {
                    profileStatus.Text = "读取失败：" + result.Value;
                    ReportTask("网易云数据刷新", "失败", result.Value);
                }
                return result.Key;
            }
            catch (Exception ex)
            {
                profileStatus.Text = "读取失败：" + ex.Message;
                ReportTask("网易云数据刷新", "失败", ex.Message);
                return false;
            }
            finally
            {
                loadingProfile = false;
                refreshProfileButton.Enabled = true;
                refreshProfileButton.Text = "刷新网易云数据";
            }
        }

        private async Task<string> AnalyzePortrait(string scope)
        {
            if (!File.Exists(profileAnalysisScriptPath))
                throw new FileNotFoundException("找不到画像分析脚本 profile_analysis.py。", profileAnalysisScriptPath);
            var python = FindGenrePython();
            var result = await Task.Run(() => {
                var info = new ProcessStartInfo(python) {
                    Arguments = Quoted(profileAnalysisScriptPath) + " --scope " + scope
                        + " --profile " + Quoted(profileDatabasePath)
                        + " --genre-db " + Quoted(genreDatabasePath),
                    WorkingDirectory = AppDomain.CurrentDomain.BaseDirectory,
                    UseShellExecute = false, CreateNoWindow = true,
                    RedirectStandardOutput = true, RedirectStandardError = true,
                    StandardOutputEncoding = Encoding.UTF8, StandardErrorEncoding = Encoding.UTF8
                };
                using (var process = new Process { StartInfo = info }) {
                    process.ErrorDataReceived += delegate(object sender, DataReceivedEventArgs e) { if (e.Data != null) WriteLog(e.Data); };
                    process.Start();
                    process.BeginErrorReadLine();
                    var output = process.StandardOutput.ReadToEnd();
                    process.WaitForExit();
                    return new KeyValuePair<int, string>(process.ExitCode, output);
                }
            });
            string portrait = null;
            var comparisons = new List<ListViewItem>();
            foreach (var line in result.Value.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries)) {
                var fields = line.Split('\t');
                if (fields[0] == "SUMMARY" && fields.Length >= 2) portrait = DecodeWorkerText(fields[1]);
                else if (fields[0] == "COMPARE" && fields.Length >= 7)
                    comparisons.Add(new ListViewItem(fields.Skip(1).Take(6).Select(DecodeWorkerText).ToArray()));
                else if (fields[0] == "FATAL" && fields.Length >= 2)
                    throw new InvalidOperationException(DecodeWorkerText(fields[1]));
            }
            if (result.Key != 0 || portrait == null)
                throw new InvalidOperationException("画像分析没有返回有效结果（退出码 " + result.Key + "）。");
            if (scope == "recent") {
                recentGenreComparison.BeginUpdate();
                try {
                    recentGenreComparison.Items.Clear();
                    recentGenreComparison.Items.AddRange(comparisons.ToArray());
                }
                finally { recentGenreComparison.EndUpdate(); }
            }
            return portrait;
        }

        private async Task RefreshPortrait(string scope)
        {
            var taskName = scope == "all" ? "所有画像分析" : "近30天画像分析";
            var box = scope == "all" ? allPortraitText : recentPortraitText;
            box.Text = "正在读取本地画像缓存…";
            ReportTask(taskName, "开始", "正在分析本地缓存。");
            try {
                var portrait = await AnalyzePortrait(scope);
                var report = FindExistingChartReport(scope);
                box.Text = report == null ? portrait : WithChartReportLink(portrait, report, "上次生成的图表报告");
                ReportTask(taskName, "完成", "本地画像已载入。");
            }
            catch (Exception ex) {
                box.Text = "画像读取失败：" + ex.Message;
                ReportTask(taskName, "失败", ex.Message);
            }
        }

        internal async Task RefreshAllPortraits(bool openReport = true)
        {
            if (loadingPortrait || loadingProfile) return;
            loadingPortrait = true;
            allPortraitButton.Enabled = false;
            allPortraitText.Text = "正在刷新网易云数据及两类画像…";
            recentPortraitText.Text = "正在刷新网易云数据及两类画像…";
            ReportTask("全部画像分析", "开始", "正在刷新网易云数据、两类画像和图表报告。");
            string allPortrait = null;
            string recentPortrait = null;
            try {
                if (!await RefreshProfile())
                    throw new InvalidOperationException("网易云数据刷新失败；未用旧档案覆盖本次画像。");
                refreshProfileButton.Enabled = false;
                allPortrait = await AnalyzePortrait("all");
                allPortraitText.Text = allPortrait + "\r\n\r\n正在生成图表报告…";
                recentPortrait = await AnalyzePortrait("recent");
                recentPortraitText.Text = recentPortrait + "\r\n\r\n正在生成图表报告…";
                var reportResult = await RefreshChartReport("both");
                var allReport = FindExistingChartReport("all");
                var recentReport = FindExistingChartReport("recent");
                if (allReport == null || recentReport == null)
                    throw new FileNotFoundException("两份图表报告未完整生成。");
                allPortraitText.Text = WithChartReportLink(allPortrait, allReport, "图表报告已刷新");
                recentPortraitText.Text = WithChartReportLink(recentPortrait, recentReport, "图表报告已刷新");
                if (openReport)
                    Process.Start(new ProcessStartInfo(reportResult.Key) { UseShellExecute = true });
                var aiStates = reportResult.Value.Split('|');
                if (aiStates.Length == 2 && aiStates.All(state => state == "ready"))
                    ReportTask("全部画像分析", "完成", "两类规则画像、AI 解读和图表报告均已更新。");
                else if (aiStates.Length == 2 && aiStates.All(state => state == "disabled"))
                    ReportTask("全部画像分析", "完成", "两类规则画像和图表报告已更新；AI 总开关已关闭。");
                else
                    ReportTask("全部画像分析", "部分失败", "规则画像和图表已更新；AI 解读状态：所有歌曲 "
                        + (aiStates.Length > 0 ? aiStates[0] : "未知") + "，近30天 "
                        + (aiStates.Length > 1 ? aiStates[1] : "未知") + "。请检查任务消息与接口设置。");
            }
            catch (Exception ex) {
                allPortraitText.Text = allPortrait == null ? "画像刷新失败：" + ex.Message : allPortrait;
                recentPortraitText.Text = recentPortrait == null ? "画像刷新失败：" + ex.Message : recentPortrait;
                ReportTask("全部画像分析", "失败", ex.Message);
            }
            finally {
                loadingPortrait = false;
                allPortraitButton.Enabled = true;
                refreshProfileButton.Enabled = true;
            }
        }

        private static string WithChartReportLink(string message, string reportPath, string caption)
        {
            return message + "\r\n\r\n" + caption + "：\r\n" + new Uri(reportPath).AbsoluteUri;
        }

        private static string PortraitAiSaveFailureMessage(string stderr)
        {
            var detail = (stderr ?? "").Trim();
            if (detail.Length == 0) return "保存配置失败；请检查接口地址、模型和提示词。";
            const int limit = 600;
            var message = "保存配置失败：" + (detail.Length > limit ? "…" + detail.Substring(detail.Length - limit) : detail);
            if (detail.IndexOf("PermissionError", StringComparison.OrdinalIgnoreCase) >= 0
                || detail.IndexOf("UnauthorizedAccess", StringComparison.OrdinalIgnoreCase) >= 0
                || detail.IndexOf("拒绝", StringComparison.Ordinal) >= 0
                || detail.IndexOf("denied", StringComparison.OrdinalIgnoreCase) >= 0)
                message += "\r\n\r\n配置文件目录不可写（当前工具箱进程权限不足，通常是沙箱构建的 Low 完整性 EXE）。"
                    + "请从正常安装、以 Medium 完整性运行的网易云工具箱中保存。";
            return message;
        }

        private string RunPortraitAiSettingsCommand(string action, string input = null)
        {
            if (!File.Exists(portraitAiScriptPath))
                throw new FileNotFoundException("找不到 AI 画像配置脚本。", portraitAiScriptPath);
            var info = new ProcessStartInfo(FindGenrePython()) {
                Arguments = Quoted(portraitAiScriptPath) + " " + action,
                WorkingDirectory = AppDomain.CurrentDomain.BaseDirectory,
                UseShellExecute = false, CreateNoWindow = true,
                RedirectStandardInput = input != null, RedirectStandardOutput = true,
                RedirectStandardError = true, StandardOutputEncoding = Encoding.UTF8,
                StandardErrorEncoding = Encoding.UTF8
            };
            using (var process = new Process { StartInfo = info }) {
                process.Start();
                if (input != null) { process.StandardInput.Write(Convert.ToBase64String(Encoding.UTF8.GetBytes(input))); process.StandardInput.Close(); }
                var output = process.StandardOutput.ReadToEnd();
                var error = process.StandardError.ReadToEnd();
                process.WaitForExit();
                if (process.ExitCode != 0) throw new InvalidOperationException(
                    action == "save" ? PortraitAiSaveFailureMessage(error)
                    : action == "rank-memory" ? "AI 语义排序失败。" + error.Substring(0, Math.Min(300, error.Length))
                    : "读取 AI 设置失败。" + error.Substring(0, Math.Min(500, error.Length)));
                return output.Trim();
            }
        }

        private void ShowPortraitAiSettings()
        {
            try {
                var serializer = new JavaScriptSerializer();
                var current = serializer.Deserialize<Dictionary<string, object>>(RunPortraitAiSettingsCommand("show"));
                var prompts = (Dictionary<string, object>)current["prompts"];
                using (var dialog = new Form { Text = "AI 画像接口与提示词", Size = new Size(760, 700),
                    MinimumSize = new Size(650, 580), StartPosition = FormStartPosition.CenterParent,
                    BackColor = background, ForeColor = ink, Font = Font }) {
                    var layout = new TableLayoutPanel { Dock = DockStyle.Fill, Padding = new Padding(18),
                        ColumnCount = 1, RowCount = 10, AutoScroll = true };
                    layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 28));
                    layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 34));
                    layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 28));
                    layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 34));
                    layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 28));
                    layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 34));
                    layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 28));
                    layout.RowStyles.Add(new RowStyle(SizeType.Percent, 50));
                    layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 28));
                    layout.RowStyles.Add(new RowStyle(SizeType.Percent, 50));
                    var url = new TextBox { Dock = DockStyle.Fill, Text = Convert.ToString(current["url"]) };
                    var model = new TextBox { Dock = DockStyle.Fill, Text = Convert.ToString(current["model"]) };
                    var key = new TextBox { Dock = DockStyle.Fill, UseSystemPasswordChar = true };
                    var all = new TextBox { Dock = DockStyle.Fill, Multiline = true, ScrollBars = ScrollBars.Vertical,
                        Text = Convert.ToString(prompts["all"]) };
                    var recent = new TextBox { Dock = DockStyle.Fill, Multiline = true, ScrollBars = ScrollBars.Vertical,
                        Text = Convert.ToString(prompts["recent"]) };
                    var aiEnabled = new CheckBox { Text = "启用 AI 分析（同时控制所有歌曲和近30天）",
                        AutoSize = true, Checked = Convert.ToBoolean(current["enabled"]), ForeColor = ink,
                        Location = new Point(18, 12) };
                    layout.Controls.Add(Label("接口地址（OpenAI 兼容 HTTPS /v1）", 9, false, muted), 0, 0);
                    layout.Controls.Add(url, 0, 1);
                    layout.Controls.Add(Label("模型", 9, false, muted), 0, 2);
                    layout.Controls.Add(model, 0, 3);
                    layout.Controls.Add(Label(Convert.ToBoolean(current["hasKey"]) ? "API Key（已保存；留空则保持不变）" : "API Key（未配置）", 9, false, muted), 0, 4);
                    layout.Controls.Add(key, 0, 5);
                    layout.Controls.Add(Label("所有歌曲提示词", 9, false, muted), 0, 6);
                    layout.Controls.Add(all, 0, 7);
                    layout.Controls.Add(Label("近30天新红心提示词", 9, false, muted), 0, 8);
                    layout.Controls.Add(recent, 0, 9);
                    var saveButton = new Button { Text = "保存设置", Dock = DockStyle.Right, Width = 120, Height = 36 };
                    StyleButton(saveButton, ButtonRole.Primary);
                    saveButton.Click += async delegate {
                        try {
                            RunPortraitAiSettingsCommand("save", serializer.Serialize(new {
                                url = url.Text, model = model.Text, key = key.Text, enabled = aiEnabled.Checked,
                                prompts = new Dictionary<string, string> { { "all", all.Text }, { "recent", recent.Text } }
                            }));
                            dialog.DialogResult = DialogResult.OK;
                        }
                        catch (Exception ex) { MessageBox.Show(dialog, ex.Message, "保存失败", MessageBoxButtons.OK, MessageBoxIcon.Warning); return; }
                        ReportTask("AI 画像设置", "开始", "设置已保存，正在同步两份本地图表报告；不会调用 AI 接口。");
                        try {
                            await RefreshChartReport("all", false);
                            ReportTask("AI 画像设置", "完成", "总开关与提示词已保存，两份图表报告已同步。");
                        }
                        catch (Exception ex) { ReportTask("AI 画像设置", "部分失败", "设置已保存，但图表报告同步失败：" + ex.Message); }
                    };
                    dialog.Controls.Add(layout);
                    var footer = new WallpaperPanel { Dock = DockStyle.Bottom, Height = 52, Padding = new Padding(18, 4, 18, 8) };
                    footer.Controls.Add(aiEnabled);
                    footer.Controls.Add(saveButton);
                    dialog.Controls.Add(footer);
                    PrepareThemedDialog(dialog);
                    dialog.ShowDialog(DialogOwner);
                }
            }
            catch (Exception ex) {
                ReportTask("AI 画像设置", "失败", ex.Message);
                MessageBox.Show(DialogOwner, ex.Message, "AI 设置", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            }
        }

        private string ChartReportProjectPath()
        {
            if (File.Exists(chartReportSettingPath))
            {
                var saved = File.ReadAllText(chartReportSettingPath, Encoding.UTF8).Trim();
                if (saved.Length > 0) return ResolvePortablePath(saved);
            }
            return NeteaseAuth.AccountFolder("Reports");
        }

        private string FindExistingChartReport(string scope)
        {
            try {
                var reportPath = Path.Combine(ChartReportProjectPath(), ".data-app-offline", "exports",
                    "music-profile-report-" + scope + ".html");
                return File.Exists(reportPath) ? reportPath : null;
            }
            catch (Exception ex) {
                WriteLog("查找已有图表报告失败：" + ex.Message);
                return null;
            }
        }

        private async Task<KeyValuePair<string, string>> RefreshChartReport(string scope, bool requestAi = true)
        {
            var project = ChartReportProjectPath();
            Directory.CreateDirectory(project);
            var script = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "portable_report.py");
            if (!File.Exists(script))
                throw new FileNotFoundException("找不到图表报告刷新脚本。", script);
            var python = FindGenrePython();
            return await Task.Run(() => {
                var info = new ProcessStartInfo(python) {
                    Arguments = Quoted(script) + " --scope " + scope + " --output-dir " + Quoted(project) + (requestAi ? " --ai" : ""),
                    WorkingDirectory = Path.GetDirectoryName(script),
                    UseShellExecute = false,
                    CreateNoWindow = true,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    StandardOutputEncoding = Encoding.UTF8,
                    StandardErrorEncoding = Encoding.UTF8
                };
                using (var process = new Process { StartInfo = info }) {
                    process.ErrorDataReceived += delegate(object sender, DataReceivedEventArgs e) { if (e.Data != null) WriteLog(e.Data); };
                    process.Start();
                    process.BeginErrorReadLine();
                    var output = process.StandardOutput.ReadToEnd();
                    process.WaitForExit();
                    var result = output.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries);
                    var fatal = result.FirstOrDefault(line => line.StartsWith("FATAL\t", StringComparison.Ordinal));
                    if (fatal != null) throw new InvalidOperationException(DecodeWorkerText(fatal.Substring(6)));
                    if (process.ExitCode != 0) throw new InvalidOperationException("图表报告进程退出码：" + process.ExitCode);
                    var report = result.FirstOrDefault(line => line.StartsWith("REPORT\t" + scope + "\t", StringComparison.Ordinal));
                    if (report == null) throw new InvalidOperationException("图表报告未返回生成文件。");
                    var path = DecodeWorkerText(report.Substring(("REPORT\t" + scope + "\t").Length));
                    if (!File.Exists(path)) throw new FileNotFoundException("图表报告未生成。", path);
                    if (scope == "both") {
                        var states = new[] { "all", "recent" }.Select(itemScope => {
                            var line = result.FirstOrDefault(item => item.StartsWith("AI_STATUS\t" + itemScope + "\t", StringComparison.Ordinal));
                            var state = line == null ? "unavailable" : line.Substring(("AI_STATUS\t" + itemScope + "\t").Length);
                            var error = result.FirstOrDefault(item => item.StartsWith("AI_ERROR\t" + itemScope + "\t", StringComparison.Ordinal));
                            return error == null ? state : state + "（" + DecodeWorkerText(error.Substring(("AI_ERROR\t" + itemScope + "\t").Length)) + "）";
                        });
                        return new KeyValuePair<string, string>(path, string.Join("|", states));
                    }
                    var ai = result.FirstOrDefault(line => line.StartsWith("AI_STATUS\t" + scope + "\t", StringComparison.Ordinal));
                    return new KeyValuePair<string, string>(path, ai == null ? "unavailable" : ai.Substring(("AI_STATUS\t" + scope + "\t").Length));
                }
            });
        }

        private async Task ExportGenres()
        {
            if (genreSongs.Items.Count == 0) return;
            using (var dialog = new System.Windows.Forms.SaveFileDialog())
            {
                dialog.Title = "导出曲风分析结果";
                dialog.Filter = "CSV 文件 (*.csv)|*.csv";
                dialog.FileName = "网易云曲风分析.csv";
                if (dialog.ShowDialog(DialogOwner) != DialogResult.OK) return;
                try
                {
                    exportGenresButton.Enabled = false;
                    ReportTask("曲风导出", "开始", Path.GetFileName(dialog.FileName));
                    var result = await Task.Run(() => RunGenreWorker(new[] { "--export", dialog.FileName }));
                    if (result.ExitCode != 0 || result.ErrorCount > 0 || !File.Exists(dialog.FileName))
                        ReportTask("曲风导出", "失败", result.FirstError ?? "未生成 CSV；退出码 " + result.ExitCode);
                    else ReportTask("曲风导出", "完成", dialog.FileName);
                }
                catch (Exception ex) { ReportTask("曲风导出", "失败", ex.Message); }
                finally { exportGenresButton.Enabled = genreSongs.Items.Count > 0; }
            }
        }

        private async Task EditGenreResult()
        {
            if (analyzingGenres || genreSongs.SelectedItems.Count != 1) return;
            var item = genreSongs.SelectedItems[0];
            var audioPath = item.Tag as string;
            if (string.IsNullOrEmpty(audioPath) || !File.Exists(audioPath)) return;
            using (var dialog = new Form {
                Text = "手动修正曲风",
                Size = new Size(460, 220),
                MinimumSize = new Size(460, 220),
                MaximumSize = new Size(460, 220),
                StartPosition = FormStartPosition.CenterParent,
                BackColor = panel,
                ForeColor = ink,
                Font = UiFont(9F)
            })
            {
                var title = Label(Path.GetFileNameWithoutExtension(audioPath), 10, true, ink);
                title.Location = new Point(20, 16);
                dialog.Controls.Add(title);
                var primaryLabel = Label("主曲风", 9, false, muted);
                primaryLabel.Location = new Point(20, 53);
                dialog.Controls.Add(primaryLabel);
                var primary = new TextBox { Text = item.SubItems[1].Text == "未分析" ? "" : item.SubItems[1].Text, Location = new Point(112, 49), Width = 310 };
                dialog.Controls.Add(primary);
                var influenceLabel = Label("融合元素（逗号分隔）", 9, false, muted);
                influenceLabel.Location = new Point(20, 88);
                dialog.Controls.Add(influenceLabel);
                var influences = new TextBox { Text = item.SubItems[2].Text, Location = new Point(160, 84), Width = 262 };
                dialog.Controls.Add(influences);
                var save = Button("保存修正", true);
                save.Width = 112;
                save.Location = new Point(188, 130);
                save.DialogResult = DialogResult.OK;
                dialog.Controls.Add(save);
                var cancel = Button("取消", false);
                cancel.Width = 90;
                cancel.Location = new Point(310, 130);
                cancel.DialogResult = DialogResult.Cancel;
                dialog.Controls.Add(cancel);
                dialog.AcceptButton = save;
                dialog.CancelButton = cancel;
                PrepareThemedDialog(dialog);
                if (dialog.ShowDialog(DialogOwner) != DialogResult.OK) return;
                if (string.IsNullOrWhiteSpace(primary.Text)) { ReportTask("曲风修正", "失败", "主曲风不能为空。"); return; }
                try
                {
                    ReportTask("曲风修正", "开始", Path.GetFileName(audioPath));
                    var result = await Task.Run(() => RunGenreWorker(new[] {
                        "--set", audioPath, "--primary", primary.Text.Trim(), "--influences", influences.Text.Trim()
                    }));
                    if (result.ExitCode != 0 || result.ErrorCount > 0) ReportTask("曲风修正", "失败", result.FirstError ?? "退出码 " + result.ExitCode);
                    else ReportTask("曲风修正", "完成", Path.GetFileName(audioPath));
                }
                catch (Exception ex) { ReportTask("曲风修正", "失败", ex.Message); }
            }
        }

        private async Task ExtractSelectedCover()
        {
            var active = songTabs.SelectedIndex == 1 ? convertedSongs : songs;
            if (converting || extractingCover || active.SelectedItems.Count != 1) return;
            var source = active.SelectedItems[0].Tag as string;
            if (source == null || !File.Exists(source)) { ReportTask("封面提取", "失败", "找不到歌曲源文件，请重新扫描。"); return; }
            if (!File.Exists(scriptPath)) { ReportTask("封面提取", "失败", "转换脚本不存在。"); return; }
            string destination;
            using (var dialog = new FolderBrowserDialog())
            {
                dialog.Description = "选择封面图片的保存文件夹";
                dialog.ShowNewFolderButton = true;
                var output = OutputFor(source);
                var preferred = output == null ? CustomOutputDirectoryFor(source) : Path.GetDirectoryName(output);
                dialog.SelectedPath = preferred != null && Directory.Exists(preferred) ? preferred : Path.GetDirectoryName(source);
                if (dialog.ShowDialog(DialogOwner) != DialogResult.OK) return;
                destination = dialog.SelectedPath;
            }
            extractingCover = true;
            UpdateConversionSelectionButtons();
            ReportTask("封面提取", "开始", Path.GetFileName(source));
            WriteLog("开始提取封面：" + Path.GetFileName(source));
            try
            {
                var exitCode = await Task.Run(() => {
                    var info = new ProcessStartInfo("powershell.exe") {
                        Arguments = "-NoLogo -NoProfile -ExecutionPolicy Bypass -File " + Quoted(scriptPath)
                            + " -ExtractCover -CoverOutputDir " + Quoted(destination) + " " + Quoted(source),
                        WorkingDirectory = AppDomain.CurrentDomain.BaseDirectory,
                        UseShellExecute = false,
                        CreateNoWindow = true,
                        RedirectStandardOutput = true,
                        RedirectStandardError = true,
                        StandardOutputEncoding = Encoding.UTF8,
                        StandardErrorEncoding = Encoding.UTF8
                    };
                    using (var process = new Process { StartInfo = info })
                    {
                        process.OutputDataReceived += delegate(object sender, DataReceivedEventArgs e) { if (e.Data != null) WriteLog(e.Data); };
                        process.ErrorDataReceived += delegate(object sender, DataReceivedEventArgs e) { if (e.Data != null) WriteLog(e.Data); };
                        process.Start();
                        process.BeginOutputReadLine();
                        process.BeginErrorReadLine();
                        process.WaitForExit();
                        return process.ExitCode;
                    }
                });
                if (exitCode != 0) ReportTask("封面提取", "失败", "退出码 " + exitCode);
                else ReportTask("封面提取", "完成", Path.GetFileName(source));
            }
            catch (Exception ex) { ReportTask("封面提取", "失败", ex.Message); }
            finally { extractingCover = false; UpdateConversionSelectionButtons(); }
        }

        private async Task ConvertSelectedSongs()
        {
            var reconvert = songTabs.SelectedIndex == 1;
            var active = reconvert ? convertedSongs : songs;
            var selected = active.SelectedItems.Cast<ListViewItem>()
                .Select(item => item.Tag as string)
                .Where(path => path != null && File.Exists(path))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToArray();
            if (selected.Length == 0)
            {
                ReportTask(reconvert ? "重新转换" : "转换歌曲", "失败", "没有可转换的选中歌曲，请重新扫描后再试。");
                UpdateConversionSelectionButtons();
                return;
            }
            await ConvertFiles(selected, reconvert);
        }

        private DedupePalette CurrentDedupePalette()
        {
            return new DedupePalette(background, panel, ink, muted, accent, theme.Border, theme.Secondary,
                theme.ButtonInk, theme.Danger, theme.Success, theme.Error);
        }

        private void OpenSafeDedupe()
        {
            if (converting || deduplicating || extractingCover || analyzingGenres)
            {
                ReportTask("安全去重", "失败", "当前有转换或分析任务进行中，请稍后再试。");
                return;
            }
            if (string.IsNullOrEmpty(InputRoot) || !Directory.Exists(InputRoot))
            {
                ReportTask("安全去重", "失败", "请先选择音乐目录。");
                return;
            }
            if (!File.Exists(scriptPath))
            {
                ReportTask("安全去重", "失败", "转换脚本不存在。");
                return;
            }
            deduplicating = true;
            UpdateConversionSelectionButtons();
            try
            {
                using (var form = new DedupePreviewForm(InputRoot, outputRoot, scriptPath,
                    CurrentDedupePalette(), ReportTask, delegate { ScanSongs(); }))
                {
                    if (IsWpfHosted) form.StartPosition = FormStartPosition.CenterScreen;
                    form.ShowDialog(DialogOwner);
                }
            }
            finally
            {
                deduplicating = false;
                ScanSongs();
                UpdateConversionSelectionButtons();
            }
        }

        private void OpenTrash()
        {
            if (converting || deduplicating || extractingCover || analyzingGenres)
            {
                ReportTask("工具箱回收站", "失败", "当前有转换或分析任务进行中，请稍后再试。");
                return;
            }
            deduplicating = true;
            UpdateConversionSelectionButtons();
            try
            {
                using (var form = new DedupeTrashForm(CurrentDedupePalette(), ReportTask, delegate { ScanSongs(); }))
                {
                    if (IsWpfHosted) form.StartPosition = FormStartPosition.CenterScreen;
                    form.ShowDialog(DialogOwner);
                }
            }
            finally
            {
                deduplicating = false;
                ScanSongs();
                UpdateConversionSelectionButtons();
            }
        }

        private async Task ConvertFiles(string[] snapshot, bool reconvert = false)
        {
            if (converting || deduplicating || extractingCover || snapshot.Length == 0) return;
            var taskName = reconvert ? "重新转换" : "转换歌曲";
            if (!File.Exists(scriptPath)) { ReportTask(taskName, "失败", "转换脚本不存在。"); return; }
            converting = true;
            UpdateConversionSelectionButtons();
            var destination = outputRoot;
            convertButton.Enabled = false;
            deduplicateButton.Enabled = false;
            scanButton.Enabled = false;
            chooseOutputButton.Enabled = false;
            resetOutputButton.Enabled = false;
            convertSelectedButton.Enabled = false;
            convertButton.Text = "正在转换…";
            ReportTask(taskName, "开始", "共 " + snapshot.Length + " 首歌曲。");
            WriteLog(reconvert
                ? "开始重新转换 " + snapshot.Length + " 首已转换歌曲。"
                : "开始转换 " + snapshot.Length + " 首歌曲。");
            var skippedDuplicates = 0;
            try
            {
                if (!reconvert)
                {
                    var decision = await DetectConversionDuplicates(snapshot);
                    if (decision.Cancel)
                    {
                        ReportTask(taskName, "已取消", "已取消转换，未修改任何文件。");
                        return;
                    }
                    snapshot = decision.Files;
                    skippedDuplicates = decision.Skipped;
                    if (snapshot.Length == 0)
                    {
                        ReportTask(taskName, "完成", "所选歌曲均已有更高质量版本，未执行转换；源 NCM 未删除。");
                        return;
                    }
                    if (skippedDuplicates > 0)
                        ReportTask(taskName, "开始", "已跳过 " + skippedDuplicates + " 首低音质重复项，准备转换 " + snapshot.Length + " 首。");
                }
                var conversionTaskId = Guid.NewGuid().ToString("N");
                var result = await Task.Run(() => {
                    var outcome = new WorkerResult();
                    var gate = new object();
                    for (int offset = 0; offset < snapshot.Length; offset += 30)
                    {
                        var batch = snapshot.Skip(offset).Take(30).ToArray();
                        var awaitingErrorDetail = false;
                        var info = new ProcessStartInfo("powershell.exe") {
                            Arguments = "-NoLogo -NoProfile -ExecutionPolicy Bypass -File " + Quoted(scriptPath)
                                + (destination == null ? "" : " -OutputDir " + Quoted(destination))
                                + " -SourceRoot " + Quoted(InputRoot)
                                + " -ConversionTaskId " + Quoted(conversionTaskId)
                                + " " + string.Join(" ", batch.Select(Quoted)),
                            WorkingDirectory = AppDomain.CurrentDomain.BaseDirectory,
                            UseShellExecute = false,
                            CreateNoWindow = true,
                            RedirectStandardOutput = true,
                            RedirectStandardError = true,
                            StandardOutputEncoding = Encoding.UTF8,
                            StandardErrorEncoding = Encoding.UTF8
                        };
                        using (var process = new Process { StartInfo = info })
                        {
                            process.OutputDataReceived += delegate(object sender, DataReceivedEventArgs e) {
                                if (e.Data == null) return;
                                if (e.Data.StartsWith("失败：", StringComparison.Ordinal)) {
                                    lock (gate) {
                                        outcome.ErrorCount++;
                                        awaitingErrorDetail = outcome.FirstError == null;
                                        if (awaitingErrorDetail) outcome.FirstError = e.Data;
                                    }
                                }
                                else if (e.Data.StartsWith("完成：", StringComparison.Ordinal)) {
                                    lock (gate) { outcome.CompletedCount++; awaitingErrorDetail = false; }
                                }
                                else {
                                    lock (gate) {
                                        if (awaitingErrorDetail && !string.IsNullOrWhiteSpace(e.Data)) {
                                            outcome.FirstError += "；" + e.Data;
                                            awaitingErrorDetail = false;
                                        }
                                    }
                                }
                                WriteLog(e.Data);
                            };
                            process.ErrorDataReceived += delegate(object sender, DataReceivedEventArgs e) {
                                if (e.Data == null) return;
                                lock (gate) { if (outcome.FirstError == null) outcome.FirstError = e.Data; }
                                WriteLog(e.Data);
                            };
                            process.Start();
                            process.BeginOutputReadLine();
                            process.BeginErrorReadLine();
                            process.WaitForExit();
                            if (process.ExitCode != 0) { outcome.ExitCode = process.ExitCode; WriteLog("转换进程退出码：" + process.ExitCode); }
                        }
                    }
                    return outcome;
                });
                if (result.ExitCode != 0 || result.ErrorCount > 0)
                    ReportTask(taskName, result.CompletedCount > 0 ? "部分失败" : "失败", result.CompletedCount + " 首完成，" + result.ErrorCount + " 首失败" +
                        (result.FirstError == null ? "；进程退出码 " + result.ExitCode : "；" + result.FirstError));
                else ReportTask(taskName, "完成", "已转换 " + result.CompletedCount + " 首歌曲。"
                    + (skippedDuplicates > 0 ? "已按重复检测跳过 " + skippedDuplicates + " 首低音质重复项，源 NCM 未删除。" : "")
                    + (result.CompletedCount < snapshot.Length ? "部分歌曲未完成，请查看任务消息。" : ""));
            }
            catch (Exception ex) { ReportTask(taskName, "失败", ex.Message); }
            finally
            {
                converting = false;
                scanButton.Enabled = true;
                chooseOutputButton.Enabled = true;
                UpdateOutputStatus();
                convertButton.Text = "转换全部新歌";
                deduplicateButton.Enabled = true;
                ScanSongs();
            }
        }

        private sealed class ConversionDedupeDecision
        {
            public bool Cancel;
            public string[] Files;
            public int Skipped;
        }

        private async Task<ConversionDedupeDecision> DetectConversionDuplicates(string[] snapshot)
        {
            var decision = new ConversionDedupeDecision { Files = snapshot, Skipped = 0 };
            if (string.IsNullOrWhiteSpace(InputRoot) || !Directory.Exists(InputRoot) || !File.Exists(scriptPath))
                return decision;
            ReportTask("重复检测", "开始", "正在只读检查待转换歌曲是否已有更高质量版本。");
            try
            {
                var options = new DedupePlanOptions
                {
                    Root = InputRoot,
                    OutputRoot = outputRoot,
                    Recurse = true,
                    ShowAssociated = false,
                    IncludeSuspected = false,
                    VerifyAssociatedHashes = false
                };
                var plan = await Task.Run(() =>
                {
                    var scan = DedupeScanner.Scan(scriptPath, InputRoot, true, CancellationToken.None);
                    return DedupePlanner.Build(scan, options, new List<ConversionRecord>(), new List<string>());
                });
                if (IsDisposed) return decision;
                var recommended = new HashSet<string>(
                    plan.Groups.SelectMany(group => group.Files).Where(file => file.IsSelected)
                        .Select(file => DedupeFileSystem.Normalize(file.Path)),
                    StringComparer.OrdinalIgnoreCase);
                var skip = snapshot.Where(path => recommended.Contains(DedupeFileSystem.Normalize(path)))
                    .Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
                if (skip.Length == 0)
                {
                    ReportTask("重复检测", "完成", "未发现需要跳过的低音质重复项。");
                    return decision;
                }
                var names = string.Join("\r\n", skip.Take(8).Select(path => Path.GetFileName(path)).ToArray());
                if (skip.Length > 8) names += "\r\n…";
                var message = "发现 " + skip.Length + " 首待转换歌曲已有更高质量的同曲版本。\r\n\r\n"
                    + names + "\r\n\r\n是：跳过这些低音质版本并继续转换\r\n否：仍然全部转换\r\n取消：取消本次转换"
                    + "\r\n\r\n跳过和取消都不会删除任何源 NCM 文件。";
                var choice = MessageBox.Show(DialogOwner, message, "转换前重复检测",
                    MessageBoxButtons.YesNoCancel, MessageBoxIcon.Question, MessageBoxDefaultButton.Button1);
                if (choice == DialogResult.Cancel)
                {
                    decision.Cancel = true;
                    ReportTask("重复检测", "已取消", "已取消转换，未修改任何文件。");
                    return decision;
                }
                if (choice == DialogResult.No)
                {
                    ReportTask("重复检测", "完成", "已选择仍然全部转换，未删除任何文件。");
                    return decision;
                }
                var skipSet = new HashSet<string>(skip.Select(path => DedupeFileSystem.Normalize(path)), StringComparer.OrdinalIgnoreCase);
                decision.Files = snapshot.Where(path => !skipSet.Contains(DedupeFileSystem.Normalize(path))).ToArray();
                decision.Skipped = skip.Length;
                ReportTask("重复检测", "完成", "已选择跳过 " + skip.Length + " 首低音质重复项；源 NCM 未删除。");
                return decision;
            }
            catch (Exception ex)
            {
                ReportTask("重复检测", "部分失败", "检查失败，将继续转换且不会删除任何文件：" + ex.Message);
                return decision;
            }
        }

        private const string LyricStopEventName = @"Local\NcmToolboxLyricStop";
        private const string LyricTwoLineEventName = @"Local\NcmToolboxLyricTwoLine";
        private EventWaitHandle lyricTwoLineEvent;

        private bool LyricStopEventExists()
        {
            try
            {
                using (EventWaitHandle.OpenExisting(LyricStopEventName)) return true;
            }
            catch (WaitHandleCannotBeOpenedException) { return false; }
            catch (Exception) { return false; }
        }

        private bool LyricHelperRunning()
        {
            return LyricHelperIds().Count > 0 || LyricStopEventExists();
        }

        private EventWaitHandle LyricTwoLineHandle()
        {
            if (lyricTwoLineEvent == null)
            {
                try { lyricTwoLineEvent = new EventWaitHandle(false, EventResetMode.ManualReset, LyricTwoLineEventName); }
                catch (Exception ex) { WriteLog("无法创建歌词模式事件：" + ex.Message); return null; }
            }
            return lyricTwoLineEvent;
        }

        private bool LyricTwoLine()
        {
            var handle = LyricTwoLineHandle();
            return handle != null && handle.WaitOne(0);
        }

        private void SetLyricTwoLine(bool two)
        {
            var handle = LyricTwoLineHandle();
            if (handle == null) return;
            if (two) handle.Set(); else handle.Reset();
        }

        private List<int> LyricHelperIds()
        {
            var ids = new List<int>();
            try
            {
                using (var search = new ManagementObjectSearcher("SELECT ProcessId, ExecutablePath, CommandLine FROM Win32_Process WHERE Name='python.exe'"))
                using (var results = search.Get())
                {
                    foreach (ManagementObject process in results)
                    {
                        var exe = Convert.ToString(process["ExecutablePath"]);
                        var cmd = Convert.ToString(process["CommandLine"]);
                        var legacyLauncher = Path.GetFullPath(Path.Combine(AppDomain.CurrentDomain.BaseDirectory, @"..\Lyric3\launcher.py"));
                        if (cmd.IndexOf(launcherPath, StringComparison.OrdinalIgnoreCase) < 0 && cmd.IndexOf(legacyLauncher, StringComparison.OrdinalIgnoreCase) < 0) continue;
                        ids.Add(Convert.ToInt32(process["ProcessId"]));
                    }
                }
            }
            catch (Exception ex) { WriteLog("无法检查歌词进程：" + ex.Message); }
            return ids;
        }

        private void UpdateLyricStatus()
        {
            bool on = LyricHelperRunning();
            bool two = on && LyricTwoLine();
            bool three = on && !two;
            lyricStatus.Text = !on ? "○ 三行歌词已关闭" : two ? "○ 三行歌词已关闭 · 两行小字" : "● 三行歌词已开启";
            lyricStatus.ForeColor = three ? theme.Success : muted;
            lyricButton.Text = three ? "关闭" : "开启";
            lyricStatus.Left = Math.Max(260, lyricButton.Left - lyricStatus.PreferredWidth - 18);
            LyricStatusChanged?.Invoke(three, !on ? "桌面三行歌词：已关闭" : two ? "桌面三行歌词：两行小字" : "桌面三行歌词：已开启");
        }

        private void StartLyrics(bool automatic)
        {
            if (LyricHelperRunning()) { UpdateLyricStatus(); return; }
            if (!File.Exists(pythonPath) || !File.Exists(launcherPath) || !File.Exists(Path.Combine(ClientFolder(), "cloudmusic.exe")))
            {
                if (!automatic) ReportTask("三行歌词", "失败", "请在设置与维护中选择网易云安装目录，并保留随包的 Lyric3 文件夹。");
                UpdateLyricStatus();
                return;
            }
            SetLyricTwoLine(false);
            try
            {
                ReportTask("三行歌词", "开始", "正在校验网易云客户端版本并连接歌词补丁。");
                var helper = new Process { StartInfo = new ProcessStartInfo(pythonPath) {
                    Arguments = Quoted(launcherPath) + " --app-dir " + Quoted(ClientFolder()),
                    WorkingDirectory = LyricFolder(), UseShellExecute = false, CreateNoWindow = true,
                    RedirectStandardOutput = true, RedirectStandardError = true,
                    StandardOutputEncoding = Encoding.UTF8, StandardErrorEncoding = Encoding.UTF8
                } };
                helper.OutputDataReceived += delegate(object sender, DataReceivedEventArgs args) {
                    if (args.Data == null) return;
                    WriteLog(args.Data);
                    if (args.Data.Contains("补丁已连接")) ReportTask("三行歌词", "完成", args.Data);
                    if (args.Data.Contains("版本已变化") || args.Data.Contains("未找到") || args.Data.Contains("无法连接")) ReportTask("三行歌词", "失败", args.Data);
                };
                helper.ErrorDataReceived += delegate(object sender, DataReceivedEventArgs args) { if (args.Data != null) ReportTask("三行歌词", "失败", args.Data); };
                helper.Start(); helper.BeginOutputReadLine(); helper.BeginErrorReadLine();
                Task.Run(() => { helper.WaitForExit(); if (helper.ExitCode != 0) ReportTask("三行歌词", "失败", "歌词启动器退出码：" + helper.ExitCode); helper.Dispose(); });
            }
            catch (Exception ex) { WriteLog("启动歌词失败：" + ex.Message); }
            UpdateLyricStatus();
        }

        internal void ToggleLyrics()
        {
            if (!LyricHelperRunning())
            {
                SetLyricTwoLine(false);
                StartLyrics(false);
                return;
            }
            bool two = LyricTwoLine();
            SetLyricTwoLine(!two);
            ReportTask("三行歌词", "完成", two ? "已恢复三行歌词。" : "已关闭三行歌词，保留两行小字。");
            UpdateLyricStatus();
        }

        private void WriteLog(string message)
        {
            if (IsDisposed) return;
            if (!IsWpfHosted)
            {
                if (!IsHandleCreated) return;
                if (InvokeRequired) { BeginInvoke((Action<string>)WriteLog, message); return; }
            }
            else if (System.Windows.Application.Current != null && !System.Windows.Application.Current.Dispatcher.CheckAccess())
            {
                System.Windows.Application.Current.Dispatcher.BeginInvoke((Action)(() => WriteLog(message)));
                return;
            }
            if (string.IsNullOrWhiteSpace(message)) return;
            log.AppendText(DateTime.Now.ToString("HH:mm:ss") + "  " + message + Environment.NewLine);
            if (log.Lines.Length > 300) log.Lines = log.Lines.Skip(log.Lines.Length - 200).ToArray();
        }

        private void ReportTask(string task, string phase, string detail)
        {
            if (IsDisposed) return;
            if (!IsWpfHosted)
            {
                if (!IsHandleCreated) return;
                if (InvokeRequired) { BeginInvoke((Action)(() => ReportTask(task, phase, detail))); return; }
            }
            else if (System.Windows.Application.Current != null && !System.Windows.Application.Current.Dispatcher.CheckAccess())
            {
                System.Windows.Application.Current.Dispatcher.BeginInvoke((Action)(() => ReportTask(task, phase, detail)));
                return;
            }
            var message = DateTime.Now.ToString("HH:mm:ss") + "  " + task + " · " + phase
                + (string.IsNullOrWhiteSpace(detail) ? "" : "：" + detail.Trim());
            taskStatus.Text = message;
            TaskReported?.Invoke(task, phase, detail);
            taskStatus.ForeColor = phase == "失败" || phase == "部分失败" ? theme.Error
                : phase == "完成" ? theme.Success : ink;
            tips.SetToolTip(taskStatus, message);
            taskMessages.Items.Insert(0, message);
            while (taskMessages.Items.Count > 100) taskMessages.Items.RemoveAt(taskMessages.Items.Count - 1);
            if (phase == "失败" || phase == "部分失败") taskMessagesButton.ForeColor = theme.Error;
            WriteLog(message);
        }

        private void ConfigureTrayIcon()
        {
            var menu = new ContextMenuStrip();
            menu.Items.Add("打开网易云工具箱", null, delegate { ShowMainWindow(); });
            menu.Items.Add("打开播放记录文件夹", null, delegate { OpenPlaybackHistory(); });
            menu.Items.Add(new ToolStripSeparator());
            menu.Items.Add("退出工具箱（下次登录继续记录）", null, delegate { ExitToolbox(false); });
            menu.Items.Add("关闭自动捕获并退出", null, delegate { ExitToolbox(true); });
            trayIcon = new NotifyIcon {
                Text = "网易云播放记录自动捕获",
                Icon = AppIcon,
                ContextMenuStrip = menu,
                Visible = true
            };
            trayIcon.DoubleClick += delegate { ShowMainWindow(); };
        }

        private void EnsureAutoStart()
        {
            const string runPath = @"Software\Microsoft\Windows\CurrentVersion\Run";
            const string valueName = "NeteaseToolboxPlaybackCapture";
            var command = "\"" + Application.ExecutablePath + "\" --background";
            using (var key = Registry.CurrentUser.CreateSubKey(runPath))
            {
                if (key == null) throw new InvalidOperationException("无法注册 Windows 登录启动项。");
                var current = key.GetValue(valueName) as string;
                if (!string.Equals(current, command, StringComparison.OrdinalIgnoreCase))
                    key.SetValue(valueName, command, RegistryValueKind.String);
            }
        }

        private void RemoveAutoStart()
        {
            using (var key = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Run", true))
                if (key != null) key.DeleteValue("NeteaseToolboxPlaybackCapture", false);
        }

        private void OpenPlaybackHistory()
        {
            try
            {
                var directory = Path.GetDirectoryName(playbackHistoryPath);
                Directory.CreateDirectory(directory);
                Process.Start("explorer.exe", Quoted(directory));
            }
            catch (Exception ex) { ReportTask("播放记录", "失败", "无法打开记录文件夹：" + ex.Message); }
        }

        private void ShowMainWindow()
        {
            if (IsWpfHosted && System.Windows.Application.Current != null)
            {
                System.Windows.Application.Current.Dispatcher.BeginInvoke((Action)(() => {
                    var win = System.Windows.Application.Current.MainWindow;
                    if (win != null)
                    {
                        if (!win.IsVisible) win.Show();
                        if (win.WindowState == System.Windows.WindowState.Minimized)
                            win.WindowState = System.Windows.WindowState.Normal;
                        win.Activate();
                        win.Topmost = true;
                        win.Topmost = false;
                        win.Focus();
                    }
                }));
                return;
            }
            if (!Visible) Show();
            WindowState = FormWindowState.Normal;
            Activate();
        }

        private void ExitToolbox(bool disableAutoStart)
        {
            try
            {
                if (disableAutoStart) RemoveAutoStart();
                allowExit = true;
                if (trayIcon != null) trayIcon.Visible = false;
                if (IsWpfHosted && System.Windows.Application.Current != null)
                {
                    System.Windows.Application.Current.Dispatcher.BeginInvoke((Action)(() => {
                        try { Close(); } catch { }
                        System.Windows.Application.Current.Shutdown();
                    }));
                    return;
                }
                Application.Exit();
            }
            catch (Exception ex) { ReportTask("播放记录", "失败", ex.Message); }
        }

        private void ShowTaskMessagesWindow()
        {
            if (taskMessagesWindow == null || taskMessagesWindow.IsDisposed)
            {
                taskMessagesWindow = new Form {
                    Text = "任务消息", Size = new Size(720, 400), MinimumSize = new Size(480, 280),
                    StartPosition = FormStartPosition.CenterParent, BackColor = panel, ForeColor = ink
                };
                taskMessages.Dock = DockStyle.Fill;
                taskMessages.BackColor = panel;
                taskMessages.ForeColor = ink;
                taskMessages.Font = UiFont(10F);
                taskMessages.HorizontalScrollbar = true;
                taskMessagesWindow.Controls.Add(taskMessages);
                taskMessagesWindow.FormClosing += delegate(object sender, FormClosingEventArgs e) {
                    if (e.CloseReason == CloseReason.UserClosing) { e.Cancel = true; taskMessagesWindow.Hide(); }
                };
            }
            taskMessagesButton.ForeColor = theme.ButtonInk;
            PrepareThemedDialog(taskMessagesWindow);
            if (!taskMessagesWindow.Visible) { var owner = DialogOwner; if (owner != null) taskMessagesWindow.Show(owner); else taskMessagesWindow.Show(); }
            else { taskMessagesWindow.WindowState = FormWindowState.Normal; taskMessagesWindow.Activate(); }
        }

        protected override void OnFormClosing(FormClosingEventArgs e)
        {
            if (e.CloseReason == CloseReason.UserClosing && !allowExit)
            {
                e.Cancel = true;
                // Hidden forms already disappear from the taskbar. Changing
                // ShowInTaskbar here recreates every child HWND before hiding.
                Hide();
                return;
            }
            base.OnFormClosing(e);
        }

        protected override void OnFormClosed(FormClosedEventArgs e)
        {
            StopDailySignin();
            audioUiTimer.Dispose();
            audioForwarder.Dispose();
            multiListenTimer.Dispose();
            StopPhoneSyncServer();
            StopLocalPlayback();
            if (playbackRecorder != null) playbackRecorder.Dispose();
            if (trayIcon != null) { trayIcon.Visible = false; trayIcon.Dispose(); }
            if (watcher != null) watcher.Dispose();
            scanTimer.Dispose();
            statusTimer.Dispose();
            tips.Dispose();
            base.OnFormClosed(e);
        }
    }
}
