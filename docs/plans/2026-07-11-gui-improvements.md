# 计划：控制窗 GUI 体验改进 + 主题切换 + i18n

> 状态：Batch 1/2/3 已完成（编译通过）  
> 日期：2026-07-11  
> 完成：Batch 1 体验主线 + Batch 2 主题 + Batch 3 i18n  
> 延后：无  
> 范围：基于既有分析与用户明确需求，**不**新增主页/快捷键帮助入口/清空历史 UI

---

## 0. 用户确认的需求边界

### 要做

| # | 项 | 说明 |
|---|----|------|
| 1 | 删除废弃能力 | 删除主页 / 快捷键帮助 / 清空历史相关**未使用**代码 |
| 2 | 地址栏加载指示 | 地址栏右侧细进度条 / 加载态 |
| 3 | 设置/下载面板动效 | ModernExpander 或高度+过渡动画；互斥逻辑下沉 ViewModel |
| 4 | 收藏 / 历史列表优化 | 键盘、右键菜单去重、交互一致性 |
| 5 | 下载面板优化 | 体验与绑定完善 |
| 6 | 主浮窗标题栏优化 | 视觉与控件细节 |
| 7 | 主题与控件细节 | 统一控件观感 |
| 8 | MVVM 补齐 | 减少 code-behind 业务逻辑，便于后续改 UI |
| 9 | 历史上限 200 | `MaxHistoryEntries: 50 → 200` |
| 10 | 亮/暗主题切换 | 切换按钮放在**设置面板内** |
| 11 | i18n | 中英资源，UI 文案可切换 |

### 不做（本轮）

- 主页按钮 / `NavigateHome` UI 入口
- 快捷键 `[?]` 帮助入口
- 清空历史按钮 / 确认清空流程 UI
- 系统托盘
- 收藏夹分组 / 拖拽排序
- 完整 MainWindow 架构大拆分（仅做与本轮相关的接口清理）
- 单元测试项目（可后续单独做）

---

## 1. 现状结论（调研摘要）

| 文件 | 现状 | 与本轮关系 |
|------|------|-----------|
| `Windows/ControlWindow.xaml` (463 行) | 设置/下载用 `Visibility` 硬切；地址栏无加载条；后退/前进未绑 `IsEnabled` | 改 XAML 主体 |
| `Windows/ControlWindow.xaml.cs` | 设置/下载互斥在 click；右键菜单重复；搜索 TextChanged | 下沉 VM / 去重 |
| `ViewModels/ControlWindowViewModel.cs` (758 行) | 含 `NavigateHomeCommand` / `ClearHistoryCommand` / `HelpTooltip`；有 `IsSettingsExpanded` 但互斥在视图 | 删废弃 + 补命令/状态 |
| `Windows/IControlBrowser.cs` | 暴露 `NavigateHome` / `ClearHistoryAsync` | 删除或保留内部实现二选一见下 |
| `MainWindow.xaml(.cs)` | 标题栏简陋；导航状态未暴露 `IsNavigating` | 标题栏 + 导航状态 |
| `Resources/Theme.xaml` (568 行) | 仅暗色；已有 `ModernExpander` / `ModernProgressBar` / Toast 动画 | 拆亮暗色 + 控件微调 |
| `Constants/AppConfig.cs` | `MaxHistoryEntries = 50`；地址栏占位与 XAML 不一致 | 改 200 + 统一占位 |
| `Models/AppSettings.cs` | 无 Theme / Language 字段 | 扩展持久化 |
| `Services/HistoryService.cs` | 已按 `AppConfig.Data.MaxHistoryEntries` 截断 | 只改常量即可生效 |

### 关键调用链

```
MainWindow (IControlBrowser)
  ├─ BrowserStateChanged / DownloadsChanged / ZoomChanged
  └─ ControlWindow
        └─ ControlWindowViewModel
              ├─ Commands → IControlBrowser
              └─ UI 状态：IsSettingsExpanded / IsDownloadsExpanded / SearchText / Toast
```

主题切换需影响：`App` 资源字典、`MainWindow` 标题栏、`ControlWindow`、`ThemedMessageBox`（共享 brush key 即可）。

---

## 2. 分阶段实施

### Phase A — 清理废弃代码（小、先做，降低噪音）

**目标**：去掉用户明确不要的死代码，避免后续 i18n/主题时还要翻译无用文案。

| 步骤 | 改动 | 文件 |
|------|------|------|
| A1 | 删除 `NavigateHomeCommand`、`ClearHistoryCommand`、`HelpTooltip`、`ConfirmClear`、`ClearHistoryWithConfirmAsync` | `ControlWindowViewModel.cs` |
| A2 | 删除 `ControlWindow` 中 `ConfirmClear` 注入 | `ControlWindow.xaml.cs` |
| A3 | 从 `IControlBrowser` **删除** `NavigateHome()`、`ClearHistoryAsync()` | `IControlBrowser.cs` |
| A4 | 删除 `MainWindow` 中对应 public 实现（若内部无其它调用） | `MainWindow.xaml.cs` |
| A5 | `HistoryService.ClearAllAsync`：**保留**（服务层能力，后续若需要可再暴露）；本轮 UI/接口不调用即可 | 无强制删除 |

**验收**：编译通过；无对已删成员的引用。

---

### Phase B — 数据与配置

| 步骤 | 改动 | 文件 |
|------|------|------|
| B1 | `MaxHistoryEntries = 200` | `Constants/AppConfig.cs` |
| B2 | 地址栏占位**单一来源**：XAML 使用与 `AppConfig.Ui.AddressBarPlaceholder` 一致的文案；推荐后续走 i18n 键，本阶段先统一为常量或资源 | `AppConfig.cs` + `ControlWindow.xaml` |
| B3 | `AppSettings` 增加：`ThemeMode`（`Dark`/`Light`/`System` 或仅 `Dark`/`Light`）、`Language`（`zh-CN`/`en-US`） | `Models/AppSettings.cs` |
| B4 | `SettingsService` 读写新字段（已有序列化应自动兼容；确认默认值） | `Services/SettingsService.cs` |

**主题默认**：`Dark`（与现状一致）  
**语言默认**：`zh-CN`

**验收**：改历史上限后新访问超过 50 条仍保留至 200；旧 settings.json 缺字段不炸。

---

### Phase C — 导航加载态 + 地址栏进度条

| 步骤 | 改动 | 文件 |
|------|------|------|
| C1 | `MainWindow` 在 `NavigationStarting` / `NavigationCompleted`（及失败）维护 `IsNavigating` | `MainWindow.xaml.cs` |
| C2 | `IControlBrowser` 增加 `bool IsNavigating { get; }`；状态变化走现有 `BrowserStateChanged` 或单独事件（优先复用 `BrowserStateChanged`） | `IControlBrowser.cs` |
| C3 | VM：`IsNavigating` 属性，在 `RefreshFromBrowser` 同步 | `ControlWindowViewModel.cs` |
| C4 | 地址栏区域：右侧（打开/刷新旁或地址框下方）增加 `ProgressBar`，`IsIndeterminate=True`，绑定 `IsNavigating` 可见性；样式复用 `ModernProgressBar` 或新增 `AddressBarProgressStyle`（更细、2–3px） | `ControlWindow.xaml` + 可选 `Theme.xaml` |
| C5 | 后退/前进按钮：`IsEnabled="{Binding CanGoBack/CanGoForward}"`（命令已有 CanExecute，XAML 显式绑定更稳） | `ControlWindow.xaml` |

**验收**：导航中显示细进度条；完成后消失；无历史时后退灰显。

---

### Phase D — 设置/下载面板：动画 + 互斥下沉 VM

| 步骤 | 改动 | 文件 |
|------|------|------|
| D1 | VM 增加 `ToggleSettingsCommand` / `ToggleDownloadsCommand`：切换自身，并互斥关闭另一方 | `ControlWindowViewModel.cs` |
| D2 | 删除 `SettingsButton_OnClick` / `DownloadsButton_OnClick` 中的互斥逻辑，改为 Command 绑定 | `ControlWindow.xaml` + `.xaml.cs` |
| D3 | 面板呈现方式二选一（**推荐方案 1**）： | |
| | **方案 1（推荐）**：保留顶栏图标按钮触发，内容区 `Border` 用附加行为/动画改 `MaxHeight`+`Opacity`（0→目标高度），避免 Expander 双层标题 | `ControlWindow.xaml` + 小工具类或 Storyboard |
| | **方案 2**：改用 `ModernExpander`，并增强 `ModernExpander` 模板支持展开高度动画 | `Theme.xaml` + XAML |
| D4 | 下载新增时自动展开下载面板（已有）保持；同时关闭设置面板（在 VM 的 `IsDownloadsExpanded` setter 或 CollectionChanged 中统一） | `ControlWindowViewModel.cs` |

**动画参数建议**：Duration ~180–220ms，`CubicEase EaseOut`；与现有 Toast/Loaded 动画一致。

**验收**：点设置/下载互斥；展开收起有过渡；无 code-behind 点击互斥。

---

### Phase E — 收藏 / 历史列表 + 下载面板

| 步骤 | 改动 | 文件 |
|------|------|------|
| E1 | 右键菜单去重：`ShowListItemContextMenu(listBox, e, deleteHeader, deleteAction)` | `ControlWindow.xaml.cs` |
| E2 | 列表键盘：`Enter` → 打开选中项；`Delete` → 删除（收藏/历史对应命令） | code-behind 或 InputBindings + Command |
| E3 | 搜索框：尽量 `Text="{Binding SearchText, UpdateSourceTrigger=PropertyChanged}"`，去掉 `SearchBox_OnTextChanged`（若 Placeholder 行为冲突则保留最小 code-behind） | `ControlWindow.xaml` |
| E4 | 双击打开：可改为 `InputBinding`/`MouseBinding` 或保留薄 code-behind 调 `OpenItemCommand` | 优先 Command |
| E5 | 下载面板：空态与列表互斥更清晰；进行中数量角标已有；可选「进行中置顶」不强制 | `ControlWindow.xaml` |
| E6 | `BrowserListItemTemplate` 微样式：行高/hover 已由 ListBoxItem 处理则只做间距微调 | `Theme.xaml` |

**验收**：收藏/历史右键一致；键盘 Enter/Delete 可用；搜索仍过滤。

---

### Phase F — 主浮窗标题栏 + 主题控件细节

| 步骤 | 改动 | 文件 |
|------|------|------|
| F1 | 标题栏：统一高度、图标对齐、hover 已有 `TitleBarButton`；可显示简短状态或模式点（克制，避免信息过载） | `MainWindow.xaml` |
| F2 | 标题栏拖拽区域与按钮命中不重叠（现状基本 OK，复查） | `MainWindow.xaml` |
| F3 | `Theme.xaml` 控件细节：按钮/输入框 focus、ProgressBar indeterminate 支持、列表选中对比度 | `Theme.xaml` |
| F4 | 地址栏占位与主题 tertiary 色绑定（随亮暗变） | 已有 brush key |

**验收**：主窗标题栏在亮/暗下可读；控件 focus 可见。

---

### Phase G — 亮暗主题切换

**设计**

```
Resources/
  Theme.xaml          → 控件样式（引用 brush key，不写死颜色）
  Themes/
    ColorsDark.xaml   → 暗色 Color + Brush
    ColorsLight.xaml  → 亮色 Color + Brush
```

或：

```
Theme.xaml            → 仅样式
ThemeDark.xaml        → 颜色字典
ThemeLight.xaml       → 颜色字典
```

**推荐结构**（改动可控）：

1. 把当前 `Theme.xaml` 顶部 Color/Brush 抽到 `Resources/Themes/Dark.xaml`
2. 新增 `Resources/Themes/Light.xaml`（GitHub 浅色系：白底、深字、蓝强调）
3. `Theme.xaml` 只保留 Style/Template/DataTemplate，brush 用 `DynamicResource`
4. `App.xaml` 合并：`Theme.xaml` + 默认 `Dark.xaml`
5. 运行时切换：替换 MergedDictionaries 中颜色字典；**样式中颜色引用改为 `DynamicResource`**

**关键技术点**

- 现有大量 `StaticResource` 引用 brush → 需要把**会随主题变的** brush 引用改为 `DynamicResource`，否则切换无效
- 工作量集中在 `Theme.xaml` / 各窗口 XAML 的 brush 引用
- 策略：**Theme.xaml 内 Style 的 brush 改为 DynamicResource**；窗口级若直接写 `StaticResource BackgroundPrimary` 也要改

**设置面板 UI**

- 在浮窗设置面板内增加一行：`主题` + 切换按钮/分段（暗色 | 亮色）
- 绑定 `ThemeMode` / `ToggleThemeCommand` 或 `SetThemeCommand`
- 持久化到 `AppSettings.ThemeMode`

**服务**

- 新增 `Services/ThemeService.cs`（或 `App` 内静态方法）：`Apply(ThemeMode mode)`
- 启动时在 `App.OnStartup` 或 `MainWindow` 加载设置后应用

**验收**：设置里切换立即生效（控制窗 + 主窗 + MessageBox）；重启后保持。

---

### Phase H — i18n

**方案（WPF 实用、低依赖）**

```
Resources/i18n/
  Strings.zh-CN.xaml   <!-- ResourceDictionary: x:String keys -->
  Strings.en-US.xaml
```

或 `.resx`：`Resources/Strings.resx` + `Strings.en-US.resx`  
**推荐 XAML ResourceDictionary 字符串表**：切换方式与主题一致，且无需 WinForms resx 工具链。

**步骤**

| 步骤 | 改动 |
|------|------|
| H1 | 抽取 `ControlWindow` / `MainWindow` / `ThemedMessageBox` / Toast 文案 / `DownloadItem.StateText` 等用户可见字符串为键 |
| H2 | 中英文两套字典 |
| H3 | `LocalizationService`：`Apply(language)` 替换字符串字典；`AppSettings.Language` 持久化 |
| H4 | 设置面板：语言切换（中文 / English），放在主题切换附近 |
| H5 | VM 中硬编码中文（Toast、ModeText、空态）改为查资源或绑定动态字符串 |
| H6 | `DownloadItem.StateText` 等模型层文案：改为资源查找或由 VM 包装显示文本，避免模型写死中文 |

**范围控制（本轮）**

- **必须**：控制窗可见 UI、设置/下载/列表空态、标题栏按钮 ToolTip、主题/语言标签、常见 Toast
- **可延后**：日志、异常细节、极低频 MessageBox

**验收**：切到 English 后主界面文案切换；重启保持；切回中文正常。

---

### Phase I — MVVM 补齐（与上列穿插，不单独大拆）

本轮不做 ViewModel 文件级大拆分，只做：

| 项 | 说明 |
|----|------|
| 面板互斥/切换 | 全在 VM |
| 主题/语言 | Command + Service，视图只绑定 |
| 列表打开/删除 | 尽量 Command |
| 右键菜单 | 可保留 code-behind 构建 Menu（WPF 常见），但动作走 Command/接口 |
| 地址栏文本同步 | 保持「无焦点时刷新」的薄视图逻辑（合理例外） |
| 快捷键录制 | 继续 PreviewKeyDown 在视图（输入捕获合理例外） |

**不做**：`FavoritesViewModel` / `DownloadPanelViewModel` 拆分（可列为后续）。

---

## 3. 建议实施顺序（依赖）

```
A 清理废弃
  → B 配置（历史 200 + Settings 字段）
    → C 加载态/进度条/前进后退灰态
      → D 面板动画 + 互斥 VM
        → E 列表/下载交互
          → F 标题栏 + 控件细节
            → G 主题拆分与切换（DynamicResource 改造）
              → H i18n（可与 G 部分并行，但建议 G 后，避免双改 XAML）
```

**可合并批次（若一次 PR）**

1. **Batch 1**：A + B + C + D + E + F（体验主线，无主题/i18n）
2. **Batch 2**：G 主题
3. **Batch 3**：H i18n

推荐分 2–3 个提交，降低回归面。

---

## 4. 关键文件清单

| 操作 | 路径 |
|------|------|
| 改 | `Constants/AppConfig.cs` |
| 改 | `Models/AppSettings.cs` |
| 改 | `Services/SettingsService.cs`（若需显式默认） |
| 改 | `Windows/IControlBrowser.cs` |
| 改 | `MainWindow.xaml` / `MainWindow.xaml.cs` |
| 改 | `Windows/ControlWindow.xaml` / `.xaml.cs` |
| 改 | `ViewModels/ControlWindowViewModel.cs` |
| 改 | `Resources/Theme.xaml` |
| 改 | `App.xaml` / `App.xaml.cs` |
| 改 | `Models/DownloadItem.cs`（StateText i18n 时） |
| 改 | `Windows/ThemedMessageBox.xaml`（文案/动态资源） |
| 新增 | `Resources/Themes/Dark.xaml` |
| 新增 | `Resources/Themes/Light.xaml` |
| 新增 | `Services/ThemeService.cs` |
| 新增 | `Resources/i18n/Strings.zh-CN.xaml` |
| 新增 | `Resources/i18n/Strings.en-US.xaml` |
| 新增 | `Services/LocalizationService.cs` |
| 可选新增 | `Services/AnimatedPanelBehavior.cs` 或 `Helpers/PanelAnimation.cs` |

---

## 5. 风险与缓解

| 风险 | 缓解 |
|------|------|
| `StaticResource` → `DynamicResource` 改漏导致切换主题部分控件不变 | 按 brush key 全局检索；手动切主题肉眼验收主路径 |
| 透明主窗 + 亮色标题栏对比度 | Light 主题单独调 `BackgroundTertiary` / 标题栏色 |
| i18n 漏翻导致混语 | 先抽控制窗高频字符串；Toast 用键 |
| 历史上限提高后 JSON 变大 | 200 条可接受；已有原子写 |
| 动画与布局抖动 | 用 MaxHeight 动画 + ClipToBounds；列表区 `*` 吃剩余空间 |
| 删除 `ClearHistory` 后磁盘历史只能条数淘汰 | 符合用户「不要清空历史功能」 |

---

## 6. 验收清单（总）

- [ ] 编译 0 error
- [ ] 无主页/帮助/清空历史入口与死命令
- [ ] 历史可积累到 200
- [ ] 导航中地址栏细进度条显示/隐藏正确
- [ ] 后退/前进灰态正确
- [ ] 设置/下载互斥 + 展开动画
- [ ] 收藏/历史：双击、Enter、Delete、右键打开/复制/删除
- [ ] 下载角标与取消/打开/清除已结束仍可用
- [ ] 主浮窗标题栏观感正常
- [x] 设置内切换亮/暗，两窗 + 对话框一致，重启保持
- [x] 设置内切换中/英，可见文案切换，重启保持
- [ ] 不透明度/缩放/快捷键录制回归正常

---

## 7. 请你确认的决策点

请确认或修正以下默认选择后，我再按计划改代码：

1. **面板动画**：采用 **方案 1**（保留顶栏按钮 + 内容区高度/透明度动画），而不是可见的 Expander 标题行。  
2. **主题选项**：先做 **Dark / Light** 两档（不做 System 跟随），可吗？  
3. **i18n 语言**：先做 **zh-CN / en-US**，设置里切换。  
4. **提交策略**：倾向 **Batch 1 → 2 → 3** 分批落地；若你希望一次全做也可以。  
5. **`HistoryService.ClearAllAsync`**：服务层保留、仅删 UI/接口，可吗？

---

## 8. 确认后执行方式

你回复例如：

- `确认，按计划执行`  
- 或指出修改项：`主题加 System` / `动画用 Expander` / `先做 Batch1 不做 i18n` 等  

确认后严格按 Phase 顺序实现，每批自检编译与关键交互。
