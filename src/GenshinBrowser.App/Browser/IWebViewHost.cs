using Microsoft.UI.Xaml.Controls;

namespace GenshinBrowser.Browser;

internal interface IWebViewHost
{
    WebView2 CurrentWebView { get; }

    WebView2 ReplaceWebView();

    Task WaitForCurrentWebViewLoadedAsync(CancellationToken cancellationToken);
}
