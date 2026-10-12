# Помощники для живых проверок (". tools\testlib.ps1"): запуск двух копий с фиксированным сидом и переход в нужную комнату.
. "$PSScriptRoot\devlib.ps1"

# Запустить две копии, дождаться соединения, зафиксировать сид хоста и загрузить башню (до TopOfTower).
function Start-Pair([int]$Seed = 4242) {
    Get-Process 'Lucky Tower Ultimate', 'CoopNet' -ErrorAction SilentlyContinue | Stop-Process -Force
    & pwsh -NoProfile -File "$PSScriptRoot\local-online.ps1" -DevServer -Force | Select-Object -Last 1
    for ($i = 0; $i -lt 60; $i++) { Start-Sleep 1; if ((DevB 'net status') -match 'state=Connected') { break } }
    if ($Seed) { DevA "seed $Seed" | Out-Null }
    DevA 'load tower' | Out-Null
    for ($i = 0; $i -lt 60; $i++) { Start-Sleep 1; if ((DevB 'log 5 room ->') -match 'TopOfTower') { break } }
    Start-Sleep 3
    DevA god | Out-Null
}

# Пройти дверью (или люком) в комнату Target: оба игрока встают к двери, хост нажимает вверх, клиент голосует.
function Enter-Room([string]$Target) {
    $inv = [Globalization.CultureInfo]::InvariantCulture
    $room = Get-Room (DevA where)
    if ($room -eq $Target) { return $true }
    $d = ((DevA doors) -split "`n") | ForEach-Object {
        $m = [regex]::Match($_, '^(.+?) center=\(([-\d.]+), ([-\d.]+).*target=(\S+)')
        if ($m.Success -and $m.Groups[4].Value -eq $Target) { [pscustomobject]@{ Name = $m.Groups[1].Value; X = [double]$m.Groups[2].Value; Y = [double]$m.Groups[3].Value } }
    } | Select-Object -First 1
    if (-not $d) { Write-Output "нет двери из $room в $Target"; return $false }
    $x = $d.X.ToString($inv); $y = ($d.Y - 1.5).ToString($inv)
    DevA 'killall' | Out-Null; DevA 'unlock' | Out-Null
    DevA "tp p1 $x $y" | Out-Null; Start-Sleep -Milliseconds 400; DevA 'tap p1 up' | Out-Null; Start-Sleep 1
    DevA "tp p2 $x $y" | Out-Null; Start-Sleep -Milliseconds 800; DevA "tp p2 $x $y" | Out-Null; Start-Sleep -Milliseconds 150; DevB 'tap p1 up' | Out-Null
    for ($i = 0; $i -lt 20; $i++) { Start-Sleep 1; if ((Get-Room (DevA where)) -ne $room) { break } }
    Start-Sleep 3
    return ((Get-Room (DevA where)) -eq $Target)
}
