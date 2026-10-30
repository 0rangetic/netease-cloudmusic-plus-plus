using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;

namespace NeteaseToolbox.Wpf
{
    public partial class SettingsView : UserControl
    {
        private static readonly Brush SuccessBrush = Frozen(42, 157, 143);
        private static readonly Brush WarningBrush = Frozen(233, 150, 62);
        private static readonly Brush ErrorBrush = Frozen(231, 111, 81);
        private static readonly Brush MutedBrush = Frozen(108, 117, 125);

        // Only reports produced by actions on this page are mirrored here; the
        // global task bar still receives everything.
        private static readonly HashSet<string> SettingsTaskNames = new HashSet<string>(StringComparer.Ordinal)
        {
            "转换输出目录", "安全去重", "工具箱回收站", "AI 画像设置",
            "网易云安装目录", "运行环境检查", "配置运行环境"
        };

        private const int StatusDetailLimit = 600;

        private readonly ToolboxForm facade;
        private bool operationBusy, operationReported, operationFailed, reporting, statusReady;
        private bool chooseOutputAvailable = true, resetOutputAvailable = true, maintenanceAvailable = true;

        public SettingsView() : this(null) { }

        internal SettingsView(ToolboxForm facade)
        {
            this.facade = facade ?? ToolboxForm.Instance;
            InitializeComponent();
            Loaded += OnLoaded;
            Unloaded += OnUnloaded;
            IsVisibleChanged += OnIsVisibleChanged;
        }

        private static Brush Frozen(byte red, byte green, byte blue)
        {
            var brush = new SolidColorBrush(Color.FromRgb(red, green, blue));
            brush.Freeze();
            return brush;
        }

        private void OnLoaded(object sender, RoutedEventArgs e)
        {
            if (!reporting)
            {
                ToolboxForm.TaskReported += OnTaskReported;
                reporting = true;
            }
            RefreshSnapshot();
        }

        private void OnUnloaded(object sender, RoutedEventArgs e)
        {
            if (!reporting) return;
            ToolboxForm.TaskReported -= OnTaskReported;
            reporting = false;
        }

        private void OnIsVisibleChanged(object sender, DependencyPropertyChangedEventArgs e)
        {
            // Navigating back from a WinForms page only toggles ContentArea
            // visibility, so Loaded does not fire again; refresh on show.
            if (IsLoaded && IsVisible) RefreshSnapshot();
        }

        private void OnTaskReported(string task, string phase, string detail)
        {
            if (string.IsNullOrEmpty(task) || !SettingsTaskNames.Contains(task)) return;
            if (!Dispatcher.CheckAccess())
            {
                Dispatcher.BeginInvoke((Action)(() => OnTaskReported(task, phase, detail)));
                return;
            }

            operationReported = true;
            statusReady = true;
            if (phase == "失败" || phase == "部分失败") operationFailed = true;

            string status = task + " · " + phase;
            string trimmed = detail == null ? "" : detail.Trim();
            if (trimmed.Length > 0)
            {
                if (trimmed.Length > StatusDetailLimit) trimmed = trimmed.Substring(0, StatusDetailLimit) + "…（完整内容见“任务消息”）";
                status += "：" + trimmed;
            }
            SetStatus(status, PhaseBrush(phase));
        }

        private static Brush PhaseBrush(string phase)
        {
            if (phase == "失败") return ErrorBrush;
            if (phase == "部分失败") return WarningBrush;
            if (phase == "完成") return SuccessBrush;
            return MutedBrush;
        }

        private Brush AccentBrush()
        {
            return TryFindResource("SystemControlHighlightAccentBrush") as Brush ?? SuccessBrush;
        }

        private void SetStatus(string message, Brush dot)
        {
            if (StatusTextBlock == null) return;
            StatusTextBlock.Text = message ?? "";
            if (StatusDot != null) StatusDot.Fill = dot ?? MutedBrush;
        }

        private void RefreshSnapshot()
        {
            if (facade == null)
            {
                ShowError("未找到工具箱设置服务，请重启工具箱。");
                return;
            }
            try
            {
                var snapshot = facade.ReadSettingsSnapshot();
                OutputStatusText.Text = string.IsNullOrWhiteSpace(snapshot.OutputStatus)
                    ? "输出位置：默认（每首歌曲目录下的 unlock 文件夹）"
                    : snapshot.OutputStatus;
                chooseOutputAvailable = snapshot.ChooseOutputEnabled;
                resetOutputAvailable = snapshot.ResetOutputEnabled;
                maintenanceAvailable = snapshot.MaintenanceEnabled;
                if (snapshot.ClientFolderConfigured)
                    ClientFolderText.Text = "网易云安装目录：" + snapshot.ClientFolder;
                else if (string.IsNullOrWhiteSpace(snapshot.ClientFolder))
                    ClientFolderText.Text = "网易云安装目录：未配置（可自动检测，或手动选择包含 cloudmusic.exe 的目录）";
                else
                    ClientFolderText.Text = "网易云安装目录：" + snapshot.ClientFolder + "（未找到 cloudmusic.exe）";
                if (!statusReady && !operationBusy)
                {
                    statusReady = true;
                    SetStatus("就绪：设置状态已载入，维护操作的结果会显示在这里。", MutedBrush);
                }
            }
            catch (Exception ex)
            {
                ShowError("读取设置状态失败：" + ex.Message);
            }
            UpdateBusyState();
        }

        private void UpdateBusyState()
        {
            bool idle = !operationBusy;
            if (ChooseOutputButton != null) ChooseOutputButton.IsEnabled = idle && chooseOutputAvailable;
            if (ResetOutputButton != null) ResetOutputButton.IsEnabled = idle && resetOutputAvailable;
            if (DedupeButton != null) DedupeButton.IsEnabled = idle && maintenanceAvailable;
            if (TrashButton != null) TrashButton.IsEnabled = idle && maintenanceAvailable;
            if (PortraitAiButton != null) PortraitAiButton.IsEnabled = idle;
            if (ClientFolderButton != null) ClientFolderButton.IsEnabled = idle;
            if (CheckEnvironmentButton != null) CheckEnvironmentButton.IsEnabled = idle;
            if (SetupEnvironmentButton != null) SetupEnvironmentButton.IsEnabled = idle;
            if (BusyBar != null) BusyBar.Visibility = operationBusy ? Visibility.Visible : Visibility.Collapsed;
        }

        private void ShowError(string message)
        {
            if (string.IsNullOrWhiteSpace(message)) { HideError(); return; }
            ErrorTextBlock.Text = message;
            ErrorPanel.Visibility = Visibility.Visible;
        }

        private void HideError()
        {
            if (ErrorTextBlock != null) ErrorTextBlock.Text = "";
            if (ErrorPanel != null) ErrorPanel.Visibility = Visibility.Collapsed;
        }

        private async Task RunOperationAsync(string startMessage, Func<Task> operation, string idleMessage)
        {
            if (operationBusy) return;
            operationBusy = true;
            operationReported = false;
            operationFailed = false;
            HideError();
            SetStatus(startMessage, AccentBrush());
            UpdateBusyState();
            try
            {
                await operation();
            }
            catch (Exception ex)
            {
                operationFailed = true;
                ShowError(ex.Message);
                SetStatus("操作失败：" + ex.Message, ErrorBrush);
            }
            finally
            {
                operationBusy = false;
                RefreshSnapshot();
                if (!operationReported && !operationFailed)
                    SetStatus(idleMessage, MutedBrush);
                UpdateBusyState();
            }
        }

        // WinForms dialogs block the UI thread; yield once so the busy state and
        // status message are painted before the modal dialog opens.
        private Task RunModalAsync(string startMessage, Action action, string idleMessage)
        {
            return RunOperationAsync(startMessage, async delegate
            {
                await Dispatcher.Yield(DispatcherPriority.Background);
                action();
            }, idleMessage);
        }

        private async void OnChooseOutputClick(object sender, RoutedEventArgs e)
        {
            if (facade == null || operationBusy) return;
            await RunModalAsync("正在等待选择输出目录…", facade.ChooseOutputFolderFromSettings, "未更改输出目录。");
        }

        private async void OnResetOutputClick(object sender, RoutedEventArgs e)
        {
            if (facade == null || operationBusy) return;
            await RunModalAsync("正在恢复默认输出目录…", facade.ResetOutputFolderFromSettings, "输出目录操作已结束。");
        }

        private async void OnDedupeClick(object sender, RoutedEventArgs e)
        {
            if (facade == null || operationBusy) return;
            await RunModalAsync("正在打开曲库安全去重…", facade.OpenSafeDedupeFromSettings, "安全去重窗口已关闭。");
        }

        private async void OnTrashClick(object sender, RoutedEventArgs e)
        {
            if (facade == null || operationBusy) return;
            await RunModalAsync("正在打开工具箱回收站…", facade.OpenTrashFromSettings, "工具箱回收站窗口已关闭。");
        }

        private async void OnPortraitAiClick(object sender, RoutedEventArgs e)
        {
            if (facade == null || operationBusy) return;
            await RunModalAsync("正在读取 AI 画像配置…", facade.OpenPortraitAiSettingsFromSettings, "AI 画像设置窗口已关闭。");
        }

        private async void OnClientFolderClick(object sender, RoutedEventArgs e)
        {
            if (facade == null || operationBusy) return;
            await RunModalAsync("正在等待选择网易云安装目录…", facade.ChooseClientFolderFromSettings, "未更改网易云安装目录。");
        }

        private async void OnCheckEnvironmentClick(object sender, RoutedEventArgs e)
        {
            if (facade == null || operationBusy) return;
            await RunOperationAsync("正在检查运行环境，请稍候…", facade.CheckEnvironmentFromSettingsAsync, "运行环境检查已结束。");
        }

        private async void OnSetupEnvironmentClick(object sender, RoutedEventArgs e)
        {
            if (facade == null || operationBusy) return;
            await RunOperationAsync("正在配置登录运行环境；需要联网时可能较慢…", facade.SetupEnvironmentFromSettingsAsync, "运行环境配置已结束。");
        }
    }
}
