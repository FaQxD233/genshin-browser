# 计划：代码审查修复（热键录制、数据文件保护、普通权限打开、去重、内存目标与加载条）

> 状态：已实施，本机单测与端到端通过（2026-11-04；实施记录见第 3 节），随 v0.3.10 发布；提权状态下的打开文件 / 外部程序待你在真机确认
> 日期：2026-11-04
> 来源：2026-11-04 代码审查，用户逐项拍板：修 4、2、5，做 7、8；1、3 按设计如此；新功能（攻略站 iframe 视频热键、浏览器扩展 / 广告拦截、检查更新）不做
> 流程：用户要求改完直接提交、推送，本机测试通过后发 v0.3.10

---

## 0. 需求边界

### 要做

| 编号 | 项 | 一句话 |
|------|----|--------|
| 4 | 热键录制与输入法 | 中文输入法开着时 WPF 把字母键报成 `ImeProcessed`，录进去的键永远不触发；按真实键录制，已存坏的键启动时恢复默认 |
| 2 | 数据文件读失败不覆盖 | 设置、收藏、浏览记录、下载记录读不出来时，原文件改名保留；改名也失败（被占用）就本次不写这个文件；启动后提示一次 |
| 5 | 普通权限打开 | 程序以管理员运行，下载面板「打开文件」和网页唤起的外部程序也带管理员权限：改为经桌面资源管理器以普通权限打开；网页唤起外部程序改为自己确认，非用户点击触发的直接拦掉；高危扩展名清单补全 |
| 7 | 去重 | 五组热键改为按槽位查表（落盘字段名不变）；收藏 / 浏览记录合用一个按网址去重的列表基类，三个数据服务合用一个防抖写盘组件；MainWindow 不单独拆（等主窗渲染改造） |
| 8 | 11-04 计划 S1 / S2 | 内存目标只在最小化 / 热键隐藏时设 Low；地址栏加载条动画只在加载时运行 |

### 按设计如此（不改，代码里加注释）

- 网页非用户触发的 `window.open` 也在当前页打开。
- WebView2 可回收缓存每次启动只在首屏导航后检查一次，常驻期间不再检查。

---

## 1. 方案

### 1.1 热键录制（4）

- `ControlWindow` 录制取实际按键：`System` → `SystemKey`，`ImeProcessed` → `ImeProcessedKey`，`DeadCharProcessed` → `DeadCharProcessedKey`（纯函数放 VM，便于单测）。
- `SettingsService.IsValidHotkey` 与 `KeyboardHookService` 的主键校验拒绝 `ImeProcessed`（VK_PROCESSKEY 0xE5）、`DeadCharProcessed`：已存坏的槽位启动时回默认并照常消解冲突。

### 1.2 数据文件（2，与 7 的去重一起做）

- 新增 `DataFile.Read`：文件不存在返回空；读盘 IO 异常重试 3 次（间隔 200ms）；内容损坏、超过大小上限或重试后仍读不出时，把原文件改名为 `{名}.broken-{yyyyMMdd-HHmmss}{扩展名}` 保留。改名失败返回「不可写」，调用方本次运行不再写这个文件。
- 新增 `DebouncedJsonSaver<T>`：版本号、防抖、保存闸、原子写盘、Flush、不可写开关，收藏 / 浏览记录 / 下载记录共用（三份几乎逐行相同的代码合成一份）。快照在改动当时、调用方锁内取（下载列表是 UI 线程上的 ObservableCollection，只能当场取）。
- 新增 `UrlEntryStore<T>`：收藏与浏览记录的公共基类（加载合并、按网址去重、导入合并 / 替换、Contains）；`HistoryService.AddEntryAsync` / `FavoritesService.AddOrUpdateAsync` 等对外方法名不变。条目实现 `IUrlEntry`（时间字段显式实现，JSON 字段名 `VisitedAt` / `SavedAt` 不变）。删掉没有调用方的 `HistoryService.ClearAllAsync`（清空历史 UI 已列为不做）。
- 设置读失败同样改名保留；不可写时 `SaveAsync` 跳过。
- 主窗在浏览器就绪后与数据目录提示一起提示一次。

### 1.3 普通权限打开（5）

- 新增 `ShellLauncher`：本进程未提权时照常 `ShellExecute`；已提权时用桌面资源管理器的 `Shell.Application.ShellExecute` 执行（微软 Windows SDK 示例 ExecInExplorer 的做法：`IShellWindows.FindWindowSW(SWC_DESKTOP)` → `IShellBrowser.QueryActiveShellView` → `IShellView.GetItemObject(SVGIO_BACKGROUND)` → `Application.ShellExecute`），进程由 explorer.exe 拉起、带普通权限。失败时不退回提权打开。
- 下载面板「打开文件」走它；失败时退回在文件夹中显示。高危扩展名补 `.jar .reg .chm .cpl .msc .msp .scf .ws .psm1 .application .appinstaller .appx .appxbundle .msix .msixbundle`。
- `LaunchingExternalUriScheme`：一律取消默认处理（默认对话框点「打开」后由浏览器进程启动，带管理员权限）；非用户点击触发的直接拦掉并记日志；用户点击触发的弹确认框，确认后经 `ShellLauncher` 以普通权限打开。

### 1.4 S1 / S2（8）

- S1：`UpdateWebViewMemoryTargetLevel` 只看是否最小化（F7 隐藏也是最小化）；激活 / 失焦、切模式不再改内存目标。
- S2：`AddressBarProgressBar` 不再固定 `IsIndeterminate=True`，改为绑定 `IsNavigating`；模板的不定进度动画在退出时 `RemoveStoryboard`。

---

## 2. 测试

- 单测：录制取键、坏热键恢复默认、0xE5 被拒；四个数据文件损坏 → 改名保留、之后的保存不动保留文件；文件被独占 → 不写盘；热键查表的读写与原批量接口语义。
- 本机端到端（真主窗 + WebView2 离屏，数据目录指向 D 盘测试副本）：坏收藏文件启动后改名保留并提示；浮窗可见 Normal、最小化 Low、恢复 Normal；加载结束后加载条动画停止；热键改键 / 恢复默认；经资源管理器启动的进程父进程是 explorer.exe；脚本自动唤起外部协议被拦。
- Release + `TreatWarningsAsErrors` 编译、`dotnet format --verify-no-changes`。

---

## 3. 实施记录

### 3.1 改动

- 新增：`Models/HotkeySlot.cs`、`Models/HotkeyBinding.cs`、`Models/IUrlEntry.cs`、`Services/DataFile.cs`、`Services/DebouncedJsonSaver.cs`、`Services/UrlEntryStore.cs`、`Services/ShellLauncher.cs`、`ViewModels/HotkeyRowViewModel.cs`。
- `KeyboardHookService`：五组字段 / 属性 / `TrySet*` / 事件合成按槽位的一张表，`TrySetHotkey(slot, …)`、`TrySetBuiltInHotkeys(字典)`、`HotkeyPressed(slot)`；默认值取自 `AppSettings`。
- `MainWindow` / `IControlBrowser`：十个热键属性与五个 `TrySet*` 换成 `GetHotkey` / `TrySetHotkey`；五个钩子回调合成一个；恢复默认与导入共用 `ApplyHotkeys(AppSettings)`。
- `ControlWindowViewModel`：五组录制标志、键帽文字、录制命令换成五个行 VM（XAML 绑定 `ToggleModeHotkey.KeyText` 等）；录制提示与成功提示的文案键按槽位名拼（`Toast.Record{槽位}`、`Toast.{槽位}Set`，单测守着），槽位 `Mode` 因此改名 `ToggleMode`。
- `HistoryService` / `FavoritesService` 只剩构造参数和对外方法名；`DownloadsService` 的防抖写盘换成共用组件；删掉没有调用方的 `HistoryService.ClearAllAsync`。
- 启动提示（数据目录、数据文件）改到首屏导航结束后发：原来在浏览器初始化后发，一两秒后就被「页面已加载」的提示盖掉。
- 文案：中英各加 6 条（数据文件两条、外部程序四条）。

### 3.2 偏差

- 原计划用宿主 `ExecuteScriptAsync` 模拟「网页自己唤起外部协议」：实测 WebView2 把它当成用户触发（`IsUserInitiated=true`），改用页面里的定时器脚本测；页面脚本触发的确为 false，被拦并记日志。

### 3.3 测试

- 单测 91/91（新增 16：数据文件 7、热键槽位与录制取键 7、钩子 2）；Release + `TreatWarningsAsErrors` 编译、`dotnet format --verify-no-changes` 通过。
- 端到端 22/22（测试副本，favorites.json 截断成坏文件）：坏文件改名保留、内容原样、首屏后提示且没被盖掉、关闭后新收藏写进新文件；热键与钩子注册一致、改键、冲突被拒、键帽绑定刷新、恢复默认与还原；浮窗可见 Normal、最小化 Low、恢复 Normal；加载中动画在跑、加载完停止；经资源管理器启动的进程父进程是 explorer.exe；页面脚本唤起外部协议被拦、用户手势弹本程序确认框、选「否」不启动。
- 离屏截图核对按键绑定面板：五个键帽与录制中高亮、提示文案正常。
- 未覆盖：这里的进程是普通权限，提权下 `ShellLauncher` 经资源管理器打开只测了内部方法；确认框选「是」后的打开没测（会真的打开系统设置）。
