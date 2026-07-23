# Genshin Browser — WinUI 3 全项目迁移计划

> 用户批准日期：2026-07-23  
> 计划状态：执行中  
> 总体目标：将浏览器浮窗和控制窗口全部从 WPF 重构为 WinUI 3  
> 参考实现：`E:\testcode1\Snap.Hutao.Remastered-main` 的透明紧凑 WebView2 浮窗  
> 唯一计划文件：本文件；所有 agent 每完成一步必须勾选并记录验证证据

---

> **当前执行门槛（2026-07-23）**：用户已明确要求先勾选现有实现，再执行 build、test 和启动验证。P1–P9 已落地的实现项按本次指令标记完成；阶段总项、人工验收项和发布验收项仍以实际验证结果为准，不得用静态检查替代运行验收。

## 0. 总进度

- [x] P0.0 建立根目录 `AGENTS.md` 与本规范计划
- [ ] P0 基线、边界与兼容性清单完成
- [ ] P1 新 WinUI 3 解决方案与应用骨架可编译
- [ ] P2 平台无关核心与状态契约完成
- [ ] P3 WebView2 会话与浏览器能力迁移完成
- [ ] P4 WinUI `BrowserWindow`、窗口模式与整窗半透明完成
- [ ] P5 WinUI `ControlWindow` 与全部控制功能迁移完成
- [ ] P6 应用生命周期、快捷键和多窗口协同完成
- [ ] P7 主题、语言、样式、对话框和辅助交互完成
- [ ] P8 设置、数据、WebView profile 与窗口位置兼容完成
- [ ] P9 测试、CI、发布和自包含分发完成
- [ ] P10 删除 WPF、完成最终回归与文档切换
- [ ] 全部 WinUI 3 迁移验收完成

### 接手看板

| 项目 | 当前状态 |
|---|---|
| 本轮授权 | 接续 Claude 未完成修改；修复 WinUI 视觉等价和半透明性能问题，并执行 build/test/run |
| 当前活跃领取 | P4.5/P5/P7 视觉与性能收口，Codex `/root` |
| 已有迁移代码 | P1–P9 大部分实现已写入，但未经过正式构建、测试和人工回归 |
| 下一执行门槛 | 用户明确要求继续实施；随后先确认稳定工具链并完成可编译门槛 |
| Legacy WPF | 保留到 WinUI 构建、功能回归和发布验收全部通过后，再执行 P10 删除 |
| 完成标记 | 只有“实现 + 该步骤验证 + Progress log 证据”齐全才从 `[ ]` 改为 `[x]` |

### Current work

开始任何步骤前，agent 必须在下表声明；完成、暂停或放弃时删除对应行，避免留下假占用。

| Step | Agent | Started | Planned files | Status / blocker |
|---|---|---|---|---|
| P4.5 + P5 + P7 | Codex `/root` | 2026-07-24 | `src/GenshinBrowser.App/{Resources,Windows,Browser}`, `src/GenshinBrowser.Core/ViewModels` | 对齐旧 WPF 控制窗和浏览窗视觉；减少半透明路径的高频更新与无效渲染；随后 build/test/run |

### Awaiting verification

下表记录已写入但尚未满足勾选条件的存量实现，**不代表步骤仍被对应 Agent 占用**。

| Step | Agent | Started | Planned files | Status / blocker |
|---|---|---|---|---|
| P1.3 + P1.4 | Codex `/root` | 2026-07-23 | `src/GenshinBrowser.App/App.xaml*`, `Hosting/ApplicationHost.cs`, `Windows/*.xaml*` | 实现已写入，XML 静态检查通过；等待后续统一 build/run 后才能勾选 |
| P2.3 | Codex `/root` | 2026-07-23 | `src/GenshinBrowser.Core/Models/Hotkey*`, `src/GenshinBrowser.Core/Services/KeyboardHookService.cs`, Core csproj | HotkeyModifiers/gesture/formatter、旧 WPF Key 转换和 Core hook alias 已写入；等待测试执行后勾选 |
| P2.6 | Codex `/root` | 2026-07-23 | `src/GenshinBrowser.Core/ViewModels`, `src/GenshinBrowser.Core/Threading`, `src/GenshinBrowser.App/Threading`, `src/GenshinBrowser.App/Localization` | 已建立完整 UI-neutral 控制 VM、文本资源契约及 DispatcherQueue 适配；静态检索无 WPF 类型，等待统一 build 后勾选 |
| P3.1 - P3.8 | Codex `/root` | 2026-07-23 | `src/GenshinBrowser.App/Browser`, `src/GenshinBrowser.Core/Services/{DownloadsService,WebViewDataSizeCalculator}.cs`, `Hosting/ApplicationHost.cs` | 共享环境、标准 WebView2、导航/历史/收藏/下载/播放/zoom/缓存/进程恢复代码已写入；缓存限定为 DiskCache；恢复等待点和初始化入口均有 shutdown 闸门，关闭期不会新建 WebView；等待统一 build/run 后逐项勾选 |
| P4.1 - P4.7 | Codex `/root` | 2026-07-23 | `src/GenshinBrowser.App/Windows/BrowserWindow.xaml*`, `src/GenshinBrowser.App/Windowing` | AppWindow 模式、AppWindow.TitleBar、系统 caption inset、独立可交互标题栏、DPI 位置服务和本地 HWND alpha 已写入；尺寸/四角操作会先还原最大化并夹紧工作区，浮窗标题栏感应区在显示时关闭命中；等待统一验证 |
| P5.1 - P5.7 | Codex `/root` | 2026-07-23 | `src/GenshinBrowser.App/Windows/ControlWindow.xaml*`, `src/GenshinBrowser.Core/ViewModels/ControlWindowViewModel.cs` | 导航、收藏/历史、下载、浮窗设置、快捷键录制、`Ctrl+L` 地址栏聚焦、模式/语言选中态、AppWindow.TitleBar inset 与 BrowserSession 接线已写入；等待统一 build/run 后逐项勾选 |
| P6.1 - P6.5 | Codex `/root` | 2026-07-23 | `src/GenshinBrowser.App/App.xaml.cs`, `Hosting/ApplicationHost.cs`, `Browser/BrowserSession.cs`, `Windows/*.xaml*`, `Windowing/WindowOwnerService.cs`, `src/GenshinBrowser.Core/Services/{History,Favorites,Downloads}Service.cs` | 已实现预关闭清理、双窗激活聚合、ControlWindow owner/tool-window 关系、页面状态收口和防抖写盘退出加固；静态检查通过，等待统一 build/run 验证 |
| P7.1 - P7.6 | Codex `/root` | 2026-07-23 | `src/GenshinBrowser.App/Localization`, `Converters`, `App.xaml`, `Resources/Styles.xaml`, `Windows/*.xaml*`, `Resources/i18n/*.xaml` | Light/Dark 色板、共享样式和非循环过渡已接入；`LocalizationBinding` 支持主文案 + tooltip 双 key 并同步 AutomationName；恢复后新 WebView2 也重新设置本地化 AutomationName；等待 build/run/人工检查 |
| P8.1 - P8.4 | Codex `/root` | 2026-07-23 | `src/GenshinBrowser.Core/Models/AppSettings.cs`, `Services/SettingsService.cs`, `Services/JsonFileWriter.cs`, `tests/GenshinBrowser.Tests/MigrationPolicyTests.cs`, `src/GenshinBrowser.App/Hosting/ApplicationDataPaths.cs`, `Windowing/AppWindowPlacementService.cs` | 已识别无 SchemaVersion 与 schema v1 的旧 WPF settings；无效/越界窗口值有 Core 迁移用例；临时文件清理仅限顶层 JSON GUID 文件；Profile 无迁移/目录删除且缓存只清 DiskCache；等待旧版数据与多 DPI 运行回归 |
| P9.1 - P9.5 | Codex `/root` | 2026-07-23 | `tests/GenshinBrowser.Tests`, `.github/workflows/build.yml`, `release`, App csproj, `README.md` | 测试引用、关键迁移测试、solution 级 Debug/Release CI、WinUI 发布入口和文档已切换代码；统一验证暂缓：本机离线 NuGet 缓存没有 `Microsoft.WindowsAppSDK 2.2.0`，遵守用户要求不触发下载/提权 |
| P10.2 - P10.4 preparation | Codex `/root` | 2026-07-23 | `src/GenshinBrowser.Core/Services`, `src/GenshinBrowser.Core/ViewModels`, Legacy csproj links | 平台无关服务/命令已物理移入 Core；Legacy 暂时反向链接，待 WinUI 验证通过后删除 WPF UI 与条件分支 |

### Progress log

完成步骤后必须追加一行，证据应包含执行过的构建、测试、检索或人工验收。

| Date | Step | Result | Verification evidence |
|---|---|---|---|
| 2026-07-23 | P0.0 | 完成 | 新增根目录 `AGENTS.md` 与 `WINUI3_MIGRATION_PLAN.md`；未修改产品代码 |
| 2026-07-23 | P0.1 | 完成 | `git status --short` 仅显示既有 `MainWindow.xaml.cs` 修改，以及新建的 `AGENTS.md`、`WINUI3_MIGRATION_PLAN.md`；`git diff --stat` 仍为既有 51 行删除 |
| 2026-07-23 | P1.1 | 完成 | 新增 `GenshinBrowser.sln`，包含 Legacy、Core、WinUI App、Tests；新增 Core 与 App csproj；静态检查确认 solution/project 路径存在且引用正确，未执行 restore/build/test |
| 2026-07-23 | P1.2 | 完成 | WinUI App csproj 已配置 `UseWinUI=true`、`UseWPF=false`、unpackaged、Windows App SDK self-contained、.NET self-contained、x64、现有 manifest、WebView2 `1.0.4078.44` 与 Windows App SDK `2.2.0`；未执行 restore/build/test |
| 2026-07-23 | P2.1 | 完成 | Core 新增 UI-independent AppConfig、浏览器状态/窗口模式/历史/收藏/下载模型及 URL/导航/时间工具；`rg` 静态检索确认 Core 无 `System.Windows`、PresentationFramework 或 WPF WebView2 引用；未执行 build/test |
| 2026-07-23 | P2.2 | 完成 | Core 编入 Settings、History、Favorites、Downloads、FileLogger、JsonFileWriter；设置 schema v2 保持旧属性名并内置 WPF Key→VK 转换；静态检索确认这些 Core 数据服务无 UI 框架依赖；未执行 build/test |
| 2026-07-23 | P2.4 | 完成 | 新增 UI-neutral `IBrowserSession`、`WindowCorner`、`WindowBounds`；静态检索确认契约未暴露 WPF/WinUI Window、控件、键盘事件或 Dispatcher 类型；未执行 build/test |
| 2026-07-23 | P2.5 | 完成 | 新增 `IWindowModeService`、`IWindowPlacementService`、`IWindowTransparencyService`；契约只使用 Core 的 mode/bounds/corner 类型，静态检索无 UI 框架类型；未执行 build/test |
| 2026-07-23 | P0.3 | 完成 | 对照旧 `IControlBrowser`、`MainWindow` 与 `ControlWindow` 公开能力，将导航、数据、下载、透明度、模式、快捷键、主题语言、生命周期和发布入口逐项写入本计划功能等价矩阵 |
| 2026-07-23 | P0.4 | 完成 | 从现有服务与新 `ApplicationDataPaths` 固定 `%LocalAppData%\GenshinBrowser` 下 settings/history/favorites/downloads/logs/WebViewProfile 名称，并记录 settings schema v2 与旧 WPF Key→VK 兼容规则 |
| 2026-07-23 | 验证环境 | 暂缓 | `dotnet nuget locals global-packages --list` 指向的离线缓存及用户缓存均无 `Microsoft.WindowsAppSDK`；未执行 restore/build/test，避免触发网络或权限请求 |
| 2026-07-23 | WinUI 静态收尾审计 | 静态检查通过 | PowerShell Roslyn 解析全部 C# 无语法错误；全部 XAML 为合法 XML；两个 WinUI 窗口的事件处理器全部可解析；132 个运行时本地化 key 在中英文资源中齐全；72 个 `{Binding}` 与 8 个 `{x:Bind}` 路径静态存在；`git diff --check` 通过；`src` 无 `MemoryUsageTargetLevel`、WPF CompositionControl、GraphicsCapture/D3DImage 或透明页面 CSS；既有 `MainWindow.xaml.cs` 仍为 0 增/51 删；未执行 restore/build/test/run |
| 2026-07-23 | P7/P8 静态加固检查 | 实现完成、阶段未勾选 | 新增 WinUI `Resources/Styles.xaml` 并合并到 App；两个窗口应用共享样式，3 处进入过渡均为非循环 `EntranceThemeTransition`；识别无 SchemaVersion 的旧 settings 并新增迁移用例；stale cleanup 仅匹配顶层 `*.json.{32位N格式GUID}.tmp`；WebView 缓存清理仅为 DiskCache。Roslyn 解析 108 个 C# 文件无语法错误，13 个 XAML 均为合法 XML，41 个窗口事件处理器齐全，30 个 `Gb*` 资源引用无缺失，中英文各 188 个资源 key 覆盖全部 60 个窗口本地化 key；`git diff --check` 通过，变更的产品/测试文件无尾随空白；`src` 无低内存优先级、WPF 合成捕获、透明页面 CSS、AllDomStorage、循环动画或 Profile 删除/移动；`MainWindow.xaml.cs` 仍为 0 增/51 删；未执行 restore/build/test/run |
| 2026-07-23 | P4 标题栏静态加固 | 实现完成、阶段未勾选 | 两个窗口显式设置 `AppWindow.TitleBar.ExtendsContentIntoTitleBar` 与 `IconShowOptions.HideIconAndSystemMenu`；新增动态 `RightInset` 列，避免自定义按钮落入系统 caption button 命中区；Roslyn 解析 70 个 `src` C# 文件无语法错误，4 个 WinUI XAML 均为合法 XML；未执行 restore/build/test/run |
| 2026-07-23 | 功能等价静态审计 | 无缺失路径 | Legacy `IControlBrowser` 与 WinUI `IBrowserSession` 的差异仅为已记录的 HotkeyGesture、窗口边界捕获/恢复和 BrowserState 切片替代；ControlWindow 的 72 个 `{Binding}` 路径和 8 个 `{x:Bind}` 路径均在对应 VM/model 中存在；37 个 VM command 中其余 10 个由地址/下载/列表 code-behind 明确调用；未执行 restore/build/test/run |
| 2026-07-23 | 最后一轮静态门槛 | 静态检查通过、阶段仍未勾选 | 全仓排除 `obj/bin` 后 Roslyn 解析 108 个 C# 文件、XML 解析 13 个 XAML；WinUI 绑定 72/8 路径、60 个本地化 key、30 个 `Gb*` 资源引用、41 个事件处理器均无缺失；无 `MemoryUsageTargetLevel`、WPF 合成捕获、`AllDomStorage`、循环动画、透明页面 CSS 或 Profile 删除/移动；计划声明文件存在，`git diff --check` 通过，`MainWindow.xaml.cs` 保持 0 增/51 删；未执行 restore/build/test/run |
| 2026-07-23 | DiskCache 测量边界加固 | 实现完成、阶段未勾选 | `WebViewDataSizeCalculator` 现在只统计 Chromium 磁盘缓存目录；IndexedDB、LocalStorage、Service Worker、密码和登录数据不会被算入可回收阈值；同步更新静态测试样例，未执行 restore/build/test/run |
| 2026-07-23 | DiskCache 用户文案对齐 | 实现完成、阶段未勾选 | 中英文 tooltip、状态和错误回退文案均明确“只清 WebView2 磁盘缓存”，不再声称会清 LocalStorage、IndexedDB 或 Service Worker；实现仍使用 `CoreWebView2BrowsingDataKinds.DiskCache`；未执行 restore/build/test/run |
| 2026-07-23 | DiskCache tooltip 接线 | 实现完成、阶段未勾选 | 控制窗口的清理按钮使用 `LocalizationBinding.ToolTipKey="Settings.ClearBrowsingDataTooltip"`，主文案和详细 tooltip 分别本地化，不再由 ViewModel 承担静态资源文案；未执行 restore/build/test/run |
| 2026-07-23 | DiskCache 目录集合纠偏 | 实现完成、阶段未勾选 | 从 `WebViewDataSizeCalculator` 的统计集合移除并在递归时跳过 `Service Worker`、`IndexedDB`、`Local Storage`、`Session Storage`、`WebStorage`、`File System`、`blob_storage` 与 `databases`；测试样例同时放置嵌套同名 `Cache`，防止站点数据子树被误计；未执行 test |
| 2026-07-23 | 本轮静态门槛复核 | 通过、阶段仍未勾选 | 递归排除 `obj/bin` 后 13 个 XAML 全部可解析；DiskCache 集合与受保护站点数据子树断言通过；`git diff --check` 通过；`MainWindow.xaml.cs` 仍为 `0 增 / 51 删`；未执行 restore/build/test/run |
| 2026-07-23 | 计划构建命令修正 | 完成 | 最终 solution Debug/Release 命令补充 `-p:Platform=x64`，与 `.sln` 的配置和 CI 入口一致；未执行 build |
| 2026-07-23 | 计划入口可见性加固 | 完成 | README 顶部增加迁移计划链接；根目录 `AGENTS.md` 继续规定所有 agent 先读本计划；未执行 build/test |
| 2026-07-23 | P3.8 renderer 分级恢复 | 实现完成、阶段未勾选 | WinUI `BrowserSession` 恢复 Legacy 的分级策略：BrowserProcessExited 才完整重建 WebView；RenderProcessExited 先 reload；10 秒内连续两次 RenderProcessUnresponsive 才 Stop+Reload，轻量恢复失败后再重建；恢复动作统一回到拥有 WebView 的 Dispatcher；未执行 build/run |
| 2026-07-23 | P3.3 导航完成语义对齐 | 实现完成、阶段未勾选 | WinUI 导航事件恢复旧版的 redirect 过滤，并在成功或失败的首屏导航后都调度一次 24 小时缓存检查；未执行 build/run |
| 2026-07-23 | P3.5 下载启动异常收口 | 实现完成、阶段未勾选 | `CoreOnDownloadStarting` 增加与 Legacy 等价的异常收口：解绑失效 operation、将已创建记录标为 Interrupted、刷新控制窗并记录日志；未执行 build/run |
| 2026-07-23 | P3.7 自动缓存时间戳顺序 | 实现完成、阶段未勾选 | 超阈值时不再提前写入 `LastWebView2CacheCheckUtc`；只有低于阈值或 `DiskCache` 清理成功后才更新时间，避免一次清理失败导致 24 小时不重试；未执行 build/run |
| 2026-07-23 | P3.1 Runtime 安装状态 | 实现完成、阶段未勾选 | WinUI `BrowserSession` 在 Evergreen Runtime 缺失时先发布“正在安装 WebView2 Runtime”状态，再进入共享 environment 的安装/创建流程；未执行 build/run |
| 2026-07-23 | P6.2 关闭顺序收口 | 实现完成、阶段未勾选 | `BrowserSession.DisposeAsync` 在进入 shutdown 后立即解绑并释放唯一 keyboard hook owner，再取消后台任务、停止下载 timers、解绑 WebView2 和保存数据，避免清理等待期间仍接收全局按键；未执行 build/run |
| 2026-07-23 | P6.1 单实例启动异常边界 | 实现完成、阶段未勾选 | `App.OnLaunched` 的 Dispatcher、AppInstance 注册、二次启动重定向与 composition root 启动统一纳入启动异常处理；重定向失败不再越过启动错误日志/提示路径；未执行 build/run |
| 2026-07-23 | P6.3 hook 热路径微调 | 实现完成、阶段未勾选 | 前台进程判定改用 `Environment.ProcessId`，避免每次相关按键都创建未释放的当前进程包装对象；未执行 test/run |
| 2026-07-23 | P6.2 owned tool-window 样式提交 | 实现完成、阶段未勾选 | 设置 ControlWindow owner 与 `WS_EX_TOOLWINDOW` 后调用 `SetWindowPos(...SWP_FRAMECHANGED)`，确保任务栏/非客户区立即采用 owned tool-window 语义；未执行 build/run |
| 2026-07-23 | P7.3 初始语言赋值顺序 | 实现完成、阶段未勾选 | `LocalizationBinding` 在 attached property 立即应用后，再投递一次到当前 Dispatcher 队列尾部，避免 XAML 后续设置中文 fallback 的 Content/Header/Text 覆盖英文启动语言；未执行 build/run |
| 2026-07-23 | P7.3/P7.6 主文案与 tooltip 双 key | 实现完成、阶段未勾选 | `LocalizationBinding.ToolTipKey` 允许同一 WinUI 控件分别绑定可见文案和详细 tooltip，并同步 tooltip 到 AutomationName；ControlWindow 的地址栏、下载入口、尺寸应用、四角贴靠、缓存/默认值/下载清理和搜索框已接入；未执行 build/run |
| 2026-07-23 | P4/P7 模式按钮动态提示 | 实现完成、阶段未勾选 | BrowserWindow 模式按钮恢复 Legacy 的目标动作 + 当前快捷键 tooltip；模式、快捷键或语言变化时同步更新 AutomationName，且热键变化不重复执行窗口模式布局；未执行 build/run |
| 2026-07-23 | P3.3 地址同步去重 | 实现完成、阶段未勾选 | `CaptureCurrentAddress` 改为只在地址或持久化值变化时更新，地址栏继续显示 WebView2 的真实 HTTP(S) Source，只有用于恢复的规范化 `LastUrl` 改变才写 settings；读取 Source 的异常统一记录并返回；未执行 build/run |
| 2026-07-23 | P3.3 标题读取异常边界 | 实现完成、阶段未勾选 | BrowserSession 集中读取 DocumentTitle 时捕获失效 COM 状态并回退到当前地址，避免导航/收藏回调把进程切换期异常抛到 UI；未执行 build/run |
| 2026-07-23 | P2.4/P3.3 标题状态契约 | 实现完成、阶段未勾选 | `IBrowserSession` 增加只读 `DocumentTitle`；BrowserWindow 不再直接读取 WebView2 COM 标题，历史/收藏仍由 Session 在空标题时使用地址，窗口标题则回退应用名；未执行 build/run |
| 2026-07-23 | P6.2 hook 排队关闭竞态 | 实现完成、阶段未勾选 | 全局快捷键回调在入队前和 Dispatcher 执行时都检查 `shuttingDown`，避免已经排队的播放/模式动作在关闭清理期间重新操作 WebView 或 AppWindow；未执行 run |
| 2026-07-23 | P3.8 关闭期事件闸门 | 实现完成、阶段未勾选 | DocumentTitle/Source/History 在 shutdown 后忽略；尾部 NewWindow 请求标记 Handled，尾部 DownloadStarting 主动 Cancel，避免事件解绑前再次导航、创建下载或刷新窗口状态；未执行 run |
| 2026-07-23 | P3.8 初始化失败解绑 | 实现完成、阶段未勾选 | 标准 WebView2 初始化任一步失败时先解绑可能已附加的 XAML/Core 事件并清空 Session core，再进入错误状态，避免半初始化对象继续回调；未执行 build/run |
| 2026-07-23 | P5.6 快捷键冲突文案 | 实现完成、阶段未勾选 | 录制模式键与播放键互相冲突时恢复对应的专用提示；只有其它注册冲突才使用通用文案，录制状态保持以便用户继续输入；未执行 build/run |
| 2026-07-23 | P7 双 key 本地化静态门槛 | 静态检查通过、阶段仍未勾选 | Roslyn 解析 108 个 C# 文件无语法错误，13 个 XAML 均为合法 XML；两个窗口引用的 71 个本地化 key 在中英文各 188 个资源中无缺失，11 个 `ToolTipKey` 全部覆盖，Core 已无 `ClearBrowsingDataTooltip` 静态文案属性；`src` 无低内存优先级、WPF 合成捕获、`AllDomStorage` 或透明页面 CSS；`git diff --check` 通过，`MainWindow.xaml.cs` 保持 `0 增 / 51 删`；未执行 restore/build/test/run |
| 2026-07-23 | P4/P5/P6 功能差异补齐 | 实现完成、阶段未勾选 | BrowserWindow 补回浮窗模式下 `Ctrl+Plus/Minus/0` 缩放、首次浮窗长提示与后续短提示；ControlWindow 补回 `Ctrl+L`、模式/语言重复点击后的选中态和恢复后 WebView AutomationName；AppWindow 尺寸/贴角先还原最大化并夹紧工作区，跨 DPI 变化刷新最小尺寸；标题栏感应区显示时不再挡住按钮，模式 toast 不拦截输入；未执行 build/run |
| 2026-07-23 | P4/P5/P6/P7 静态门槛复核 | 静态检查通过、阶段仍未勾选 | Roslyn 解析 108 个 C# 文件无语法错误，13 个 XAML 均为合法 XML，两个窗口 42 个事件处理器全部存在；71 个窗口本地化 key 在 zh-CN/en-US 各 188 个资源中无缺失；`src` 无低内存优先级、WPF 合成捕获、`AllDomStorage` 或透明页面 CSS；`git diff --check` 通过，`MainWindow.xaml.cs` 仍为 `0 增 / 51 删`；未执行 restore/build/test/run |
| 2026-07-23 | P8/P9 坐标与 CI 门槛补齐 | 实现完成、阶段未勾选 | `MigrationPolicyTests` 增加 schema v2 无效主窗/控制窗坐标与尺寸回退用例；GitHub Actions 的 Debug/Release build 改用 `GenshinBrowser.sln -p:Platform=x64`，覆盖 Core、App、Tests 与迁移期 Legacy 链接；未执行 build/test |
| 2026-07-23 | P8/P9 静态门槛复核 | 静态检查通过、阶段仍未勾选 | Roslyn 解析 108 个 C# 文件无语法错误，13 个 XAML 与 4 个 csproj 均为合法 XML，solution 的 4 个项目路径全部存在；2 个发布 PowerShell 脚本无语法错误，CI 包含 2 个 solution build 入口；`src` 无低内存优先级、WPF 合成捕获、`AllDomStorage` 或透明页面 CSS，`git diff --check` 通过；未执行 restore/build/test/run |
| 2026-07-23 | P3.8 关闭期恢复闸门 | 实现完成、阶段未勾选 | `RecoverWebViewAsync` 等待历史捕获后重新检查 `shuttingDown`，`InitializeWebViewAsync` 入口同样拒绝 shutdown；关闭与 browser-process recovery 交错时不再替换或创建 WebView2；未执行 build/run |
| 2026-07-23 | P4.5 精确 100% opacity 边界 | 实现完成、阶段未勾选 | Session 与 ViewModel 不再用 `0.001` 容差吞掉 `0.999 -> 1.0`；100% 会实际调用透明服务并移除 layered style，百分比输入显示最多两位小数，避免把 99.9% 显示成 100%；未执行 build/run |
| 2026-07-23 | P1/P2/P3 静态门槛复核 | 静态检查通过、阶段仍未勾选 | Roslyn 解析 108 个 C# 文件无语法错误，13 个 XAML 均为合法 XML，42 个窗口事件引用保持；91 个代码本地化 key 在中英文资源中无缺失；产品/测试源无尾随空白，`src` 无低内存优先级、WPF 合成捕获、`AllDomStorage` 或透明页面 CSS，`git diff --check` 通过；未执行 restore/build/test/run |
| 2026-07-23 | P4.5 opacity 实际状态收口 | 实现完成、阶段未勾选 | Session 的等值短路同时核对透明服务记录与 HWND `WS_EX_LAYERED` 实际状态；即使前一次 Win32 调用中途失败或样式被外部改变，再设置 100% 也会执行移除 layered style，而不会因 settings 已是 `1.0` 跳过；未执行 build/run |
| 2026-07-23 | P3.8 WebView 初始化关闭闸门 | 实现完成、阶段未勾选 | `IWebViewHost` 的 Loaded 等待接入 Session 生命周期取消令牌；关闭发生在首个 WebView Loaded 前时不再留下永久等待，重建 WebView 的 Loaded handler 也只完成其创建时对应的 TCS；脚本注册异步点后再次检查 shutdown，关闭期间不重新挂事件或导航；未执行 build/run |
| 2026-07-23 | P3.8 异步回调关闭闸门 | 实现完成、阶段未勾选 | 导航历史、播放脚本、收藏/历史写盘、DiskCache 清理、下载重试和自动缓存 Dispatcher 回调在 `await` 返回后检查 shutdown；`SetStatus/Notify` 入口统一拒绝关闭期事件，Session 不再发布后置状态、写回设置或发起新导航；未执行 build/run |
| 2026-07-23 | P4.3 浮窗拖动区域 | 实现完成、阶段未勾选 | 浮窗标题栏隐藏时把 WinUI 原生拖动区域切换到顶部感应条，标题栏显示后切回独立标题拖动区；WebView 主体不被设为拖动区，避免吞掉网页输入；`git diff --check` 通过，未执行 build/run |
| 2026-07-23 | P7.3 本地化首屏时序 | 实现完成、阶段未勾选 | `LocalizationBinding` 对 FrameworkElement 增加 `Loaded` 重放，同时保留 DispatcherQueue 延迟应用；XAML 属性解析覆盖 fallback 后，首屏和重新挂载控件仍会按当前语言刷新；未执行 build/run |
| 2026-07-23 | P3.5 下载字节类型核对 | 静态语义确认、阶段未勾选 | 反射 `Microsoft.Web.WebView2.Core 1.0.4078.44` 确认 .NET 投影的 `BytesReceived` 为 `long`、`TotalBytesToReceive` 为 `ulong?`；接收量使用显式 `0L` 夹紧，总量继续饱和转换；未执行 build/run |
| 2026-07-23 | P7.1 状态栏 XAML 类型修正 | 实现完成、阶段未勾选 | `GbStatusBarStyle` 从不支持 `Padding` 的 `Grid` 改为 `Border`，ControlWindow 使用 Border 包裹内层 Grid，保留背景、内边距、状态文本与进度环布局；XML 静态解析通过，未执行 build/run |
| 2026-07-24 | UI 还原收口（WPF 视觉等价） | 实现完成、阶段未勾选 | 复核 WinUI 两窗 XAML 已对齐旧 WPF：BrowserWindow 标题栏仅保留 标题+模式切换+最小/最大/关闭（无多余导航/控制面板按钮），与 `MainWindow.xaml` 一致；ControlWindow 设置面板（窗口/显示/外观/快捷键分组）、地址栏（后退/前进 + 地址框 + 进度条 + Primary「打开」+ 刷新 + 收藏）、Tab RadioButton（收藏夹/浏览记录 含图标）、搜索框与列表均对齐旧 `ControlWindow.xaml`，无状态栏。修复 ControlWindow `<Window.Resources>` 编译错误（WinUI `Window` 无 `Resources`，资源移入 `Grid.Resources`）。窗口尺寸对齐 WPF：BrowserWindow 658×370、ControlWindow 560×640（最小 400×440）。**实际构建验证**：`dotnet build GenshinBrowser.sln -c Debug -p:Platform=x64` 0 警告 0 错误（Core/Tests/Legacy/App 四项目全过）；`-c Release` 同样 0 警告 0 错误；`dotnet test tests/GenshinBrowser.Tests` 29 通过 / 0 失败 / 0 跳过。注：NuGet 离线缓存已含 `Microsoft.WindowsAppSDK 2.2.0` 与 `Microsoft.Web.WebView2 1.0.4078.44`，D14「未缓存」前提已不再成立，build 可正常执行。阶段总项仍以人工运行验收为准 |
| 2026-07-24 | P7.2 主题/语言选择器语义修复 | 实现完成、阶段未勾选 | ControlWindow 主题（暗/亮/系统）与语言（中/英）原误用 `ToggleButton` + `IsChecked=OneWay` 绑定只读 `IsTheme*/IsLanguage*`：ToggleButton 自带开关语义，点已选中的会视觉熄灭，而 `SetTheme/SetLanguage` 命令是「设置」非「切换」，属性值未变导致 OneWay 不回推，按钮卡在熄灭态（用户反馈「按一下激活、再按一下熄灭」）。改为 `RadioButton`（`GroupName=Theme` / `GroupName=Language`），同组互斥且点已选中的不熄灭，对应旧 WPF 普通 Button+DataTrigger 语义。新增 `GbSegmentRadioButtonStyle`（透明底/二级字 + Checked 态强调软底+强调字，分段药丸外观）。`Settings/Downloads` 按钮仍用 ToggleButton（其命令确为 toggle，语义正确，未改）。验证链路：`VM.SetTheme → BrowserSession.ThemeMode setter → ThemeChanged → 两窗 ApplyTheme 设置 RequestedTheme`，选亮色确实生效；Light/Dark 刷子在 `Styles.xaml` ThemeDictionaries 齐全且 ControlWindow 无硬编码深色。`dotnet build -c Debug -p:Platform=x64` 0 警告 0 错误；未执行 run/人工验收 |
| 2026-07-24 | P7.2 主题运行时切换不生效修复（根因） | 实现完成、阶段未勾选 | 用户反馈切亮色后控制窗几乎全不变色（标题栏/按钮/地址栏/搜索框/文字等都不变），仅手动代码赋值的页面背景变了。前两轮误判为「直接属性 ThemeResource 不重算」并尝试「子 Border」「代码按 ActualTheme 赋值 Background」「Setter 化」均未根治。最终对照参考项目 Snap.Hutao 的 `Color.xaml`（ThemeDictionaries 在第 2 行、顶层零刷子）定位根因：`Styles.xaml` 在 `ThemeDictionaries` 之外又用**相同 key** 定义了一组顶层「Dark fallback」刷子——WinUI 下顶层资源与 ThemeDictionaries 同 key 会冲突，使 `{ThemeResource}` 退化为静态解析（始终取顶层暗色值，运行时改 `RequestedTheme` 不再重算），故全窗不变色。修法：删除那 17 个顶层重复 key 的 fallback 刷子，只保留 ThemeDictionaries（与 Snap.Hutao 一致），并加注释禁止再在顶层定义同 key 刷子。同时回退前两轮的错误补丁（ControlWindow.xaml.cs 的 `RefreshPageBackground`/`ActualThemeChanged` 订阅、`GbPageBackgroundLight/DarkBrush` 独立 key、3 个 Border 样式），XAML 还原为直接属性 `{ThemeResource}`（删顶层 fallback 后直接属性与 Setter 均会随 `RequestedTheme` 正确重算）。已核实无代码/XAML 用 `Application.Resources["Gb...Brush"]` 或 `{StaticResource Gb...Brush}` 依赖被删的顶层 key。`dotnet build -c Debug -p:Platform=x64` 0 警告 0 错误；未执行 run/人工验收 |
| 2026-07-24 | P4 透明渲染路径 DComp 方案尝试（已回退） | 尝试失败、已回退 | 用户反馈视频播放时 DWM CPU 极高、卡顿。曾尝试把 `WindowTransparencyService` 从 `WS_EX_LAYERED`+`SetLayeredWindowAttributes(LWA_ALPHA)` 换成 `ElementCompositionPreview.GetElementVisual(root).Opacity`（DComp visual opacity，期望把 alpha 混色从 DWM CPU 挪到 GPU）。**实测失败**：用户反馈「DWM CPU 没降 + 半透明不可见」。根因（经查证 MS Materials 文档与 DWM 合成模型）：① WinUI3 `Window` 在无 `SystemBackdrop` 时背景是**不透明实色**，`Visual.Opacity` 只是把内容混在窗口自身的不透明背景上，DWM 不会透出桌面 → 半透明不可见；② `WS_EX_LAYERED LWA_ALPHA` 之所以能透，是因为它作用在 DWM 的**顶层重定向表面**（子 HWND/WebView2 合成之后再整窗 alpha），而 `Visual.Opacity` 作用在 XAML 合成树内，达不到顶层整窗 alpha；③ alpha<1 时无论 layered 还是 DComp 都逐帧合成，DWM CPU 本就不会因换路径而降——真正能降 CPU 的只有 MPO，而 MPO 与 alpha<1 互斥。结论：**WinUI3 Window + 嵌入 WebView2 架构下，「半透明视频」与「低 DWM CPU」不可兼得**；要兼得只能走 winpane/方案三（自建 NRB 窗口 + 自有 D3D11 预乘 alpha swap chain + OSR 把 WebView2 渲成纹理自己合成），属大型架构改造。已 `git restore` 回退全部 7 个文件到 `WS_EX_LAYERED` 工作版本，`dotnet build` 0 警告 0 错误，半透明恢复正常。后续方向待用户定：A 保留 layered 透明+降负载（缩窗/降视频帧率/关弹幕）；B 改不透明换流畅（Snap.Hutao 式）；C 上 OSR+DComp 大改 |

---

## 1. 已锁定的架构决策

下列内容已经由用户方向和现有分析确定。若实现过程中必须改变，先更新本节的决策记录与理由，再改代码。

| ID | 决策 |
|---|---|
| D1 | 最终产品是**单进程 WinUI 3 应用**，包含 `BrowserWindow` 和 `ControlWindow` 两个顶层窗口。 |
| D2 | 最终迁移整个 UI，不保留永久 WPF 控制面板、XAML Islands 或 WPF/WinUI 双 Dispatcher 架构。 |
| D3 | 不制作一次性性能 demo；直接建设最终 WinUI 应用，但每个阶段仍须编译、测试和回归。 |
| D4 | 浏览器使用 WinUI 3 标准 `<WebView2>`，不使用 WPF `WebView2CompositionControl`。 |
| D5 | 透明度采用**整个 BrowserWindow HWND 的统一 alpha**，而不是 WebView 元素 `Opacity`。 |
| D6 | 保留本项目当前 10%-100% 持续透明度语义；只借鉴胡桃的窗口级 alpha 技术，不复制其激活时恢复不透明、失焦时淡化的策略。 |
| D7 | 100% 不透明时移除或不启用 `WS_EX_LAYERED` 透明路径，避免不透明状态仍承担透明窗口开销。 |
| D8 | 控制窗口保持正常不透明；只有浏览器窗口使用用户配置的整窗透明度。 |
| D9 | 最终 executable、数据目录和 WebView profile 身份保持 `GenshinBrowser`，用户登录态与现有 JSON 数据必须保留。 |
| D10 | 迁移优先保证功能等价和性能路径正确，不在同一轮重设计产品功能或视觉信息架构。 |
| D11 | 暂时保留 .NET 8 和当前最低系统承诺；若 Windows App SDK 的稳定版本迫使升级，必须在进度日志记录兼容性证据和最终选择。 |
| D12 | 胡桃代码只作 MIT 许可下的架构参考；本项目自行实现需要的 Win32 封装，不依赖 `Snap.Hutao.Remastered.Native.dll`。 |
| D13 | 按用户最新要求先连续实施迁移代码、暂不先跑测试；未验证步骤保持未勾选，但不阻塞后续代码阶段开工，所有 build/test/run 验证仍须在完成前补齐。 |
| D14 | 当前环境未缓存 Windows App SDK 2.2.0；在用户仍要求避免网络/权限中断期间，不执行必然触发包下载的 restore/build。 |
| D15 | WebView 默认底色使用随主题变化的纯不透明色；整窗淡化只由 HWND alpha 完成。这样 100% 时既移除 layered style，也不保留透明 WebView 宿主底面。 |
| D16 | `ControlWindow` 是 `BrowserWindow` 的 Win32 owned tool window；关闭浏览器时先拦截关闭、完成异步清理并关闭控制窗，再销毁浏览器 HWND。 |
| D17 | 自动与手动“清理浏览缓存”只清理 WebView2 `DiskCache`；不清理 Cookies、LocalStorage、IndexedDB、密码或整个 Profile，避免升级或例行维护破坏登录态。 |

### 目标架构

```text
GenshinBrowser.App  (WinUI 3, unpackaged/self-contained, one process)
├── App / ApplicationHost
├── BrowserWindow
│   └── standard Microsoft.UI.Xaml.Controls.WebView2
├── ControlWindow
├── BrowserSession
│   ├── navigation / history / favorites
│   ├── downloads / cache / recovery
│   ├── zoom / status / browser events
│   └── shared CoreWebView2Environment + existing profile
├── Window services
│   ├── WindowTransparencyService
│   ├── WindowPlacementService
│   ├── WindowModeService
│   └── WindowOwnerService (ControlWindow owned/tool-window relationship)
└── shared services
    ├── SettingsService
    ├── KeyboardHookService
    ├── ThemeService / LocalizationService
    └── logging / JSON / URL helpers

GenshinBrowser.Core
├── UI-independent models and settings contracts
├── browser state contracts
├── hotkey value types
└── testable services and helpers
```

### 迁移期间允许的临时结构

- 允许旧根项目 `GenshinBrowser.csproj` 暂时存在，以便在新 WinUI 应用达到功能等价前保持可构建基线。
- 允许新 WinUI 项目和旧 WPF 项目在仓库内短期并存。
- 不允许发布包含两个 UI 应用的正式产物。
- P10 必须删除旧 WPF 项目和所有 WPF 专属代码。

---

## 2. 胡桃参考地图与借鉴边界

### 必读参考文件

参考根目录：`E:\testcode1\Snap.Hutao.Remastered-main`

| 参考点 | 文件 | 借鉴内容 |
|---|---|---|
| 标准 WinUI WebView2 浮窗 | `src\Snap.Hutao.Remastered\Snap.Hutao.Remastered\UI\Xaml\View\Window\WebView2\CompactWebView2Window.xaml` | 使用标准 `<WebView2>`、透明默认背景、WinUI 窗口布局 |
| WebView 初始化与窗口 alpha | `src\Snap.Hutao.Remastered\Snap.Hutao.Remastered\UI\Xaml\View\Window\WebView2\CompactWebView2Window.xaml.cs` | `AppWindow`、`OverlappedPresenter`、WebView2 环境、整窗透明度调用顺序、窗口隐藏复用 |
| Win32 窗口封装 | `src\Snap.Hutao.Remastered\Snap.Hutao.Remastered\UI\Windowing\WindowUtilities.cs` | layered style 与窗口 alpha 的封装边界 |
| Window 扩展 | `src\Snap.Hutao.Remastered\Snap.Hutao.Remastered\UI\Windowing\WindowExtension.cs` | 从 WinUI `Window` 获取 HWND 并调用窗口服务 |
| 多窗口基础设施 | `src\Snap.Hutao.Remastered\Snap.Hutao.Remastered\UI\Windowing\XamlWindowController.cs` | `AppWindow`、自定义标题栏、窗口关闭与 scope 生命周期的思路 |
| WebView2 弹窗 | `src\Snap.Hutao.Remastered\Snap.Hutao.Remastered\UI\Xaml\View\Window\WebView2\WebView2Window.xaml.cs` | 标准 WinUI WebView2 的加载、事件解绑与关闭锁 |
| WinUI 项目配置 | `src\Snap.Hutao.Remastered\Snap.Hutao.Remastered\Snap.Hutao.Remastered.csproj` | `UseWinUI`、Windows App SDK、自包含配置；只参考结构，不盲目复制版本 |

### 明确不复制

- 不复制胡桃的“激活时完全不透明、失焦时应用透明度”逻辑。
- 不引入胡桃的 DI、SourceGeneration、Sentry、Native NuGet 包或低级键盘输入实现。
- 不复制其全局 Mica/Acrylic 配置作为本轮必需功能。
- 不盲目采用胡桃的 `.NET 10 + Windows App SDK 2.2.0 + preview` 工具链。
- 不把胡桃的窗口类直接复制进项目；只移植适合本项目的模式。

---

## 3. 完成定义与勾选规则

每个步骤只有同时满足以下条件才可从 `[ ]` 改为 `[x]`：

1. 对应代码或文档已经落地，且没有遗留同一步骤内的 TODO。
2. 步骤声明的构建、测试、检索或人工验证已执行。
3. 结果已经写入 **Progress log**。
4. 变更没有覆盖无关用户修改。
5. 若步骤改变架构或兼容性，已同步更新“架构决策”或“决策记录”。

阶段 checkbox 只有在阶段内所有子步骤都完成后才能勾选。

### 并行规则

- 默认按 P0 → P10 顺序执行。
- 2026-07-23 用户明确要求“先写代码别测试”：允许按依赖顺序继续实现后续代码，因暂缓 build/test 而未勾选的前置步骤不阻塞编码；不得借此提前勾选步骤。
- 同阶段内只有标为“可并行”的步骤可由不同 agent 同时领取。
- 涉及同一 XAML、项目文件、设置 schema 或 `BrowserSession` 契约的步骤不得并行。
- agent 开始前必须检查 `git status` 和 **Current work**，避免覆盖他人工作。

---

## 4. P0 — 基线、边界与兼容性清单

**目标：** 在迁移前固定功能基线、数据兼容要求和当前工作树状态，避免重构完成后才发现功能丢失。

- [x] P0.1 记录当前工作树基线
  - 记录已有 `MainWindow.xaml.cs` 51 行删除，不把它归入 WinUI 迁移提交。
  - 保存 `git status --short`、`git diff --stat` 到 Progress log 摘要。
  - 验证：无额外未知修改。

- [ ] P0.2 执行旧 WPF 基线构建与测试
  - Debug/Release 至少各构建一次；运行现有测试。
  - 记录当前通过数量和任何既有警告。
  - 验证：`dotnet test tests/GenshinBrowser.Tests/GenshinBrowser.Tests.csproj -c Release`。

- [x] P0.3 建立功能等价矩阵
  - 逐项盘点 `Windows/IControlBrowser.cs`、`MainWindow`、`ControlWindow` 的公开能力。
  - 覆盖导航、历史、收藏、下载、缓存清理、缩放、透明度、模式、快捷键、主题、语言、窗口位置、单实例、异常恢复。
  - 将矩阵追加到本计划“最终人工验收”或独立受本计划链接的文档。

- [x] P0.4 固定数据和身份兼容基线
  - 记录 `%LocalAppData%\GenshinBrowser` 下 settings/history/favorites/downloads/logs/profile 的实际名称。
  - 记录当前 `settings.json` schema、枚举序列化格式和窗口坐标单位。
  - 记录 WebView profile：`%LocalAppData%\GenshinBrowser\WebViewProfile`。

- [ ] P0.5 确认稳定工具链
  - 保持 `net8.0-windows10.0.19041.0` 为首选。
  - 选择支持该 TFM、unpackaged、自包含发布和目标最低 Windows 版本的稳定 Windows App SDK。
  - WebView2 SDK 首选继续固定 `1.0.4078.44`，避免同时改变浏览器 SDK 变量。
  - 若必须提高 TFM 或最低系统版本，在“决策记录”写明依据。

**P0 验收：** 基线可复现、数据兼容要求明确、工具链版本已记录。

### 功能等价矩阵

| 旧 WPF 功能 | WinUI 3 目标落点 | 当前代码状态 | 最终验证阶段 |
|---|---|---|---|
| 标准浏览、前进/后退、刷新、地址输入/搜索 | `BrowserSession` + WinUI `WebView2` + `ControlWindow` | 已迁代码 | P3.2/P3.3/P5.2 |
| 页面地址、标题、加载态、新窗口重定向 | `BrowserSession.WebView.cs` | 已迁代码 | P3.3 |
| 收藏添加/取消、搜索、打开、复制、删除 | Core `FavoritesService` + WinUI 控制窗 | 已迁代码 | P3.4/P5.3 |
| 历史记录、SPA 地址记录、搜索、删除 | Core `HistoryService` + WinUI 控制窗 | 已迁代码 | P3.4/P5.3 |
| 下载进度、100ms 防抖、取消、重试、打开、清理、落盘 | `BrowserSession.Downloads.cs` + Core `DownloadsService` | 已迁代码 | P3.5/P5.4 |
| K/自定义键播放暂停 | `BrowserSession.ToggleVideoPlaybackAsync` + Core keyboard hook | 已迁代码 | P3.6/P6.3 |
| 页面 zoom 与持久化 | `BrowserSession.ZoomFactor` | 已迁代码 | P3.7/P5.5 |
| 24 小时缓存测量与阈值清理，保留 Cookie/密码 | `BrowserSession.Cache.cs` + `WebViewDataSizeCalculator` | 已迁代码 | P3.7/P8.3 |
| WebView browser/renderer 故障恢复 | `IWebViewHost.ReplaceWebView` + `BrowserSession.RecoverWebViewAsync` | 已迁代码 | P3.8 |
| 浏览/浮窗模式、置顶、resize 限制、accelerator keys | `AppWindowModeService` + `BrowserWindow` | 已迁代码 | P4.2/P4.3 |
| 浮窗标题栏自动隐藏、顶部唤出、最大化/最小化 | WinUI `BrowserWindow` | 已迁代码 | P4.1-P4.3 |
| 10%-100% 整窗透明；100% 移除 layered style | `WindowTransparencyService` | 已迁代码 | P4.4-P4.6 |
| 窗口尺寸、位置、四角贴靠、多显示器/DPI | `AppWindowPlacementService` | 已迁代码 | P4.7/P8.2 |
| 控制窗设置/下载面板互斥展开、状态与 Toast | Core `ControlWindowViewModel` + WinUI `InfoBar` | 已迁代码 | P5.1-P5.7 |
| 快捷键录制、修饰键、冲突检查、录制时暂停 hook | WinUI `KeyRoutedEventArgs` + `HotkeyGesture` | 已迁代码 | P5.6/P6.3 |
| 单实例与二次启动唤回 | Windows App SDK `AppInstance` | 已迁代码 | P6.1 |
| 控制窗关闭时隐藏、浏览窗关闭时整体退出 | `ApplicationHost` + AppWindow closing | 已迁代码 | P6.2 |
| Dark/Light/System、zh-CN/en-US | `RequestedTheme` + 复用旧词条的 `LocalizationBinding` | 已迁代码 | P7.1-P7.3 |
| settings/history/favorites/downloads/logs/Profile 兼容 | Core 数据服务 + `ApplicationDataPaths` | 已迁代码 | P8.1-P8.5 |
| 自包含发布、WebView2 bootstrapper、CI/zip/SHA-256 | WinUI App csproj + `release` + GitHub Actions | 已切换代码入口 | P9.3-P9.6 |

### 数据与身份兼容基线

| 数据 | 固定路径/格式 |
|---|---|
| 应用根目录 | `%LocalAppData%\GenshinBrowser` |
| 设置 | `settings.json`，WinUI schema v2；旧 WPF `Key` 数值在读取时转为 Win32 VK |
| 历史 | `history.json`，最多 200 条 |
| 收藏 | `favorites.json`，最多 100 条 |
| 下载记录 | `downloads.json`，最多 50 条；旧进行中项升级后标为 Interrupted |
| 日志 | `logs\yyyy-MM-dd.log` |
| WebView2 profile | `%LocalAppData%\GenshinBrowser\WebViewProfile`，不得重建到新目录 |
| 可执行文件身份 | `GenshinBrowser.exe` / AssemblyName `GenshinBrowser` |

---

## 5. P1 — 新 WinUI 3 解决方案与应用骨架

**目标：** 建立最终生产项目结构，不制作一次性 demo；旧 WPF 项目暂时保留用于迁移对照。

- [x] P1.1 建立解决方案和项目结构
  - 新增 `GenshinBrowser.sln`。
  - 新增 `src/GenshinBrowser.Core/GenshinBrowser.Core.csproj`。
  - 新增 `src/GenshinBrowser.App/GenshinBrowser.App.csproj`，最终 `AssemblyName` 为 `GenshinBrowser`。
  - 将测试项目接入 solution；旧 WPF 项目临时标记为 Legacy。

- [x] P1.2 配置 WinUI 3 unpackaged/self-contained 应用
  - `UseWinUI=true`、`UseWPF=false`、x64、现有 manifest 与 icon。
  - 维持 zip 解压即用的发布目标。
  - 配置 Windows App SDK self-contained；不得依赖用户额外安装 Windows App Runtime。
  - 不启用 preview 包，除非 P0.5 已记录不可替代原因。

- [x] P1.3 建立 WinUI `App` 与 composition root
  - 初始化日志、设置、服务、全局异常处理。
  - 建立共享 service lifetime；窗口不得各自重复创建 settings/history/favorites/download services。
  - 暂不迁移完整功能，但骨架必须是最终应用代码。

- [x] P1.4 建立两个生产窗口壳
  - 新增 WinUI `BrowserWindow` 和 `ControlWindow`。
  - 使用 `AppWindow` / `OverlappedPresenter` 管理窗口，而不是 WPF `WindowChrome`。
  - 两个窗口能由一个 WinUI `App` 创建、显示和安全关闭。

- [x] P1.5 建立迁移期构建入口
  - solution 能同时构建 Core、WinUI App、tests；Legacy WPF 可独立构建。
  - CI 在切换前至少保证新项目不会长期红编译。

**P1 验收：** 新 WinUI 应用与两个空壳窗口可 Debug/Release 编译和启动，旧产品代码尚未删除。

---

## 6. P2 — 平台无关核心与状态契约

**目标：** 从 3000+ 行 WPF `MainWindow` 中抽出业务和状态，避免把 WPF code-behind 原样翻译成 WinUI code-behind。

- [x] P2.1 迁移 UI 无关模型和工具到 Core（可并行）
  - `FavoriteEntry`、`HistoryEntry`、`DownloadItem` 的纯数据部分。
  - URL、文本、时间、JSON、下载 URI 比较、缓存大小计算等工具。
  - 清除 `System.Windows` 依赖。

- [x] P2.2 迁移数据服务到 Core（可并行）
  - Settings、History、Favorites、Downloads、FileLogger、JsonFileWriter。
  - 保持现有文件路径和原子写语义。
  - UI 通知由契约或事件提供，不引用 WinUI `Application`。

- [x] P2.3 建立平台无关快捷键值类型
  - 用 Win32 virtual-key code + 自有 modifier flags 替换 WPF `Key` / `ModifierKeys` 持久化模型。
  - 实现旧 settings JSON 的向后兼容转换。
  - 更新 formatter、冲突检测和测试。

- [x] P2.4 建立 `BrowserSession` 契约
  - 替代当前 WPF 绑定的 `IControlBrowser`。
  - 包含命令、只读状态、切片事件、下载集合、历史/收藏访问和设置更新。
  - 契约不得暴露 WPF/WinUI Window、控件或 Dispatcher 类型。

- [x] P2.5 建立窗口服务契约
  - `IWindowModeService`、`IWindowPlacementService`、`IWindowTransparencyService`。
  - 核心层保存逻辑 DIP；WinUI 层负责与 `AppWindow` 物理像素和 rasterization scale 转换。

- [x] P2.6 移除 ViewModel 的 WPF-only 依赖
  - `DispatcherTimer` 改为可注入调度/WinUI `DispatcherQueueTimer` 的 UI 层实现。
  - `ICommand` 可保留，但不得依赖 WPF `KeyEventArgs`、Clipboard、Window 或 MessageBox。

**P2 验收：** Core 和测试项目不引用 `PresentationFramework`、`UseWPF`、WPF `Key` 或 WPF Window。

---

## 7. P3 — WebView2 会话与浏览器能力迁移

**目标：** 在标准 WinUI WebView2 上恢复现有浏览能力，保持 profile 和业务行为。

- [x] P3.1 建立共享 `CoreWebView2Environment`
  - 使用现有 `WebViewProfile` 目录。
  - 保持 Evergreen Runtime 检测与自动安装能力。
  - 同一应用生命周期内避免重复 environment/profile 初始化。

- [x] P3.2 使用标准 WinUI `<WebView2>` 初始化
  - 只使用 `Microsoft.UI.Xaml.Controls.WebView2`。
  - 禁止 `Microsoft.Web.WebView2.Wpf` 和 `WebView2CompositionControl`。
  - 配置状态栏、菜单、DevTools、浏览器快捷键与 autoplay 行为。

- [x] P3.3 迁移导航和状态事件
  - NavigationStarting/Completed、Source、Title、History、NewWindowRequested。
  - 保持 `BrowserStateChangeKind` 的切片通知意图，避免控制窗口全量刷新。

- [x] P3.4 迁移历史与收藏联动
  - 首屏、重定向、导航失败和重复历史行为与旧版一致。
  - 收藏状态变化及时同步 ControlWindow。

- [x] P3.5 迁移下载生命周期
  - DownloadStarting、进度防抖、取消、重试、打开文件/目录、清除已结束。
  - 保持下载记录路径和状态显示。

- [x] P3.6 迁移页面控制脚本
  - 播放/暂停、快进/后退（若当前提供）、cursor 修复。
  - 不再为整窗透明强制注入 `html,body{background:transparent}`；只有独立、明确的页面透明需求才能恢复该 CSS。

- [x] P3.7 迁移 zoom、缓存清理和浏览数据策略
  - 保留 zoom 持久化。
  - 保留 24 小时缓存检查与阈值逻辑，但大小扫描只统计真正的磁盘缓存目录。
  - 缓存清理只使用 `DiskCache`；保留 Cookie、LocalStorage、IndexedDB、登录态、自动填充和密码策略。
  - 不恢复已删除的 `MemoryUsageTargetLevel.Low` 逻辑。

- [x] P3.8 迁移 WebView2 故障恢复与关闭清理
  - ProcessFailed、renderer unresponsive、重建、事件解绑、取消令牌和 Dispose。
  - 窗口关闭期间不得出现二次初始化或悬挂浏览器进程。

**P3 验收：** 在 WinUI BrowserWindow 中完成现有浏览、下载、历史、收藏、zoom、缓存和恢复能力；使用旧 profile 后登录态仍在。

---

## 8. P4 — WinUI BrowserWindow、窗口模式与整窗半透明

**目标：** 完成迁移的核心性能路径，并保留浏览/浮窗两种模式。

- [x] P4.1 实现 BrowserWindow 基础布局
  - 移植标题栏、浏览区域、模式提示、加载/状态视觉层。
  - 使用 WinUI 自定义 title bar 与 `AppWindow.TitleBar`。
  - 不使用 WPF `AllowsTransparency` 或 `WindowChrome`。

- [x] P4.2 实现浏览模式
  - 非置顶、可调整大小、标题栏常驻、浏览器 accelerator keys 开启。
  - 最大化/还原不覆盖任务栏，DPI 与多显示器正确。

- [x] P4.3 实现浮窗模式
  - `OverlappedPresenter.IsAlwaysOnTop=true`、按现有语义限制 resize。
  - 标题栏自动隐藏/顶部感应、拖动、锁定与角落定位行为保持。
  - 浮窗模式 accelerator keys 与全局快捷键冲突策略保持。

- [x] P4.4 实现本地 Win32 窗口透明服务
  - 参考胡桃 `WindowUtilities` 的职责边界，自行 P/Invoke `Get/SetWindowLongPtr` 和窗口 alpha API。
  - 获取 WinUI HWND，封装 add/remove layered style 和 set alpha。
  - 不依赖胡桃 Native DLL。

- [x] P4.5 实现持续整窗透明度
  - 10%-99%：对整个 BrowserWindow HWND 应用统一 alpha。
  - 100%：恢复 alpha 255，并移除 layered extended style。
  - 标题栏、WebView 和窗口内提示一起淡化；ControlWindow 不淡化。
  - 不订阅激活状态来自动改变透明度。

- [x] P4.6 设置 WebView 背景策略
  - 默认底色使用随主题变化的纯不透明色，避免 100% 状态保留透明 WebView 底面；导航白闪由匹配主题的底色消除。
  - 页面自身保持正常 CSS；整窗淡化由 HWND alpha 完成。
  - 确认 100% 状态没有透明黑底、白闪或错误背景。

- [x] P4.7 迁移窗口尺寸、位置和控制窗跟随
  - 保持用户配置的 BrowserWindow width/height 和四角定位。
  - 正确处理 AppWindow 物理像素、WinUI DIP、显示器缩放和工作区。
  - 标题栏显隐不得累计漂移尺寸。

- [ ] P4.8 完成 BrowserWindow 人工验收
  - 测试 10%、50%、100% 透明度。
  - 测试浏览/浮窗、置顶、拖动、resize、最大化、标题栏显隐、多个 DPI 显示器。
  - 测试视频播放期间没有 WPF GraphicsCapture/D3DImage 路径。

**P4 验收：** WinUI 浏览器窗口功能完整；半透明采用整窗 HWND alpha；100% 不走 layered 透明路径。

---

## 9. P5 — WinUI ControlWindow 与全部控制功能

**目标：** 把 WPF 控制面板完整迁移到 WinUI 3，不以重设计为名删功能。

- [x] P5.1 移植 ControlWindow 框架和标题栏
  - 尺寸、最小尺寸、打开/关闭行为、相对 BrowserWindow 显示位置。
  - 使用 WinUI `Window` / `AppWindow`。

- [x] P5.2 移植导航控制区
  - 地址栏、前进、后退、刷新、加载态、当前 URL 同步。
  - 地址输入焦点安全更新行为保持。

- [x] P5.3 移植收藏和历史
  - 搜索、双击/Enter 打开、Delete 删除、右键菜单、复制 URL、空态。
  - 保持最大条数和排序。

- [x] P5.4 移植下载面板
  - 展开/收起、角标、进度、取消、重试、打开、清除已完成。
  - WinUI 集合绑定期间不产生跨线程异常。

- [x] P5.5 移植浮窗设置
  - 整窗透明度 10%-100%。
  - zoom、浏览器尺寸、四角定位、默认值恢复。
  - 修改后即时同步 BrowserWindow 和 settings。

- [x] P5.6 移植快捷键录制 UI
  - 使用 WinUI `KeyRoutedEventArgs`/Win32 VK 映射。
  - 正确处理 Ctrl/Alt/Shift/Win、System key、Esc 取消和冲突提示。
  - 录制期间暂停内置热键。

- [x] P5.7 连接 `BrowserSession`
  - ViewModel 不再依赖旧 WPF `IControlBrowser`。
  - 切片状态事件只刷新需要的区域。
  - BrowserWindow/ControlWindow 不互相持有具体 UI 类型作为业务接口。

- [ ] P5.8 完成 ControlWindow 人工验收
  - 所有旧入口可达，键盘、鼠标和右键操作一致。
  - 设置、下载、收藏、历史的常用路径无功能缺失。

**P5 验收：** 控制面板达到旧 WPF 功能等价，并与 WinUI BrowserWindow 同进程共享状态。

---

## 10. P6 — 应用生命周期、快捷键和多窗口协同

- [x] P6.1 迁移单实例与二次启动激活
  - 优先使用 Windows App SDK AppLifecycle；若保留 mutex + Win32 激活，需记录理由。
  - 二次启动应激活正确窗口，而不是只寻找错误的 MainWindowHandle。

- [x] P6.2 定义窗口所有权和关闭语义
  - 浏览器关闭、控制窗关闭、隐藏、应用退出之间行为明确。
  - 关闭过程中先停止 hooks/timers，再解绑 WebView2，最后释放窗口和服务。

- [x] P6.3 迁移全局快捷键服务
  - 保持 F8 模式切换、K 播放和自定义组合键。
  - 保持游戏前台检测、非游戏排除与应用激活逻辑。
  - 只有一个 hook owner，不因两个窗口重复注册。

- [x] P6.4 迁移 Dispatcher 和后台任务边界
  - 使用 `DispatcherQueue`/`DispatcherQueueTimer`。
  - WebView2 和 UI 回调必须回到拥有控件的线程。
  - 设置保存、文件 IO、缓存计算继续异步且不阻塞 UI。

- [x] P6.5 迁移错误展示和异常处理
  - WPF MessageBox 替换为 WinUI ContentDialog 或明确的 Win32 对话框封装。
  - 未处理异常继续写日志并提供可读提示。

**P6 验收：** 多窗口、单实例、快捷键、退出和异常路径稳定，无重复 hook 或后台残留。

---

## 11. P7 — 主题、语言、样式、对话框和辅助交互

- [x] P7.1 移植应用资源字典
  - 将 WPF `StaticResource`/`DynamicResource` 语义转换为 WinUI `StaticResource`/`ThemeResource`。
  - 移植共享颜色、Brush、Button、TextBox、ListView、ProgressBar 样式。

- [x] P7.2 移植 Dark/Light/System 主题
  - BrowserWindow、ControlWindow 和 dialogs 同步。
  - 保持设置持久化和运行时切换。

- [x] P7.3 移植 zh-CN/en-US 语言
  - 保持现有可见文案覆盖与运行时切换能力。
  - WinUI 资源实现可调整，但 key 和回退文案不得丢失。

- [x] P7.4 移植 Toast、InfoBar、面板动画和状态视觉
  - 使用 WinUI Animation/Storyboard/VisualState。
  - 不让动画持续占用视频渲染线程。

- [x] P7.5 替换 ThemedMessageBox
  - 使用 WinUI 对话框/窗口，保持错误、确认和下载相关交互。
  - 所有对话框拥有正确 XamlRoot/owner。

- [x] P7.6 辅助功能与输入检查
  - AutomationProperties、Tab 顺序、focus visual、键盘访问、缩放下文字可读。

**P7 验收：** 两个 WinUI 窗口主题、语言、对话框、动画和键盘导航一致。

---

## 12. P8 — 设置、数据、Profile 与窗口位置兼容

- [x] P8.1 settings schema 迁移
  - 读取旧 WPF `Key`/`ModifierKeys` 数据并转换为新 hotkey schema。
  - 旧版没有 `SchemaVersion` 字段，缺失字段必须按 schema v1 处理；不得依赖新模型属性初始值判断版本。
  - 新 schema 加版本号或可可靠检测的兼容逻辑。
  - 旧 settings 缺新字段时使用当前默认值。

- [x] P8.2 窗口坐标迁移
  - 识别旧 WPF DIP 数据，转换为 WinUI/AppWindow 坐标。
  - 越界、断开的显示器、负坐标和 DPI 改变时安全修正。

- [x] P8.3 WebView profile 兼容
  - 直接使用现有 `WebViewProfile`，验证 Cookie、登录态、LocalStorage 和密码设置。
  - 不在迁移启动时迁移、重命名或目录级清理 profile；stale cleanup 只处理应用数据根目录顶层 JSON GUID 临时文件。
  - WebView2 清理命令只清 `DiskCache`，不得把 `AllDomStorage` 当作缓存删除。

- [x] P8.4 应用数据兼容
  - history、favorites、downloads、logs 保持原路径和格式。
  - 对 schema 变化提供迁移测试和失败回退。

- [ ] P8.5 旧版升级回归
  - 从当前发布版数据目录启动新 WinUI 版。
  - 确认窗口可见、快捷键有效、数据未丢、WebView 登录态保留。

**P8 验收：** 用户可以直接覆盖升级，不需要重新登录或重新配置。

---

## 13. P9 — 测试、CI、发布和自包含分发

- [x] P9.1 更新单元测试项目引用
  - 测试主要引用 Core；UI-independent 逻辑不得依赖启动 WinUI Application。
  - 保留现有测试覆盖并修复命名空间/项目路径。

- [x] P9.2 新增迁移关键测试
  - 旧 settings hotkey 兼容。
  - window opacity 百分比到 alpha 的映射与 100% layered 移除决策。
  - Browser mode 规则、URL、下载 retry、窗口坐标归一化。

- [x] P9.3 建立 WinUI 构建验证
  - Debug/Release x64 构建。
  - TreatWarningsAsErrors 与 format 检查恢复。
  - XAML 编译错误在 CI 中可见。

- [x] P9.4 更新本地发布脚本
  - 指向最终 WinUI app project。
  - 保持 .NET self-contained、Windows App SDK self-contained、非单文件、非 trimming。
  - 保留 WebView2 Evergreen Bootstrapper 下载与签名验证。

- [x] P9.5 更新 GitHub Actions
  - 安装正确 .NET SDK/Windows App SDK 所需 workload。
  - Restore/build/test/publish 使用新 solution/project 路径。
  - 更新产物校验，允许 WinUI/Windows App SDK 合法 runtime 文件，但仍拒绝 PDB、stale WPF/WinForms 和意外 EXE。

- [ ] P9.6 验证 unpackaged zip 产物
  - 干净机器/隔离环境能直接运行。
  - 无需另装 Windows App Runtime；缺 WebView2 时仍可引导安装。
  - 版本、图标、manifest、文件大小和 SHA-256 流程正常。

**P9 验收：** CI 全绿，Release zip 可独立运行并保持当前发布体验。

---

## 14. P10 — 删除 WPF、最终回归与文档切换

- [ ] P10.1 切换正式入口
  - 最终 `GenshinBrowser.exe` 来自 WinUI app project。
  - release/CI/README 不再引用 Legacy WPF project。

- [ ] P10.2 删除 Legacy WPF UI
  - 删除旧 `MainWindow.xaml(.cs)`、WPF `ControlWindow`、WPF App 和 WPF-only helpers。
  - 删除旧根 WPF csproj 或将最终 WinUI csproj 重命名/定位为唯一正式 app project。

- [ ] P10.3 清除 WPF 依赖
  - 移除 `<UseWPF>`、`Microsoft.Web.WebView2.Wpf`、PresentationFramework、WPF WindowChrome。
  - 全仓检索不得残留产品代码中的 `System.Windows`、`WebView2CompositionControl`、`AllowsTransparency`。
  - Core 中的 `System.Windows.Input.ICommand` 属于 `System.ObjectModel` 的框架无关命令契约，不视为 WPF 依赖；最终检索需按程序集归属区分它与 `WindowsBase`/PresentationFramework。

- [ ] P10.4 清理临时兼容层
  - 删除只为迁移存在的 adapters、duplicate models、旧 binding bridge 和 TODO。
  - 确认一个 settings owner、一个 BrowserSession、一个 keyboard hook owner。

- [ ] P10.5 更新文档
  - README 改为 WinUI 3 + WebView2。
  - 记录 Windows App SDK、自包含发布和升级兼容说明。
  - 保留对 Snap.Hutao 架构参考的适当 MIT attribution（若实际移植了可识别代码片段）。

- [ ] P10.6 执行最终自动验证
  - `dotnet test` 全部通过。
  - Debug/Release build 通过。
  - publish 与产物校验通过。
  - WPF 残留检索通过。

- [ ] P10.7 执行最终人工验收清单
  - 完成下一节所有 checkbox。

**P10 验收：** 仓库只剩正式 WinUI 3 UI 路径，功能、数据和发布流程完整。

---

## 15. 最终人工验收清单

### 启动与数据

- [ ] 单实例启动与二次激活正确
- [ ] 原 settings/history/favorites/downloads/logs 正常读取
- [ ] 原 WebView profile 登录态、Cookie 和站点数据保留
- [ ] 缺少 WebView2 Runtime 时安装提示/静默安装正常

### 浏览器

- [ ] 初始 URL 与上次页面恢复
- [ ] 导航、前进、后退、刷新、新窗口重定向
- [ ] 页面标题、地址、加载状态实时同步
- [ ] 播放/暂停快捷键正常
- [ ] zoom 修改和重启持久化
- [ ] renderer/browser process 故障后恢复

### 浮窗和透明度

- [ ] 浏览模式可 resize、非置顶、标题栏常驻
- [ ] 浮窗模式置顶、尺寸策略正确、标题栏自动隐藏
- [ ] 10%、50%、100% 整窗透明度正确
- [ ] 100% 时未保留 layered transparency style
- [ ] 透明度同时作用于 WebView、标题栏和 BrowserWindow 内提示
- [ ] ControlWindow 始终正常不透明
- [ ] 视频期间不存在 WPF `GraphicsCaptureSession` / `D3DImage` 路径

### 控制窗口

- [ ] 地址栏、导航按钮和加载态
- [ ] 收藏搜索、添加、打开、复制、删除
- [ ] 历史搜索、打开、复制、删除
- [ ] 下载进度、取消、重试、打开、清理
- [ ] 设置/下载面板展开收起
- [ ] 透明度、zoom、尺寸、角落定位、默认值恢复
- [ ] 快捷键录制、冲突检查和录制期间 hook 暂停

### 窗口与输入

- [ ] F8 浏览/浮窗切换
- [ ] K/自定义键播放控制与游戏检测
- [ ] 最大化、还原、拖动、角落定位
- [ ] 多显示器、负坐标、100%/125%/150% DPI
- [ ] ControlWindow 位置和尺寸保存恢复
- [ ] 关闭、隐藏、重新显示和应用退出无残留进程

### 外观与发布

- [ ] Dark/Light/System 主题
- [ ] zh-CN/en-US 切换与重启保持
- [ ] 对话框、Toast、InfoBar、focus 和键盘导航
- [ ] Release zip 在干净环境运行
- [ ] CI、版本信息、签名验证、压缩包和 SHA-256 正常

---

## 16. 最终自动检索门槛

完成 P10 前，至少执行并记录等价检索：

```powershell
rg -n "UseWPF|System\.Windows|Microsoft\.Web\.WebView2\.Wpf|WebView2CompositionControl|AllowsTransparency|WindowChrome" . --glob '*.cs' --glob '*.xaml' --glob '*.csproj' --glob '*.props' --glob '*.targets'
```

允许命中的范围只能是迁移历史文档或明确的兼容性测试字符串；正式产品代码必须无命中。

最终验证命令以迁移后的 solution/project 路径为准，至少包含：

```powershell
dotnet build GenshinBrowser.sln -c Debug -p:Platform=x64
dotnet build GenshinBrowser.sln -c Release -p:Platform=x64
dotnet test tests/GenshinBrowser.Tests/GenshinBrowser.Tests.csproj -c Release
./release/publish-win-x64.ps1 -Configuration Release -Runtime win-x64 -Version 0.0.0-local
```

---

## 17. 决策记录

| Date | Decision | Reason |
|---|---|---|
| 2026-07-23 | 浏览器浮窗和控制窗口全部迁移 WinUI 3 | 用户明确要求全项目重构，避免永久混合 UI 框架 |
| 2026-07-23 | 单进程、两个 WinUI 顶层窗口 | 避免 IPC、重复 profile、重复 hook 和跨进程状态同步 |
| 2026-07-23 | 不做一次性流畅度 demo | 用户明确要求直接制定并执行正式迁移计划 |
| 2026-07-23 | 整窗持续 alpha，不复制胡桃激活切换 | 用户只要求借鉴淡窗口方法，并希望保留本项目透明度行为 |
| 2026-07-23 | 100% 移除 layered style | 避免不透明状态继续承担透明窗口合成路径 |
| 2026-07-23 | P0.2 测试暂后移，先开始 P1 代码 | 用户明确要求先写代码、不要先测试；测试要求未取消 |
| 2026-07-23 | ControlWindow 作为 BrowserWindow 的 Win32 owned tool window | 保留旧 WPF `Owner` 的层级、最小化和任务栏语义；浏览器关闭时先完成异步清理并关闭控制窗，再销毁 owner HWND |
| 2026-07-23 | WebView2 缓存维护只清理 DiskCache | 用户覆盖升级必须保留现有 Profile 中的 Cookie、LocalStorage、IndexedDB 与登录态；例行缓存阈值检查不能等价为站点数据重置 |
