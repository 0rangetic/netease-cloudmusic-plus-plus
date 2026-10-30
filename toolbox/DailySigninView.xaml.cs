using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;

namespace NeteaseToolbox.Wpf
{
    public partial class DailySigninView : UserControl
    {
        private readonly ToolboxForm facade;
        private readonly DispatcherTimer refreshTimer;
        private readonly ObservableCollection<DailySigninHistoryItem> historyItems = new ObservableCollection<DailySigninHistoryItem>();
        private DailySigninSnapshot lastSnapshot;
        private bool refreshing, operationBusy, settingsLoaded, settingsDirty, applyingSettings, initialized, snapshotErrorVisible;
        private string historySignature = "";

        private static readonly Brush SuccessBrush = Frozen(42, 157, 143);
        private static readonly Brush WarningBrush = Frozen(233, 150, 62);
        private static readonly Brush ErrorBrush = Frozen(231, 111, 81);

        public DailySigninView() : this(null) { }

        internal DailySigninView(ToolboxForm facade)
        {
            this.facade = facade ?? ToolboxForm.Instance;
            InitializeComponent();
            HistoryList.ItemsSource = historyItems;
            for (int hour = 0; hour < 24; hour++) HourCombo.Items.Add(hour.ToString("00"));
            for (int minute = 0; minute < 60; minute++) MinuteCombo.Items.Add(minute.ToString("00"));
            refreshTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(2) };
            refreshTimer.Tick += delegate { Refresh(false); };
            Loaded += OnLoaded;
            Unloaded += delegate { refreshTimer.Stop(); };
            IsVisibleChanged += delegate {
                if (!IsLoaded) return;
                if (IsVisible) { refreshTimer.Start(); Refresh(false); } else refreshTimer.Stop();
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
            refreshTimer.Start();
            if (!initialized && facade != null)
            {
                initialized = true;
                // The WinForms page owns the timer and scheduled task; reuse its one-time setup.
                _ = facade.InitializeDailySigninAsync();
            }
            Refresh(true);
        }

        private void Refresh(bool applySettings)
        {
            if (refreshing) return;
            if (facade == null)
            {
                ShowError("未找到工具箱签到服务，请重启工具箱。");
                return;
            }
            refreshing = true;
            try
            {
                var snapshot = facade.ReadDailySigninSnapshot();
                lastSnapshot = snapshot;
                ApplySnapshot(snapshot, applySettings);
            }
            catch (Exception ex) { ShowError(DailySigninService.SafeError(ex)); }
            finally { refreshing = false; }
        }

        private void ApplySnapshot(DailySigninSnapshot snapshot, bool applySettings)
        {
            if (snapshot.SettingsLoaded && (applySettings || !settingsLoaded || !settingsDirty))
            {
                applyingSettings = true;
                try
                {
                    EnabledSwitch.IsOn = snapshot.Enabled;
                    HourCombo.SelectedIndex = Math.Max(0, Math.Min(23, snapshot.Hour));
                    MinuteCombo.SelectedIndex = Math.Max(0, Math.Min(59, snapshot.Minute));
                }
                finally { applyingSettings = false; }
                settingsLoaded = true;
                settingsDirty = false;
            }

            StatusTextBlock.Text = string.IsNullOrEmpty(snapshot.Status) ? "尚未执行签到。" : snapshot.Status;
            StatusDot.Fill = StatusBrush(snapshot);
            UpdateBusyState(snapshot.Busy);
            ApplyHistory(snapshot.History);
            if (!string.IsNullOrEmpty(snapshot.Error)) { ShowError(snapshot.Error); snapshotErrorVisible = true; }
            else if (snapshotErrorVisible) { HideError(); snapshotErrorVisible = false; }
        }

        private Brush StatusBrush(DailySigninSnapshot snapshot)
        {
            string status = snapshot.Status ?? "";
            if (!string.IsNullOrEmpty(snapshot.Error) || (status.Contains("失败") && !status.Contains("部分失败"))) return ErrorBrush;
            if (status.Contains("部分失败")) return WarningBrush;
            if (status.Contains("完成")) return SuccessBrush;
            return (Brush)TryFindResource("SystemControlHighlightAccentBrush") ?? SuccessBrush;
        }

        private void UpdateBusyState(bool facadeBusy)
        {
            bool busy = operationBusy || facadeBusy;
            SaveButton.IsEnabled = !busy;
            RunButton.IsEnabled = !busy;
            EnabledSwitch.IsEnabled = !busy;
            HourCombo.IsEnabled = !busy;
            MinuteCombo.IsEnabled = !busy;
        }

        private void ApplyHistory(List<SigninEvent> history)
        {
            if (history == null) history = new List<SigninEvent>();
            string signature = history.Count == 0 ? "0" : history.Count + "|" + history[0].Id + "|" + history[history.Count - 1].Id;
            if (signature == historySignature) return;
            historySignature = signature;
            historyItems.Clear();
            foreach (var item in history.Take(200)) historyItems.Add(new DailySigninHistoryItem(item));
            HistoryEmptyText.Visibility = historyItems.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
            HistoryCountText.Text = historyItems.Count == 0 ? "" : "共 " + historyItems.Count + " 条";
            HistoryScroll.ScrollToTop();
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

        private void OnSettingToggled(object sender, RoutedEventArgs e)
        {
            if (!applyingSettings) settingsDirty = true;
        }

        private void OnSettingSelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (!applyingSettings) settingsDirty = true;
        }

        private async void OnSaveClick(object sender, RoutedEventArgs e)
        {
            if (facade == null || operationBusy) return;
            operationBusy = true;
            UpdateBusyState(lastSnapshot != null && lastSnapshot.Busy);
            try
            {
                int hour = HourCombo.SelectedIndex < 0 ? 0 : HourCombo.SelectedIndex;
                int minute = MinuteCombo.SelectedIndex < 0 ? 0 : MinuteCombo.SelectedIndex;
                await facade.SaveDailySigninSettingsAsync(EnabledSwitch.IsOn, hour, minute);
                settingsDirty = false;
            }
            catch (Exception ex) { ShowError(DailySigninService.SafeError(ex)); }
            finally
            {
                operationBusy = false;
                Refresh(true);
            }
        }

        private async void OnRunClick(object sender, RoutedEventArgs e)
        {
            if (facade == null || operationBusy) return;
            operationBusy = true;
            UpdateBusyState(lastSnapshot != null && lastSnapshot.Busy);
            try { await facade.RunDailySigninAsync(false); }
            catch (Exception ex) { ShowError(DailySigninService.SafeError(ex)); }
            finally
            {
                operationBusy = false;
                Refresh(false);
            }
        }
    }

    public sealed class DailySigninHistoryItem
    {
        public string Time { get; private set; }
        public string Task { get; private set; }
        public string Phase { get; private set; }
        public string Detail { get; private set; }

        internal DailySigninHistoryItem(SigninEvent source)
        {
            Time = source.Time;
            Task = source.Task;
            Phase = source.Phase;
            Detail = source.Detail;
        }
    }
}
