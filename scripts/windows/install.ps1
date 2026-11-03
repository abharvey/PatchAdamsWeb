param(
    [switch]$SkipPublish
)

$ErrorActionPreference = "Stop"
$repoRoot = (Resolve-Path (Join-Path $PSScriptRoot "../..")).Path
$runtime = "win-x64"
$publish = Join-Path $repoRoot "artifacts/windows/$runtime"
$install = Join-Path $env:LOCALAPPDATA "Programs/PatchAdams#"
$startMenu = Join-Path $env:APPDATA "Microsoft/Windows/Start Menu/Programs"
$shortcutPath = Join-Path $startMenu "PatchAdams#.lnk"
$executable = Join-Path $install "PatchAdamsSharp.exe"

if (-not $SkipPublish) {
    & (Join-Path $PSScriptRoot "publish.ps1") -Runtime $runtime
}
if (-not (Test-Path $publish)) {
    throw "Publish output not found at $publish. Run scripts/windows/publish.ps1 first."
}

New-Item -ItemType Directory -Force -Path $install | Out-Null
New-Item -ItemType Directory -Force -Path $startMenu | Out-Null
Copy-Item -Path (Join-Path $publish "*") -Destination $install -Recurse -Force

$shell = New-Object -ComObject WScript.Shell
$shortcut = $shell.CreateShortcut($shortcutPath)
$shortcut.TargetPath = $executable
$shortcut.WorkingDirectory = $install
$shortcut.Description = "Offline local patch review"
$shortcut.Save()

Write-Host "Installed PatchAdams# to $install"
Write-Host "Start menu shortcut: $shortcutPath"
