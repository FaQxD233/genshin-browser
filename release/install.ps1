param(
    [switch]$NoWebView2Install
)

$ErrorActionPreference = "Stop"
$scriptDir = Split-Path -Parent $PSCommandPath

# Step 1: Ensure WebView2 Runtime
if (-not $NoWebView2Install) {
    $bootstrapper = Join-Path $scriptDir "MicrosoftEdgeWebview2Setup.exe"
    if (Test-Path -LiteralPath $bootstrapper) {
        Write-Host "正在检查 WebView2 Runtime..."
        $proc = Start-Process -Wait -FilePath $bootstrapper -ArgumentList "/silent" -PassThru
        if ($proc.ExitCode -ne 0 -and $proc.ExitCode -ne 0) {
            Write-Warning "WebView2 安装返回异常代码 $($proc.ExitCode)，尝试继续启动..."
        } else {
            Write-Host "WebView2 Runtime 就绪。"
        }
    } else {
        Write-Warning "未找到 MicrosoftEdgeWebview2Setup.exe，跳过 WebView2 安装。"
        Write-Warning "请自行安装 WebView2 Runtime: https://developer.microsoft.com/microsoft-edge/webview2/"
    }
}

# Step 2: Launch app
$appPath = Join-Path $scriptDir "GenshinBrowser.exe"
if (-not (Test-Path -LiteralPath $appPath)) {
    Write-Error "未找到 GenshinBrowser.exe，请确认解压完整。"
    exit 1
}

Write-Host "正在启动 Genshin Browser..."
Start-Process -Wait -FilePath $appPath
