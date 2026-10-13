<#
.SYNOPSIS
    Builds the AvatarDeck installer (Velopack): artifacts\releases\AvatarDeck-win-Setup.exe + update packages.
.EXAMPLE
    ./build/pack.ps1                 # version from Directory.Build.props
    ./build/pack.ps1 -Version 0.2.0
#>
param(
    [string]$Version,
    [string]$Runtime = "win-x64"
)

$ErrorActionPreference = "Stop"
$root = Split-Path -Parent $PSScriptRoot
Push-Location $root
try {
    if (-not $Version) {
        $Version = ([xml](Get-Content "Directory.Build.props")).Project.PropertyGroup.Version | Where-Object { $_ } | Select-Object -First 1
    }

    $publish = Join-Path $root "artifacts\publish"
    $releases = Join-Path $root "artifacts\releases"
    if (Test-Path $publish) { Remove-Item $publish -Recurse -Force }

    Write-Host "Publishing AvatarDeck $Version ($Runtime)..."
    # Self-contained (no .NET install needed) and ReadyToRun (fast start when SteamVR launches it).
    dotnet publish src/AvatarDeck.App -c Release -r $Runtime --self-contained `
        -p:PublishReadyToRun=true -p:Version=$Version -o $publish
    if ($LASTEXITCODE -ne 0) { throw "dotnet publish failed" }

    dotnet tool restore | Out-Null
    Write-Host "Packing installer..."
    dotnet vpk pack --packId AvatarDeck --packVersion $Version --packDir $publish --mainExe AvatarDeck.exe `
        --packTitle AvatarDeck --packAuthors Code9914 --icon src/AvatarDeck.UI/Assets/avatardeck.ico `
        --runtime $Runtime --outputDir $releases
    if ($LASTEXITCODE -ne 0) { throw "vpk pack failed" }

    Write-Host "Installer: $(Join-Path $releases 'AvatarDeck-win-Setup.exe')"
}
finally {
    Pop-Location
}
