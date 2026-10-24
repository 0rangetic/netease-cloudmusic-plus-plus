# 网易云 PC 官方多人一起听：实验补丁 0.4.2

适配现场：Windows 网易云音乐 **3.1.41.205529** 与 **3.1.42.205594** x64，最初验证的热更新 UI 为 `83304b1`。0.4.2 修复客户端自动升级到 3.1.42 后不可用的问题：两版 `libcef.dll` 完全相同（同一 CEF 91 / branch 4472），CEF 结构与 `native-agent.js` 的偏移未变，热接入仍会逐字节核对 `libcef.dll` 的 SHA-256；但渲染层 webpack 模块 id 发生位移（邀请解析器 2093→2092、播放进度 109.b→110.b），补丁改为按能力解析这两个模块并在使用前校验。心跳调度与 0.4.1 一致。集成到工具箱 1.25.2.0。通过工具箱正式 EXE 部署，不另发独立 ZIP。当前发布记录见 [RELEASE-0.4.2.md](RELEASE-0.4.2.md)，历史热接入验收见 [RELEASE-0.3.0.md](RELEASE-0.3.0.md)，用户说明见 [manual.md](manual.md)。

## 使用

**工具箱集成版：**可以先用原快捷方式启动网易云，等主界面加载后，再在工具箱“多人一起听”页点击“启动并连接”。不需要先退出客户端。工具箱随带 Node 和包含 `frida`、`psutil` 的 `runtime/python`，可连接普通启动、没有 9222 调试口的客户端。客户端未启动时，也可以直接从工具箱启动并连接。

**独立便携包 / 源码启动器：**运行 `start.ps1`（便携包入口为 `启动补丁.cmd`）。默认便携构建只附 Node.js 24.19.0 x64：客户端未启动时仍以 `--remote-debugging-address=127.0.0.1 --remote-debugging-port=9222` 启动；纯 Node 运行环境不能热接入普通启动的客户端。需要热接入时，可在打包时通过 `build-portable.ps1 -PythonDirectory <完整运行环境目录>` 附带 `runtime/python`，或把 `NCM_MULTI_PYTHON` 设置为已安装 `frida`、`psutil` 的 **python.exe 绝对路径**。缺少该环境会明确报错；也可完全退出网易云后，再由默认 Node 便携包启动。不会强杀客户端。

网易云主界面右上出现“多人一起听”面板。启动后恢复本账号已加入的多人房间。收到邀请可点击原生私信卡片；也可打开对应私信，点“读取私信邀请”再“加入邀请房间”，或粘贴官方多人链接。多个不同房间时要求明确选择，不擅自替换已加入房间。

“本机暂停”只暂停本机；“重新同步”恢复跟听；“退出房间”经官方接口退出。关闭/收起面板不是退出房间。界面保留最近 100 条任务消息，工具箱也显示任务开始、完成或失败。工具箱状态与日志在 `%LOCALAPPDATA%\NeteaseToolbox\MultiListen\`，本机热接入状态与 helper 日志在其 `native\` 子目录；独立启动器的客户端路径与普通桥日志仍在 `%LOCALAPPDATA%\Netease\CloudMusic\CodexMulti\`。

命令行：`node control.mjs status` 查看状态；`node control.mjs stop` 退出房间；`node control.mjs shutdown` 退出并停用；`node control.mjs watch` 为前台调试入口。状态与停止入口兼容本机热接入和原有 CDP 连接；状态查询不会自行启动热接入 helper。关闭客户端后需重新启动补丁；当前实验不自动适配网页重载或客户端热更新。不与其他注入程序同时修改本脚本的钩子。

## 协议与实现

参考 [Music Party 的协议研究](https://github.com/tabidachinokaze/music-party/blob/main/docs/OFFICIAL-MULTIPLAYER.md)、[对应固定端点表](https://github.com/tabidachinokaze/music-party/blob/main/src/main/multi-api.ts)、[官方分享页](https://st.music.163.com/listen-together/multishare/index.html) 和 [api-enhanced 的官方令牌注册接口](https://github.com/NeteaseCloudMusicApiEnhanced/api-enhanced/blob/main/module/register_checktoken_v3.js)。代码独立实现，未打包第三方仓库、APK 或反编译资源。

多人固定接口前缀 `/api/listen/together/multi/`：`landing/info/get`、`match/ack`、`match/status/get`、`match/heartbeat`、`match/exit`。ACK 使用 `roomId,inviterUid,agree=true,checkToken`；ACK/退出以 `data.success` 判断成功。预览可用不当作入房成功。

请求复用本机客户端的登录与加密网络层；加入所需的官方令牌由 Node 对固定网易云易盾端点即时获取，通过 CDP binding 在内存内返回，不保存 Cookie/token，不请求上传本机日志。

0.3.0 有两种连接方式：原有方式使用本机 `127.0.0.1:9222` CDP；本机热接入由 Python/Frida helper 调用客户端进程内 CEF DevTools API，通过随机命名管道和每次启动生成的 secret 认证与 Node 交换消息，不为热接入新增 TCP 端口。helper 先校验客户端文件版本、x64 架构与 `libcef.dll` 的精确 SHA-256（`a2a441490a9600f7e06b8c4dc609354ff7214add445a55c8fa699760d264e210`），再校验运行中的 CEF wrapper、引用计数链及宿主结构；不匹配即停止，不能把相同 EXE 版本视为充分兼容条件。客户端版本门只接受已实测的 3.1.41.205529 与 3.1.42.205594；`libcef.dll` 哈希仍是更严格的必要条件，其他版本即使版本号相近也会被拒绝。`native/state.json` 含本机管道认证信息，不应分享或打包。

原生导出的只读解析器保持原函数（按能力解析，见下），通过 `Debugger.setBreakpointOnFunctionCall` 的条件表达式取得点击参数，条件返回 false 不暂停调试；多人错误提示仅在匹配到该次真实多人邀请时暂时抑制。桥退出或客户端重载后断点消失。普通双人/其他链接不接管。

播放复用客户端正常歌曲权限检查和原生音频引擎，等曲目加载完成后按毫秒快照换算为秒定位。心跳快照平时约每 10 秒获取；按房间快照和本机经过时间推算，歌曲开头 3 秒及最后 5 秒约每秒获取一次，预计结束后再跟进一次。开头窗口在首次观察时只使用剩余时间，最多 3 秒，不被进度停滞或抖动续期。曲尾每秒窗口连同边界唤醒最多约 7 秒；短歌首尾窗口共同计时且只用一个定时器，不叠加请求。新播放实例重新计算，本机暂停回到普通频率。失败按 10、20、30 秒退避，上限 30 秒。成员刷新仍独立按至少 15 秒间隔判断，不随首尾心跳加速。尚未接入房间实时 IM 推送，提前切歌可能延迟一个普通轮询周期，首次发现时也可能已过新歌开头 3 秒。账号/房间变化使旧加载失效；登录或房间失效停止跟听并在面板显示失败。

当前已实现加入、恢复、人数、跟听和退出。推歌、共享队列编辑、聊天、开房、原生下一首映射多人操作、热更新后自动恢复尚未实现。原生下一首/进度条仍属本机操作，会在下一次同步回到房间状态；暂停请用面板“本机暂停”，原生暂停键目前可能被同步恢复；房间暂停不由本机控制。退出、账号切换、断网、长期挂机和不同权限组合仍需继续验收。

## 停用与回退

回退时应同时恢复对应工具箱与插件组件。

恢复普通使用：先在面板“退出并停用补丁”，确认退出成功；需要彻底重建客户端会话时，再退出网易云并用原来的方式启动。没有磁盘客户端补丁需要撤销。
