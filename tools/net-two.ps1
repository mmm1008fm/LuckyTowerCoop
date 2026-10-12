# Online test on one PC: two game copies, A (debug port 47800) is the host, B (47801) is the client over Direct.
param([switch]$NoBuild)
$t = $PSScriptRoot; $g = 'D:\game\Lucky.Tower.Ultimate.v1.1.4'
function devA($c) { & pwsh -NoProfile -File "$t\dev.ps1" $c -Port 47800 }
function devB($c) { & pwsh -NoProfile -File "$t\dev.ps1" $c -Port 47801 }
Get-Process 'Lucky Tower Ultimate' -ErrorAction SilentlyContinue | Stop-Process -Force; Start-Sleep -Milliseconds 700
if (-not $NoBuild) {
    $b = dotnet build (Join-Path $t '..\src\LuckyTowerCoop\LuckyTowerCoop.csproj') -c Debug -nologo -v q 2>&1
    if ($b -match ': error ') { "BUILD FAILED"; $b | Select-String ': error ' | Select-Object -Unique; exit 1 }
    "build ok"
}
Start-Process "$g\Lucky Tower Ultimate.exe" -WorkingDirectory $g | Out-Null
for ($i = 0; $i -lt 60; $i++) { Start-Sleep 1; if ((devA ping) -match 'MainMenu') { break } }
Start-Process "$g\Lucky Tower Ultimate.exe" -WorkingDirectory $g | Out-Null
for ($i = 0; $i -lt 60; $i++) { Start-Sleep 1; if ((devB ping) -match 'MainMenu') { break } }
"A: $(devA ping)"; "B: $(devB ping)"
