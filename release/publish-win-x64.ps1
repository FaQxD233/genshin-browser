param(
    [string]$Configuration = "Release",
    [string]$Runtime = "win-x64",
    [string]$Version = "0.0.0-local",

    # Also produce the full package with the bundled WebView2 Fixed Version runtime (dist/<Runtime>-full);
    # the version is pinned in webview2-fixed-runtime.json.
    [switch]$BundleWebView2,

    # Skip restore when CI has already restored (with TreatWarningsAsErrors).
    [switch]$NoRestore,

    # Cache directory for the Fixed Version .cab (default in add-webview2-runtime.ps1); CI passes RUNNER_TEMP.
    [string]$WebView2CacheDirectory
)

$ErrorActionPreference = "Stop"
$ProgressPreference = "SilentlyContinue"

if ($Runtime -notmatch '^win-(x64|x86|arm64)$') {
    throw "Unsupported runtime '$Runtime'. Expected win-x64, win-x86, or win-arm64."
}

if ($BundleWebView2 -and $Runtime -ne "win-x64") {
    throw "The bundled WebView2 runtime is only configured for win-x64 (see webview2-fixed-runtime.json)."
}

$semanticVersion = $Version.Trim().TrimStart('v')
if ($semanticVersion -notmatch '^(0|[1-9]\d*)\.(0|[1-9]\d*)\.(0|[1-9]\d*)(-[0-9A-Za-z.-]+)?$') {
    throw "Invalid version '$Version'. Expected MAJOR.MINOR.PATCH with an optional prerelease suffix."
}

$projectRoot = (Resolve-Path (Split-Path -Parent $PSScriptRoot)).Path
$projectPath = Join-Path $projectRoot "GenshinBrowser.csproj"
$distRoot = [System.IO.Path]::GetFullPath((Join-Path $projectRoot "dist"))
$outputPath = [System.IO.Path]::GetFullPath((Join-Path $distRoot "$Runtime-self-contained"))
$fullOutputPath = [System.IO.Path]::GetFullPath((Join-Path $distRoot "$Runtime-full"))
$dotnetCommand = Get-Command "dotnet" -ErrorAction SilentlyContinue

foreach ($path in @($outputPath, $fullOutputPath)) {
    if (-not $path.StartsWith(
            $distRoot + [System.IO.Path]::DirectorySeparatorChar,
            [StringComparison]::OrdinalIgnoreCase)) {
        throw "Refusing to publish outside the dist directory: $path"
    }
}

if ($dotnetCommand) {
    $dotnetPath = $dotnetCommand.Source
} else {
    $dotnetPath = "C:\Program Files\dotnet\dotnet.exe"
    if (-not (Test-Path -LiteralPath $dotnetPath)) {
        throw "dotnet CLI was not found. Install the .NET SDK or add dotnet to PATH."
    }
}

# Ensure repeated local publishes cannot retain stale files from an older package/runtime.
foreach ($path in @($outputPath, $fullOutputPath)) {
    if (Test-Path -LiteralPath $path) {
        Remove-Item -LiteralPath $path -Recurse -Force
    }
}

$publishArguments = @(
    "publish", $projectPath,
    "-c", $Configuration,
    "-r", $Runtime,
    "--self-contained", "true",
    "-p:PublishSingleFile=false",
    "-p:PublishTrimmed=false",
    "-p:DebugSymbols=false",
    "-p:DebugType=None",
    "-p:Version=$semanticVersion",
    "-o", $outputPath
)
if ($NoRestore) {
    $publishArguments += "--no-restore"
}

& $dotnetPath @publishArguments
if ($LASTEXITCODE -ne 0) {
    throw "dotnet publish failed with exit code $LASTEXITCODE."
}

# The full package is a copy of the published app (taken before the Evergreen bootstrapper is added) plus the bundled runtime.
if ($BundleWebView2) {
    Copy-Item -LiteralPath $outputPath -Destination $fullOutputPath -Recurse
    $runtimeArguments = @{ PackageDirectory = $fullOutputPath }
    if ($WebView2CacheDirectory) {
        $runtimeArguments.CacheDirectory = $WebView2CacheDirectory
    }

    & (Join-Path $PSScriptRoot "add-webview2-runtime.ps1") @runtimeArguments
}

$bootstrapperUrl = "https://go.microsoft.com/fwlink/p/?LinkId=2124703"
$bootstrapperPath = Join-Path $outputPath "MicrosoftEdgeWebview2Setup.exe"
Write-Host "Downloading WebView2 Evergreen Bootstrapper..."
Invoke-WebRequest -Uri $bootstrapperUrl -OutFile $bootstrapperPath -UseBasicParsing

$packages = @(@{ Path = $outputPath; Variant = "Standard" })
if ($BundleWebView2) {
    $packages += @{ Path = $fullOutputPath; Variant = "Full" }
}

foreach ($package in $packages) {
    Copy-Item -LiteralPath (Join-Path $PSScriptRoot "README.txt") -Destination (Join-Path $package.Path "README.txt") -Force
    & (Join-Path $PSScriptRoot "verify-package.ps1") -PackageDirectory $package.Path -Version $semanticVersion -Variant $package.Variant
}

Write-Host "Publish completed: $($packages.Path -join ', ')"
