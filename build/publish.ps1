# Publishes self-contained Cuebot builds.
#   ./build/publish.ps1                 -> win-x64 and linux-x64
#   ./build/publish.ps1 -Runtimes win-x64
param(
    [string[]]$Runtimes = @("win-x64", "linux-x64"),
    [string]$Configuration = "Release"
)
$ErrorActionPreference = "Stop"
$root = Split-Path -Parent $PSScriptRoot
$artifacts = Join-Path $root "artifacts"

foreach ($rid in $Runtimes) {
    $out = Join-Path $artifacts "publish/$rid"
    if (Test-Path $out) { Remove-Item -Recurse -Force $out }
    dotnet publish (Join-Path $root "src/Cuebot.App/Cuebot.App.csproj") `
        -c $Configuration -r $rid --self-contained true -o $out -p:DebugType=none
    if ($LASTEXITCODE -ne 0) { throw "publish failed for $rid" }

    if ($rid -like "win-*") {
        $zip = Join-Path $artifacts "Cuebot-$rid.zip"
        if (Test-Path $zip) { Remove-Item $zip }
        Compress-Archive -Path (Join-Path $out "*") -DestinationPath $zip
        Write-Host "Windows package: $zip"
    } else {
        Write-Host "Linux build: $out (run packaging/linux/build-appimage.sh on Linux to make the AppImage)"
    }
}
