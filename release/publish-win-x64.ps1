param(
    [string]$Configuration = "Release",
    [string]$Runtime = "win-x64"
)

$ErrorActionPreference = "Stop"

$projectRoot = Split-Path -Parent $PSScriptRoot
$outputPath = Join-Path $projectRoot "dist\$Runtime-self-contained"

& "C:\Program Files\dotnet\dotnet.exe" publish $projectRoot -c $Configuration -r $Runtime --self-contained true -o $outputPath

Copy-Item -LiteralPath (Join-Path $PSScriptRoot "README.txt") -Destination (Join-Path $outputPath "README.txt") -Force

"Publish completed: $outputPath"
