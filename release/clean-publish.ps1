param(
    [Parameter(Mandatory = $true)]
    [string]$PublishDirectory
)

$ErrorActionPreference = "Stop"

function Test-RemovableAsset([string]$assetPath) {
    $fileName = [System.IO.Path]::GetFileName($assetPath)
    return $fileName -like "*Forms*" `
        -or $fileName -eq "System.Design.dll" `
        -or $fileName -eq "createdump.exe"
}

$publishRoot = (Resolve-Path -LiteralPath $PublishDirectory).Path
$removedFiles = Get-ChildItem -LiteralPath $publishRoot -Recurse -File | Where-Object {
    Test-RemovableAsset $_.Name
}

foreach ($file in $removedFiles) {
    Remove-Item -LiteralPath $file.FullName -Force
}

$depsFiles = Get-ChildItem -LiteralPath $publishRoot -Filter "*.deps.json" -File
foreach ($depsFile in $depsFiles) {
    $document = Get-Content -LiteralPath $depsFile.FullName -Raw | ConvertFrom-Json
    foreach ($targetProperty in @($document.targets.PSObject.Properties)) {
        foreach ($libraryProperty in @($targetProperty.Value.PSObject.Properties)) {
            if ($libraryProperty.Name -like "*Forms*") {
                $targetProperty.Value.PSObject.Properties.Remove($libraryProperty.Name)
                continue
            }

            $dependencies = $libraryProperty.Value.dependencies
            if ($null -ne $dependencies) {
                foreach ($dependencyProperty in @($dependencies.PSObject.Properties)) {
                    if ($dependencyProperty.Name -like "*Forms*") {
                        $dependencies.PSObject.Properties.Remove($dependencyProperty.Name)
                    }
                }
            }

            foreach ($sectionName in @("runtime", "native", "resources")) {
                $section = $libraryProperty.Value.$sectionName
                if ($null -eq $section) {
                    continue
                }

                foreach ($assetProperty in @($section.PSObject.Properties)) {
                    if (Test-RemovableAsset $assetProperty.Name) {
                        $section.PSObject.Properties.Remove($assetProperty.Name)
                    }
                }
            }
        }
    }

    foreach ($libraryProperty in @($document.libraries.PSObject.Properties)) {
        if ($libraryProperty.Name -like "*Forms*") {
            $document.libraries.PSObject.Properties.Remove($libraryProperty.Name)
        }
    }

    $json = $document | ConvertTo-Json -Depth 100 -Compress
    [System.IO.File]::WriteAllText(
        $depsFile.FullName,
        $json,
        [System.Text.UTF8Encoding]::new($false))

    $staleReference = Select-String -LiteralPath $depsFile.FullName `
        -Pattern 'Forms|System\.Design\.dll|createdump\.exe' `
        -Quiet
    if ($staleReference) {
        throw "Publish dependency metadata still contains a removed asset: $($depsFile.FullName)"
    }
}

$removedBytes = ($removedFiles | Measure-Object -Property Length -Sum).Sum
if ($null -eq $removedBytes) {
    $removedBytes = 0
}

Write-Host "Removed $($removedFiles.Count) unused publish files ($([math]::Round($removedBytes / 1MB, 2)) MB) and cleaned dependency metadata."
