# Прогон на износ: хост (47800) и клиент (47801) случайно играют N секунд.
# Каждые несколько ходов — пауза без ввода и сверка: та же комната, позиция персонажа клиента у хоста (P2) и у клиента (P1).
# В конце — сводка ресинхронизаций и ошибок из логов.
param([int]$Seconds = 180, [switch]$NoLaunch, [switch]$Desync)
$t = $PSScriptRoot
. "$t\devlib.ps1"

if (-not $NoLaunch) {
    & pwsh -NoProfile -File "$t\net-two.ps1" | Select-Object -Last 2
    DevA 'net host direct' | Out-Null; DevA 'load tower' | Out-Null; Start-Sleep -Seconds 6; DevA god | Out-Null
    DevB 'net join 127.0.0.1 27015' | Out-Null
    for ($i = 0; $i -lt 40; $i++) { Start-Sleep 1; if ((DevB 'log 3 room ->') -match 'TopOfTower') { break } }
    Start-Sleep 2
}

$actions = 'left', 'right', 'left', 'right', 'jump', 'attack', 'pickup', 'up', 'down', 'throw', 'swap', 'use'
$rng = [System.Random]::new()
$end = (Get-Date).AddSeconds($Seconds)
$tick = 0; $checks = 0; $bad = 0; $roomBad = 0; $maxDiff = 0.0; $rooms = @{}
while ((Get-Date) -lt $end) {
    $tick++
    foreach ($port in 47800, 47801) {
        $a = $actions[$rng.Next($actions.Count)]
        $dur = [math]::Round(0.1 + $rng.NextDouble() * 0.9, 2)
        $cmd = if ($a -in 'left', 'right') { "hold p1 $a $dur" } else { "tap p1 $a" }
        Dev $cmd $port | Out-Null
    }
    if ($Desync -and $tick % 25 -eq 0) { "tick ${tick}: $(DevB 'net desync 3')" }
    Start-Sleep -Milliseconds 600
    if ($tick % 6 -eq 0) {
        Start-Sleep -Milliseconds 1500   # даём персонажам остановиться и снапшотам дойти
        $wa = DevA where; $wb = DevB where
        $ra = Get-Room $wa; $rb = Get-Room $wb; $rooms[$ra] = 1
        $checks++
        if ($ra -ne $rb) { $roomBad++; "tick ${tick}: rooms differ A=$ra B=$rb" }
        $ha = Get-Pos $wa 'P2'; $cb = Get-Pos $wb 'P1'
        if ($ha -and $cb) {
            $d = [math]::Sqrt([math]::Pow($ha[0] - $cb[0], 2) + [math]::Pow($ha[1] - $cb[1], 2))
            if ($d -gt $maxDiff) { $maxDiff = $d }
            if ($d -gt 0.5) { $bad++; "tick ${tick}: client pos host=($($ha -join ',')) client=($($cb -join ',')) d=$([math]::Round($d,2)) room=$ra" }
        }
        $sa = DevA 'net status'; $sb = DevB 'net status'
        if ($sa -notmatch 'state=Connected' -or $sb -notmatch 'state=Connected') { "tick ${tick}: A[$sa] B[$sb]" }
        if ($sa -match 'timeScale=0 ') { "tick ${tick}: host timeScale=0" }
    }
}
"--- soak done: ticks=$tick checks=$checks pos>0.5: $bad rooms-differ: $roomBad max diff: $([math]::Round($maxDiff, 2)) rooms visited: $($rooms.Keys -join ' ')"
"--- resync: host $(@((DevA 'log 4000 resync') -split "`n" | Where-Object { $_ -match 'resync' -and $_ -notmatch 'Dev\]' }).Count) lines, client $(@((DevB 'log 4000 resync') -split "`n" | Where-Object { $_ -match 'resync' -and $_ -notmatch 'Dev\]' }).Count) lines"
foreach ($pair in @(@('A', 47800), @('B', 47801))) {
    $errs = Get-Errors $pair[1]
    "--- $($pair[0]) errors: $(@($errs).Count) kinds"
    $errs | Select-Object -First 12 | ForEach-Object { "  x$($_.Count) $($_.Name.Substring(0, [Math]::Min(220, $_.Name.Length)))" }
}
