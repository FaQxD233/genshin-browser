# 计划：Claude 配色、「凸起块 + 投影」选中态与视频铺满按钮

> 状态：已实施，待你在真实环境验收（2026-10-23；实施记录见第 6 节）
> 日期：2026-10-23
> 来源：2026-10-22 ~ 23 主题讨论。用户定了：蓝色换 Claude 橙 #D97757（两套主题都用，不要更亮的 #E58261）；灰色换 Claude 暖灰；选中态回到早期「橙字 + 无底线的白（暗色为深）块」，并在块下加大厂常用的投影
> 流程：本文件确认后再改代码；按第 3 节顺序实施，每步自检编译与测试。T8 视频铺满按钮是同一批实施的小改动（方案此前在对话里确认）

---

## 0. 需求边界

### 要做

| 编号 | 项 | 一句话 |
|------|----|--------|
| T1 | 配色 | Dark / Light 两套 token 换成 Claude 暖灰 + Claude 橙 |
| T2 | 分段控件选中态 | 去掉底边强调线；选中 = 凸起块（发丝描边 + 投影）+ 橙字 |
| T3 | 键帽 | 底边 2px 改四边等粗 1px，与选中块同一套投影；录制中只换橙色描边和橙字，不再填底色 |
| T4 | 投影机制 | 新增附加属性 `Elevation.Shadow`，`ModernButton` 模板垫一层投影层，不复制模板 |
| T5 | 工具栏展开按钮 | 设置 / 按键 / 下载 三个按钮各自复制的内联样式合并成一个 |
| T6 | 像素对齐 | 控制窗、主窗开 `UseLayoutRounding`，125% 缩放下 1px 边框不再发虚 |
| T7 | 模式闪框 | 浏览 = 橙，浮窗 = 蓝（原浮窗琥珀色与橙太近） |
| T8 | 视频铺满按钮 | 主窗标题栏、最小化左边，两种模式都有；B 站点播放器自己的全屏，其它网站对视频元素全屏；再点或 Esc 退出 |

### Not planned（用户 2026-10-23 决定不做）

| 项 | 一句话 |
|----|--------|
| OSD 带进度 | 快进 / 暂停时底部提示加「12:34 / 25:00」和细进度条 |
| 上一 P / 下一 P | 标题栏按钮或热键调用播放器的「下一个」 |
| A-B 循环 | 标两个时间点循环播放，标题栏显示循环状态 |
| 穿透时临时可点 | 开着鼠标穿透时按住一个键临时恢复点击 |
| 「正在播放」卡片 | 控制窗顶部显示标题、UP 主、进度和遥控按钮 |
| 元素主题色 | 设置里用色块选七元素配色 |

### 不做（本轮）

- 普通按钮不加投影：试过整套实底按钮都带极淡投影，变化很小，反而让选中块不那么突出。想要的话改动很小：基础按钮样式加一个 setter，两套主题各加一个更淡的投影 token。
- Toast、地址栏联想下拉、右键菜单这些浮层不加投影。
- 「收藏夹 / 浏览记录」标签页底部的橙色线保留：它是无圆角的整宽直线，不会出现月牙。
- 成功 / 警告 / 危险色、关闭按钮按下色不变。
- 列表选中项左侧色条、开关、进度条、输入框焦点框、下载角标只跟着强调色变橙，样式不动。

---

## 1. 现状结论（调研摘要）

- 颜色全部在 `Resources/Themes/Dark.xaml`、`Light.xaml`（GitHub 风格冷灰 + 蓝），样式与窗口都用 `DynamicResource` 引用。`ThemeService` 运行时原位替换这两个字典之一。
- C# 里的硬编码颜色只有两处：`MainWindow.FlashModeBorder` 找不到 token 时的回退色（琥珀 / 蓝），`Placeholder` 未设前景时的回退灰 `#6E7681`（两处 XAML 都设了前景，基本走不到）。
- 「怪」的来源：`SegmentedItem` 用 `BorderThickness="0,0,0,2"` + 6px 圆角画强调线，WPF 会把只有底边有厚度的边框沿圆角画成两头尖的月牙；`KeyCapButton` 的 `1,1,1,2` 同理。
- `ModernButton` 模板根是 `ButtonChrome`（Border），VSM 挂在它上面；`IconButton`、`SegmentedItem`、`KeyCapButton` 都继承它。
- 工具栏三个展开按钮（`SettingsButton` / `KeyBindingsButton` / `DownloadsButton`）各写了一份几乎相同的内联 `Style`，只差绑定的属性名。
- 两个窗口都没开 `UseLayoutRounding`，程序是 PerMonitorV2；125% 缩放下 1px = 1.25 物理像素，边框发虚。
- `SelectionBrush` 只剩输入框选区在用（键帽录制底色本轮去掉）；`TextBox` 以默认 `SelectionOpacity` 0.4 叠加。
- `ListDivider` token 在 XAML / C# 里都没有引用，本轮只改色值，不处理。

---

## 2. 设计方案

### 2.1 配色（T1、T7）

色值取自 claude.ai 的 CSS：暗色用经典暗色暖灰（最新暗色底接近纯黑，不显暖，不用），亮色用当前奶油色；橙色两套都用 clay `#D97757`。

| token | 暗色 现在 → 计划 | 亮色 现在 → 计划 |
|-------|------------------|------------------|
| BackgroundPrimary（窗口） | #0F1419 → **#262624** | #FFFFFF → **#F9F9F7** |
| BackgroundSecondary（面板） | #1C2128 → **#30302E** | #F6F8FA → **#F3F3F0** |
| BackgroundTertiary（按钮 / Toast） | #2D333B → **#383835** | #EAEFF3 → **#FFFFFF** |
| BackgroundHover | #373E47 → **#454442** | #DDF4FF → **#E7E6E1** |
| Border | #444C56 → **#4B4A47** | #D0D7DE → **#D2D1C7** |
| BorderStrong | #8B949E → **#7B7974** | #8C959F → **#A5A49A** |
| AccentPrimary | #58A6FF → **#D97757** | #0969DA → **#D97757** |
| ModeBrowsingAccent | #58A6FF → **#D97757** | #0969DA → **#D97757** |
| ModeFloatingAccent | #F0A020 → **#58A6FF** | #BF8700 → **#0969DA** |
| SegmentTrack | #161B22 → **#1F1E1D** | #EAEFF3 → **#EAE9E4** |
| SegmentThumb | #2D333B → **#3D3D3A** | #FFFFFF → #FFFFFF |
| SegmentTrackBorder（新） | 原用 Border → **#42413E** | 原用 Border → **#DEDDD6** |
| ListDivider | #21262D → #2C2C2A | #D8DEE4 → #E7E6E1 |
| TextPrimary | #E6EDF3 → **#FAF9F5** | #1F2328 → **#141413** |
| TextSecondary | #8B949E → **#C2C0B6** | #656D76 → **#6D6B67** |
| TextTertiary | #6E7681 → **#9C9A92** | #8C959F → **#97958D** |
| Selection（输入框选区） | #264F78 → **#D97757** | #B6E3FF → **#D97757** |

对比度（WCAG）：选中橙字在白块上 3.1:1、在暗色选中块上 3.5:1，低于正文 4.5:1，按之前的讨论接受。未选中字在轨道上：亮色 4.4:1，暗色 9.1:1。

### 2.2 凸起块：投影 + 发丝描边（T2、T3、T4）

做法参照 iOS / macOS 的分段控件：选中滑块是白色，带极淡描边和柔和投影，不画下划线。暗色下投影几乎看不见，改用「表面更亮 + 顶边细高光」表现层级（Material 暗色主题也是越高的表面越亮）。不用 Windows 11 那种底边加深的描边：它在小按钮上还是一条底线，正是这次要去掉的东西。

| token（新） | 暗色 | 亮色 |
|-------------|------|------|
| RaisedBorderBrush | 竖向渐变：顶边 18% 白 → 45% 处 5% 白 → 底边透明（只有顶边亮，底边不出线） | 7% 黑发丝描边 `#12000000` |
| RaisedShadow（DropShadowEffect） | 黑，正下方，距离 1、模糊 4、不透明度 0.55 | 黑，正下方，距离 2、模糊 8、不透明度 0.2 |

亮色投影试了三档（1.5/6/0.14、2/8/0.2、1/3/0.22），选中间这档：最像系统分段控件，又不会溢出轨道。

**机制（不复制按钮模板）**：

- 新增 `Services/Elevation.cs`：附加属性 `Elevation.Shadow`（类型 `Effect`，默认 null）。WPF 常见做法，MaterialDesignInXaml 也是用附加属性控制阴影。
- `ModernButton` 模板根改成 `Grid`，在 `ButtonChrome` 下面垫一层同形状、同底色的投影层，`Effect="{TemplateBinding services:Elevation.Shadow}"`。默认 null 时什么都不画，其余按钮外观不变。
- 投影不直接加在按钮上：Effect 会让整棵子树（含文字）走位图效果管线，文字失去 ClearType，还会带上文字阴影。
- VSM 必须挂在模板根上，随根一起移到 `Grid`；禁用态的 0.4 不透明度也挪到根上，投影一起变淡。
- 样式里用 `<Setter Property="services:Elevation.Shadow" Value="{DynamicResource RaisedShadow}"/>`；换主题时跟着换（demo 里已验证：运行中暗→亮切换后的截图与直接用亮色渲染逐像素一致）。

### 2.3 分段项（T2）

- `BorderThickness` 改为四边 1px，未选中透明。这样不会出现月牙，切换时也不跳动。
- 选中：底色 `SegmentThumbBackground`，描边 `RaisedBorderBrush`，文字 `AccentPrimary`，投影 `RaisedShadow`。字重不变。
- `SegmentedTrack` 描边改用新 token `SegmentTrackBorderBrush`，比普通边框淡一级，白块更突出。
- 4 处用法（顶部模式、主题、语言、热键范围）都不用改 XAML。

### 2.4 键帽（T3）

- 去掉 `BorderThickness="1,1,1,2"`，继承 `ModernButton` 的四边 1px；加 `RaisedShadow`，看起来像一颗按键。
- 录制中（Tag=True）：描边和文字用 `AccentPrimary`，不再填 `SelectionBrush` 底色。

### 2.5 工具栏展开按钮（T5）

- `Theme.xaml` 新增 `ToolbarToggleButton`（BasedOn `IconButton`，Padding 0）。Tag=True 时底色 `BackgroundHover`，描边 `AccentPrimary`，外观与现在相同（描边随强调色变橙）。
- 三个按钮改为 `Style="{StaticResource ToolbarToggleButton}" Tag="{Binding IsSettingsExpanded}"` 等，删掉三份内联样式（约 30 行）。

### 2.6 像素对齐（T6）

- `ControlWindow.xaml`、`MainWindow.xaml` 根元素加 `UseLayoutRounding="True"`。demo 的计划版就是开着它渲染的。

### 2.7 视频铺满按钮（T8）

- 位置：主窗标题栏，模式按钮与最小化之间。浏览和浮窗共用这条标题栏，浮窗时鼠标移到顶部出现。
- 进入：B 站页面点播放器自己的全屏按钮 `.bpx-player-ctrl-full`（播放器把 `.bpx-player-container` 全屏，弹幕与控制栏都保留）；其它网站对视频元素 `requestFullscreen()`。主窗不处理 `ContainsFullScreenElementChanged` 的窗口化，按 WebView2 默认行为全屏元素只铺满 WebView 区域，不占满屏幕。
- 退出：再点一次或按 Esc（主窗 `PreviewKeyDown` 直接处理），都走 `document.exitFullscreen()`。B 站在播放器容器上监听 `fullscreenchange`，会自己复位按钮状态。
- 状态：订阅 `ContainsFullScreenElementChanged` 缓存是否铺满，图标 E740（铺满）/ E73F（退出）与提示跟着切换；浏览器进程恢复或退出时清零。
- 反馈：铺满 / 退出本身就是画面反馈；找不到视频或页面没响应时，按视频热键的规则提示（浮窗显示主窗 OSD，浏览模式进控制窗 Toast）。
- 脚本复用视频热键的「找视频」片段（拆成 `FindVideoScript`），但不做「正在打字」判断：按钮点击不是页面内按键。
- 最大化按钮原来用的就是 E740 / E73F，改为 Windows 标准标题栏码位 E922 / E923（与已用的最小化 E921、关闭 E8BB 同一套），两个按钮不再同图标。

---

## 3. 实施步骤

1. **T1 / T7 配色**：改 `Dark.xaml`、`Light.xaml`（色值见 2.1，文件头注释改为 Claude 配色），新增 `ColorSegmentTrackBorder` / `SegmentTrackBorderBrush`、`RaisedBorderBrush`、`RaisedShadow`。`MainWindow.FlashModeBorder` 的回退色与注释改为浏览橙 / 浮窗蓝；`Placeholder` 回退灰改为 `#9C9A92`。
2. **T4 机制**：新增 `Services/Elevation.cs`；`Theme.xaml` 加 `xmlns:services`，改 `ModernButton` 模板（2.2）。
3. **T2 / T3**：改 `SegmentedTrack`、`SegmentedItem`、`KeyCapButton` 及其注释（2.3、2.4）。
4. **T5**：`Theme.xaml` 加 `ToolbarToggleButton`；`ControlWindow.xaml` 三个按钮换用它。
5. **T6**：两个窗口加 `UseLayoutRounding="True"`。
6. **T8**：`MainWindow.xaml` 加按钮、改最大化码位；`MainWindow.xaml.cs` 加全屏状态、脚本、Esc；两份语言文件各加 5 条文案。
7. **测试**：新增 `ResourceDictionaryKeyTests`，读取成对的 XAML 源文件（暗 / 亮主题、中 / 英文案），断言 `x:Key` 集合一致，防止以后只在一边加 token 或文案、另一边 `DynamicResource` 静默落空。
8. 自检：编译（`TreatWarningsAsErrors`）、`dotnet test`、`dotnet format --verify-no-changes`；用离屏预览重新渲染真实构建，与 demo 对比。

改动文件：`Resources/Themes/Dark.xaml`、`Resources/Themes/Light.xaml`、`Resources/Theme.xaml`、`Services/Elevation.cs`（新）、`Windows/ControlWindow.xaml`、`MainWindow.xaml`、`MainWindow.xaml.cs`、`Services/Placeholder.cs`、`Resources/i18n/Strings.zh-CN.xaml`、`Resources/i18n/Strings.en-US.xaml`、`tests/GenshinBrowser.Tests/ResourceDictionaryKeyTests.cs`（新）、`README.md`（功能列表加「视频铺满」）。

---

## 4. 验收（需要你在真实环境看）

- 控制窗暗 / 亮 / 跟随系统切换：选中块投影、键帽、轨道描边都跟着换。
- 悬停 / 按下 / 禁用（前进后退无历史时）三种状态正常，禁用时投影一起变淡。
- 热键录制中的键帽是橙描边加橙字。
- 切模式时主窗闪框：浏览橙、浮窗蓝。
- 125% 缩放下 1px 边框清晰、不发虚；主窗标题栏、Toast、浮窗不受影响。
- 视频铺满：B 站视频页点标题栏按钮，视频铺满窗口（不占满屏幕），弹幕与 B 站控制栏还在；再点或按 Esc 退出，B 站播放器按钮状态正确；浮窗模式下鼠标移到顶部能看到按钮；非视频页点击有「没有可控制的视频」提示；其它视频网站（如 YouTube）也能铺满。
- 最大化按钮图标变成方框，最大化后变成两个叠放的方框。

---

## 5. 风险与回退

- `DropShadowEffect` 只加在选中块和键帽上（同屏最多约 9 个），开销可以忽略；普通按钮的投影层 Effect 为 null，不进效果管线。
- `UseLayoutRounding` 会让布局取整到物理像素，个别依赖小数尺寸的地方可能挪动 1px，靠第 3 步第 7 条的渲染对比和第 4 节人工验收兜底。
- 全部是样式和资源改动加一个附加属性类，回退只需还原这几个文件。

---

## 6. 实施记录（2026-10-23）

与计划的偏差：

- 测试由 `ThemeDictionaryTests` 扩成 `ResourceDictionaryKeyTests`，同时校验中英文案键一致（T8 两边各加了 5 条文案）。
- T8 一并把最大化按钮换成标准码位（见 2.7 末条）。
- 三个工具栏按钮去掉与新样式重复的 `Padding="0"`。

自检：

- Release + `TreatWarningsAsErrors` 编译通过；`dotnet test` 52/52 通过；`dotnet format --verify-no-changes` 两个项目均无改动。
- 离屏渲染真实构建：控制窗暗 / 亮两套、设置与按键两个面板，与 demo 逐像素一致（工具栏样式合并后外观不变）。
- B 站（BV1GJ411x7h7 视频页）：脚本 `click()` 播放器的全屏按钮会对 `.bpx-player-container` 同步调用 `requestFullscreen`；播放器监听容器上的 `fullscreenchange`，外部 `exitFullscreen` 后会自己复位。真正的铺满（WebView2 内全屏）在这个环境里跑不了，需要你在程序里实测。

后续调整（用户 2026-10-23 看过后提出，在 `2a84ba0` 之后）：

- 设置、按键绑定面板去掉分组小标题（窗口 / 显示 / 外观 / 快捷键），组间也不额外留空，各行统一 6px；删掉不再使用的 `SettingsSectionTitle` 样式和 4 条文案。
- 设置面板标题「浮窗设置」改为「设置」（面板里也有主题、语言这类全局项），工具栏齿轮的提示同步；英文为 Settings。
- T3 改为键帽不加投影，与普通按钮一样只有四边 1px 描边；投影只留给分段控件的选中块。
- 键盘焦点框替换系统默认虚线框，只在键盘导航时出现：`FocusRing`（外扩 2px、圆角 8，按钮 / 分段项 / 键帽 / 标签页）、`FocusRingInset`（画在边界内侧，用于挨着排的标题栏按钮和列表项）、`FocusRingSwitch`（只套开关轨道）。设置面板滚动区的上、右各挪出 2px，首行「应用」和右缘贴边按钮的焦点框不被滚动区裁掉，内容位置不变。
- 自检：编译 0 警告，52/52 测试通过，format 无改动；离屏预览各处焦点框完整、未被裁。
