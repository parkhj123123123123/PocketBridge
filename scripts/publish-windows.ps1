$ErrorActionPreference = 'Stop'
$repoRoot = Split-Path -Parent $PSScriptRoot
$artifactDirectory = Join-Path $repoRoot 'artifacts/windows-x64'

dotnet publish (Join-Path $repoRoot 'src/PocketBridge.Windows/PocketBridge.Windows.csproj') `
    -c Release `
    -r win-x64 `
    --self-contained true `
    -p:PublishSingleFile=true `
    -p:IncludeNativeLibrariesForSelfExtract=true `
    -o $artifactDirectory

Write-Host "PocketBridge Windows build: $artifactDirectory"
