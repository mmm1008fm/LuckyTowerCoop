# Launch the game for autotests: kills the old process, starts it, waits for the main menu.
param([string]$GameDir = 'D:\game\Lucky.Tower.Ultimate.v1.1.4', [int]$TimeoutSec = 90)
$dev = Join-Path $PSScriptRoot 'dev.ps1'
Get-Process 'Lucky Tower Ultimate' -ErrorAction SilentlyContinue | Stop-Process -Force
Start-Sleep -Milliseconds 500
$p = Start-Process -FilePath (Join-Path $GameDir 'Lucky Tower Ultimate.exe') -WorkingDirectory $GameDir -PassThru
$deadline = (Get-Date).AddSeconds($TimeoutSec)
while ((Get-Date) -lt $deadline) {
    Start-Sleep -Seconds 1
    if ($p.HasExited) { "GAME EXITED code=$($p.ExitCode)"; exit 1 }
    $r = & pwsh -NoProfile -File $dev ping -TimeoutMs 800
    if ($r -match 'scene=MainMenu') { "ready pid=$($p.Id): $r"; exit 0 }
}
"TIMEOUT waiting for MainMenu"; exit 1
