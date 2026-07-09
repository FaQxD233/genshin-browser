param(
    [string]$Configuration = "Release",
    [string]$Runtime = "win-x64"
)

$ErrorActionPreference = "Stop"

$projectRoot = Split-Path -Parent $PSScriptRoot
$outputPath = Join-Path $projectRoot "dist\$Runtime-self-contained"
$dotnetCommand = Get-Command "dotnet" -ErrorAction SilentlyContinue

if ($dotnetCommand) {
    $dotnetPath = $dotnetCommand.Source
} else {
    $dotnetPath = "C:\Program Files\dotnet\dotnet.exe"
    if (-not (Test-Path -LiteralPath $dotnetPath)) {
        throw "dotnet CLI was not found. Install the .NET SDK or add dotnet to PATH."
    }
}

# 1. Build WebView2-compatible version
& $dotnetPath publish $projectRoot -c $Configuration -r $Runtime --self-contained true -o $outputPath
if ($LASTEXITCODE -ne 0) {
    throw "dotnet publish failed with exit code $LASTEXITCODE."
}

# 2. Download WebView2 Evergreen Bootstrapper
$bootstrapperUrl = "https://go.microsoft.com/fwlink/p/?LinkId=2124703"
$bootstrapperPath = Join-Path $outputPath "MicrosoftEdgeWebview2Setup.exe"
try {
    Write-Host "正在下载 WebView2 Evergreen Bootstrapper..."
    Invoke-WebRequest -Uri $bootstrapperUrl -OutFile $bootstrapperPath -UseBasicParsing
    Write-Host "WebView2 Bootstrapper 已下载: $( [math]::Round((Get-Item $bootstrapperPath).Length / 1KB, 1) ) KB"
} catch {
    Write-Warning "下载 WebView2 Bootstrapper 失败: $_"
}

# 3. Copy auxiliary files
Copy-Item -LiteralPath (Join-Path $PSScriptRoot "README.txt") -Destination (Join-Path $outputPath "README.txt") -Force
Copy-Item -LiteralPath (Join-Path $PSScriptRoot "install.ps1") -Destination (Join-Path $outputPath "install.ps1") -Force

"Publish completed: $outputPath"
