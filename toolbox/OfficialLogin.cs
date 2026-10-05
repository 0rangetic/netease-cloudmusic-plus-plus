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
            using (var web = new WebView2 { Dock = DockStyle.Fill })
            using (var timer = new System.Windows.Forms.Timer { Interval = 1800 }) {
                var message = new Label { Dock = DockStyle.Top, Height = 58, Padding = new Padding(16, 8, 16, 8), Text = "在网易云官方页面点击右上角“登录”，用手机扫码。若出现验证，请在此窗口完成。登录后工具箱会自动保存。" };
                var footer = new FlowLayoutPanel { Dock = DockStyle.Bottom, Height = 48, Padding = new Padding(12, 5, 12, 5) };
                var reload = new Button { Text = "重新加载官方网页", Width = 160, Height = 32 };
                var done = new Button { Text = "我已登录，检查状态", Width = 160, Height = 32 };
                footer.Controls.Add(reload); footer.Controls.Add(done);
                window.Controls.Add(web); window.Controls.Add(message); window.Controls.Add(footer);
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
                    try {
                        string folder = Path.Combine(Path.GetDirectoryName(NeteaseAuth.SessionPath), "WebLogin");
                        var environment = await CoreWebView2Environment.CreateAsync(null, folder);
                        if (!alive) return;
                        var options = environment.CreateCoreWebView2ControllerOptions(); options.IsInPrivateModeEnabled = true;
                        options.ProfileName = "NeteaseLogin";
                        stage = "浏览器控件";
                        await web.EnsureCoreWebView2Async(environment, options);
                        if (!alive) return;
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
                        if (alive) { message.Text = "无法启动官方登录窗口（" + stage + "）：" + ex.GetBaseException().Message; ReportTask("网易云官方网页登录", "失败", message.Text); }
                    }
                };
                window.FormClosed += delegate { alive = false; timer.Stop(); if (!completed) ReportTask("网易云官方网页登录", "取消", "官方登录窗口已关闭。"); };
                window.ShowDialog(owner);
            }
        }
    }
}
