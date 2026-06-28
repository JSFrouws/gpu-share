# Install GpuAgent:
#   1. Register HTTP URL ACL so the agent can listen on all LAN interfaces (runs netsh as admin)
#   2. Register autostart via HKCU Run key (no admin needed)
#   3. Update the desktop shortcut to launch the exe
#   4. Start the agent now
#
# Run ONCE after build.ps1. Re-run if you move the exe or change the port.

$exePath = (Resolve-Path "$PSScriptRoot\..\dist\agent\GpuAgent.exe" -ErrorAction Stop).Path
$port    = 9000  # must match controlPort in appsettings.json

Write-Host "Installing GPU Agent from: $exePath"

# ── 1. URL ACL (required for LAN-wide listen; localhost works without it) ────
$existing = netsh http show urlacl | Select-String ":$port/"
if ($existing) {
    Write-Host "URL ACL already registered for port $port."
} else {
    Write-Host "Registering HTTP URL ACL (needs admin)..."
    Start-Process powershell `
        -ArgumentList "-Command", "netsh http add urlacl url=http://+:$port/ user=$env:USERNAME" `
        -Verb RunAs -Wait
}

# ── 2. Autostart via registry ─────────────────────────────────────────────────
$regKey = "HKCU:\Software\Microsoft\Windows\CurrentVersion\Run"
Set-ItemProperty -Path $regKey -Name "GpuAgent" -Value "`"$exePath`""
Write-Host "Autostart registered (HKCU\Run\GpuAgent)."

# ── 3. Desktop shortcut ───────────────────────────────────────────────────────
$ws = New-Object -ComObject WScript.Shell
$s  = $ws.CreateShortcut("$env:USERPROFILE\Desktop\gpu-share.lnk")
$s.TargetPath       = $exePath
$s.WorkingDirectory = Split-Path $exePath
$s.Description      = "GPU Share Agent"
$s.IconLocation     = "$exePath,0"
$s.Save()
Write-Host "Desktop shortcut updated -> $exePath"

# ── 4. Launch ─────────────────────────────────────────────────────────────────
Write-Host ""
Write-Host "Starting GPU Agent..."
Start-Process $exePath
Write-Host "Done. Look for the tray icon (bottom-right, in the system tray)."
