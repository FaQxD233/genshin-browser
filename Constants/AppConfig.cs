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
