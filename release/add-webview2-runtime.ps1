# Adds the WebView2 Fixed Version runtime pinned in webview2-fixed-runtime.json to <package>\WebView2Runtime (full package).
# The downloaded .cab is verified by SHA-256 and cached for reuse; signatures and version are checked by verify-package.ps1.
param(
    [Parameter(Mandatory = $true)]
    [string]$PackageDirectory,

    # Local default: dist/.cache/webview2 (git-ignored; keeps the ~300 MB download off the system drive). CI passes RUNNER_TEMP.
    [string]$CacheDirectory = (Join-Path (Split-Path -Parent $PSScriptRoot) "dist\.cache\webview2")
)

$ErrorActionPreference = "Stop"
# The progress bar slows large downloads in Windows PowerShell by an order of magnitude.
$ProgressPreference = "SilentlyContinue"

$config = Get-Content -LiteralPath (Join-Path $PSScriptRoot "webview2-fixed-runtime.json") -Raw | ConvertFrom-Json
$cabName = [System.IO.Path]::GetFileName(([Uri]$config.url).AbsolutePath)
New-Item -ItemType Directory -Force -Path $CacheDirectory | Out-Null
$cabPath = Join-Path $CacheDirectory $cabName

function Get-Sha256([string]$Path) {
    return (Get-FileHash -LiteralPath $Path -Algorithm SHA256).Hash.ToLowerInvariant()
}

if (-not (Test-Path -LiteralPath $cabPath) -or (Get-Sha256 $cabPath) -ne $config.sha256) {
    Write-Host "Downloading WebView2 Fixed Version $($config.version): $cabName"
    $partialPath = "$cabPath.partial"
    Invoke-WebRequest -Uri $config.url -OutFile $partialPath -UseBasicParsing
    Move-Item -LiteralPath $partialPath -Destination $cabPath -Force
}

$actualHash = Get-Sha256 $cabPath
if ($actualHash -ne $config.sha256) {
    # When upgrading, this message reports the new hash: check that the .cab is signed by Microsoft before pinning it.
    $signature = Get-AuthenticodeSignature -LiteralPath $cabPath
    throw "SHA-256 mismatch for ${cabName}: expected '$($config.sha256)', got '$actualHash' " +
        "(signature: $($signature.Status), $($signature.SignerCertificate.Subject)). Update webview2-fixed-runtime.json only after checking the source."
}

$packageRoot = (Resolve-Path -LiteralPath $PackageDirectory).Path
$destination = Join-Path $packageRoot "WebView2Runtime"
# Extract next to the destination so the final move is a rename on the same volume.
$extractRoot = Join-Path $packageRoot ".webview2-extract"
foreach ($path in @($destination, $extractRoot)) {
    if (Test-Path -LiteralPath $path) {
        Remove-Item -LiteralPath $path -Recurse -Force
    }
}

New-Item -ItemType Directory -Path $extractRoot | Out-Null
try {
    # Microsoft's docs require expand.exe; extracting with File Explorer may produce the wrong folder layout.
    $expandOutput = & expand.exe $cabPath "-F:*" $extractRoot
    if ($LASTEXITCODE -ne 0) {
        throw "expand.exe failed with exit code ${LASTEXITCODE}: $($expandOutput -join [Environment]::NewLine)"
    }

    $runtimeFolders = @(Get-ChildItem -LiteralPath $extractRoot -Directory)
    if ($runtimeFolders.Count -ne 1) {
        throw "Expected one runtime folder in $cabName, found $($runtimeFolders.Count)."
    }

    Move-Item -LiteralPath $runtimeFolders[0].FullName -Destination $destination
} finally {
    if (Test-Path -LiteralPath $extractRoot) {
        Remove-Item -LiteralPath $extractRoot -Recurse -Force
    }
}

$files = @(Get-ChildItem -LiteralPath $destination -Recurse -File)
$totalMiB = [math]::Round((($files | Measure-Object Length -Sum).Sum / 1MB), 2)
Write-Host "Added WebView2 Fixed Version $($config.version) to ${destination}: $($files.Count) files, $totalMiB MiB."
