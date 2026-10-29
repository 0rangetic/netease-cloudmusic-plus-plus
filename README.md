# 网易云++

面向 Windows 的网易云音乐本地工具项目，当前包含：

- `toolbox/`：网易云工具箱 WinForms 前端、曲风分析、听歌画像和播放记录同步。
- `convert.ps1`：NCM 转换、封面与歌词处理、歌曲去重。
- `desktop-lyrics/`：网易云音乐三行桌面歌词补丁源码和诊断脚本。
- `music-profile/`：听歌画像分析与图表报告应用。

正式工具箱部署为正式发布目录中的 `网易云工具箱.exe`。构建方式、运行依赖和功能说明见 [toolbox/README.md](toolbox/README.md)。

## 安全与本机数据

网易云 Cookie、AI 服务密钥和输出目录等配置只从本机配置读取，不纳入 Git。打包运行时、解包资源、编译产物、缓存和生成报告也由 `.gitignore` 排除。
