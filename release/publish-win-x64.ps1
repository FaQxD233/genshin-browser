param(
    [string]$Configuration = "Release",
    [string]$Runtime = "win-x64",
    [string]$Version = "0.0.0-local"
)

$ErrorActionPreference = "Stop"

if ($Runtime -notmatch '^win-(x64|x86|arm64)$') {
    throw "Unsupported runtime '$Runtime'. Expected win-x64, win-x86, or win-arm64."
}

$semanticVersion = $Version.Trim().TrimStart('v')
if ($semanticVersion -notmatch '^(0|[1-9]\d*)\.(0|[1-9]\d*)\.(0|[1-9]\d*)(-[0-9A-Za-z.-]+)?$') {
    throw "Invalid version '$Version'. Expected MAJOR.MINOR.PATCH with an optional prerelease suffix."
}

$projectRoot = (Resolve-Path (Split-Path -Parent $PSScriptRoot)).Path
$projectPath = Join-Path $projectRoot "GenshinBrowser.csproj"
$distRoot = [System.IO.Path]::GetFullPath((Join-Path $projectRoot "dist"))
$outputPath = [System.IO.Path]::GetFullPath((Join-Path $distRoot "$Runtime-self-contained"))
$dotnetCommand = Get-Command "dotnet" -ErrorAction SilentlyContinue

if (-not $outputPath.StartsWith(
        $distRoot + [System.IO.Path]::DirectorySeparatorChar,
        [StringComparison]::OrdinalIgnoreCase)) {
    throw "Refusing to publish outside the dist directory: $outputPath"
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
if (Test-Path -LiteralPath $outputPath) {
    Remove-Item -LiteralPath $outputPath -Recurse -Force
}

& $dotnetPath publish $projectPath `
    -c $Configuration `
    -r $Runtime `
    --self-contained true `
    -p:PublishSingleFile=false `
    -p:PublishTrimmed=false `
    -p:DebugSymbols=false `
    -p:DebugType=None `
    "-p:Version=$semanticVersion" `
    -o $outputPath
if ($LASTEXITCODE -ne 0) {
    throw "dotnet publish failed with exit code $LASTEXITCODE."
}

$bootstrapperUrl = "https://go.microsoft.com/fwlink/p/?LinkId=2124703"
$bootstrapperPath = Join-Path $outputPath "MicrosoftEdgeWebview2Setup.exe"
Write-Host "Downloading WebView2 Evergreen Bootstrapper..."
Invoke-WebRequest -Uri $bootstrapperUrl -OutFile $bootstrapperPath -UseBasicParsing

$signature = Get-AuthenticodeSignature -LiteralPath $bootstrapperPath
$subject = $signature.SignerCertificate.Subject
if ($signature.Status -ne 'Valid' -or $subject -notmatch 'O=Microsoft Corporation') {
    Remove-Item -LiteralPath $bootstrapperPath -Force
    throw "WebView2 installer signature validation failed: status=$($signature.Status), subject=$subject"
}

Write-Host "Verified Microsoft-signed WebView2 Bootstrapper: $([math]::Round((Get-Item $bootstrapperPath).Length / 1KB, 1)) KiB"
Copy-Item -LiteralPath (Join-Path $PSScriptRoot "README.txt") -Destination (Join-Path $outputPath "README.txt") -Force

$expectedExecutables = @("GenshinBrowser.exe", "MicrosoftEdgeWebview2Setup.exe")
$executables = @(Get-ChildItem -LiteralPath $outputPath -Recurse -File -Filter "*.exe")
$unexpectedExecutables = @($executables | Where-Object { $_.Name -notin $expectedExecutables })
if ($executables.Count -ne $expectedExecutables.Count -or $unexpectedExecutables.Count -ne 0) {
    throw "Unexpected executables in publish output: $($executables.Name -join ', ')"
}

$forbiddenFiles = @(Get-ChildItem -LiteralPath $outputPath -Recurse -File | Where-Object {
    $_.Name -like "*Forms*" -or $_.Name -eq "System.Design.dll" -or $_.Name -eq "createdump.exe"
})
if ($forbiddenFiles.Count -ne 0) {
    throw "Unused publish files remain: $($forbiddenFiles.Name -join ', ')"
}

$depsFiles = @(Get-ChildItem -LiteralPath $outputPath -File -Filter "*.deps.json")
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

$pdbFiles = @(Get-ChildItem -LiteralPath $outputPath -Recurse -File -Filter "*.pdb")
if ($pdbFiles.Count -ne 0) {
    throw "Release output contains debug symbols: $($pdbFiles.Name -join ', ')"
}

$application = Get-Item -LiteralPath (Join-Path $outputPath "GenshinBrowser.exe")
if (-not $application.VersionInfo.ProductVersion.StartsWith($semanticVersion, [StringComparison]::Ordinal)) {
    throw "Application version '$($application.VersionInfo.ProductVersion)' does not match '$semanticVersion'."
}

$files = @(Get-ChildItem -LiteralPath $outputPath -Recurse -File)
$totalMiB = [math]::Round((($files | Measure-Object Length -Sum).Sum / 1MB), 2)
Write-Host "Publish completed and verified: $outputPath ($($files.Count) files, $totalMiB MiB)"
