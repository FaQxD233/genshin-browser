# 计划：浮窗提示、地址栏联想、控件改版与刻晴应用图标

> 状态：已实施，待你在真实环境验收（2026-10-21；实施记录与偏差见第 8 节）
> 日期：2026-10-21
> 来源：2026-10-21 前端功能 / 美观性建议，用户逐项拍板（见第 0 节）
> 流程：本文件确认后再改代码；按第 3 节 Phase 顺序实施，每批自检编译与测试

---

## 0. 需求边界

### 要做

| 编号 | 项 | 一句话 |
|------|----|--------|
| 一.1 | 浮窗内轻量提示（OSD） | 浮窗模式下按热键播放/暂停/快进退时，在主窗显示短提示 |
| 二.5 | 地址栏联想 | 输入时从收藏 / 历史下拉候选 |
| A.1 | 应用图标 | 刻晴 Q 版表情包 + 多尺寸 ICO |
| A.4 | 去掉按钮悬停放大 | 改为遮罩淡入淡出 |
| A.6 | 图标字体回退链 | Segoe Fluent Icons 优先，回退 Segoe MDL2 Assets |
| B.2 | 控件方案重做 | 按语义分为分段 / 开关 / 键帽三类（方案见 2.6，待确认） |
| B.3 | 布尔项用开关 | 鼠标穿透改为 ToggleSwitch |

### Not planned（用户 2026-10-21 决定不做）

| 编号 | 项 |
|------|----|
| 一.2 | 一键静音 |
| 一.3 | 浮窗里调不透明度 |
| 一.4 | 倍速 |
| 二.6 | 列表标题去站点后缀 |
| 二.7 | 历史按日期分组 |
| 二.8 | 列表项站点图标 / 视频封面 |
| 二.9 | 布局预设 |
| A.2 | 控制窗标题栏染色 / Mica |
| A.3 | 对比度修正（滚动条滑块、TextTertiary、亮色悬停与选中） |
| A.5 | 贴角箭头与 −/+ 改为图形 |
| A.7 | 空状态图标 |
| B.1 | 字号 / 间距 token |
| B.4 | 不透明度滑块 |
| B.5 | 面板展开动画按实际高度 |
| C.1 | 控制窗信息架构（抽屉 / 独立页） |
| C.2 | 「浏览｜浮窗」换成主按钮 |
| C.3 | 浮窗标题栏悬浮叠加 |
| C.4 | 主窗圆角与描边 |

2026-07-11 计划已排除、本轮继续不做：主页按钮、快捷键帮助入口、清空历史 UI、系统托盘、收藏分组 / 拖拽排序。网页「另存为」进下载面板见 `.claude/CLAUDE.md` 已知问题。

---

## 1. 现状结论（调研摘要）

- 浮窗模式下控制窗被 `Hide()`（`MainWindow.UpdateControlWindowVisibility`），而状态反馈只走控制窗 Toast（`ControlWindowViewModel.RefreshFromBrowser` → `ShowToast`）：浮窗里按 K / `;` / `'` 没有任何可见反馈。
- 主窗已有底部居中 Toast（`ModeToastBorder` + `ShowMainModeToast`），目前只用于模式切换；叠在 `WebView2CompositionControl` 上方能正常显示。
- 收藏 / 历史真源是 `IControlBrowser.FavoriteEntries` / `HistoryEntries`（最近的在前，上限 100 / 200）。VM 的 `_allFavoriteItems` / `_allHistoryItems` 只同步当前可见 Tab，隐藏 Tab 可能过期，联想不能用它们。
- 列表搜索规则：标题或 URL 包含查询串，忽略大小写（`ControlWindowViewModel.FilterItems`）。
- `ControlWindow_OnPreviewMouseDown` 在点击非输入区域时清掉输入焦点；Popup 内容的路由事件同样经过窗口，联想下拉必须加豁免。
- `ModernButton` 悬停缩放 1.03 / 按下 0.98，背景与边框颜色是瞬切触发器；`IconButton`、`SegmentButton` 都继承它。
- `SegmentButton` 共 18 处：顶部模式 2（内联派生样式）、鼠标穿透 2、主题 3、语言 2、热键录制 5、热键范围 3，另有 1 处注释提及；选中态 / 录制态都靠 `Tag` + DataTrigger。
- `ModeFloatingAccentSoft` 只被顶部「浮窗」分段使用；控制窗只在浏览模式可见，这个选中态实际从未显示。`ModeFloatingAccent` 仍被 `FlashModeBorder` 使用，保留。
- 图标字体：项目用到 25 个码位，已逐个渲染对比，Segoe Fluent Icons 全部存在且含义一致；OSD 需要的 E769 暂停、EB9E 快退、EB9D 快进两套字体都有。Win10 默认不带 Fluent 字体，且不允许随应用分发。
- 仓库没有任何 `.ico`，csproj 无 `ApplicationIcon`；WPF SDK 不会默认把 `.ico` 收进 Resource，需要显式 `<Resource Include>`。

---

## 2. 设计方案

### 2.1 一.1 浮窗 OSD

- 复用主窗底部 Toast，并重命名 `ModeToastBorder` / `ModeToastText` / `ShowMainModeToast` / `_modeToastTimer` → `MainToast*` / `ShowMainToast`（含义从「模式提示」扩为「主窗提示」）。文字左侧加一个图标位，无图标时折叠。
- 显示条件：浮窗模式，不在 F7 隐藏中，窗口未最小化。浏览模式不显示（控制窗 Toast 已覆盖）。
- 结果映射写在 `ToggleVideoPlaybackAsync` / `SeekVideoAsync` 的 switch 内，统一走一个小 helper；状态消息（控制窗 Toast）保持不变。

| 脚本结果 | 图标 | 文案 | 时长 |
|----------|------|------|------|
| play | E768 | 播放中 | 0.9 s |
| pause | E769 | 已暂停 | 0.9 s |
| seek ok | EB9E / EB9D | −5 秒 / +5 秒 | 0.9 s |
| no-video | E7BA（警告色） | 当前页面没有可控制的视频。 | 2 s |
| play-blocked | E7BA（警告色） | 浏览器拦截了自动播放…… | 2 s |
| at-edge | E946 | 已到视频开头或末尾。 | 1.2 s |
| typing / play-interrupted / 其它 | 不提示 | — | — |

- 新增 i18n 键 4 个（中英各一）：`Osd.Play`、`Osd.Pause`、`Osd.SeekBackward`（`−{0} 秒`）、`Osd.SeekForward`（`+{0} 秒`）；警告类复用现有 `Status.*`。

### 2.2 二.5 地址栏联想

- VM：`AddressSuggestions`（ObservableCollection）+ `IsAddressSuggestionsOpen`；`UpdateAddressSuggestions(text)`、`CloseAddressSuggestions()`、`OpenAddressSuggestion(item)`。候选项为不可变 record：标题、简短网址、URL、是否收藏。
- 候选构建抽成可测的静态方法：数据源取 `FavoriteEntries` + `HistoryEntries`；收藏在前，历史按最近访问；URL 去重（忽略大小写）；最多 8 条；空白输入或与当前地址相同时返回空。
- 匹配规则与列表搜索一致，抽成 `EntryText.MatchesQuery(title, url, query)`，`FilterItems` 同步改用它。
- 视图交互（code-behind，属输入捕获类例外）：
  - 地址栏有键盘焦点且是用户输入时才更新候选；程序回填（含打开候选后回填 URL）用抑制标志跳过。
  - ↓ / ↑ 移动高亮。
  - Enter：有高亮时打开该项，否则保持现有行为（按输入跳转或 B 站搜索）。
  - Esc：第一次关下拉，第二次才恢复地址（现有行为）。
  - Tab、失焦、控制窗移动、打开候选后都关闭下拉。
  - 单击候选即打开；`ControlWindow_OnPreviewMouseDown` 对 Popup 内的点击豁免。
  - 打开候选后，地址栏回填该 URL 并全选。
- 样式：Popup（`AllowsTransparency`）卡片，`BackgroundTertiary` 底，`BorderColor` 1px 边框，6px 圆角，宽度与地址框一致，位于地址框下方 4px。每行是收藏星标 E734 或历史时钟 E121，加标题（12px）和简短网址（11px，`TextTertiary`）；高亮沿用 `ModernListBoxItem` 选中态。列表与项 `Focusable=False`，不抢地址栏焦点。

### 2.3 A.1 刻晴 Q 版应用图标

- 来源：自绘 SVG 首稿与按官方形象重画的稿都被否（「一点也不像」「丑爆了」）。最终按用户要求，直接用用户提供的刻晴 Q 版表情包：在浏览器里按原图渲染，抠成透明底（四周洪泛 + 边缘按邻近前景色反算透明度，放在品红、绿色底上检查都没有灰边）。
- 源图：
  - `Resources/AppIcon/keqing-sticker.png`：512×512 完整贴纸。
  - `Resources/AppIcon/keqing-head.png`：380×380 头部特写，避开左侧星星，底边和右边做短渐隐。
  - 两张源图的透明像素都填成描边深色，缩放时不会泛出浅色毛边。
- 产物 `Resources/AppIcon/app.ico`，九个 PNG 帧（Win10+ 支持 PNG 帧），提交进仓库。256 / 128 / 64 / 48 用完整贴纸；40 / 32 / 24 / 20 / 16 用头部特写，因为整张贴纸缩到 32px 以下时脸会糊成一团。
- 生成脚本 `release/build-app-icon.ps1`：用 System.Drawing 高质量缩放两张源图并打包 ICO，无第三方依赖，Windows PowerShell 5.1 与 PowerShell 7 都能运行。换图后重跑即可。
- 接入：
  - csproj 加 `<ApplicationIcon>Resources\AppIcon\app.ico</ApplicationIcon>`，覆盖 exe、任务栏、Alt+Tab 和控制窗原生标题栏。
  - 主窗自绘标题栏左侧加 16px 图标，需要 `<Resource Include>`（见决策点 3）。

### 2.4 A.4 按钮悬停改为遮罩渐变

- `ModernButton` 模板：删除 Style 与模板里的 `ScaleTransform`，以及 MouseOver / Pressed 的缩放动画。
- 在底色与内容之间加一层 `StateOverlay`（`TextPrimary` 色，圆角与按钮一致）。视觉状态：MouseOver 0.08（120 ms）、Pressed 0.14（60 ms）、Normal 0（150 ms）。
- 删除悬停换 `BorderColorStrong` 边框、按下换 `BorderColor` 底色这两个瞬切触发器；`IsEnabled=False` 的 0.4 不透明度保留。
- 影响：所有继承 `ModernButton` 的按钮统一动效；亮色主题下悬停从淡蓝变为中性灰（预期内）。标题栏按钮、Tab、列表项、菜单项不在本轮范围。

### 2.5 A.6 图标字体

- `Mdl2IconText` 的 FontFamily 改为 `Segoe Fluent Icons, Segoe MDL2 Assets`，WPF 会逐字回退。样式键名不改，避免牵动约 30 处引用。

### 2.6 B.2 控件方案（已确认采用推荐方案）

问题有两面：
- 现状每个选项都是独立描边按钮，看不出是同一组，选中靠蓝框，整片都是框。
- 全部改胶囊又和全局 6px 圆角体系冲突，整体偏圆偏软。

推荐按语义分成三类，每类用一种控件：

| 语义 | 控件 | 用在 | 形态 |
|------|------|------|------|
| 互斥单选（2–3 项） | 分段控件 | 顶部浏览｜浮窗、主题、语言、热键范围 | 圆角矩形轨道（外圆角 8、内边距 2、1px 边框）；选中项凸起底色 + 底部 2px 强调色线；未选中项无边框、次要文字色；各项按内容宽度（最小 48），整组左对齐 |
| 开关 | ToggleSwitch | 鼠标穿透 | 34×18 轨道 + 10px 圆钮，右侧显示「开启 / 关闭」，圆钮 120 ms 滑动 |
| 快捷键 | 键帽按钮 | 5 个热键录制按钮 | 浅底 + 1px 边框，底边加厚到 2px，像一颗按键；按内容宽度左对齐，最小宽 44；录制中强调色描边 + 选中底色 |
| 动作 | 现有 ModernButton | 其余按钮 | 不变（带 2.4 的遮罩动效） |

- 选中线用「仅底边 2px + 6px 圆角」画出跟随圆角的弧线（同 Win11 输入框聚焦线），不需要新模板。未选中项保留透明底边，避免选中时内容跳 2px。
- 备选：
  - B 下拉框：Win11 设置页的做法，最省空间；但多一次点击，且要完整重写 ComboBox 模板。
  - C 单选圆点：语义最准；但横向最占地，观感偏表单。
  - 两种备选的草图已在对话中给出。
- 实现：
  - Theme.xaml 新增 `SegmentedTrack`（Border）、`SegmentedItem` 和 `KeyCapButton`（均 BasedOn `ModernButton`，继承遮罩动效），以及 `ToggleSwitch`（CheckBox 模板）。
  - 删除 `SegmentButton`，以及顶部模式分段的两个内联派生样式。
- 颜色 token（Dark / Light 各一份）：
  - 新增 `SegmentTrackBackground`（#161B22 / #EAEFF3）、`SegmentThumbBackground`（#2D333B / #FFFFFF）。
  - 原计划的 `KeyCapBottomBorder` 没有加：WPF 的 Border 不能逐边设色，键帽改为底边与四周同色、只加厚。
  - 删除不再使用的 `ModeFloatingAccentSoft`。
- VM 不变：仍是 Command + `Tag` 绑定选中态。

### 2.7 B.3 鼠标穿透开关

- XAML：两个分段按钮换成 `CheckBox`（`ToggleSwitch` 样式，`IsChecked` 绑定 `IsClickThroughOn`），ToolTip 不变。
- VM：`IsClickThroughOn` 改为可写，setter 调现有 `SetFloatingClickThrough`（含 Toast）；删除 `IsClickThroughOff`、`SetClickThroughOnCommand`、`SetClickThroughOffCommand`。
- i18n：新增通用 `Toggle.On` / `Toggle.Off`（开启 / 关闭，On / Off）；删除不再使用的 `Settings.ClickThroughOn` / `Settings.ClickThroughOff`。

---

## 3. 实施顺序

1. Phase 1 样式底座：A.6 → A.4（Theme.xaml）。
2. Phase 2 控件：B.2 + B.3（Theme.xaml、Dark / Light.xaml、ControlWindow.xaml、VM、i18n）。
3. Phase 3 OSD：一.1（MainWindow.xaml / .cs、i18n）。
4. Phase 4 联想：二.5（EntryText、VM、ControlWindow.xaml / .cs、单测）。
5. Phase 5 图标：A.1（源图、脚本、ICO、csproj、MainWindow.xaml）。
6. 每个 Phase 都要 `dotnet build` 0 error、`dotnet test` 全过；全部完成后按第 6 节验收。

---

## 4. 关键文件

| 操作 | 文件 |
|------|------|
| 改 | `Resources/Theme.xaml` |
| 改 | `Resources/Themes/Dark.xaml`、`Resources/Themes/Light.xaml` |
| 改 | `Resources/i18n/Strings.zh-CN.xaml`、`Resources/i18n/Strings.en-US.xaml` |
| 改 | `Windows/ControlWindow.xaml`、`Windows/ControlWindow.xaml.cs` |
| 改 | `ViewModels/ControlWindowViewModel.cs` |
| 改 | `MainWindow.xaml`、`MainWindow.xaml.cs` |
| 改 | `Utils/EntryText.cs`、`Constants/AppConfig.cs` |
| 改 | `GenshinBrowser.csproj` |
| 改 | `README.md`（功能列表加 OSD、地址栏联想；写明图标来源与重新生成方法） |
| 新增 | `ViewModels/AddressSuggestion.cs` |
| 新增 | `Resources/AppIcon/keqing-sticker.png`、`Resources/AppIcon/keqing-head.png`、`Resources/AppIcon/app.ico` |
| 新增 | `release/build-app-icon.ps1` |
| 新增 | `tests/GenshinBrowser.Tests/AddressSuggestionTests.cs`（联想匹配与候选构建） |

---

## 5. 风险与缓解

| 风险 | 缓解 |
|------|------|
| Fluent 字形度量与 MDL2 略有差异，个别图标显大或偏移 | 改完逐个检查主窗标题栏、控制窗顶栏、下载项、Toast |
| 去掉缩放后按钮反馈变弱 | 遮罩分 0.08 / 0.14 两档，验收时按需微调 |
| 联想 Popup 抢焦点、点击被「点空白清焦点」吃掉、窗口移动后悬空 | 列表不可聚焦；PreviewMouseDown 豁免；LocationChanged 或失焦即关闭 |
| 中文输入法组字时方向键 / 回车被 IME 消费 | 只处理 Up / Down / Enter / Escape，`ImeProcessed` 一律放行 |
| OSD 底部居中会短暂盖住字幕 | 普通提示只显示 0.9 s；需要时改位置（决策点 4） |
| ICO 用 PNG 帧 | Win Vista+ 均支持，目标系统为 Win10+ |
| ToggleSwitch 首次加载会播一次滑动动画 | 设置面板默认折叠，看不到 |

---

## 6. 验收清单

已勾选的是我已验证的（编译、单测、离屏渲染）；未勾选的需要你在真实环境验收。

- [x] 编译 0 警告 0 错误，测试 50/50 通过（新增 8 个联想测试）
- [ ] 浮窗模式：K 显示「播放中 / 已暂停」；`;` `'` 显示「−5 秒 / +5 秒」；无视频页显示警告；F7 隐藏中按键不显示；浏览模式不显示
- [x] 联想候选卡片：收藏星标 / 历史时钟、高亮样式，亮暗主题均正常（离屏渲染）
- [ ] 地址栏实操：输入关键词出现候选；↑↓ 选择、Enter 打开、Esc 两段式、单击打开；失焦或移动窗口时关闭；中文输入法正常；原有回车跳转与 B 站搜索不受影响
- [ ] 所有 ModernButton 系按钮悬停为渐变遮罩、无缩放；禁用态正常（悬停需要鼠标，离屏渲染测不了）
- [x] Win11 上图标为 Fluent 风格，含义与原来一致（25 个码位逐个渲染对比）
- [x] 分段控件：模式、主题、语言、热键范围选中态正确，亮暗主题都清楚；中英文在 560 与 400 宽下都不截断
- [x] 鼠标穿透开关：开 / 关两态与 Toast 显示正常（离屏渲染）
- [ ] 鼠标穿透开关：重启后保持；浮窗穿透行为不变
- [x] 热键键帽：正常显示，录制中高亮（离屏渲染）
- [x] exe 内嵌刻晴图标（从生成的 exe 读回确认）
- [ ] 任务栏、Alt+Tab、主窗标题栏、控制窗标题栏显示刻晴图标；F7 隐藏后能从任务栏认出并恢复

---

## 7. 决策记录

1. 图标：两版自绘稿被否，改用用户提供的表情包（见 2.3）。
2. B.2：采用推荐方案（分段 + 开关 + 键帽）。
3. 主窗自绘标题栏左侧显示 16px 图标（用头部特写）。
4. OSD 沿用底部居中。
5. 联想最多 8 条、收藏优先。
6. 验收时启动程序实测：已同意，但做不到，原因见第 8 节。

---

## 8. 实施记录（2026-10-21）

- 与计划的偏差：
  - 主题、语言从同一行拆成两行：分段控件按内容宽度排，同一行放两组在 400px 最小宽度下会溢出。设置面板滚动区上限相应从 280 调到 312，展开动画目标从 340 调到 372，否则会出现滚动条。
  - 分段项用横向 StackPanel（最小宽 48），不用 UniformGrid：英文长标签等宽后，在最小宽度下会溢出。
  - OSD「播放」文案改为「播放中」，与英文 Playing 对齐。
  - 顺带修复：主窗提示连按时每次都从透明度 0 淡入、会一闪一闪；改为已在显示（含淡出途中）时从当前值续播。
- 验证方式：
  - 编译与单测。
  - 离屏渲染：用真实 XAML、样式和假数据驱动控制窗，覆盖暗 / 亮主题、中 / 英、560 / 400 宽、开关两态、键帽录制态、联想卡片。不弹窗，不碰本机数据。
- 没能实测真实程序：`%LocalAppData%\GenshinBrowser` 是指向 D 盘的目录符号链接。Claude 桌面版是 MSIX 打包应用，从它里面启动的进程都在 Windows 的 AppData 虚拟化里，这层虚拟化里放在 AppData 下、指向其它盘的链接打不开（报「名称含有至少一个装入点……」），关闭命令沙箱也一样。程序在代码里固定用这个路径，从这边启动会读不到设置和 WebView 数据，所以没有启动。第 6 节未勾选的项需要你手动验收。
- 观察到但不在本轮范围：400px 最小宽度下，「显示」行（不透明度与缩放并排）和英文的设置标题行本来就放不下，改动前已存在。
