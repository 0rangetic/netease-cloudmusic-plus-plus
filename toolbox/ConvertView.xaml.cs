using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;

namespace NeteaseToolbox.Wpf
{
    /// <summary>
    /// 原生 WPF 音乐转换页。业务逻辑仍由 ToolboxForm 的 WinForms 页面持有，
    /// 这里只轮询快照、呈现两个歌曲列表并触发既有操作。
    /// </summary>
    public partial class ConvertView : UserControl
    {
        private readonly ToolboxForm facade;
        private readonly DispatcherTimer refreshTimer;

        private List<ConvertSongRow> pendingRows = new List<ConvertSongRow>();
        private List<ConvertSongRow> convertedRows = new List<ConvertSongRow>();
        private List<ConvertTaskRow> taskRows = new List<ConvertTaskRow>();

        private ConvertPageSnapshot lastSnapshot;
        private string pendingVersion = "";
        private string convertedVersion = "";
        private string taskSignature = "";
        private bool refreshing;
        private bool operationBusy;

        private static readonly Brush SuccessBrush = Frozen(42, 157, 143);
        private static readonly Brush WarningBrush = Frozen(233, 150, 62);
        private static readonly Brush ErrorBrush = Frozen(231, 111, 81);
        private static readonly Brush RunningBrush = Frozen(210, 166, 42);
        private static readonly Brush ErrorPanelBrush = Frozen(0x26, 231, 111, 81);
        private static readonly Brush ErrorPanelBorder = Frozen(0x59, 231, 111, 81);
        private static readonly Brush PartialPanelBrush = Frozen(0x26, 233, 150, 62);
        private static readonly Brush PartialPanelBorder = Frozen(0x59, 233, 150, 62);

        public ConvertView() : this(null) { }

        internal ConvertView(ToolboxForm facade)
        {
            this.facade = facade ?? ToolboxForm.Instance;
            InitializeComponent();

            PendingGrid.ItemsSource = pendingRows;
            ConvertedGrid.ItemsSource = convertedRows;
            TaskList.ItemsSource = taskRows;

            refreshTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(1500) };
            refreshTimer.Tick += delegate { Refresh(false); };

            Loaded += OnLoaded;
            Unloaded += delegate { refreshTimer.Stop(); };
            IsVisibleChanged += delegate
            {
                if (!IsLoaded) return;
                if (IsVisible) { refreshTimer.Start(); Refresh(false); }
                else refreshTimer.Stop();
            };

            UpdateActionStates();
        }

        private static Brush Frozen(byte red, byte green, byte blue)
        {
            var brush = new SolidColorBrush(Color.FromRgb(red, green, blue));
            brush.Freeze();
            return brush;
        }

        private static Brush Frozen(byte alpha, byte red, byte green, byte blue)
        {
            var brush = new SolidColorBrush(Color.FromArgb(alpha, red, green, blue));
            brush.Freeze();
            return brush;
        }

        internal static Brush PhaseColor(string phase)
        {
            if (phase == "完成") return SuccessBrush;
            if (phase == "部分失败") return WarningBrush;
            if (phase == "失败") return ErrorBrush;
            if (phase == "开始" || phase == "进行中") return RunningBrush;
            var app = Application.Current;
            var brush = app == null ? null : app.TryFindResource("SystemControlForegroundBaseMediumBrush") as Brush;
            return brush ?? SuccessBrush;
        }

        private void OnLoaded(object sender, RoutedEventArgs e)
        {
            refreshTimer.Start();
            Refresh(true);
        }

        // ------------------------------------------------------------------
        // 轮询
        // ------------------------------------------------------------------
        private void Refresh(bool force)
        {
            if (refreshing) return;
            if (facade == null)
            {
                ShowError("未找到工具箱转换服务，请重启工具箱。");
                return;
            }
            refreshing = true;
            try
            {
                var snapshot = facade.ReadConvertSnapshot();
                lastSnapshot = snapshot;
                ApplySnapshot(snapshot, force);
            }
            catch (Exception ex) { ShowError(ex.Message); }
            finally { refreshing = false; }
        }

        private void ApplySnapshot(ConvertPageSnapshot snapshot, bool force)
        {
            InputFolderText.Text = string.IsNullOrWhiteSpace(snapshot.InputFolder)
                ? "尚未选择，点击右上角“选择音乐目录”"
                : snapshot.InputFolder;
            InputFolderText.ToolTip = snapshot.InputFolder;

            string output = string.IsNullOrWhiteSpace(snapshot.OutputStatus)
                ? "输出位置：默认（每首歌曲目录下的 unlock 文件夹）"
                : snapshot.OutputStatus;
            OutputText.Text = output;
            OutputText.ToolTip = output;

            PendingCountText.Text = snapshot.PendingCount + " 首";
            ConvertedCountText.Text = snapshot.ConvertedCount + " 首";
            string pendingHeader = snapshot.PendingCount > 0 ? "待转换歌曲 · " + snapshot.PendingCount : "待转换歌曲";
            string convertedHeader = snapshot.ConvertedCount > 0 ? "已转换歌曲 · " + snapshot.ConvertedCount : "已转换歌曲";
            if (!Equals(PendingTab.Header, pendingHeader)) PendingTab.Header = pendingHeader;
            if (!Equals(ConvertedTab.Header, convertedHeader)) ConvertedTab.Header = convertedHeader;

            if (force || snapshot.PendingVersion != pendingVersion) RebuildPending(snapshot);
            if (force || snapshot.ConvertedVersion != convertedVersion) RebuildConverted(snapshot);

            ApplyStatus(snapshot);
            ApplyMessages();
            UpdateActionStates();
        }

        private void RebuildPending(ConvertPageSnapshot snapshot)
        {
            var selected = new HashSet<string>(
                PendingGrid.SelectedItems.Cast<ConvertSongRow>().Select(row => row.FullPath),
                StringComparer.OrdinalIgnoreCase);
            var entries = facade.ReadConvertSongs(false);
            var rows = new List<ConvertSongRow>(entries.Count);
            foreach (var entry in entries) rows.Add(new ConvertSongRow(entry));
            pendingRows = rows;
            pendingVersion = snapshot.PendingVersion;
            PendingGrid.ItemsSource = rows;
            if (selected.Count > 0)
            {
                PendingGrid.SelectedItems.Clear();
                foreach (var row in rows)
                    if (selected.Contains(row.FullPath)) PendingGrid.SelectedItems.Add(row);
            }
            PendingEmptyText.Visibility = rows.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        }

        private void RebuildConverted(ConvertPageSnapshot snapshot)
        {
            var selected = new HashSet<string>(
                ConvertedGrid.SelectedItems.Cast<ConvertSongRow>().Select(row => row.FullPath),
                StringComparer.OrdinalIgnoreCase);
            var entries = facade.ReadConvertSongs(true);
            var rows = new List<ConvertSongRow>(entries.Count);
            foreach (var entry in entries) rows.Add(new ConvertSongRow(entry));
            convertedRows = rows;
            convertedVersion = snapshot.ConvertedVersion;
            ConvertedGrid.ItemsSource = rows;
            if (selected.Count > 0)
            {
                ConvertedGrid.SelectedItems.Clear();
                foreach (var row in rows)
                    if (selected.Contains(row.FullPath)) ConvertedGrid.SelectedItems.Add(row);
            }
            ConvertedEmptyText.Visibility = rows.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        }

        private void ApplyStatus(ConvertPageSnapshot snapshot)
        {
            bool busy = snapshot.IsBusy || operationBusy;
            string phase = snapshot.LastPhase ?? "";
            string message = snapshot.LastMessageText;
            if (string.IsNullOrWhiteSpace(message))
            {
                if (busy) message = "正在处理转换任务，请稍候…";
                else if (!string.IsNullOrEmpty(snapshot.Summary) && snapshot.Summary.StartsWith("正在扫描", StringComparison.Ordinal))
                    message = "正在扫描本地歌曲…";
                else message = "就绪：选择音乐目录后会自动扫描，转换进度会显示在这里。";
            }
            StatusText.Text = message;
            StatusDot.Fill = StatusBrush(phase, busy);

            string error = null;
            bool partial = false;
            if (!string.IsNullOrEmpty(snapshot.Error)) error = snapshot.Error;
            else if (phase == "失败") error = snapshot.LastMessageText;
            else if (phase == "部分失败")
            {
                error = snapshot.LastMessageText;
                partial = true;
            }
            else if (!string.IsNullOrWhiteSpace(snapshot.InputFolder)
                && !string.IsNullOrEmpty(snapshot.Summary)
                && snapshot.Summary.StartsWith("扫描失败：", StringComparison.Ordinal))
                error = snapshot.Summary;
            if (!string.IsNullOrEmpty(error)) ShowError(error, partial);
            else HideError();

            string warning = null;
            if (!snapshot.ScriptAvailable)
                warning = "未找到转换脚本 convert.ps1，转换和封面提取暂时不可用，请确认工具箱文件完整。";
            else if (string.IsNullOrWhiteSpace(snapshot.InputFolder))
                warning = "尚未选择音乐目录：点击“选择音乐目录”选择本机的 NCM 缓存文件夹，扫描和转换才会开始。";
            if (!string.IsNullOrEmpty(warning))
            {
                WarningText.Text = warning;
                WarningPanel.Visibility = Visibility.Visible;
            }
            else WarningPanel.Visibility = Visibility.Collapsed;
        }

        private Brush StatusBrush(string phase, bool busy)
        {
            if (busy) return RunningBrush;
            if (phase == "失败") return ErrorBrush;
            if (phase == "部分失败") return WarningBrush;
            if (phase == "完成") return SuccessBrush;
            if (phase == "开始" || phase == "进行中") return RunningBrush;
            return (TryFindResource("SystemControlHighlightAccentBrush") as Brush) ?? SuccessBrush;
        }

        private void ApplyMessages()
        {
            var messages = facade.ReadConvertRecentMessages(6);
            string signature = messages.Count == 0
                ? "0"
                : string.Join("|", messages.Select(message => message.Text));
            if (signature == taskSignature) return;
            taskSignature = signature;

            var rows = new List<ConvertTaskRow>(messages.Count);
            foreach (var message in messages) rows.Add(new ConvertTaskRow(message));
            taskRows = rows;
            TaskList.ItemsSource = rows;
            TaskEmptyText.Visibility = rows.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
            if (TaskScroll != null) TaskScroll.ScrollToTop();
        }

        private void UpdateActionStates()
        {
            // XAML 初始化期间 TabControl 可能先于后续元素创建，这里统一兜底。
            if (ConvertAllButton == null || SongTabs == null || ConvertSelectedButton == null
                || PendingGrid == null || ConvertedGrid == null || SelectionHintText == null
                || ChooseInputButton == null || RescanButton == null
                || ExtractCoverButton == null || OpenOutputButton == null
                || BusyBar == null || BusyText == null) return;

            var snapshot = lastSnapshot;
            bool busy = operationBusy || (snapshot != null && snapshot.IsBusy);
            bool script = snapshot != null && snapshot.ScriptAvailable;
            bool convertedTab = SongTabs.SelectedIndex == 1;
            var grid = convertedTab ? ConvertedGrid : PendingGrid;
            int selected = grid == null ? 0 : grid.SelectedItems.Count;

            ChooseInputButton.IsEnabled = !busy;
            RescanButton.IsEnabled = !busy;
            ConvertAllButton.IsEnabled = !busy && script && snapshot != null && snapshot.PendingCount > 0;
            ConvertSelectedButton.Content = convertedTab ? "重新转换选中" : "转换选中歌曲";
            ConvertSelectedButton.IsEnabled = !busy && script && selected > 0;
            ExtractCoverButton.IsEnabled = !busy && script && selected == 1;
            OpenOutputButton.IsEnabled = !busy && snapshot != null && snapshot.HasLatestOutput;
            SelectionHintText.Text = selected == 0
                ? (convertedTab ? "未选择歌曲 · 选中已转换歌曲可重新转换" : "未选择歌曲 · 按住 Ctrl 或 Shift 可多选")
                : "已选 " + selected + " 首 · 按住 Ctrl 或 Shift 可继续多选";

            if (BusyBar != null)
            {
                BusyBar.Visibility = busy ? Visibility.Visible : Visibility.Collapsed;
                BusyText.Visibility = busy ? Visibility.Visible : Visibility.Collapsed;
                BusyText.Text = snapshot == null ? "处理中…"
                    : snapshot.Converting ? "正在转换…"
                    : snapshot.ExtractingCover ? "正在提取封面…"
                    : snapshot.Deduplicating ? "正在处理…"
                    : "处理中…";
            }
        }

        private void ShowError(string message, bool partial = false)
        {
            if (string.IsNullOrWhiteSpace(message)) return;
            ErrorPanel.Background = partial ? PartialPanelBrush : ErrorPanelBrush;
            ErrorPanel.BorderBrush = partial ? PartialPanelBorder : ErrorPanelBorder;
            ErrorText.Text = message;
            ErrorPanel.Visibility = Visibility.Visible;
        }

        private void HideError()
        {
            ErrorText.Text = "";
            ErrorPanel.Visibility = Visibility.Collapsed;
        }

        // ------------------------------------------------------------------
        // 操作
        // ------------------------------------------------------------------
        private void OnChooseInputClick(object sender, RoutedEventArgs e)
        {
            if (facade == null) { ShowError("未找到工具箱转换服务，请重启工具箱。"); return; }
            try { facade.ConvertChooseInputFromView(); }
            catch (Exception ex) { ShowError(ex.Message); }
            Refresh(true);
        }

        private void OnRescanClick(object sender, RoutedEventArgs e)
        {
            if (facade == null) { ShowError("未找到工具箱转换服务，请重启工具箱。"); return; }
            // 扫描在 UI 线程同步执行，先让忙碌态画出来再开始。
            operationBusy = true;
            UpdateActionStates();
            Dispatcher.BeginInvoke(DispatcherPriority.Background, new Action(() =>
            {
                try { facade.ConvertRescanFromView(); }
                catch (Exception ex) { ShowError(ex.Message); }
                finally
                {
                    operationBusy = false;
                    Refresh(true);
                }
            }));
        }

        private async void OnConvertAllClick(object sender, RoutedEventArgs e)
        {
            if (facade == null || operationBusy) return;
            operationBusy = true;
            UpdateActionStates();
            try { await facade.ConvertAllPendingFromViewAsync(); }
            catch (Exception ex) { ShowError(ex.Message); }
            finally
            {
                operationBusy = false;
                Refresh(true);
            }
        }

        private async void OnConvertSelectedClick(object sender, RoutedEventArgs e)
        {
            if (facade == null || operationBusy) return;
            bool convertedTab = SongTabs.SelectedIndex == 1;
            var grid = convertedTab ? ConvertedGrid : PendingGrid;
            var paths = grid.SelectedItems.Cast<ConvertSongRow>()
                .Select(row => row.FullPath)
                .Where(path => !string.IsNullOrWhiteSpace(path))
                .ToArray();
            if (paths.Length == 0) { UpdateActionStates(); return; }

            operationBusy = true;
            UpdateActionStates();
            try { await facade.ConvertSelectedFromViewAsync(convertedTab, paths); }
            catch (Exception ex) { ShowError(ex.Message); }
            finally
            {
                operationBusy = false;
                Refresh(true);
            }
        }

        private async void OnExtractCoverClick(object sender, RoutedEventArgs e)
        {
            if (facade == null || operationBusy) return;
            bool convertedTab = SongTabs.SelectedIndex == 1;
            var grid = convertedTab ? ConvertedGrid : PendingGrid;
            var row = grid.SelectedItems.Cast<ConvertSongRow>().FirstOrDefault();
            if (row == null || string.IsNullOrWhiteSpace(row.FullPath)) { UpdateActionStates(); return; }

            operationBusy = true;
            UpdateActionStates();
            try { await facade.ConvertExtractCoverFromViewAsync(row.FullPath, convertedTab); }
            catch (Exception ex) { ShowError(ex.Message); }
            finally
            {
                operationBusy = false;
                Refresh(true);
            }
        }

        private void OnOpenOutputClick(object sender, RoutedEventArgs e)
        {
            if (facade == null) { ShowError("未找到工具箱转换服务，请重启工具箱。"); return; }
            try { facade.ConvertOpenLatestOutputFromView(); }
            catch (Exception ex) { ShowError(ex.Message); }
            Refresh(true);
        }

        private void OnTabSelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (ConvertSelectedButton == null || SelectionHintText == null) return;
            UpdateActionStates();
        }

        private void OnPendingSelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (SelectionHintText == null) return;
            UpdateActionStates();
        }

        private void OnConvertedSelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (SelectionHintText == null) return;
            UpdateActionStates();
        }
    }

    public sealed class ConvertSongRow
    {
        public string Title { get; private set; }
        public string Directory { get; private set; }
        public string FullPath { get; private set; }

        internal ConvertSongRow(ConvertSongEntry source)
        {
            Title = source.Title;
            Directory = source.Directory;
            FullPath = source.FullPath;
        }
    }

    public sealed class ConvertTaskRow
    {
        public string Time { get; private set; }
        public string Task { get; private set; }
        public string Phase { get; private set; }
        public string Detail { get; private set; }
        public string Text { get; private set; }
        public Brush PhaseBrush { get; private set; }

        internal ConvertTaskRow(ConvertMessageEntry source)
        {
            Time = source.Time;
            Task = string.IsNullOrEmpty(source.Task) ? "转换" : source.Task;
            Phase = source.Phase;
            Detail = source.Detail;
            Text = source.Text;
            PhaseBrush = ConvertView.PhaseColor(source.Phase);
        }
    }
}
