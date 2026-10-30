using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;

namespace NeteaseToolbox.Wpf
{
    /// <summary>
    /// 音乐记忆原生 WPF 页。检索、日期范围、AI 开关、结果与证据都驱动既有的
    /// ToolboxForm.SearchMemoryRecall，页面只负责呈现与轮询，不复制检索逻辑。
    /// </summary>
    public partial class MemoryView : UserControl
    {
        private readonly ToolboxForm facade;
        private readonly DispatcherTimer refreshTimer;
        private readonly ObservableCollection<MemoryRecallRow> rows = new ObservableCollection<MemoryRecallRow>();
        private string rowsSignature = "";
        private int selectedIndex = -1;
        private bool refreshing, operationBusy, inputsLoaded, suppressSelection;

        private static readonly Brush SuccessBrush = Frozen(42, 157, 143);
        private static readonly Brush WarningBrush = Frozen(233, 150, 62);
        private static readonly Brush ErrorBrush = Frozen(231, 111, 81);

        public MemoryView() : this(null) { }

        internal MemoryView(ToolboxForm facade)
        {
            this.facade = facade ?? ToolboxForm.Instance;
            InitializeComponent();
            ResultsGrid.ItemsSource = rows;
            StartDatePicker.SelectedDate = DateTime.Today.AddDays(-6);
            EndDatePicker.SelectedDate = DateTime.Today;
            refreshTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(1500) };
            refreshTimer.Tick += delegate { Refresh(false); };
            Loaded += OnLoaded;
            Unloaded += delegate { refreshTimer.Stop(); };
            IsVisibleChanged += delegate {
                if (!IsLoaded) return;
                if (IsVisible) { refreshTimer.Start(); Refresh(false); }
                else refreshTimer.Stop();
            };
        }

        /// <summary>外壳可能晚于页面创建，读取时再兜底取单例。</summary>
        private ToolboxForm Facade { get { return facade ?? ToolboxForm.Instance; } }

        private static Brush Frozen(byte red, byte green, byte blue)
        {
            var brush = new SolidColorBrush(Color.FromRgb(red, green, blue));
            brush.Freeze();
            return brush;
        }

        private void OnLoaded(object sender, RoutedEventArgs e)
        {
            refreshTimer.Start();
            Refresh(true);
        }

        private void Refresh(bool loadInputs)
        {
            if (refreshing) return;
            var target = Facade;
            if (target == null || target.IsDisposed)
            {
                ShowError("未找到音乐记忆检索服务，请重启工具箱。");
                return;
            }
            refreshing = true;
            try { ApplySnapshot(target.ReadMemoryRecallViewSnapshot(), loadInputs); }
            catch (Exception ex) { ShowError(ex.Message); }
            finally { refreshing = false; }
        }

        private void ApplySnapshot(MemoryRecallViewSnapshot snapshot, bool loadInputs)
        {
            if (snapshot == null) return;

            if (loadInputs && !inputsLoaded)
            {
                inputsLoaded = true;
                QueryBox.Text = snapshot.Query ?? "";
                LimitDatesBox.IsChecked = snapshot.LimitDates;
                StartDatePicker.SelectedDate = snapshot.StartDate;
                EndDatePicker.SelectedDate = snapshot.EndDate;
                UseAiBox.IsChecked = snapshot.UseAi;
                UpdateDateInputs();
            }

            StatusTextBlock.Text = string.IsNullOrWhiteSpace(snapshot.Status)
                ? "尚未检索。输入描述或勾选日期范围后开始检索。" : snapshot.Status;
            StatusDot.Fill = StatusBrush(snapshot);

            ApplyRows(snapshot.Results);
            UpdateBusyState(snapshot.Busy);
            ShowNotice(snapshot.Notice, snapshot.Error);
        }

        private void ApplyRows(List<MemoryRecallViewItem> results)
        {
            if (results == null) results = new List<MemoryRecallViewItem>();
            string signature = results.Count == 0 ? "0"
                : results.Count + "|" + results[0].PlayedText + "|" + results[0].Title
                  + "|" + results[results.Count - 1].PlayedText + "|" + results[results.Count - 1].Title;

            bool changed = signature != rowsSignature;
            if (changed)
            {
                rowsSignature = signature;
                int keep = selectedIndex;
                suppressSelection = true;
                try
                {
                    rows.Clear();
                    foreach (var item in results) rows.Add(new MemoryRecallRow(item));
                    if (rows.Count == 0) keep = -1;
                    else if (keep < 0 || keep >= rows.Count) keep = 0;
                    ResultsGrid.SelectedIndex = keep;
                }
                finally { suppressSelection = false; }

                ResultCountTextBlock.Text = rows.Count == 0 ? "" : "共 " + rows.Count + " 条";
                if (keep >= 0) LoadEvidence(keep);
                else
                {
                    selectedIndex = -1;
                    EvidenceTextBlock.Text = "检索结果会在这里显示播放时间和歌词命中。";
                    EvidenceSubtitleTextBlock.Text = "选中结果后显示播放时间、设备和歌词命中。";
                    OpenSongButton.Visibility = Visibility.Collapsed;
                }
            }

            EmptyResultTextBlock.Visibility = rows.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        }

        private void UpdateBusyState(bool facadeBusy)
        {
            bool busy = operationBusy || facadeBusy;
            SearchButton.IsEnabled = !busy;
            SearchButton.Content = busy ? "正在检索…" : "开始检索";
            QueryBox.IsEnabled = !busy;
            LimitDatesBox.IsEnabled = !busy;
            UseAiBox.IsEnabled = !busy;
            UpdateDateInputs();
            OpenSongButton.IsEnabled = !busy && OpenSongButton.Visibility == Visibility.Visible;
        }

        private void UpdateDateInputs()
        {
            bool enabled = LimitDatesBox.IsEnabled && LimitDatesBox.IsChecked == true;
            StartDatePicker.IsEnabled = enabled;
            EndDatePicker.IsEnabled = enabled;
        }

        private Brush StatusBrush(MemoryRecallViewSnapshot snapshot)
        {
            string status = snapshot.Status ?? "";
            if (!string.IsNullOrEmpty(snapshot.Error) || (status.Contains("失败") && !status.Contains("部分失败"))) return ErrorBrush;
            if (status.Contains("部分失败")) return WarningBrush;
            if (status.Contains("完成")) return SuccessBrush;
            return (Brush)TryFindResource("SystemControlHighlightAccentBrush") ?? SuccessBrush;
        }

        private void LoadEvidence(int index)
        {
            if (Facade == null || index < 0 || index >= rows.Count)
            {
                selectedIndex = -1;
                EvidenceTextBlock.Text = "选择结果查看播放时间与歌词命中。";
                EvidenceSubtitleTextBlock.Text = "未选择结果";
                OpenSongButton.Visibility = Visibility.Collapsed;
                return;
            }

            selectedIndex = index;
            try
            {
                var detail = Facade.SelectMemoryRecallViewResult(index);
                EvidenceTextBlock.Text = string.IsNullOrWhiteSpace(detail.Evidence)
                    ? "这条记录没有可显示的本机证据。" : detail.Evidence;
                EvidenceSubtitleTextBlock.Text = string.IsNullOrWhiteSpace(detail.Artist)
                    ? detail.Title : detail.Title + " — " + detail.Artist;
                OpenSongButton.Visibility = detail.HasSongId ? Visibility.Visible : Visibility.Collapsed;
                OpenSongButton.IsEnabled = detail.HasSongId && !operationBusy;
            }
            catch (Exception ex) { ShowError(ex.Message); }
        }

        private void ShowNotice(string notice, string error)
        {
            if (!string.IsNullOrWhiteSpace(error))
            {
                ErrorTextBlock.Text = error;
                ErrorPanel.Visibility = Visibility.Visible;
            }
            else ErrorPanel.Visibility = Visibility.Collapsed;

            if (string.IsNullOrWhiteSpace(error) && !string.IsNullOrWhiteSpace(notice))
            {
                NoticeTextBlock.Text = notice;
                NoticePanel.Visibility = Visibility.Visible;
            }
            else NoticePanel.Visibility = Visibility.Collapsed;
        }

        private void ShowError(string message)
        {
            ShowNotice(null, string.IsNullOrWhiteSpace(message) ? "音乐记忆页出现未知错误。" : message);
        }

        private async void OnSearchClick(object sender, RoutedEventArgs e)
        {
            await RunSearchAsync();
        }

        private async void OnQueryKeyDown(object sender, KeyEventArgs e)
        {
            if (e.Key == Key.Enter && (Keyboard.Modifiers & ModifierKeys.Control) == ModifierKeys.Control)
            {
                e.Handled = true;
                await RunSearchAsync();
            }
        }

        private async System.Threading.Tasks.Task RunSearchAsync()
        {
            if (Facade == null || operationBusy) return;
            operationBusy = true;
            ShowNotice(null, null);
            UpdateBusyState(false);
            try
            {
                var snapshot = await Facade.RunMemoryRecallViewSearchAsync(
                    QueryBox.Text,
                    LimitDatesBox.IsChecked == true,
                    StartDatePicker.SelectedDate ?? DateTime.Today,
                    EndDatePicker.SelectedDate ?? DateTime.Today,
                    UseAiBox.IsChecked == true);
                ApplySnapshot(snapshot, false);
            }
            catch (Exception ex) { ShowError(ex.Message); }
            finally
            {
                operationBusy = false;
                Refresh(false);
            }
        }

        private void OnLimitDatesChanged(object sender, RoutedEventArgs e)
        {
            UpdateDateInputs();
        }

        private void OnResultSelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (suppressSelection) return;
            int index = ResultsGrid.SelectedIndex;
            if (index < 0) return;
            LoadEvidence(index);
        }

        private void OnResultDoubleClick(object sender, MouseButtonEventArgs e)
        {
            if (Facade == null || selectedIndex < 0) return;
            Facade.OpenMemoryRecallViewSong(selectedIndex);
        }

        private void OnOpenSongClick(object sender, RoutedEventArgs e)
        {
            if (Facade == null || selectedIndex < 0) return;
            Facade.OpenMemoryRecallViewSong(selectedIndex);
        }
    }

    /// <summary>检索结果行。字段来自 ToolboxForm 的检索快照，只用于绑定。</summary>
    public sealed class MemoryRecallRow
    {
        public string PlayedText { get; private set; }
        public string Title { get; private set; }
        public string Artist { get; private set; }
        public string Evidence { get; private set; }

        internal MemoryRecallRow(MemoryRecallViewItem source)
        {
            PlayedText = source.PlayedText;
            Title = source.Title;
            Artist = source.Artist;
            Evidence = source.Evidence;
        }
    }
}
