$ErrorActionPreference = "Stop"
$install = Join-Path $env:LOCALAPPDATA "Programs/PatchAdams#"
$shortcutPath = Join-Path $env:APPDATA "Microsoft/Windows/Start Menu/Programs/PatchAdams#.lnk"

if (Test-Path $shortcutPath) {
    Remove-Item -LiteralPath $shortcutPath -Force
}
if (Test-Path $install) {
    Remove-Item -LiteralPath $install -Recurse -Force
}

Write-Host "Removed PatchAdams# for the current Windows user."
