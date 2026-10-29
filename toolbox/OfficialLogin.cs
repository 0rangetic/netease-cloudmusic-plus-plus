using System;
using System.Collections.Generic;
using System.Drawing;
using System.IO;
using System.Threading.Tasks;
using System.Windows.Forms;
using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.WinForms;

namespace NeteaseToolbox
{
    internal sealed partial class ToolboxForm
    {
        private void ShowOfficialNeteaseLogin(Form owner, Func<string, Func<bool>, Task> finish)
        {
            using (var window = new Form { Text = "网易云官方网页登录", Size = new Size(1080, 780), MinimumSize = new Size(850, 650), StartPosition = FormStartPosition.CenterParent, Font = Font })
            using (var timer = new System.Windows.Forms.Timer { Interval = 1800 }) {
                var web = new WebView2 { Dock = DockStyle.Fill };
                var message = new Label { Dock = DockStyle.Top, Height = 58, Padding = new Padding(16, 8, 16, 8), Text = "在网易云官方页面点击右上角“登录”，用手机扫码。若出现验证，请在此窗口完成。登录后工具箱会自动保存。" };
                var footer = new FlowLayoutPanel { Dock = DockStyle.Bottom, Height = 48, Padding = new Padding(12, 5, 12, 5) };
                var reload = new Button { Text = "重新加载官方网页", Width = 160, Height = 32 };
                var done = new Button { Text = "我已登录，检查状态", Width = 160, Height = 32 };
                footer.Controls.Add(reload); footer.Controls.Add(done);
                window.Controls.Add(web); window.Controls.Add(message); window.Controls.Add(footer);
                // A failed EnsureCoreWebView2Async leaves this WinForms control bound to the faulted initialization,
                // so a real retry needs a brand new control (added back at child index 0 to keep the Dock layout).
                Action replaceWeb = delegate {
                    var previous = web;
                    window.Controls.Remove(previous);
                    try { previous.Dispose(); } catch (Exception) { }
                    web = new WebView2 { Dock = DockStyle.Fill };
                    window.Controls.Add(web);
                    window.Controls.SetChildIndex(web, 0);
                };
                bool alive = true, checking = false, completed = false;
                string failedCookie = null;
                Func<Task> check = async () => {
                    if (!alive || checking || web.CoreWebView2 == null) return;
                    checking = true;
                    string attemptedCookie = null;
                    try {
                        var cookies = await web.CoreWebView2.CookieManager.GetCookiesAsync("https://music.163.com/");
                        if (!alive) return;
                        var parts = new List<string>(); bool authenticated = false;
                        foreach (var cookie in cookies) {
                            if (cookie.Name != "MUSIC_U" && cookie.Name != "MUSIC_A" && cookie.Name != "__csrf" && cookie.Name != "_csrf") continue;
                            parts.Add(cookie.Name + "=" + cookie.Value);
                            if (cookie.Name == "MUSIC_U" && !string.IsNullOrWhiteSpace(cookie.Value)) authenticated = true;
                        }
                        if (!authenticated) return;
                        string credential = string.Join("; ", parts);
                        attemptedCookie = credential;
                        if (credential == failedCookie) return;
                        message.Text = "已收到官方网页的登录状态，正在验证并保存…";
                        timer.Stop();
                        // Close/cancel before the response arrives must not save a credential.
                        await Task.Run(() => NeteaseAuth.Validate(credential));
                        if (!alive) return;
                        await finish(credential, () => alive);
                        completed = true;
                        if (alive) { window.DialogResult = DialogResult.OK; window.Close(); }
                    } catch (Exception ex) {
                        if (alive) {
                            message.Text = "登录状态接收失败：" + ex.Message;
                            ReportTask("网易云官方网页登录", "失败", ex.Message);
                            failedCookie = attemptedCookie;
                        }
                    } finally { checking = false; }
                };
                timer.Tick += async delegate { await check(); };
                done.Click += async delegate { failedCookie = null; message.Text = "正在检查官方网页登录状态；请确保已完成扫码或验证。"; await check(); };
                reload.Click += delegate { if (web.CoreWebView2 != null && !checking) { failedCookie = null; web.CoreWebView2.Reload(); timer.Start(); } };
                window.Shown += async delegate {
                    ReportTask("网易云官方网页登录", "开始", "正在打开网易云官方登录页面。");
                    string stage = "浏览器环境";
                    Exception startupError = null;
                    bool initialized = false;
                    string folder = Path.Combine(Path.GetDirectoryName(NeteaseAuth.SessionPath), "WebLogin");
                    for (int attempt = 0; attempt < 2 && !initialized; attempt++) {
                        if (!alive) return;
                        try {
                            stage = "浏览器环境";
                            var environment = await CoreWebView2Environment.CreateAsync(null, folder);
                            if (!alive) return;
                            var options = environment.CreateCoreWebView2ControllerOptions();
                            options.IsInPrivateModeEnabled = true;
                            // Unique profile per attempt: a crashed/busy earlier run can leave the fixed profile locked (ERROR_BUSY).
                            options.ProfileName = "NeteaseLogin-" + Guid.NewGuid().ToString("N");
                            stage = "浏览器控件";
                            await web.EnsureCoreWebView2Async(environment, options);
                            if (!alive) return;
                            initialized = true;
                        } catch (Exception ex) {
                            startupError = ex;
                            if (attempt == 0 && alive && IsWebViewBusyError(ex)) {
                                message.Text = "浏览器控件被占用，正在用新的独立浏览器配置重试一次…";
                                // "进行中" keeps this informational retry out of the red "失败" styling used for unknown phases.
                                ReportTask("网易云官方网页登录", "进行中", "浏览器控件被占用（ERROR_BUSY 0x800700AA），已改用新的独立浏览器配置重试一次。");
                                replaceWeb();
                                continue;
                            }
                            break;
                        }
                    }
                    if (!initialized) {
                        if (alive && startupError != null) {
                            message.Text = DescribeLoginStartupFailure(stage, startupError);
                            ReportTask("网易云官方网页登录", "失败", message.Text);
                        }
                        return;
                    }
                    try {
                        web.ZoomFactor = 0.9;
                        web.CoreWebView2.Settings.AreDevToolsEnabled = false;
                        web.CoreWebView2.Settings.IsPasswordAutosaveEnabled = false;
                        web.CoreWebView2.Settings.IsGeneralAutofillEnabled = false;
                        web.CoreWebView2.NewWindowRequested += delegate(object sender, CoreWebView2NewWindowRequestedEventArgs e) {
                            e.Handled = true; Uri uri;
                            if (Uri.TryCreate(e.Uri, UriKind.Absolute, out uri) && uri.Scheme == "https" && (uri.Host == "music.163.com" || uri.Host.EndsWith(".163.com", StringComparison.OrdinalIgnoreCase))) web.CoreWebView2.Navigate(e.Uri);
                        };
                        web.CoreWebView2.NavigationCompleted += delegate(object sender, CoreWebView2NavigationCompletedEventArgs e) {
                            if (!alive) return;
                            if (!e.IsSuccess && e.WebErrorStatus != CoreWebView2WebErrorStatus.ConnectionAborted && e.WebErrorStatus != CoreWebView2WebErrorStatus.OperationCanceled) { message.Text = "官方网页加载失败（" + e.WebErrorStatus + "）。请检查网络后重新加载。"; ReportTask("网易云官方网页登录", "失败", "网页加载失败：" + e.WebErrorStatus); }
                        };
                        web.CoreWebView2.Navigate("https://music.163.com/"); timer.Start();
                    } catch (Exception ex) {
                        if (alive) { message.Text = "无法启动官方登录窗口（浏览器控件）：" + ex.GetBaseException().Message; ReportTask("网易云官方网页登录", "失败", message.Text); }
                    }
                };
                window.FormClosed += delegate { alive = false; timer.Stop(); try { web.Dispose(); } catch (Exception) { } if (!completed) ReportTask("网易云官方网页登录", "取消", "官方登录窗口已关闭。"); };
                window.ShowDialog(owner);
            }
        }

        private const int WebViewBusyHResult = unchecked((int)0x800700AA);

        // ERROR_BUSY from WebView2 can arrive directly, wrapped, or aggregated; the HResult or the
        // "(0x800700AA)" suffix in the message is the reliable marker.
        private static bool IsWebViewBusyError(Exception exception)
        {
            if (exception == null) return false;
            var pending = new Stack<Exception>();
            pending.Push(exception);
            while (pending.Count > 0) {
                var current = pending.Pop();
                if (current == null) continue;
                if (current.HResult == WebViewBusyHResult) return true;
                if (!string.IsNullOrEmpty(current.Message) && current.Message.IndexOf("0x800700AA", StringComparison.OrdinalIgnoreCase) >= 0) return true;
                var aggregate = current as AggregateException;
                if (aggregate != null) { foreach (var inner in aggregate.InnerExceptions) pending.Push(inner); }
                else if (current.InnerException != null) pending.Push(current.InnerException);
            }
            return false;
        }

        private static string DescribeLoginStartupFailure(string stage, Exception exception)
        {
            string prefix = "无法启动官方登录窗口（" + stage + "）：";
            string detail = exception.GetBaseException().Message;
            if (IsWebViewBusyError(exception))
                return prefix + "浏览器控件被占用（ERROR_BUSY 0x800700AA）；请关闭其他工具箱/浏览器窗口后重试，或从普通目录重新启动。原始信息：" + detail;
            return prefix + detail;
        }
    }
}
