using System;
using System.Collections.Generic;
using System.Drawing;
using System.IO;
using System.Threading.Tasks;
using System.Windows.Forms;

namespace NeteaseToolbox
{
    internal sealed partial class ToolboxForm
    {
        private readonly Button accountButton = new Button();
        private readonly Label accountStatus = new Label();
        private readonly PictureBox accountAvatar = new PictureBox();
        private bool loginWindowOpen;
        private void BuildAccountUi(Control sidebar)
        {
            accountButton.Name = "NeteaseLoginButton"; accountButton.Text = "登录网易云";
            accountButton.Location = new Point(16, 310); accountButton.Size = new Size(138, 40);
            StyleButton(accountButton, ButtonRole.Secondary);
            accountButton.Click += delegate { ShowNeteaseLogin(); };
            accountStatus.Name = "NeteaseAccountStatus"; accountStatus.Location = new Point(18, 358);
            accountStatus.Size = new Size(136, 58); accountStatus.ForeColor = muted;
            accountAvatar.Location = new Point(18, 422); accountAvatar.Size = new Size(36, 36); accountAvatar.SizeMode = PictureBoxSizeMode.Zoom;
            sidebar.Controls.Add(accountButton); sidebar.Controls.Add(accountStatus); sidebar.Controls.Add(accountAvatar);
            UpdateAccountUi();
        }
        private void UpdateAccountUi()
        {
            try { var session = NeteaseAuth.Load(); accountButton.Text = "网易云账号"; accountStatus.Text = NeteaseAuth.Text(session, "nickname") + "\n登录状态待检查"; }
            catch { accountButton.Text = "登录网易云"; accountStatus.Text = "未登录 · 手机扫码即可"; }
        }
        private async Task CheckNeteaseSession()
        {
            if (!File.Exists(NeteaseAuth.SessionPath)) return;
            try {
                string cookie = NeteaseAuth.Cookie();
                var session = await Task.Run(() => NeteaseAuth.Validate(cookie));
                if (!IsDisposed) accountStatus.Text = NeteaseAuth.Text(session, "nickname") + "\n已登录";
                await LoadAccountAvatar(session);
            } catch (Exception ex) {
                if (!IsDisposed) { accountStatus.Text = "登录状态检查失败"; ReportTask("网易云登录", "失败", ex.Message); }
            }
        }
        private async Task LoadAccountAvatar(Dictionary<string, object> session)
        {
            try {
                Uri url;
                if (!Uri.TryCreate(NeteaseAuth.Text(session, "avatarUrl"), UriKind.Absolute, out url) || !url.Host.EndsWith(".music.126.net", StringComparison.OrdinalIgnoreCase)) return;
                var bitmap = await Task.Run(() => {
                    var request = (System.Net.HttpWebRequest)System.Net.WebRequest.Create(new UriBuilder(url) { Scheme = "https", Port = -1 }.Uri);
                    request.Proxy = null; request.Timeout = 10000; request.ReadWriteTimeout = 10000;
                    using (var response = request.GetResponse()) using (var stream = response.GetResponseStream()) using (var image = Image.FromStream(stream)) return new Bitmap(image);
                });
                if (IsDisposed || NeteaseAuth.Text(NeteaseAuth.Load(), "userId") != NeteaseAuth.Text(session, "userId")) { bitmap.Dispose(); return; }
                if (accountAvatar.Image != null) accountAvatar.Image.Dispose(); accountAvatar.Image = bitmap;
            } catch { /* Avatar failure does not invalidate an authenticated account. */ }
        }
        private void ShowNeteaseLogin()
        {
            if (loginWindowOpen) return;
            loginWindowOpen = true;
            using (var window = new Form { Text = "网易云账号", ClientSize = new Size(420, 540), StartPosition = FormStartPosition.CenterParent, FormBorderStyle = FormBorderStyle.FixedDialog, MaximizeBox = false, MinimizeBox = false, Font = Font, BackColor = background, ForeColor = ink }) {
                var instruction = new Label { Text = "打开手机网易云音乐，扫一扫并确认登录", Location = new Point(20, 18), Size = new Size(380, 30) };
                var picture = new PictureBox { Location = new Point(60, 55), Size = new Size(300, 300), SizeMode = PictureBoxSizeMode.CenterImage, BackColor = Color.White };
                var status = new Label { Text = "点击生成二维码", Location = new Point(20, 365), Size = new Size(380, 52) };
                var official = Button("官方网页登录（推荐）", true); official.Location = new Point(20, 423); official.Width = 380;
                var refresh = Button("备用接口二维码", false); refresh.Location = new Point(20, 475); refresh.Width = 164;
                var import = Button("导入 Cookie", false); import.Location = new Point(192, 475); import.Width = 100;
                var logout = Button("退出账号", false); logout.Location = new Point(300, 475); logout.Width = 100;
                window.Controls.AddRange(new Control[] { instruction, picture, status, official, refresh, import, logout });
                bool alive = true, busy = false, pending = false;
                int generation = 0;
                string key = null;
                var qrSession = new System.Net.CookieContainer();
                var timer = new System.Windows.Forms.Timer { Interval = 2500 };
                Action<string> fail = message => { status.Text = message; accountStatus.Text = "登录未完成"; ReportTask("网易云登录", "失败", message); };
                Func<string, Func<bool>, Task> finish = async (cookie, canSave) => {
                    var session = await Task.Run(() => NeteaseAuth.Validate(cookie));
                    if (!alive || !canSave()) return;
                    if (loadingProfile || loadingPortrait || analyzingGenres || loadingGenreResults || searchingMemory)
                        throw new InvalidOperationException("请等待当前分析任务完成，再登录或切换账号。");
                    NeteaseAuth.Save(session);
                    timer.Stop(); pending = false;
                    accountButton.Text = "网易云账号"; accountStatus.Text = NeteaseAuth.Text(session, "nickname") + "\n已登录";
                    if (accountAvatar.Image != null) { accountAvatar.Image.Dispose(); accountAvatar.Image = null; }
                    status.Text = "已登录：" + NeteaseAuth.Text(session, "nickname");
                    profileSongs.Items.Clear(); profileStatus.Text = "账号已登录，请刷新网易云数据。";
                    allPortraitText.Clear(); recentPortraitText.Clear();
                    ReportTask("网易云登录", "完成", "已登录为“" + NeteaseAuth.Text(session, "nickname") + "”，登录凭证已在本机加密保存。");
                    try { NeteaseAuth.MigratePersonalCache(session); }
                    catch (Exception ex) { ReportTask("旧账号缓存迁移", "部分失败", "登录已保存，旧文件保留；缓存复制失败：" + ex.Message); }
                    genreSongs.Items.Clear(); genreRows.Clear(); memoryResults.Items.Clear(); memoryEvidence.Clear(); recentGenreComparison.Items.Clear();
                    ScanSongs();
                    if (playbackRecorder != null) playbackRecorder.Dispose();
                    playbackRecorder = new PlaybackRecorder(playbackHistoryPath, ReportPlaybackMessage);
                    await playbackRecorder.StartAsync();
                    await LoadAccountAvatar(session);
                };
                official.Click += delegate {
                    if (busy) return;
                    timer.Stop(); pending = false; busy = true;
                    try { ShowOfficialNeteaseLogin(window, finish); }
                    catch (Exception) { fail("无法打开官方登录窗口，请检查 WebView2 安装文件和运行环境。"); }
                    finally { busy = false; if (alive) status.Text = accountStatus.Text + "\n可点击官方网页登录继续验证或切换账号。"; }
                };
                refresh.Click += async delegate {
                    if (busy) return;
                    busy = true; refresh.Enabled = import.Enabled = logout.Enabled = false; timer.Stop(); generation++; int current = generation;
                    qrSession = new System.Net.CookieContainer();
                    status.Text = "正在获取登录二维码…"; ReportTask("网易云登录", "开始", "正在生成扫码登录二维码。");
                    try {
                        var qr = await Task.Run(() => {
                            string ignored; var response = NeteaseAuth.RequestQr("/api/login/qrcode/unikey", new Dictionary<string, object> { { "type", 3 } }, qrSession, out ignored);
                            string value = NeteaseAuth.Text(response, "unikey");
                            if (NeteaseAuth.Text(response, "code") != "200" || value.Length == 0) throw new InvalidOperationException("网易云未返回登录二维码，请稍后重试。");
                            var image = NeteaseAuth.QrImage("https://music.163.com/login?codekey=" + Uri.EscapeDataString(value), File.Exists(pythonPath) ? pythonPath : FindGenrePython());
                            return new KeyValuePair<string, Bitmap>(value, image);
                        });
                        if (!alive || current != generation) { qr.Value.Dispose(); return; }
                        key = qr.Key; if (picture.Image != null) picture.Image.Dispose(); picture.Image = qr.Value;
                        status.Text = "等待手机扫码"; pending = true; timer.Start();
                    } catch (Exception ex) { if (alive) fail(ex.Message); }
                    finally { busy = false; if (alive) refresh.Enabled = import.Enabled = logout.Enabled = true; }
                };
                timer.Tick += async delegate {
                    if (busy || !alive || key == null) return;
                    busy = true; refresh.Enabled = import.Enabled = logout.Enabled = false;
                    try {
                        string cookie = ""; string activeKey = key;
                        var response = await Task.Run(() => NeteaseAuth.RequestQr("/api/login/qrcode/client/login", new Dictionary<string, object> { { "key", activeKey }, { "type", 3 } }, qrSession, out cookie));
                        if (!alive) return;
                        string code = NeteaseAuth.Text(response, "code");
                        if (code == "801") status.Text = "等待手机扫码";
                        else if (code == "802") status.Text = "已扫码，请在手机网易云确认登录";
                        else if (code == "803" || (code == "200" && cookie.IndexOf("MUSIC_U=", StringComparison.Ordinal) >= 0)) {
                            timer.Stop();
                            if (cookie.IndexOf("MUSIC_U=", StringComparison.Ordinal) < 0) throw new InvalidOperationException("扫码已确认，但未取得有效登录凭证，请刷新二维码重试。");
                            status.Text = "正在验证账号…"; await finish(cookie, () => alive);
                        } else if (code == "800") { timer.Stop(); pending = false; fail("二维码已过期，请点击刷新二维码。"); }
                        else if (code == "8821") throw new InvalidOperationException("网易云要求行为验证（8821）。请点击“官方网页登录”完成验证；继续刷新接口二维码无法完成此验证。");
                        else throw new InvalidOperationException("网易云扫码检查失败（状态码：" + (code.Length == 0 ? "缺失" : code) + "），请使用官方网页登录。");
                    } catch (Exception ex) { timer.Stop(); pending = false; if (alive) fail(ex.Message); }
                    finally { busy = false; if (alive) refresh.Enabled = import.Enabled = logout.Enabled = true; }
                };
                import.Click += async delegate {
                    if (busy) return;
                    timer.Stop();
                    using (var dialog = new Form { Text = "备用登录：导入 Cookie", ClientSize = new Size(430, 170), StartPosition = FormStartPosition.CenterParent }) {
                        var input = new TextBox { Location = new Point(15, 35), Size = new Size(400, 32), UseSystemPasswordChar = true };
                        var save = new Button { Text = "验证并登录", Location = new Point(290, 115), Size = new Size(125, 35), DialogResult = DialogResult.OK };
                        dialog.Controls.AddRange(new Control[] { input, save });
                        if (dialog.ShowDialog(window) != DialogResult.OK) { if (pending) timer.Start(); return; }
                        timer.Stop(); busy = true; refresh.Enabled = import.Enabled = logout.Enabled = false;
                        ReportTask("网易云登录", "开始", "正在验证导入的登录凭证。");
                        try { await finish(input.Text.Trim(), () => alive); }
                        catch (Exception ex) { if (alive) fail(ex.Message); }
                        finally { busy = false; if (alive) refresh.Enabled = import.Enabled = logout.Enabled = true; }
                    }
                };
                logout.Click += delegate {
                    if (busy) return;
                    if (loadingProfile || loadingPortrait || analyzingGenres || loadingGenreResults || searchingMemory) { fail("请等待当前分析任务完成，再退出账号。"); return; }
                    try {
                        timer.Stop(); pending = false; key = null;
                        if (playbackRecorder != null) { playbackRecorder.Dispose(); playbackRecorder = null; }
                        if (File.Exists(NeteaseAuth.SessionPath)) File.Delete(NeteaseAuth.SessionPath);
                        if (accountAvatar.Image != null) { accountAvatar.Image.Dispose(); accountAvatar.Image = null; }
                        UpdateAccountUi(); profileSongs.Items.Clear(); allPortraitText.Clear(); recentPortraitText.Clear(); status.Text = "已退出；本机登录凭证已删除。";
                        genreSongs.Items.Clear(); genreRows.Clear(); memoryResults.Items.Clear(); memoryEvidence.Clear(); recentGenreComparison.Items.Clear();
                        ReportTask("网易云登录", "完成", "已退出账号并删除本机登录凭证。");
                    } catch (Exception ex) { fail(ex.Message); }
                };
                window.FormClosed += delegate { alive = false; generation++; timer.Stop(); if (pending || busy) ReportTask("网易云登录", "取消", "扫码窗口已关闭。"); };
                window.Shown += delegate { if (!File.Exists(NeteaseAuth.SessionPath)) official.PerformClick(); else status.Text = accountStatus.Text + "\n可使用官方网页登录切换账号。"; };
                try { window.ShowDialog(this); }
                finally { alive = false; timer.Dispose(); if (picture.Image != null) picture.Image.Dispose(); loginWindowOpen = false; }
            }
        }
        private void ReportPlaybackMessage(string message)
        {
            if (IsDisposed || !IsHandleCreated) return;
            Action report = () => {
                if (message.StartsWith("失败：", StringComparison.Ordinal)) { ReportTask("播放记录同步", "失败", message.Substring(3)); if (message.Contains("登录")) accountStatus.Text = "登录可能已失效 · 点击重新扫码"; }
                else if (message.StartsWith("正在同步", StringComparison.Ordinal)) ReportTask("播放记录同步", "开始", message);
                else ReportTask("播放记录同步", "完成", message);
            };
            if (InvokeRequired) BeginInvoke(report); else report();
        }
    }
}
