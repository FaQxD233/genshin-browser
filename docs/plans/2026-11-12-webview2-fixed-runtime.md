# 计划：完整版发布包内置固定版本 WebView2 运行时

> 状态：已实施并提交，本地自检与端到端通过（2026-11-12；实施记录见第 5 节）；工作流尚未在 GitHub 实跑（用户决定先不验证）
> 日期：2026-11-12
> 来源：用户 2026-11-11 ~ 12：WebView2 运行时自动升到 157 后视频铺满失效（已在 v0.3.12 修复）。用户希望固定内核版本、优先保证能用；最终确认：现在的标准版照旧，另出一个内置固定版本 WebView2 的完整版，GitHub Actions 加选项，勾上就同时生成两种包；不做 ESR 那样的长期分支
> 流程：本文件确认后再改代码；改完自检，提交、推送、发版另听用户安排

---

## 0. 需求边界

- 两种包，程序文件完全相同，只差有没有内置运行时：
  - 标准版 `GenshinBrowser-vX-win-x64.zip`：和现在一样，用系统里的 Evergreen 运行时，缺了首次启动自动安装（包里带 Evergreen 安装程序）。
  - 完整版 `GenshinBrowser-vX-win-x64-full.zip`：exe 旁边的 `WebView2Runtime\` 里是固定版本运行时，程序只用这一份，不检测、不安装系统的 WebView2，包里不带 Evergreen 安装程序。
- 首个固定版本：157 的最新补丁版 x64，目前是 157.0.4341.46（.cab 296.6 MiB）；执行时官网如果已经出了 157 更新的补丁版，就用新的。理由：
  - 官网只提供最近两个大版本各自的最新补丁版（现在是 157.0.4341.46 和 156.0.4314.58），更新的正式版还没出，Beta / Dev / Canary 是预览版，不能用于发布。
  - 157 安全修复最多；固定版本以后不会自己打补丁，从最新的开始，能用得最久。
  - 本程序今天所有测试（含视频铺满修复）都在 157.0.4341.46 上跑过；本机浏览器数据（`WebViewProfile\EBWebView\Last Version`）上次也是 157.0.4341.46 打开的，换过去不涉及降级。
  - 156 没有多出任何功能，少了 157 的安全修复，还会让本机浏览器数据降级；视频铺满在两个版本上都能用，不是选 156 的理由。
- 开发构建（`dotnet build`、端到端测试工程）旁边没有内置运行时，照旧用系统的 Evergreen 运行时。
- 不做：程序自己更新内置运行时（跟着我们发版更新）；x86 / ARM64 完整版（现在本来就只有 win-x64）。

## 1. 方案

### 1.1 程序

- 新增 `Services/WebView2RuntimeLocator.cs`：exe 同目录下有 `WebView2Runtime\msedgewebview2.exe` 就返回这个目录，否则返回 null。
- `MainWindow`：`StartEnvironmentPreload` 与 `GetOrCreateBrowserEnvironmentAsync` 两处 `CoreWebView2Environment.CreateAsync` 带上 `browserExecutableFolder`；有内置运行时时跳过 `IsWebView2RuntimeInstalled` / `EnsureWebView2RuntimeAsync`。没有内置运行时走原来的 Evergreen 路径，原代码保留。
- Windows 10 授权：官方文档要求，固定版本 120 起，未打包的应用在 Windows 10 上要给运行时目录加「ALL APPLICATION PACKAGES」（S-1-15-2-1）与「ALL RESTRICTED APPLICATION PACKAGES」（S-1-15-2-2）的读取和执行权限（继承到子项），否则渲染进程起不来、页面打不开。程序本来就以管理员身份运行：启动时检查，缺了用 .NET 自带的 ACL 接口补上，已有（含从上级目录继承）就跳过，失败记日志。Windows 11 不需要，但补上也无害，为简单起见不分系统。
- 启动日志记一行：运行时来源（内置 / 系统）和版本号（`BrowserVersionString`），以后出问题能直接对上版本。

### 1.2 打包（本地与 CI 共用同一套脚本）

- 新增 `release/webview2-fixed-runtime.json`：版本号、x64 .cab 的官方下载链接、SHA-256（官网不给校验值，首次下载后算出写死）。
- 新增 `release/add-webview2-runtime.ps1`：按配置下载 .cab（本地缓存在 `dist/.cache/webview2/`，CI 用 `RUNNER_TEMP`）→ 校验 SHA-256 → `expand` 解压 → 内层目录放到包目录的 `WebView2Runtime\` → 校验 `msedgewebview2.exe` 是微软签名、版本号与配置一致。
- 新增 `release/verify-package.ps1`：两种包共用的发布自检（版本号、多余 exe、WinForms 残留、deps.json、pdb；标准版查 Evergreen 安装程序签名，完整版查内置运行时签名与版本），替换 `publish-win-x64.ps1` 和 CI 里各自内联的一份。
- `release/publish-win-x64.ps1`：加 `-BundleWebView2` 开关，同时输出 `dist/win-x64-self-contained`（标准版）和 `dist/win-x64-full`（完整版）；加 `-NoRestore` 供 CI 使用。
- `.github/workflows/build.yml`：发布步骤改为调用 `publish-win-x64.ps1`（不再内联一份发布与自检）；新增输入 `bundle_webview2`（默认勾上），勾上时同时打包、上传、发布完整版；发布页模板写清两种包的区别和内置版本号。
- 文案：`release/README.txt`（两种包都适用）、README「运行要求」「发布与使用」。

### 1.3 以后怎么升级内置运行时

- 从官网下载页取新版本 x64 .cab 链接，改 `release/webview2-fixed-runtime.json` 的版本、链接、SHA-256 → 本机打完整版并用端到端探针跑一遍 → 发版。
- 固定版本不会自己打安全补丁，建议至少一两个月、或微软发安全更新时升一次；只往新版本升，不往回退（往回退会遇到浏览器数据降级）。
- 官网只提供最近两个大版本，用过的 .cab 自己留一份存档（本地缓存目录里就有）。

## 2. 代价与风险（用户已决定接受，列在这里备查）

- 完整版的安全补丁跟着我们的发版走，不再由微软自动推送。程序以管理员身份运行、会打开任意网页，所以要按 1.3 定期升级。
- 体积：.cab 本身 296.6 MiB，完整版下载包会比标准版（64.9 MiB）大好几倍，实施时实测。
- Windows 10 授权这一步本机（Windows 11）测不了，有 Windows 10 用户的话需要在 Windows 10 上实测一次。
- 不能放在网络路径（UNC）上运行（官方已知问题）。
- 用户如果先用标准版、系统 Evergreen 又比内置版本新，换成完整版时浏览器数据会被更旧的内核打开（降级）。实施时用测试数据目录模拟一次，看 WebView2 是照常打开、重置数据还是报错，再决定要不要处理。

## 3. 测试

- 单测：新增运行时定位与授权的用例；全部通过，`dotnet format --verify-no-changes` 无改动，Release + `TreatWarningsAsErrors` 0 警告。
- 本地打包：`./release/publish-win-x64.ps1 -BundleWebView2` 跑通两种包的自检，报告两种包的体积。
- 端到端：把完整版的 `WebView2Runtime` 放到端到端测试程序旁边，确认用的是内置运行时（`edge://version` 里的可执行文件路径）、日志里来源与版本正确、运行时目录权限已补上，再跑 `fill-esc` 与 `resume` 两种模式。
- 浏览器数据降级模拟（见第 2 节）。
- 提交推送后，用不发布的方式（`create_release` 不勾）跑一次工作流，确认两种包都能生成。
- 用户在真机：解压完整版，以管理员身份运行，确认页面、浮窗透明、视频铺满、热键正常。

## 4. 改动文件

- 程序：`MainWindow.xaml.cs`、`Services/WebView2RuntimeLocator.cs`（新）
- 打包：`release/webview2-fixed-runtime.json`（新）、`release/add-webview2-runtime.ps1`（新）、`release/verify-package.ps1`（新）、`release/publish-win-x64.ps1`、`.github/workflows/build.yml`
- 文档：`release/README.txt`、`README.md`、本机 `.claude/CLAUDE.md`（升级步骤与测试方法）
- 测试：`tests/GenshinBrowser.Tests/WebView2RuntimeLocatorTests.cs`（新）

## 5. 实施记录（2026-11-12）

- 固定版本 .cab：157.0.4341.46 x64，296.60 MiB，微软签名有效（Microsoft Code Signing PCA 2024 签发），SHA-256 写入配置。官网当时同时提供 156.0.4314.58。
- 包体积：标准版 151.78 MiB（zip 约 65 MiB）；完整版 828.11 MiB（内置运行时 257 个文件、678.14 MiB），按 CI 同样的方式压缩后 zip 369.6 MiB。本地打两种包共 19 秒（.cab 已缓存）。
- 自检：单测 127/127（新增 4 个）；Release + `TreatWarningsAsErrors` 0 警告；`dotnet format --verify-no-changes` 无改动；工作流 YAML 解析通过；两种包的 `verify-package.ps1` 自检通过。
- 端到端（内置运行时放在测试程序旁边）：
  - 浏览器进程从 `WebView2Runtime\msedgewebview2.exe` 启动；日志记下「WebView2 runtime 157.0.4341.46 (bundled)」；两个 AppContainer 权限项（S-1-15-2-1、S-1-15-2-2，读取和执行、继承到子项）已补上，第二次启动不再改。
  - `fill-esc`：仿 B 站页、真 B 站页都能铺满，Esc 能退出。
  - `resume` 14/15：失败的「取样：不给页面用户激活」是老问题，与本次改动无关。主窗检测到鼠标移出 / 移回窗口时，`DispatchMouseLeftPageAsync` / `DispatchMouseReenteredPageAsync` 用 `ExecuteScriptAsync` 执行脚本，WebView2 会把它当成用户操作、给页面一次用户激活；测试窗口在屏幕外、鼠标始终在窗外，触发时机决定这项过不过（2026-11-04 记录的偶发失败同一原因）。用户决定先修：两处改为经 CDP 不带用户手势执行，修复记录见 `2026-11-04-playback-resume.md` 第 2 节。
- 浏览器数据降级模拟：测试数据目录的 `Last Version` 改成 999.0.0.0 后用内置 157 打开，照常启动，之前写入的 localStorage 标记还在，`Last Version` 被改回 157.0.4341.46，没有重置数据。只模拟了版本记录：真实的新版内核如果升级了某个数据库的格式，旧内核可能读不了那部分数据，这一点模拟不了。
- 与计划的偏差：
  - 下载脚本在 SHA-256 不符时同时打印 .cab 的签名，升级版本时先确认来源再填新值。
  - 发布脚本保持纯 ASCII（英文注释，与原有脚本一致）：Windows PowerShell 5.1 按系统代码页读不带 BOM 的脚本，中文注释曾让 `add-webview2-runtime.ps1`、`verify-package.ps1` 解析失败。改后用 5.1 与 PowerShell 7 本地打两种包都通过。
