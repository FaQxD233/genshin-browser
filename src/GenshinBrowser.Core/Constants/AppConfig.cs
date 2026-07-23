namespace GenshinBrowser.Constants;

public static class AppConfig
{
    public static class Browser
    {
        public const string DefaultUrl = "https://search.bilibili.com/all?keyword=%E5%8E%9F%E7%A5%9E%20%E6%94%BB%E7%95%A5";
        public const string SearchUrlTemplate = "https://search.bilibili.com/all?keyword={0}";
    }

    public static class Data
    {
        public const int MaxHistoryEntries = 200;
        public const int MaxFavoriteEntries = 100;
        public const int MaxDownloadItems = 50;
        public const int MaxSettingsFileSizeBytes = 256 * 1024;
        public const int MaxHistoryFileSizeBytes = 12 * 1024 * 1024;
        public const int MaxFavoritesFileSizeBytes = 6 * 1024 * 1024;
        public const int MaxDownloadsFileSizeBytes = 16 * 1024 * 1024;
        public const int LogRetentionDays = 14;
        public const long WebView2CacheThresholdBytes = 500L * 1024 * 1024;
        public const int MaxEntryTitleLength = 200;
        public const int MaxEntryUrlLength = 8192;
        public const int HistorySaveDebounceMs = 500;
        public const int DownloadSaveDebounceMs = 300;
    }

    public static class Ui
    {
        public const int SmoothScrollDurationMs = 200;
        public const double SmoothScrollFactor = 1.0;
        public const int SettingsSaveDebounceMs = 500;
        public const int WindowBoundsUiDebounceMs = 120;
        public const string SearchPlaceholder = "搜索...";
        public const string AddressBarPlaceholder = "输入网址或搜索...";
    }
}
