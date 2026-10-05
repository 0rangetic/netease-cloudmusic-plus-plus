using System;
using System.Diagnostics;
using System.IO;
using System.Text;
using System.Threading.Tasks;
using System.Windows.Forms;
using Microsoft.Win32;
using Microsoft.Web.WebView2.Core;

namespace NeteaseToolbox
{
    internal sealed partial class ToolboxForm
    {
        private string LyricFolder()
        {
            var bundled = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "Lyric3");
            if (File.Exists(Path.Combine(bundled, "launcher.py"))) return bundled;
            return Path.GetFullPath(Path.Combine(AppDomain.CurrentDomain.BaseDirectory, @"..\Lyric3"));
        }

        private string ClientFolder()
        {
            var setting = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "client-folder.txt");
            try
            {
                if (File.Exists(setting))
                {
                    var value = File.ReadAllText(setting, Encoding.UTF8).Trim();
                    if (value.Length > 0) return ResolvePortablePath(value);
                }
                var nearby = Path.GetFullPath(Path.Combine(AppDomain.CurrentDomain.BaseDirectory, ".."));
                if (File.Exists(Path.Combine(nearby, "cloudmusic.exe"))) return nearby;
                foreach (var process in Process.GetProcessesByName("cloudmusic"))
                    using (process)
                    {
                        try { return Path.GetDirectoryName(process.MainModule.FileName); } catch { }
                    }
                using (var key = Registry.CurrentUser.OpenSubKey(@"Software\Netease\CloudMusic"))
                {
                    if (key != null)
                    {
                        var path = Convert.ToString(key.GetValue("InstallPath"));
                        if (File.Exists(Path.Combine(path, "cloudmusic.exe"))) return path;
                    }
                }
            }
            catch { }
            return "";
        }

        private void ChooseClientFolder()
        {
            using (var dialog = new FolderBrowserDialog())
            {
                dialog.Description = "选择包含 cloudmusic.exe 的网易云音乐安装目录（三行歌词仅适配 3.1.41 x64 Build 205529）";
                dialog.SelectedPath = ClientFolder();
                if (dialog.ShowDialog(this) != DialogResult.OK) return;
                try
                {
                    if (!File.Exists(Path.Combine(dialog.SelectedPath, "cloudmusic.exe")))
                        throw new FileNotFoundException("所选目录没有 cloudmusic.exe。");
                    if (LyricHelperIds().Count > 0) ToggleLyrics();
                    File.WriteAllText(Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "client-folder.txt"),
                        SavePortablePath(Path.GetFullPath(dialog.SelectedPath)), new UTF8Encoding(false));
                    ReportTask("网易云安装目录", "完成", dialog.SelectedPath + "；客户端版本仍会在启动歌词时校验。");
                }
                catch (Exception ex) { ReportTask("网易云安装目录", "失败", ex.Message); }
            }
        }

        private async Task SetupPortableEnvironment()
        {
            ReportTask("配置运行环境", "开始", "正在检查内置组件；缺少 WebView2 时将联网安装微软运行环境。");
            try
            {
                var result = await Task.Run(() => {
                    var info = new ProcessStartInfo("powershell.exe") {
                        Arguments = "-NoLogo -NoProfile -ExecutionPolicy Bypass -File " + Quoted(Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "SetupEnvironment.ps1")),
                        UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true,
                        StandardOutputEncoding = Encoding.UTF8, StandardErrorEncoding = Encoding.UTF8
                    };
                    using (var process = new Process { StartInfo = info })
                    {
                        var output = new StringBuilder();
                        process.OutputDataReceived += delegate(object sender, DataReceivedEventArgs args) { if (args.Data != null) { lock (output) output.AppendLine(args.Data); WriteLog(args.Data); } };
                        process.ErrorDataReceived += delegate(object sender, DataReceivedEventArgs args) { if (args.Data != null) { lock (output) output.AppendLine(args.Data); } };
                        process.Start(); process.BeginOutputReadLine(); process.BeginErrorReadLine(); process.WaitForExit();
                        return new Tuple<int, string>(process.ExitCode, output.ToString());
                    }
                });
                ReportTask("配置运行环境", result.Item1 == 0 ? "完成" : "失败", result.Item2);
            }
            catch (Exception ex) { ReportTask("配置运行环境", "失败", ex.Message); }
        }

        private async Task CheckPortableEnvironment()
        {
            ReportTask("运行环境检查", "开始", "正在检查随包组件及本机配置。");
            try
            {
                var result = await Task.Run(() => {
                    var messages = new StringBuilder();
                    int missing = 0;
                    foreach (var relative in new[] { "convert.ps1", "ffmpeg.exe", @"runtime\python\python.exe",
                        @"runtime\node\node.exe", "portable_report.py", "Microsoft.Web.WebView2.Core.dll",
                        "Microsoft.Web.WebView2.WinForms.dll", "WebView2Loader.dll", @"Lyric3\launcher.py" })
                    {
                        bool exists = File.Exists(Path.Combine(AppDomain.CurrentDomain.BaseDirectory, relative));
                        messages.AppendLine((exists ? "✓ " : "缺少：") + relative);
                        if (!exists) missing++;
                    }
                    var info = new ProcessStartInfo(FindGenrePython()) {
                        Arguments = "-c \"import ssl,sqlite3,ctypes;from Crypto.Cipher import AES;import qrcode,frida,psutil;print('Python components OK')\"",
                        UseShellExecute = false, CreateNoWindow = true, RedirectStandardError = true,
                        RedirectStandardOutput = true, WorkingDirectory = AppDomain.CurrentDomain.BaseDirectory
                    };
                    using (var process = Process.Start(info))
                    {
                        var error = process.StandardError.ReadToEnd();
                        process.StandardOutput.ReadToEnd(); process.WaitForExit();
                        if (process.ExitCode != 0) { missing++; messages.AppendLine("Python 组件失败：" + error); }
                        else messages.AppendLine("✓ Python 组件可用");
                    }
                    try { messages.AppendLine("✓ WebView2：" + CoreWebView2Environment.GetAvailableBrowserVersionString()); }
                    catch { missing++; messages.AppendLine("WebView2 未安装：运行随包的“配置运行环境.cmd”，联网安装。音乐转换不受影响。"); }
                    messages.AppendLine(Directory.Exists(InputRoot) ? "✓ 音乐目录：" + InputRoot : "待配置：在音乐转换页选择音乐目录。");
                    messages.AppendLine(File.Exists(Path.Combine(ClientFolder(), "cloudmusic.exe")) ? "✓ 网易云客户端：" + ClientFolder() : "可选配置：三行歌词需选择兼容的网易云安装目录。");
                    messages.AppendLine("账号和 AI 密钥需要在新电脑重新配置；播放记录、分析缓存保存在该电脑的当前 Windows 用户目录。");
                    return new Tuple<int, string>(missing, messages.ToString());
                });
                ReportTask("运行环境检查", result.Item1 == 0 ? "完成" : "部分失败", result.Item2);
                MessageBox.Show(this, result.Item2, "运行环境检查", MessageBoxButtons.OK,
                    result.Item1 == 0 ? MessageBoxIcon.Information : MessageBoxIcon.Warning);
            }
            catch (Exception ex) { ReportTask("运行环境检查", "失败", ex.Message); }
        }
    }
}
