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

& $dotnetPath publish $projectRoot -c $Configuration -r $Runtime --self-contained true -o $outputPath
if ($LASTEXITCODE -ne 0) {
    throw "dotnet publish failed with exit code $LASTEXITCODE."
}

Copy-Item -LiteralPath (Join-Path $PSScriptRoot "README.txt") -Destination (Join-Path $outputPath "README.txt") -Force

"Publish completed: $outputPath"
