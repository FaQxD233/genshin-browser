# Package self-check shared by the standard and full packages (used by publish-win-x64.ps1 locally and in CI):
# app version, unexpected executables, WinForms leftovers, deps.json and pdb files; the standard package's Evergreen
# bootstrapper signature; the full package's bundled runtime signatures and version.
param(
    [Parameter(Mandatory = $true)]
    [string]$PackageDirectory,

    # Version without the leading v, for example 0.3.13
    [Parameter(Mandatory = $true)]
    [string]$Version,

    [ValidateSet("Standard", "Full")]
    [string]$Variant = "Standard"
)

$ErrorActionPreference = "Stop"

function Assert-MicrosoftSigned([string]$Path) {
    $signature = Get-AuthenticodeSignature -LiteralPath $Path
    $subject = $signature.SignerCertificate.Subject
    if ($signature.Status -ne 'Valid' -or $subject -notmatch 'O=Microsoft Corporation') {
        throw "Signature validation failed for ${Path}: status=$($signature.Status), subject=$subject"
    }
}

$root = (Resolve-Path -LiteralPath $PackageDirectory).Path
$runtimeFolder = Join-Path $root "WebView2Runtime"
$runtimePrefix = $runtimeFolder + [System.IO.Path]::DirectorySeparatorChar
$allFiles = @(Get-ChildItem -LiteralPath $root -Recurse -File)
# The app's own files; the bundled runtime is Microsoft's files as shipped, so only its signatures and version are checked.
$appFiles = @($allFiles | Where-Object { -not $_.FullName.StartsWith($runtimePrefix, [StringComparison]::OrdinalIgnoreCase) })

$bootstrapperName = "MicrosoftEdgeWebview2Setup.exe"
$expectedExecutables = if ($Variant -eq "Full") { @("GenshinBrowser.exe") } else { @("GenshinBrowser.exe", $bootstrapperName) }
$executables = @($appFiles | Where-Object { $_.Extension -eq ".exe" })
$unexpectedExecutables = @($executables | Where-Object { $_.Name -notin $expectedExecutables })
if ($executables.Count -ne $expectedExecutables.Count -or $unexpectedExecutables.Count -ne 0) {
    throw "Unexpected executables in ${Variant} package: $($executables.Name -join ', ')"
}

if ($Variant -eq "Full") {
    $config = Get-Content -LiteralPath (Join-Path $PSScriptRoot "webview2-fixed-runtime.json") -Raw | ConvertFrom-Json
    $browserExecutable = Join-Path $runtimeFolder "msedgewebview2.exe"
    if (-not (Test-Path -LiteralPath $browserExecutable)) {
        throw "Bundled WebView2 runtime is missing: $browserExecutable"
    }

    $runtimeVersion = (Get-Item -LiteralPath $browserExecutable).VersionInfo.ProductVersion
    if ($runtimeVersion -ne $config.version) {
        throw "Bundled WebView2 runtime version '$runtimeVersion' does not match '$($config.version)'."
    }

    foreach ($runtimeExecutable in @(Get-ChildItem -LiteralPath $runtimeFolder -Recurse -File -Filter "*.exe")) {
        Assert-MicrosoftSigned $runtimeExecutable.FullName
    }
} else {
    Assert-MicrosoftSigned (Join-Path $root $bootstrapperName)
    if (Test-Path -LiteralPath $runtimeFolder) {
        throw "Standard package must not contain a bundled WebView2 runtime: $runtimeFolder"
    }
}

$application = Get-Item -LiteralPath (Join-Path $root "GenshinBrowser.exe")
if (-not $application.VersionInfo.ProductVersion.StartsWith($Version, [StringComparison]::Ordinal)) {
    throw "Application version '$($application.VersionInfo.ProductVersion)' does not match '$Version'."
}

$forbiddenFiles = @($appFiles | Where-Object {
    $_.Name -like "*Forms*" -or $_.Name -eq "System.Design.dll" -or $_.Name -eq "createdump.exe"
})
if ($forbiddenFiles.Count -ne 0) {
    throw "Unused publish files remain: $($forbiddenFiles.Name -join ', ')"
}

$depsFiles = @(Get-ChildItem -LiteralPath $root -File -Filter "*.deps.json")
if ($depsFiles.Count -ne 1) {
    throw "Expected one .deps.json file, found $($depsFiles.Count)."
}

$null = Get-Content -LiteralPath $depsFiles[0].FullName -Raw | ConvertFrom-Json
$staleReference = Select-String -LiteralPath $depsFiles[0].FullName `
    -Pattern 'Forms|System\.Design\.dll|createdump\.exe' `
    -Quiet
if ($staleReference) {
    throw "Removed publish assets are still referenced by $($depsFiles[0].Name)."
}

$pdbFiles = @($appFiles | Where-Object { $_.Extension -eq ".pdb" })
if ($pdbFiles.Count -ne 0) {
    throw "Release output contains debug symbols: $($pdbFiles.Name -join ', ')"
}

$totalMiB = [math]::Round((($allFiles | Measure-Object Length -Sum).Sum / 1MB), 2)
Write-Host "Package verification passed (${Variant}): $root ($($allFiles.Count) files, $totalMiB MiB)"
