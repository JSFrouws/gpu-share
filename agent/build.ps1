# Build GpuAgent as a self-contained single-file exe for win-x64.
# Output: ..\dist\agent\GpuAgent.exe  (alongside appsettings.json)

$out = "$PSScriptRoot\..\dist\agent"
dotnet publish "$PSScriptRoot\GpuAgent.csproj" `
    -c Release `
    -r win-x64 `
    --self-contained true `
    -p:PublishSingleFile=true `
    -p:IncludeNativeLibrariesForSelfExtract=true `
    -o $out

if ($LASTEXITCODE -ne 0) { Write-Error "Build failed."; exit 1 }

# Copy config next to exe (only if it doesn't already exist — don't overwrite user edits)
$cfgDst = "$out\appsettings.json"
if (-not (Test-Path $cfgDst)) {
    Copy-Item "$PSScriptRoot\appsettings.json" $cfgDst
    Write-Host "Config copied — edit $cfgDst before running."
} else {
    Write-Host "Config already exists at $cfgDst — not overwritten."
}

Write-Host ""
Write-Host "Done. Exe: $out\GpuAgent.exe"
Write-Host "Run install.ps1 once to set up autostart."
