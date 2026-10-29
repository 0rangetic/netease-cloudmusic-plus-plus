using System;
using System.Drawing;
using System.Windows.Forms;

namespace NeteaseToolbox
{
    internal sealed partial class ToolboxForm
    {
        private PhoneSyncServer phoneSyncServer;
        private PhoneSyncDialog phoneSyncDialog;

        private void StartPhoneSyncServer(bool onlyIfPaired = false)
        {
            if (phoneSyncServer != null) return;
            try
            {
                var server = new PhoneSyncServer(localPlaybackRoot);
                if (onlyIfPaired && server.PairedCount == 0)
                {
                    server.Dispose();
                    return;
                }
                server.Start();
                phoneSyncServer = server;
            }
            catch (Exception ex)
            {
                phoneSyncServer = null;
                ReportTask("手机同步", "失败", "无法启动局域网接收端：" + ex.Message);
            }
        }

        private void StopPhoneSyncServer()
        {
            if (phoneSyncServer != null)
            {
                phoneSyncServer.Dispose();
                phoneSyncServer = null;
            }
        }

        internal void ShowPhoneSyncDialog()
        {
            if (phoneSyncDialog != null && !phoneSyncDialog.IsDisposed)
            {
                phoneSyncDialog.Activate();
                return;
            }
            if (phoneSyncServer == null) StartPhoneSyncServer();
            phoneSyncDialog = new PhoneSyncDialog(this, phoneSyncServer);
            PrepareThemedDialog(phoneSyncDialog);
            phoneSyncDialog.FormClosed += delegate {
                phoneSyncDialog = null;
                if (phoneSyncServer != null && phoneSyncServer.PairedCount == 0) StopPhoneSyncServer();
            };
            phoneSyncDialog.Show(DialogOwner);
        }

        private sealed class PhoneSyncDialog : Form
        {
            private readonly ToolboxForm owner;
            private readonly PhoneSyncServer server;
            private readonly Label codeValue;
            private readonly TextBox statusBox;
            private readonly Button refreshButton;
            private readonly Button clearButton;
            private readonly Timer refreshTimer;

            internal PhoneSyncDialog(ToolboxForm owner, PhoneSyncServer server)
            {
                this.owner = owner;
                this.server = server;
                Text = "手机同步";
                StartPosition = FormStartPosition.CenterParent;
                FormBorderStyle = FormBorderStyle.FixedDialog;
                MaximizeBox = false;
                MinimizeBox = false;
                ShowInTaskbar = false;
                ClientSize = new Size(540, 480);
                BackColor = owner.panel;
                ForeColor = owner.ink;
                Font = owner.UiFont(9F);

                var title = new Label
                {
                    Text = "手机网易云重播同步",
                    AutoSize = false,
                    Location = new Point(16, 14),
                    Size = new Size(508, 26),
                    Font = owner.UiFont(12F, FontStyle.Bold),
                    ForeColor = owner.ink,
                    BackColor = Color.Transparent
                };
                var hint = new Label
                {
                    Text = "手机和电脑连接同一网络，并登录同一个网易云账号。手机端打开 Music Plus → 电脑同步，输入下面的配对码。",
                    AutoSize = false,
                    Location = new Point(16, 44),
                    Size = new Size(508, 42),
                    ForeColor = owner.muted,
                    BackColor = Color.Transparent
                };
                var codeLabel = new Label
                {
                    Text = "配对码",
                    AutoSize = false,
                    Location = new Point(16, 94),
                    Size = new Size(90, 42),
                    ForeColor = owner.muted,
                    BackColor = Color.Transparent,
                    TextAlign = ContentAlignment.MiddleLeft
                };
                codeValue = new Label
                {
                    Text = "------",
                    AutoSize = false,
                    Location = new Point(108, 82),
                    Size = new Size(416, 54),
                    ForeColor = owner.accent,
                    BackColor = Color.Transparent,
                    Font = new Font("Consolas", 28F, FontStyle.Bold),
                    TextAlign = ContentAlignment.MiddleLeft
                };
                refreshButton = new Button
                {
                    Text = "刷新配对码",
                    Location = new Point(16, 142),
                    Size = new Size(130, 34)
                };
                owner.StyleButton(refreshButton, ButtonRole.Secondary);
                refreshButton.Click += delegate
                {
                    if (server != null) server.RefreshPairingCode();
                    UpdateStatus();
                };
                clearButton = new Button
                {
                    Text = "解除全部配对",
                    Location = new Point(154, 142),
                    Size = new Size(140, 34)
                };
                owner.StyleButton(clearButton, ButtonRole.Danger);
                clearButton.Click += delegate
                {
                    if (server == null || server.PairedCount == 0) return;
                    var decision = MessageBox.Show(this,
                        "解除后，已配对的手机需要重新输入配对码。已接收的重播记录不会删除。",
                        "解除全部配对", MessageBoxButtons.OKCancel, MessageBoxIcon.Warning);
                    if (decision == DialogResult.OK)
                    {
                        server.ClearPairings();
                        UpdateStatus();
                    }
                };
                var statusLabel = new Label
                {
                    Text = "接收状态",
                    AutoSize = false,
                    Location = new Point(16, 188),
                    Size = new Size(508, 22),
                    ForeColor = owner.muted,
                    BackColor = Color.Transparent
                };
                statusBox = new TextBox
                {
                    Location = new Point(16, 212),
                    Size = new Size(508, 180),
                    Multiline = true,
                    ReadOnly = true,
                    ScrollBars = ScrollBars.Vertical,
                    WordWrap = true,
                    BackColor = owner.panel,
                    ForeColor = owner.ink,
                    BorderStyle = BorderStyle.FixedSingle,
                    Font = owner.UiFont(9F)
                };
                var note = new Label
                {
                    Text = "配对码 10 分钟内有效。手机只上传网易云音乐的重播；成功一次后 1 天内不再自动上传，失败 1 小时后重试。若手机发现不了电脑，请在 Windows 防火墙允许本程序访问专用网络。",
                    AutoSize = false,
                    Location = new Point(16, 402),
                    Size = new Size(508, 62),
                    ForeColor = owner.muted,
                    BackColor = Color.Transparent
                };

                Controls.Add(title);
                Controls.Add(hint);
                Controls.Add(codeLabel);
                Controls.Add(codeValue);
                Controls.Add(refreshButton);
                Controls.Add(clearButton);
                Controls.Add(statusLabel);
                Controls.Add(statusBox);
                Controls.Add(note);

                refreshTimer = new Timer { Interval = 1000 };
                refreshTimer.Tick += delegate { UpdateStatus(); };
                refreshTimer.Start();
                FormClosed += delegate
                {
                    refreshTimer.Stop();
                    refreshTimer.Dispose();
                };
                UpdateStatus();
            }

            private void UpdateStatus()
            {
                if (server == null)
                {
                    codeValue.Text = "------";
                    statusBox.Text = "接收端未启动。请重新打开工具箱，或在任务消息里查看“手机同步”错误。";
                    refreshButton.Enabled = false;
                    clearButton.Enabled = false;
                    return;
                }
                string code = server.EnsurePairingCode();
                codeValue.Text = code.Length == 6 ? code.Substring(0, 3) + " " + code.Substring(3) : code;
                statusBox.Text = server.StatusText();
                refreshButton.Enabled = server.Running;
                clearButton.Enabled = server.PairedCount > 0;
            }
        }
    }
}
