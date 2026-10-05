# 网易云工具箱

Windows 本地音乐工具：NCM 转换、封面与歌词、曲风分析、听歌画像、播放记录同步、音乐记忆检索，以及可选三行桌面歌词补丁。

## 下载与运行

从本仓库 Releases 下载完整便携包并解压，在 Windows 10/11 x64、.NET Framework 4.8 环境中打开“网易云工具箱.exe”。程序提供内置 Python、Node.js、FFmpeg 及分析组件；不要求安装 Codex 或 npm。选择音乐目录并登录网易云即可使用主要功能。缺少 WebView2 时，可运行随包的“配置运行环境.cmd”联网安装微软运行环境。详细说明见 [便携使用说明](toolbox/portable-README.txt)。

AI 服务、LifeRecall 目录、网易云客户端目录按需配置。三行歌词仅适配网易云音乐 3.1.41 x64 Build 205529，保留 DLL 哈希校验；仓库与便携包均不包含网易云客户端。

## 隐私与文件操作

发布源码和便携包不包含个人听歌数据、登录会话、Cookie、API Key、个人报告或本机路径配置。登录与密钥在当前 Windows 用户下使用 DPAPI 加密；换电脑需重新配置。播放记录和画像缓存按账号存放于本机用户目录。网易云在线功能直接连接网易云；启用 AI 时，相关音乐信息会发送到用户配置的模型服务。

转换会保留解码音频；去重会直接删除较低质量的 NCM 及关联输出，请按需使用。程序启动会注册本机登录启动项用于播放记录同步，可在托盘菜单关闭。

## 源码与构建

- toolbox/：WinForms 界面、账号登录与后台分析脚本。
- convert.ps1：NCM 解码、标签、封面、歌词及去重。
- desktop-lyrics/：可选歌词补丁与启动器。
- music-profile/refresh_listening_report.py：画像统计口径，使用本机账号缓存生成数据。

使用 Windows .NET Framework C# 编译器运行 toolbox/build.ps1。构建时 WebView2 SDK 由 prepare-webview2.ps1 从微软 NuGet 下载并校验 SHA-256。生成便携包需预先准备运行时与 FFmpeg，执行 toolbox/build-portable.ps1；其允许列表会排除个人配置和数据。第三方组件许可证随发行包提供；FFmpeg 源码与许可见 https://ffmpeg.org/legal.html 。

本公开源码从干净快照开始，不包含个人数据分析工程或私有历史。
