using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;

namespace NeteaseToolbox.Wpf
{
    /// <summary>
    /// “多人一起听”的原生 WPF 玻璃页面。业务逻辑仍在 ToolboxForm（MultiListen.cs），
    /// 本视图只按快照渲染，并通过 Toolbox.MultiListenBridge.cs 中带 MultiListen 前缀的动作方法操作。
    /// </summary>
    public partial class MultiListenView : UserControl
    {
        private readonly ToolboxForm facade;
        private readonly DispatcherTimer refreshTimer;
        private readonly ObservableCollection<MultiListenMessageItem> messageItems = new ObservableCollection<MultiListenMessageItem>();
        private MultiListenSnapshot lastSnapshot;
        private bool operationBusy, snapshotErrorVisible;
        private string messageSignature = "";

        private static readonly Brush SuccessBrush = Frozen(42, 157, 143);
        private static readonly Brush WarningBrush = Frozen(233, 150, 62);
        private static readonly Brush ErrorBrush = Frozen(231, 111, 81);
        private static readonly Brush IdleBrush = Frozen(150, 158, 170);

        public MultiListenView() : this(null) { }

        internal MultiListenView(ToolboxForm facade)
        {
            this.facade = facade ?? ToolboxForm.Instance;
            InitializeComponent();
            MessageList.ItemsSource = messageItems;
            refreshTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1.5) };
            refreshTimer.Tick += delegate { Refresh(); };
            Loaded += OnLoaded;
            Unloaded += OnUnloaded;
            IsVisibleChanged += delegate
            {
                if (!IsLoaded) return;
                if (IsVisible) OnShown();
                else
                {
                    refreshTimer.Stop();
                    if (facade != null) facade.LeaveMultiListenView();
                }
            };
        }

        private static Brush Frozen(byte red, byte green, byte blue)
        {
            var brush = new SolidColorBrush(Color.FromRgb(red, green, blue));
            brush.Freeze();
            return brush;
        }

        private void OnLoaded(object sender, RoutedEventArgs e)
        {
            OnShown();
        }

        /// <summary>页面显示：启动快照轮询，并让表单继续后台探测补丁状态。</summary>
        internal void OnShown()
        {
            refreshTimer.Start();
            // 复用 WinForms 页面原有的后台探测（5 秒一次），并立即取一次状态。
            if (facade != null) facade.EnsureMultiListenOpened();
            Refresh();
        }

        private void OnUnloaded(object sender, RoutedEventArgs e)
        {
            refreshTimer.Stop();
            if (facade != null) facade.LeaveMultiListenView();
        }

        private void Refresh()
        {
            if (!IsVisible) return;
            if (facade == null)
            {
                ShowError("未找到工具箱多人一起听服务，请重启工具箱。");
                UpdateBusyState(false);
                return;
            }
            try
            {
                var snapshot = facade.ReadMultiListenSnapshot();
                lastSnapshot = snapshot;
                ApplySnapshot(snapshot);
            }
            catch (Exception ex) { ShowError(ex.Message); }
        }

        private void ApplySnapshot(MultiListenSnapshot snapshot)
        {
            StatusTextBlock.Text = string.IsNullOrWhiteSpace(snapshot.Status) ? "正在检查补丁状态…" : snapshot.Status;
            DetailTextBlock.Text = snapshot.Detail ?? "";
            DetailTextBlock.Visibility = string.IsNullOrWhiteSpace(snapshot.Detail) ? Visibility.Collapsed : Visibility.Visible;
            StatusDot.Fill = StatusBrush(snapshot);
            ApplyMessages(snapshot.Messages);
            UpdateBusyState(snapshot.Busy);
            // RenderMultiListen already prefixes the problem into the status line; showing it in
            // the error panel again would duplicate the same block of text on the page.
            bool errorInStatus = !string.IsNullOrWhiteSpace(snapshot.Error)
                && (snapshot.Status ?? "").IndexOf(snapshot.Error, StringComparison.Ordinal) >= 0;
            if (!string.IsNullOrWhiteSpace(snapshot.Error) && !errorInStatus) { ShowError(snapshot.Error); snapshotErrorVisible = true; }
            else if (snapshotErrorVisible) { HideError(); snapshotErrorVisible = false; }
        }

        private Brush StatusBrush(MultiListenSnapshot snapshot)
        {
            if (!string.IsNullOrEmpty(snapshot.Error)) return ErrorBrush;
            if (snapshot.Busy) return WarningBrush;
            if (snapshot.Installed) return SuccessBrush;
            return (Brush)TryFindResource("SystemControlForegroundBaseMediumBrush") ?? IdleBrush;
        }

        private void ApplyMessages(List<MultiListenMessageEntry> messages)
        {
            if (messages == null) messages = new List<MultiListenMessageEntry>();
            string signature = messages.Count == 0 ? "0"
                : messages.Count + "|" + messages[messages.Count - 1].Time + "|" + messages[messages.Count - 1].Text;
            if (signature != messageSignature)
            {
                messageSignature = signature;
                messageItems.Clear();
                foreach (var entry in messages) messageItems.Add(new MultiListenMessageItem(entry));
                MessagesScroll.ScrollToTop();
            }
            MessageCountText.Text = messageItems.Count == 0 ? "" : "共 " + messageItems.Count + " 条";
            MessagesEmptyText.Visibility = messageItems.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        }

        private void UpdateBusyState(bool facadeBusy)
        {
            bool busy = operationBusy || facadeBusy;
            StartButton.IsEnabled = !busy;
            RefreshButton.IsEnabled = !busy;
            StopButton.IsEnabled = !busy && lastSnapshot != null && lastSnapshot.Installed;
            LogFolderButton.IsEnabled = !busy;
            // 后台定时探测（每 5 秒）只禁用按钮，不显示忙碌提示，避免界面闪烁；
            // 用户发起的启动/刷新/停用才显示进度环和文字。
            BusyPanel.Visibility = operationBusy ? Visibility.Visible : Visibility.Collapsed;
            BusyText.Text = "正在执行操作，请稍候；首次连接约需 30 秒，期间窗口可以正常使用。";
        }

        /// <summary>后台探测正在运行时先等它结束，避免用户操作被 busy 判断直接丢弃。</summary>
        private async Task WaitForFacadeIdleAsync()
        {
            for (int attempt = 0; attempt < 60 && facade != null; attempt++)
            {
                if (!facade.ReadMultiListenSnapshot().Busy) return;
                await Task.Delay(250);
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

        private async void OnStartClick(object sender, RoutedEventArgs e)
        {
            if (facade == null || operationBusy) return;
            operationBusy = true;
            UpdateBusyState(lastSnapshot != null && lastSnapshot.Busy);
            try
            {
                await WaitForFacadeIdleAsync();
                await facade.StartMultiListenAsync();
            }
            catch (Exception ex) { ShowError(ex.Message); }
            finally
            {
                operationBusy = false;
                Refresh();
            }
        }

        private async void OnRefreshClick(object sender, RoutedEventArgs e)
        {
            if (facade == null || operationBusy) return;
            operationBusy = true;
            UpdateBusyState(lastSnapshot != null && lastSnapshot.Busy);
            try
            {
                await WaitForFacadeIdleAsync();
                await facade.RefreshMultiListenStatusAsync();
            }
            catch (Exception ex) { ShowError(ex.Message); }
            finally
            {
                operationBusy = false;
                Refresh();
            }
        }

        private async void OnStopClick(object sender, RoutedEventArgs e)
        {
            if (facade == null || operationBusy) return;
            var owner = Window.GetWindow(this);
            var answer = owner == null
                ? System.Windows.MessageBox.Show("确定退出多人房间并停用补丁吗？\n网易云客户端会保持运行；之后可以在本页或网易云面板重新启动。",
                    "多人一起听", MessageBoxButton.YesNo, MessageBoxImage.Warning)
                : System.Windows.MessageBox.Show(owner, "确定退出多人房间并停用补丁吗？\n网易云客户端会保持运行；之后可以在本页或网易云面板重新启动。",
                    "多人一起听", MessageBoxButton.YesNo, MessageBoxImage.Warning);
            if (answer != MessageBoxResult.Yes) return;
            operationBusy = true;
            UpdateBusyState(lastSnapshot != null && lastSnapshot.Busy);
            try
            {
                await WaitForFacadeIdleAsync();
                await facade.StopMultiListenAsync();
            }
            catch (Exception ex) { ShowError(ex.Message); }
            finally
            {
                operationBusy = false;
                Refresh();
            }
        }

        private void OnOpenLogClick(object sender, RoutedEventArgs e)
        {
            if (facade == null) return;
            facade.OpenMultiListenLogFolderAction();
        }
    }

    /// <summary>补丁消息的绑定项（不变更底层缓存）。</summary>
    public sealed class MultiListenMessageItem
    {
        public string Time { get; private set; }
        public string Text { get; private set; }
        public bool IsError { get; private set; }

        internal MultiListenMessageItem(MultiListenMessageEntry source)
        {
            Time = source.Time;
            Text = (source.IsError ? "！" : "") + source.Text;
            IsError = source.IsError;
        }
    }
}
