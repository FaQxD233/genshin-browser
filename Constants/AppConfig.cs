namespace GenshinBrowser.Constants;

/// <summary>
/// 应用程序配置常量
/// </summary>
public static class AppConfig
{
    /// <summary>
    /// 浏览器相关配置
    /// </summary>
    public static class Browser
    {
        public const string DefaultUrl = "https://search.bilibili.com/all?keyword=%E5%8E%9F%E7%A5%9E%20%E6%94%BB%E7%95%A5";
        public const string SearchUrlTemplate = "https://search.bilibili.com/all?keyword={0}";
    }

    /// <summary>
    /// 数据存储限制
    /// </summary>
    public static class Data
    {
        /// <summary>
        /// 最大历史记录条数。超过此数量的旧记录将被删除，以防止 JSON 文件过大影响启动时间。
        /// </summary>
        public const int MaxHistoryEntries = 200;

        /// <summary>
        /// 最大收藏条数
        /// </summary>
        public const int MaxFavoriteEntries = 100;

        /// <summary>
        /// 下载列表最大条数。超过此数量时自动移除最旧的已完成项，
        /// 避免长期累积占用内存（进行中的任务不会被自动移除）。
        /// </summary>
        public const int MaxDownloadItems = 50;

        /// <summary>
        /// 日志保留天数。启动时清理超过此天数的旧日志文件。
        /// </summary>
        public const int LogRetentionDays = 14;

        /// <summary>
        /// WebView2 用户数据目录大小阈值（MB）。启动时检查超过此值则静默自动清理
        /// （缓存 / DOM / Service Worker / 自动填充等；保留 Cookie、浏览历史、下载记录）。
        /// </summary>
        public const int WebView2CacheThresholdMb = 500;

        /// <summary>
        /// 历史/收藏标题最大长度。超长标题入库前截断，防止超长字符串占用内存与磁盘。
        /// </summary>
        public const int MaxEntryTitleLength = 200;

        /// <summary>
        /// 历史记录落盘防抖（毫秒）。SPA 连续切页时合并写盘，降低 IO。
        /// </summary>
        public const int HistorySaveDebounceMs = 500;
    }

    /// <summary>
    /// UI 相关配置
    /// </summary>
    public static class Ui
    {
        /// <summary>
        /// 占位符文本颜色（次要文本）
        /// </summary>
        public static readonly System.Windows.Media.Color PlaceholderTextColor = System.Windows.Media.Color.FromRgb(0x6E, 0x76, 0x81);

        /// <summary>
        /// 活动文本颜色（主文本）
        /// </summary>
        public static readonly System.Windows.Media.Color ActiveTextColor = System.Windows.Media.Color.FromRgb(0xE6, 0xED, 0xF3);

        /// <summary>
        /// 平滑滚动动画时长（毫秒）
        /// </summary>
        public const int SmoothScrollDurationMs = 200;

        /// <summary>
        /// 平滑滚动系数（鼠标滚轮灵敏度）
        /// </summary>
        public const double SmoothScrollFactor = 1.0;

        /// <summary>
        /// 配置保存防抖延迟（毫秒）
        /// </summary>
        public const int SettingsSaveDebounceMs = 500;

        /// <summary>
        /// 主窗移动/缩放后，控制窗尺寸显示与跟随位置的 UI 防抖（毫秒）
        /// </summary>
        public const int WindowBoundsUiDebounceMs = 120;

        /// <summary>
        /// 搜索框占位符文本
        /// </summary>
        public const string SearchPlaceholder = "搜索...";

        /// <summary>
        /// 地址栏占位符文本
        /// </summary>
        public const string AddressBarPlaceholder = "输入网址或搜索...";
    }
}
