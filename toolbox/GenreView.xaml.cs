using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;

namespace NeteaseToolbox.Wpf
{
    // Native WPF genre page. All business logic stays in ToolboxForm; this view
    // polls a lightweight snapshot and triggers the existing WinForms actions.
    public partial class GenreView : UserControl
    {
        private static readonly Brush SuccessBrush = Frozen(42, 157, 143);
        private static readonly Brush WarningBrush = Frozen(233, 150, 62);
        private static readonly Brush ErrorBrush = Frozen(231, 111, 81);
        private static readonly Brush MutedBrush = Frozen(126, 138, 152);

        // Only messages produced by this page's workflows are mirrored locally;
        // the global task bar still receives everything.
        private static readonly string[] RelatedTasks =
        {
            "曲风刷新", "曲风导出", "曲风修正", "标签歌单", "打开网易云", "重新扫描"
        };

        private readonly ToolboxForm facade;
        private readonly DispatcherTimer refreshTimer;
        private readonly ObservableCollection<GenreSongRow> rows = new ObservableCollection<GenreSongRow>();
        private readonly Dictionary<string, GenreSongRow> rowIndex = new Dictionary<string, GenreSongRow>(StringComparer.OrdinalIgnoreCase);
        private readonly ObservableCollection<GenreTagShare> distribution = new ObservableCollection<GenreTagShare>();
        private readonly ObservableCollection<GenreMessage> messages = new ObservableCollection<GenreMessage>();
        private readonly ICollectionView rowView;

        private GenrePageSnapshot lastSnapshot;
        private long lastSignature = long.MinValue;
        private long lastDistributionSignature = long.MinValue;
        private string actionError = "";
        private bool refreshing, operationBusy, subscribed, initialized;

        public GenreView() : this(null) { }

        internal GenreView(ToolboxForm facade)
        {
            this.facade = facade ?? ToolboxForm.Instance;
            InitializeComponent();

            rowView = CollectionViewSource.GetDefaultView(rows);
            rowView.Filter = FilterRow;
            SongGrid.ItemsSource = rowView;
            SongGrid.LoadingRow += OnSongLoadingRow;
            DistributionList.ItemsSource = distribution;
            MessageList.ItemsSource = messages;

            refreshTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(2) };
            refreshTimer.Tick += delegate { Refresh(); };
            Loaded += OnLoaded;
            Unloaded += OnUnloaded;
            IsVisibleChanged += delegate
            {
                if (!IsLoaded) return;
                if (IsVisible) { refreshTimer.Start(); Refresh(); }
                else refreshTimer.Stop();
            };
            UpdateButtons();
        }

        private static Brush Frozen(byte red, byte green, byte blue)
        {
            var brush = new SolidColorBrush(Color.FromRgb(red, green, blue));
            brush.Freeze();
            return brush;
        }

        private void OnLoaded(object sender, RoutedEventArgs e)
        {
            if (!subscribed)
            {
                ToolboxForm.TaskReported += OnTaskReported;
                subscribed = true;
            }
            refreshTimer.Start();
            Refresh();

            if (!initialized)
            {
                initialized = true;
                // The WinForms page fills the shared table on startup; recover it
                // when the toolbox was launched in the background or nothing was
                // loaded yet, and load stored results when the table is all new.
                if (facade != null)
                {
                    if (lastSnapshot == null || lastSnapshot.Total == 0) facade.GenreEnsureScanned();
                    else if (lastSnapshot.Analyzed == 0) facade.GenreRefreshResults();
                }
                Refresh();
            }
        }

        private void OnUnloaded(object sender, RoutedEventArgs e)
        {
            refreshTimer.Stop();
            if (!subscribed) return;
            ToolboxForm.TaskReported -= OnTaskReported;
            subscribed = false;
        }

        private void Refresh()
        {
            if (refreshing) return;
            if (facade == null)
            {
                ShowError("未找到工具箱曲风分析服务，请重启工具箱后重试。");
                return;
            }
            refreshing = true;
            try { ApplySnapshot(facade.ReadGenreSnapshot()); }
            catch (Exception ex) { ShowError(ex.Message); }
            finally { refreshing = false; }
        }

        private void ApplySnapshot(GenrePageSnapshot snapshot)
        {
            if (snapshot == null) return;
            lastSnapshot = snapshot;

            ApplyRows(snapshot);
            ApplyMetrics(snapshot);
            ApplyStatus(snapshot);
            ApplyDistribution(snapshot);
            UpdateButtons();

            if (!string.IsNullOrEmpty(snapshot.Error)) ShowError(snapshot.Error);
            else if (!string.IsNullOrEmpty(actionError)) ShowError(actionError);
            else HideError();
        }

        private void ApplyRows(GenrePageSnapshot snapshot)
        {
            if (snapshot.Signature == lastSignature) return;
            lastSignature = snapshot.Signature;

            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            for (int index = 0; index < snapshot.Songs.Count; index++)
            {
                var song = snapshot.Songs[index];
                if (string.IsNullOrEmpty(song.Source)) continue;
                seen.Add(song.Source);

                GenreSongRow row;
                if (rowIndex.TryGetValue(song.Source, out row))
                {
                    row.Update(song);
                    continue;
                }
                row = new GenreSongRow(song);
                rowIndex[song.Source] = row;
                rows.Insert(Math.Min(index, rows.Count), row);
            }

            for (int index = rows.Count - 1; index >= 0; index--)
            {
                if (seen.Contains(rows[index].Source)) continue;
                rowIndex.Remove(rows[index].Source);
                rows.RemoveAt(index);
            }

            UpdateListChrome();
        }

        private void ApplyMetrics(GenrePageSnapshot snapshot)
        {
            MetricTotalText.Text = snapshot.Total.ToString();
            MetricAnalyzedText.Text = snapshot.Analyzed.ToString();
            MetricPendingText.Text = snapshot.Pending.ToString();
            MetricCoverageText.Text = snapshot.Total == 0
                ? "等待扫描歌曲"
                : "覆盖率 " + (snapshot.Analyzed * 100 / snapshot.Total) + "%";
        }

        private void ApplyStatus(GenrePageSnapshot snapshot)
        {
            bool busy = snapshot.Busy || operationBusy;
            string status;
            if (!string.IsNullOrEmpty(snapshot.BusyText)) status = snapshot.BusyText;
            else if (!snapshot.ScriptAvailable) status = "曲风分析程序不可用";
            else if (snapshot.Total == 0) status = "尚未扫描到已转换的歌曲";
            else if (snapshot.Pending > 0 && snapshot.Analyzed == 0) status = "已载入 " + snapshot.Total + " 首歌曲，全部待分析";
            else if (snapshot.Pending > 0) status = "已载入 " + snapshot.Total + " 首歌曲 · 已分析 " + snapshot.Analyzed + " 首 · 待分析 " + snapshot.Pending + " 首";
            else status = "已载入 " + snapshot.Total + " 首歌曲 · 标签齐全";

            StatusTextBlock.Text = status;
            SecondaryStatusText.Text = BuildSecondaryStatus(snapshot);
            SecondaryStatusText.Visibility = string.IsNullOrEmpty(SecondaryStatusText.Text) ? Visibility.Collapsed : Visibility.Visible;

            if (!string.IsNullOrEmpty(snapshot.Error)) StatusDot.Fill = ErrorBrush;
            else if (busy) StatusDot.Fill = WarningBrush;
            else if (snapshot.Total == 0) StatusDot.Fill = MutedBrush;
            else if (snapshot.Pending > 0) StatusDot.Fill = WarningBrush;
            else if (snapshot.Analyzed > 0) StatusDot.Fill = SuccessBrush;
            else StatusDot.Fill = (Brush)TryFindResource("SystemControlHighlightAccentBrush") ?? MutedBrush;

            BusyBar.Visibility = busy ? Visibility.Visible : Visibility.Collapsed;
        }

        private static string BuildSecondaryStatus(GenrePageSnapshot snapshot)
        {
            var parts = new List<string>();
            if (!string.IsNullOrWhiteSpace(snapshot.LibraryStatus)) parts.Add(snapshot.LibraryStatus);
            if (snapshot.MusicFolderExists) parts.Add("音乐目录：" + snapshot.MusicFolder);
            else if (string.IsNullOrEmpty(snapshot.MusicFolder)) parts.Add("尚未设置音乐目录，请在“音乐转换”页选择");
            else parts.Add("音乐目录不存在：" + snapshot.MusicFolder);
            return string.Join(" · ", parts);
        }

        private void ApplyDistribution(GenrePageSnapshot snapshot)
        {
            if (snapshot.Signature == lastDistributionSignature) return;
            lastDistributionSignature = snapshot.Signature;

            distribution.Clear();
            var counts = TagPlaylistFilter.CountDistinct(rows.Select(row => (IEnumerable<string>)row.Tags));
            int total = rows.Count;
            foreach (var item in counts.Take(6))
                distribution.Add(new GenreTagShare(item.Name, item.Count, total));

            bool empty = distribution.Count == 0;
            DistributionEmptyText.Visibility = empty ? Visibility.Visible : Visibility.Collapsed;
            DistributionHintText.Text = empty ? "" : "按出现次数排序 · 前 " + distribution.Count + " 项";
        }

        private void UpdateListChrome()
        {
            int total = rows.Count;
            int shown = total == 0 ? 0 : rowView.Cast<object>().Count();
            SongCountText.Text = total == 0 ? "" : shown == total ? "共 " + total + " 首" : "显示 " + shown + " / " + total + " 首";

            if (total == 0)
            {
                EmptyTextBlock.Text = "还没有歌曲。请先在“音乐转换”页把 NCM 文件转换成音频，再回到这里分析曲风。";
                EmptyTextBlock.Visibility = Visibility.Visible;
            }
            else if (shown == 0)
            {
                EmptyTextBlock.Text = "没有匹配的歌曲，试试其他关键词。";
                EmptyTextBlock.Visibility = Visibility.Visible;
            }
            else EmptyTextBlock.Visibility = Visibility.Collapsed;
        }

        private void UpdateButtons()
        {
            bool facadeBusy = lastSnapshot != null && lastSnapshot.Busy;
            bool busy = operationBusy || facadeBusy;
            bool hasSnapshot = lastSnapshot != null;

            AnalyzeButton.IsEnabled = !busy && hasSnapshot && lastSnapshot.CanAnalyze;
            ExportButton.IsEnabled = !busy && hasSnapshot && lastSnapshot.CanExport;
            PlaylistButton.IsEnabled = !busy && hasSnapshot && lastSnapshot.CanCreatePlaylist;

            var selected = SongGrid.SelectedItem as GenreSongRow;
            OpenButton.IsEnabled = !busy && selected != null;
            EditButton.IsEnabled = !busy && selected != null && selected.CanEdit;
        }

        private bool FilterRow(object item)
        {
            var row = item as GenreSongRow;
            if (row == null) return false;
            string query = SearchBox == null ? "" : (SearchBox.Text ?? "").Trim();
            return query.Length == 0 || row.Matches(query);
        }

        private void OnSearchTextChanged(object sender, TextChangedEventArgs e)
        {
            if (rowView == null) return;
            rowView.Refresh();
            UpdateListChrome();
        }

        private void OnSongSelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            UpdateButtons();
        }

        private void OnSongLoadingRow(object sender, DataGridRowEventArgs e)
        {
            var row = e.Row.Item as GenreSongRow;
            if (row != null) e.Row.ToolTip = row.Source;
        }

        private void OnSongDoubleClick(object sender, MouseButtonEventArgs e)
        {
            if (e.ChangedButton != MouseButton.Left) return;
            var container = ItemsControl.ContainerFromElement(SongGrid, e.OriginalSource as DependencyObject) as DataGridRow;
            if (container == null) return;
            var row = container.Item as GenreSongRow;
            if (row == null) return;
            e.Handled = true;
            _ = OpenSongAsync(row);
        }

        private async void OnAnalyzeClick(object sender, RoutedEventArgs e)
        {
            if (facade == null || operationBusy) return;
            await RunOperationAsync(() => facade.GenreAnalyzeAsync(), AnalyzeButton, "正在分析…");
        }

        private async void OnExportClick(object sender, RoutedEventArgs e)
        {
            if (facade == null || operationBusy) return;
            await RunOperationAsync(() => facade.GenreExportAsync(), ExportButton, "正在导出…");
        }

        private async void OnPlaylistClick(object sender, RoutedEventArgs e)
        {
            if (facade == null || operationBusy) return;
            await RunOperationAsync(() => facade.GenreCreatePlaylistAsync(), PlaylistButton, "正在读取…");
        }

        private async void OnEditClick(object sender, RoutedEventArgs e)
        {
            var row = SongGrid.SelectedItem as GenreSongRow;
            if (facade == null || row == null || operationBusy) return;
            await RunOperationAsync(() => facade.GenreEditSongAsync(row.Source));
        }

        private async void OnOpenClick(object sender, RoutedEventArgs e)
        {
            var row = SongGrid.SelectedItem as GenreSongRow;
            if (facade == null || row == null || operationBusy) return;
            await OpenSongAsync(row);
        }

        private Task OpenSongAsync(GenreSongRow row)
        {
            return RunOperationAsync(() => facade.GenreOpenSongAsync(row.Source));
        }

        private async Task RunOperationAsync(Func<Task> action, Button busyButton = null, string busyText = null)
        {
            if (facade == null || operationBusy) return;
            operationBusy = true;
            actionError = "";
            object originalContent = busyButton == null ? null : busyButton.Content;
            if (busyButton != null) busyButton.Content = busyText;
            UpdateButtons();
            if (lastSnapshot != null) ApplyStatus(lastSnapshot);
            else BusyBar.Visibility = Visibility.Visible;
            try { await action(); }
            catch (Exception ex) { actionError = ex.Message; }
            finally
            {
                if (busyButton != null) busyButton.Content = originalContent;
                operationBusy = false;
                Refresh();
            }
        }

        private void OnTaskReported(string task, string phase, string detail)
        {
            if (!Dispatcher.CheckAccess())
            {
                Dispatcher.BeginInvoke((Action)(() => OnTaskReported(task, phase, detail)));
                return;
            }
            if (task == null || Array.IndexOf(RelatedTasks, task) < 0) return;

            messages.Insert(0, new GenreMessage(task, phase, detail));
            while (messages.Count > 12) messages.RemoveAt(messages.Count - 1);
            MessagesEmptyText.Visibility = Visibility.Collapsed;

            if (phase == "失败" || phase == "部分失败")
            {
                actionError = task + "：" + (detail ?? "");
                ShowError(actionError);
            }
            else if (phase == "完成" || phase == "开始" || phase == "取消")
            {
                actionError = "";
                if (lastSnapshot == null || string.IsNullOrEmpty(lastSnapshot.Error)) HideError();
            }
        }

        private void ShowError(string message)
        {
            if (string.IsNullOrWhiteSpace(message)) { HideError(); return; }
            ErrorTextBlock.Text = message;
            ErrorPanel.Visibility = Visibility.Visible;
        }

        private void HideError()
        {
            ErrorTextBlock.Text = "";
            ErrorPanel.Visibility = Visibility.Collapsed;
        }
    }

    // One row of the genre table. Rows keep their identity across refreshes so
    // selection and scroll position survive re-analysis.
    public sealed class GenreSongRow : INotifyPropertyChanged
    {
        private string title = "", genre = "", influences = "", moods = "", origin = "", status = "", directory = "";

        public string Source { get; private set; }
        public string Title { get { return title; } }
        public string Genre { get { return genre; } }
        public string Influences { get { return influences; } }
        public string Moods { get { return moods; } }
        public string Origin { get { return origin; } }
        public string Status { get { return status; } }
        public string Directory { get { return directory; } }
        public bool CanEdit { get; private set; }
        public string[] Tags { get; private set; }

        public event PropertyChangedEventHandler PropertyChanged;

        internal GenreSongRow(GenreSongSnapshot snapshot)
        {
            Source = snapshot.Source ?? "";
            Tags = new string[0];
            Update(snapshot);
        }

        internal void Update(GenreSongSnapshot snapshot)
        {
            if (snapshot == null) return;
            bool tagsChanged = Set(ref genre, snapshot.Genre, "Genre");
            tagsChanged |= Set(ref influences, snapshot.Influences, "Influences");
            tagsChanged |= Set(ref moods, snapshot.Moods, "Moods");
            Set(ref title, snapshot.Title, "Title");
            Set(ref origin, snapshot.Origin, "Origin");
            Set(ref status, snapshot.Status, "Status");
            Set(ref directory, snapshot.Directory, "Directory");
            if (CanEdit != snapshot.CanEdit)
            {
                CanEdit = snapshot.CanEdit;
                OnChanged("CanEdit");
            }
            if (tagsChanged || Tags == null) Tags = BuildTags();
        }

        internal bool Matches(string query)
        {
            return Contains(title, query) || Contains(genre, query) || Contains(influences, query)
                || Contains(moods, query) || Contains(status, query) || Contains(directory, query);
        }

        private string[] BuildTags()
        {
            var result = new List<string>();
            foreach (var value in new[] { genre, influences, moods })
            {
                foreach (var tag in TagPlaylistFilter.SplitTags(value))
                    if (!result.Contains(tag, StringComparer.Ordinal)) result.Add(tag);
            }
            return result.ToArray();
        }

        private static bool Contains(string value, string query)
        {
            return !string.IsNullOrEmpty(value) && value.IndexOf(query, StringComparison.CurrentCultureIgnoreCase) >= 0;
        }

        private bool Set(ref string field, string value, string name)
        {
            value = value ?? "";
            if (string.Equals(field, value, StringComparison.Ordinal)) return false;
            field = value;
            OnChanged(name);
            return true;
        }

        private void OnChanged(string name)
        {
            var handler = PropertyChanged;
            if (handler != null) handler(this, new PropertyChangedEventArgs(name));
        }
    }

    public sealed class GenreTagShare
    {
        public string Label { get; private set; }
        public double Share { get; private set; }
        public string CountText { get; private set; }
        public string ToolTipText { get; private set; }

        internal GenreTagShare(string label, int count, int total)
        {
            Label = label ?? "";
            Share = total <= 0 ? 0 : Math.Min(1.0, (double)count / total);
            CountText = count + " 首 · " + (Share * 100).ToString("0") + "%";
            ToolTipText = Label + "：" + CountText;
        }
    }

    public sealed class GenreMessage
    {
        public string Time { get; private set; }
        public string Task { get; private set; }
        public string Phase { get; private set; }
        public string Detail { get; private set; }

        internal GenreMessage(string task, string phase, string detail)
        {
            Time = DateTime.Now.ToString("HH:mm:ss");
            Task = task ?? "";
            Phase = phase ?? "";
            Detail = detail ?? "";
        }
    }
}
