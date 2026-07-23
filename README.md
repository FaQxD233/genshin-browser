# Genshin Browser

一个专门为 B 站原神攻略视频设计的 Windows 浮窗浏览器，基于 `WinUI 3 + WebView2`。

> 迁移执行清单与 agent 协作规则：[`WINUI3_MIGRATION_PLAN.md`](./WINUI3_MIGRATION_PLAN.md)。

## ✨ 功能

### 浮窗透明
- **透明度可调**：10%–100% 连续调节整个浏览器窗口透明度
- **游戏叠加使用**：半透明浮窗置于游戏上层，边玩边看
- **低开销不透明模式**：100% 不透明时移除 layered-window 路径

### 窗口模式
- **浮窗**：窗口置顶，适合叠在游戏上观看
- **浏览**：可拖动、调整大小，配合控制台找片与设置
- **快捷键切换**：`F8` 一键切换 浏览 ⇄ 浮窗

### 自定义按键
- **播放/暂停**：默认 `K` 键，可在设置中修改
- **浏览 ⇄ 浮窗**：默认 `F8` 键，可在设置中修改
- **游戏检测**：浮窗模式下 K 键仅在前台窗口为游戏时生效，在浏览器、聊天工具、编辑器等已知非游戏软件中不触发，避免误输入

### 浏览与记录
- **登录态保留**：Cookie、自动填充和已保存密码不会被缓存清理影响；缓存超过阈值时自动回收
- **收藏夹**：一键收藏，支持搜索
- **浏览历史**：自动记录访问历史，支持搜索
- **智能恢复**：记住上次浏览的页面、窗口位置和大小

### 本地日志
- **自动记录**：运行日志写入 `%LocalAppData%\GenshinBrowser\logs\`
- **异常追踪**：未处理异常自动捕获并写入日志
- **排查方便**：按日期归档，便于定位问题

### 控制窗口
- **独立面板**：主窗口专注显示内容，控制窗口提供地址栏、收藏、历史等操作
- **实时同步**：地址栏跟随浏览器 URL 变化
- **WinUI 控制面板**：主题、语言、下载与快捷键设置均在同一 WinUI 3 进程内

## 🖥️ 运行要求

- Windows 10/11
- Edge WebView2 Runtime（Windows 11 自带）
- .NET 8 Runtime（自包含版本无需安装）
- Windows App SDK Runtime（自包含版本无需单独安装）
- 应用沿用管理员启动清单，以便在管理员权限游戏前台继续接收全局快捷键

## 📦 发布与使用

### 自动化测试
```powershell
dotnet test tests/GenshinBrowser.Tests/GenshinBrowser.Tests.csproj
```

### 自包含版本（推荐）
- 本地发布：`./release/publish-win-x64.ps1 -Version v0.2.1`
- 发布目录：`dist/win-x64-self-contained/`
- 无需安装 .NET，开箱即用
- 首次启动自动检测并安装 WebView2 Runtime（如需）
- GitHub Actions 仅支持手动触发（Actions → CI and Release）：可只跑构建/测试，或完整发布 win-x64

## 🧱 技术结构

仓库同时保留两套 UI 实现，共享同一个 `GenshinBrowser.Core` 业务逻辑层：

```
GenshinBrowser.sln
├── src/GenshinBrowser.App        # WinUI 3 主版本（当前主线，发布产物来源）
├── src/GenshinBrowser.Core       # 共享核心：设置/历史/收藏/下载/快捷键等可测试逻辑
├── GenshinBrowser.csproj         # WPF 老版本（GenshinBrowser.Legacy，保留备查，仍可独立构建）
└── tests/GenshinBrowser.Tests    # 针对 Core 的单元测试
```

- 单进程 WinUI 3 应用，包含 `BrowserWindow` 与 `ControlWindow`
- 浏览器使用标准 WinUI 3 `WebView2`，沿用 `%LocalAppData%\GenshinBrowser\WebViewProfile`
- 半透明通过 BrowserWindow HWND 统一 alpha 实现；不使用 WPF CompositionControl、D3DImage 或屏幕捕获转绘
- Core 项目保存设置、历史、收藏、下载记录与可测试业务逻辑，不依赖 WPF/WinUI 控件类型
- WPF 老版本（`GenshinBrowser.Legacy`）通过 `<Compile Link>` 复用 Core 源码，仍可构建运行，作为迁移前的参考与回退方案保留

### 构建命令

```powershell
# 还原并构建整个解决方案（含 WinUI 主版本 + WPF Legacy + 测试）
dotnet restore GenshinBrowser.sln -p:Platform=x64
dotnet build GenshinBrowser.sln -c Debug -p:Platform=x64

# 仅构建 WinUI 主版本
dotnet build src/GenshinBrowser.App/GenshinBrowser.App.csproj -c Debug -p:Platform=x64

# 仅构建 WPF 老版本（Legacy）
dotnet build GenshinBrowser.csproj -c Debug

# 运行单元测试
dotnet test tests/GenshinBrowser.Tests/GenshinBrowser.Tests.csproj
```

### 快捷键
- `K`：播放/暂停视频（全局）
- `F8`：切换 浏览 / 浮窗（全局）

## 📄 开源协议

MIT License

## 致谢

WinUI 3 标准 WebView2 浮窗与窗口级 alpha 的职责划分参考了 MIT 许可的 Snap.Hutao 项目；本项目使用自己的窗口服务与 Win32 P/Invoke，不依赖其 Native DLL、DI 或源码生成基础设施。
