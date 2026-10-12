# Полный цикл для автотестов: сборка плагина -> запуск игры -> (опц.) загрузка башни и ожидание P2.
param([switch]$Tower, [switch]$NoBuild)
$t = $PSScriptRoot
Get-Process 'Lucky Tower Ultimate' -ErrorAction SilentlyContinue | Stop-Process -Force
Start-Sleep -Milliseconds 700
if (-not $NoBuild) {
    $b = dotnet build (Join-Path $t '..\src\LuckyTowerCoop\LuckyTowerCoop.csproj') -c Debug -nologo -v q 2>&1
    $errs = $b | Where-Object { $_ -match ': error ' }
    if ($errs) { "BUILD FAILED"; $errs | Select-Object -Unique; exit 1 }
    "build ok"
}
& pwsh -NoProfile -File (Join-Path $t 'run-game.ps1')
if ($LASTEXITCODE -ne 0) { exit 1 }
if ($Tower) {
    Start-Sleep -Seconds 2
    & pwsh -NoProfile -File (Join-Path $t 'dev.ps1') 'load tower' | Out-Null
    for ($i = 0; $i -lt 40; $i++) {
        Start-Sleep -Seconds 1
        $d = & pwsh -NoProfile -File (Join-Path $t 'dev.ps1') dump -TimeoutMs 1500
        if ($d -match 'Coop P2: VonWanst') { "tower ready, P2 spawned (~$i s)"; exit 0 }
    }
    "TIMEOUT waiting for P2"; $d; exit 1
}
