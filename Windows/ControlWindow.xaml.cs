using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Windows;
using System.Windows.Input;
using System.Windows.Forms;
using GenshinBrowser.Models;

namespace GenshinBrowser.Windows;

public partial class ControlWindow : Window
{
    private readonly MainWindow _browserWindow;
    private readonly ObservableCollection<HistoryItemViewModel> _historyItems = new();
    private readonly ObservableCollection<FavoriteItemViewModel> _favoriteItems = new();
    private bool _hasUserMovedWindow;
    private bool _isRestoringBounds;

    public ControlWindow(MainWindow browserWindow)
    {
        InitializeComponent();
        _browserWindow = browserWindow;
        Owner = browserWindow;
        HistoryListBox.ItemsSource = _historyItems;
        FavoritesListBox.ItemsSource = _favoriteItems;

        Loaded += ControlWindow_OnLoaded;
        LocationChanged += ControlWindow_OnLocationOrSizeChanged;
        SizeChanged += ControlWindow_OnLocationOrSizeChanged;
    }

    public bool AllowClose { get; set; }

    public void RefreshFromBrowser()
    {
        ModeTextBlock.Text = _browserWindow.CurrentMode == WindowMode.Fixed ? "固定模式" : "自由模式";
        ToggleModeButton.Content = _browserWindow.CurrentMode == WindowMode.Fixed ? "切到自由" : "切到固定";
        StatusTextBlock.Text = _browserWindow.StatusMessage;
        FavoriteToggleButton.Content = _browserWindow.IsFavorite(_browserWindow.CurrentAddress) ? "取消收藏" : "收藏当前页";

        if (!AddressBarTextBox.IsKeyboardFocusWithin)
        {
            AddressBarTextBox.Text = _browserWindow.CurrentAddress;
        }

        _favoriteItems.Clear();
        foreach (var item in _browserWindow.FavoriteEntries)
        {
            _favoriteItems.Add(new FavoriteItemViewModel(item));
        }

        _historyItems.Clear();
        foreach (var item in _browserWindow.HistoryEntries)
        {
            _historyItems.Add(new HistoryItemViewModel(item));
        }
    }

    public void ShowNearBrowserWindow()
    {
        if (_hasUserMovedWindow)
        {
            return;
        }

        var screen = Screen.FromHandle(new System.Windows.Interop.WindowInteropHelper(_browserWindow).Handle);
        var workArea = screen.WorkingArea;
        var preferredLeft = _browserWindow.Left + _browserWindow.Width + 12;
        var preferredTop = _browserWindow.Top;

        if (preferredLeft + Width > workArea.Right)
        {
            preferredLeft = _browserWindow.Left - Width - 12;
        }

        if (preferredLeft < workArea.Left)
        {
            preferredLeft = workArea.Left + 8;
        }

        var maxTop = Math.Max(workArea.Top + 8, workArea.Bottom - Height - 8);
        if (preferredTop > maxTop)
        {
            preferredTop = maxTop;
        }

        if (preferredTop < workArea.Top)
        {
            preferredTop = workArea.Top + 8;
        }

        Left = preferredLeft;
        Top = preferredTop;
    }

    public void SaveWindowBounds()
    {
        if (WindowState != WindowState.Normal)
        {
            return;
        }

        _browserWindow.SaveControlWindowBounds(Left, Top, Width, Height);
    }

    protected override void OnClosing(CancelEventArgs e)
    {
        if (!AllowClose)
        {
            SaveWindowBounds();
            e.Cancel = true;
            Hide();
            return;
        }

        base.OnClosing(e);
    }

    private void ToggleModeButton_OnClick(object sender, RoutedEventArgs e)
    {
        _browserWindow.ToggleWindowMode();
    }

    private async void PlayPauseButton_OnClick(object sender, RoutedEventArgs e)
    {
        await _browserWindow.ToggleVideoPlaybackAsync();
    }

    private void HomeButton_OnClick(object sender, RoutedEventArgs e)
    {
        _browserWindow.NavigateHome();
    }

    private void GoButton_OnClick(object sender, RoutedEventArgs e)
    {
        _browserWindow.NavigateTo(AddressBarTextBox.Text);
    }

    private void ReloadButton_OnClick(object sender, RoutedEventArgs e)
    {
        _browserWindow.ReloadPage();
    }

    private void AddressBarTextBox_OnKeyDown(object sender, System.Windows.Input.KeyEventArgs e)
    {
        if (e.Key == Key.Enter)
        {
            _browserWindow.NavigateTo(AddressBarTextBox.Text);
        }
    }

    private void HistoryListBox_OnMouseDoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (HistoryListBox.SelectedItem is HistoryItemViewModel item)
        {
            _browserWindow.NavigateTo(item.Url);
        }
    }

    private async void FavoriteToggleButton_OnClick(object sender, RoutedEventArgs e)
    {
        if (_browserWindow.IsFavorite(_browserWindow.CurrentAddress))
        {
            await _browserWindow.RemoveFavoriteAsync(_browserWindow.CurrentAddress);
            return;
        }

        await _browserWindow.AddCurrentPageToFavoritesAsync();
    }

    private void FavoritesListBox_OnMouseDoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (FavoritesListBox.SelectedItem is FavoriteItemViewModel item)
        {
            _browserWindow.NavigateTo(item.Url);
        }
    }

    private void ControlWindow_OnLoaded(object sender, RoutedEventArgs e)
    {
        _isRestoringBounds = true;
        _browserWindow.RestoreControlWindowBounds(this);
        _hasUserMovedWindow = Left >= 0 && Top >= 0;
        _isRestoringBounds = false;
    }

    private void ControlWindow_OnLocationOrSizeChanged(object? sender, EventArgs e)
    {
        if (!IsLoaded || WindowState != WindowState.Normal || _isRestoringBounds)
        {
            return;
        }

        _hasUserMovedWindow = true;
        SaveWindowBounds();
    }

    private sealed class HistoryItemViewModel
    {
        public HistoryItemViewModel(HistoryEntry item)
        {
            Url = item.Url;
            DisplayText = $"{item.Title} ({item.VisitedAt:MM-dd HH:mm})";
        }

        public string Url { get; }

        public string DisplayText { get; }
    }

    private sealed class FavoriteItemViewModel
    {
        public FavoriteItemViewModel(FavoriteEntry item)
        {
            Url = item.Url;
            DisplayText = $"{item.Title} ({item.SavedAt:MM-dd HH:mm})";
        }

        public string Url { get; }

        public string DisplayText { get; }
    }
}
