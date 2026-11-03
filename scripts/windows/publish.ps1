param(
    [string]$Configuration = "Release",
    [string]$Runtime = "win-x64"
)

$ErrorActionPreference = "Stop"
Set-StrictMode -Version Latest
$repoRoot = (Resolve-Path (Join-Path $PSScriptRoot "../..")).Path
$project = Join-Path $repoRoot "App/PatchAdams.Windows/PatchAdams.Windows.csproj"
$output = Join-Path $repoRoot "artifacts/windows/$Runtime"
$dotnetCommand = Get-Command dotnet -ErrorAction SilentlyContinue
$dotnet = if ($dotnetCommand) { $dotnetCommand.Source } else { Join-Path $env:ProgramFiles "dotnet/dotnet.exe" }
if (-not (Test-Path $dotnet)) {
    throw ".NET 10 SDK was not found. Install the SDK or add dotnet to PATH."
}

& $dotnet publish $project `
    --configuration $Configuration `
    --runtime $Runtime `
    --self-contained false `
    --output $output `
    -p:PublishSingleFile=false
if ($LASTEXITCODE -ne 0) {
    throw "dotnet publish failed with exit code $LASTEXITCODE"
}

Write-Host "Published PatchAdams# to $output"
