网易云工具箱便携包（Windows 10/11 x64，.NET Framework 4.8）

首次使用：
1. 将压缩包完整解压到可以写入的目录，保留所有子目录。
2. 打开“网易云工具箱.exe”，在音乐转换页“选择音乐目录”。
3. 如需在线功能，点击左侧“登录网易云”，在官方网页中完成手机扫码与验证。
   如果缺少 WebView2，点击设置与维护中的“配置登录运行环境”，或者双击“配置运行环境.cmd”。
   该操作只在缺少 WebView2 时联网安装微软运行环境。内置 Python 和 Node.js 不会注册到系统或改动 PATH。
4. 默认转换输出在歌曲旁的 unlock 文件夹，可以在设置中改为其他文件夹。

按需配置：
- AI：在“AI 画像接口与提示词”填写接口地址、模型名和 API Key；也可以关闭 AI，仅使用本机统计。
- 三行歌词：在设置中“网易云安装目录”选择包含 cloudmusic.exe 的目录。
  补丁只支持网易云 3.1.41 x64 Build 205529；哈希不符会拒绝注入，不会绕过版本校验。
  包内包含歌词补丁和 Frida/psutil，未包含网易云客户端。普通转换和画像不要求安装网易云客户端。
- 音乐记忆：需要本机播放记录；LifeRecall 为可选数据源，可在该页面选择其目录。

已包含 FFmpeg、Python 3.12.10、Node.js 24.19.0、PyCryptodome 3.23.0、qrcode 8.2、Frida 17.2.17、psutil 6.1.1，
以及 WebView2 配套 DLL 和微软签名的在线安装引导程序。画像报告在本机生成，不需要安装 Codex、npm 或插件。
音乐转换可以离线运行。在线登录、歌词、网易云标签、账号档案和 AI 请求需要对应服务可连接。

路径和数据：
程序目录内选择的输入/输出路径以相对路径保存；外部目录使用绝对路径，换电脑后可重新选择。
歌曲、已有输出、播放记录不会自动从旧电脑迁移。新电脑需要重新登录并配置 AI 密钥。
登录会话、AI 密钥和账号缓存保存在该电脑当前 Windows 用户的 LOCALAPPDATA/NeteaseToolbox 下。
登录与密钥使用 DPAPI 保护，不能把旧电脑的加密会话直接复制给另一台电脑。
报告按账号隔离保存在用户目录；报告可通过浏览器保存并分享，但包含个人听歌偏好。
包内没有个人账号、Cookie、API Key、歌曲、播放记录或生成报告。
启动后会注册本机登录启动项用于播放记录同步；关闭窗口会隐藏到托盘，托盘菜单可以关闭自动同步。

维护：
可在设置中点击“检查运行环境”，查看缺少的文件和待配置项目。
manifest.json 记录组件 SHA-256，便于检查搬运后的文件是否完整。
第三方许可证位于各自目录：Python LICENSE.txt，Node LICENSE，Frida COPYING，psutil/LICENSE，
qrcode/LICENSE 与 vendor/qrcode-LICENSE，PyCryptodome dist-info/LICENSE.rst，以及 ffmpeg-LICENSE.txt。
WebView2 分发说明：https://learn.microsoft.com/microsoft-edge/webview2/concepts/distribution
FFmpeg 许可证及源码信息：https://ffmpeg.org/legal.html
