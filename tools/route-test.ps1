# Автотест маршрута: хост и клиент вместе проходят N дверей (голосование у двери), после каждого перехода —
# сверка комнат, числа объектов, ненайденных у клиента, ресинхронизаций и ошибок.
param([int]$Rooms = 6, [switch]$NoLaunch, [string]$Until = '', [switch]$AvoidBalcony)
$t = $PSScriptRoot
. "$t\devlib.ps1"

if (-not $NoLaunch) {
    & pwsh -NoProfile -File "$t\local-online.ps1" -DevServer -Force | Select-Object -Last 1
    for ($i = 0; $i -lt 40; $i++) { Start-Sleep 1; if ((DevB 'net status') -match 'state=Connected') { break } }
    DevA 'load tower' | Out-Null
    for ($i = 0; $i -lt 40; $i++) { Start-Sleep 1; if ((DevB 'log 5 room ->') -match 'TopOfTower') { break } }
    Start-Sleep 3
}
DevA god | Out-Null

function Doors() {
    ((DevA doors) -split "`n") | ForEach-Object {
        $m = [regex]::Match($_, '^(.+?) center=\(([-\d.]+), ([-\d.]+).*target=(\S+)')
        if ($m.Success) { [pscustomobject]@{ Name = $m.Groups[1].Value; X = [double]$m.Groups[2].Value; Y = [double]$m.Groups[3].Value; Target = $m.Groups[4].Value } }
    }
}
function Count($port) { @(((Dev netents $port) -split "`n") | Where-Object { $_ -match '^\s*\d+ ' }).Count }

$prev = ''
$visited = @()
$seen = @{}
$bad = @{}
for ($n = 1; $n -le $Rooms; $n++) {
    $room = Get-Room (DevA where)
    $seen[$room] = 1
    $balcRooms = if ($AvoidBalcony) { @(((DevA rooms) -split "`n") | Where-Object { $_ -match '\[(BalconyEntrance|Balcony|BalconyExit)\]' } | ForEach-Object { ($_ -split ' \[')[0] }) } else { @() }
    $allDoors = @(Doors | Where-Object { $_.Target -ne 'None' -and $_.Target -ne $room -and -not $bad.ContainsKey("$room|$($_.Name)") -and -not ($AvoidBalcony -and $balcRooms -contains $_.Target) })
    $allExits = @(((DevA exits) -split "`n") | ForEach-Object { $m = [regex]::Match($_, '^(Side|Hatch) (.+?) center=\(([-\d.]+), ([-\d.]+)\) target=(\S+)'); if ($m.Success) { [pscustomobject]@{ Kind = $m.Groups[1].Value; Name = $m.Groups[2].Value; X = [double]$m.Groups[3].Value; Y = [double]$m.Groups[4].Value; Target = $m.Groups[5].Value } } })
    # сначала — куда ещё не ходили; иначе назад
    # особые комнаты («S02_F03_R01: Crusher Room») — тупики с запертым входом; идём туда только если больше некуда
    # к цели (-Until): предпочитаем двери в комнату-вход на балкон и на сам балкон
    $goal = if ($Until) { @(((DevA rooms) -split "`n") | Where-Object { $_ -match '\[BalconyEntrance\]' } | ForEach-Object { ($_ -split ' \[')[0] }) } else { @() }
    $doors = @($allDoors | Where-Object { -not $seen.ContainsKey($_.Target) } | Sort-Object { if ($Until -and ($_.Target -match $Until -or $goal -contains $_.Target)) { -1 } elseif ($_.Target -match ':$') { 1 } else { 0 } })
    $exits = @($allExits | Where-Object { -not $seen.ContainsKey($_.Target) })
    # всё обойдено: сначала в хаб (оттуда больше путей), потом куда угодно, кроме комнаты, из которой пришли
    if ($doors.Count -eq 0 -and $exits.Count -eq 0) { $doors = @($allDoors | Sort-Object { if ($_.Target -match 'HUB') { 0 } elseif ($_.Target -ne $prev) { 1 } else { 2 } }); $exits = $allExits }
    $inv = [Globalization.CultureInfo]::InvariantCulture
    # игрок хоста заперт (бочка с порохом / прерванная катсцена) — освобождаем, иначе ни одна дверь не сработает
    if ((DevA where) -match '^P1 .*move=False') { "     P1 locked: $((DevA free).Trim())" }
    if ($doors.Count -gt 0) {
        $d = $doors[0]
        $x = $d.X.ToString($inv); $y = ($d.Y - 1.5).ToString($inv)
        DevA "tp p1 $x $y" | Out-Null; Start-Sleep -Milliseconds 400; DevA 'tap p1 up' | Out-Null; Start-Sleep 1
        DevA "tp p2 $x $y" | Out-Null; Start-Sleep -Milliseconds 800; DevA "tp p2 $x $y" | Out-Null; Start-Sleep -Milliseconds 150; DevB 'tap p1 up' | Out-Null
    } elseif ($exits.Count -gt 0) {
        $d = $exits | Where-Object Kind -eq 'Hatch' | Select-Object -First 1; if (-not $d) { $d = $exits[0] }
        $x = $d.X.ToString($inv); $y = ($d.Y + 0.3).ToString($inv)
        DevA "tp p1 $x $y" | Out-Null; Start-Sleep 1.5
        DevA "tp p2 $x $y" | Out-Null
    } else { "[$n] ${room}: no forward exit"; DevA exits; break }
    $ok = $false
    for ($i = 0; $i -lt 20; $i++) { Start-Sleep 1; $ra = Get-Room (DevA where); if ($ra -ne $room) { $ok = $true; break } }
    if (-not $ok -and $d -and $doors.Count -gt 0) {
        # повтор: освободить ожидающего, добить врагов (дверь могла быть заперта), снова к двери
        DevA 'tap p2 down' | Out-Null; DevB 'tap p1 down' | Out-Null; DevA killall | Out-Null; DevA unlock | Out-Null; Start-Sleep 3
        "     retry $($d.Name): $(((DevA doors) -split "`n" | Where-Object { $_ -match "^$([regex]::Escape($d.Name)) " } | ForEach-Object { [regex]::Match($_, 'enabled=\S+ opened=\S+ lock=\S+').Value }) -join ' ')"
        DevA "tp p1 $x $y" | Out-Null; Start-Sleep -Milliseconds 600; DevA 'tap p1 up' | Out-Null; Start-Sleep 1
        DevA "tp p2 $x $y" | Out-Null; Start-Sleep -Milliseconds 800; DevA "tp p2 $x $y" | Out-Null; Start-Sleep -Milliseconds 150; DevB 'tap p1 up' | Out-Null
        for ($i = 0; $i -lt 20; $i++) { Start-Sleep 1; $ra = Get-Room (DevA where); if ($ra -ne $room) { $ok = $true; break } }
    }
    Start-Sleep 4
    $wa = DevA where; $wb = DevB where; $ra = Get-Room $wa; $rb = Get-Room $wb
    $unres = ((DevB 'log 3 not found here') -split "`n" | Where-Object { $_ -match 'not found here' } | Select-Object -Last 1)
    "[$n] $room --$($d.Name)--> host=$ra client=$rb moved=$ok | entities host=$(Count 47800) client=$(Count 47801) | gfx A:$([regex]::Matches($wa,'gfx=\w+').Value -join ',') B:$([regex]::Matches($wb,'gfx=\w+').Value -join ',')"
    if ($unres) {
        "     unresolved: $($unres.Substring(0, [Math]::Min(220, $unres.Length)))"
        # сверка иерархии: родитель первого ненайденного пути на обеих сторонах
        $m3 = [regex]::Match($unres, 'not found here: ([^;]+)')
        if ($m3.Success) {
            $p = $m3.Groups[1].Value.Trim(); $cut = $p.LastIndexOf('/'); $parent = if ($cut -gt 0) { $p.Substring(0, $cut) } else { '' }
            $la = ((DevA "ls $parent") -split "`n" | Select-Object -First 14) -join ' | '; $lb = ((DevB "ls $parent") -split "`n" | Select-Object -First 14) -join ' | '
            "     ls host:   $($la.Substring(0, [Math]::Min(600, $la.Length)))"
            "     ls client: $($lb.Substring(0, [Math]::Min(600, $lb.Length)))"
        }
    }
    if ($ra -match 'HUB') { "     hub doors host: $(((DevA doors) -split "`n" | ForEach-Object { $m2=[regex]::Match($_,'^(.+?) center=.*path=(\S+)'); if($m2.Success){ "$($m2.Groups[1].Value)@$($m2.Groups[2].Value)" } }) -join ', ')  | client: $(((DevB doors) -split "`n" | ForEach-Object { $m2=[regex]::Match($_,'^(.+?) center=.*path=(\S+)'); if($m2.Success){ "$($m2.Groups[1].Value)@$($m2.Groups[2].Value)" } }) -join ', ')" }
    $npc = ((DevA npcs) -split "`n") | Where-Object { $_ -match 'dialogue=True' } | Select-Object -First 1
    if ($npc) {
        $m = [regex]::Match($npc, '^(.+?) type=.*pos=\(([-\d.]+), ([-\d.]+)')
        $nx = ([double]$m.Groups[2].Value - 1.2).ToString($inv); $ny = ([double]$m.Groups[3].Value + 0.1).ToString($inv)
        DevA "tp p2 $nx $ny" | Out-Null; Start-Sleep 1; DevB 'tap p1 talk' | Out-Null; Start-Sleep 2.5
        $cw = ((DevB 'log 6 [NetClient] window') -split "`n") | Where-Object { $_ -match 'window' -and $_ -notmatch 'Dev\]' } | Select-Object -Last 2
        "     NPC $($m.Groups[1].Value): client window log: $(($cw | ForEach-Object { $_ -replace '^\d+ \[Info\s*:LuckyTowerCoop\] ','' }) -join ' | ')"
        DevB 'tap p1 opt1' | Out-Null; Start-Sleep 2; DevB 'tap p1 cancel' | Out-Null; Start-Sleep 1
    }
    $lt = DevA lights
    if ($lt -notmatch 'no (lights|room)') {
        DevA 'lights out' | Out-Null; Start-Sleep 2
        "     LIGHTS OUT  host: $((DevA lights).Trim())  | client: $((DevB lights).Trim())"
        DevA 'lights on' | Out-Null; Start-Sleep 2
        "     LIGHTS ON   host: $((DevA lights).Trim())  | client: $((DevB lights).Trim())"
    }
    $k = DevA killall
    if ($k -match 'killed [1-9]') {
        Start-Sleep 4
        $hostPos = @{}; foreach ($l in ((DevA netents) -split "`n")) { $m = [regex]::Match($l, '^\s*(\d+)\s+Character\s+(\S+).*pos=\(([-\d.]+), ([-\d.]+)'); if ($m.Success) { $hostPos[$m.Groups[1].Value] = @([double]$m.Groups[3].Value, [double]$m.Groups[4].Value, $m.Groups[2].Value) } }
        $worst = 0.0; $worstName = ''
        foreach ($l in ((DevB netents) -split "`n")) { $m = [regex]::Match($l, '^\s*(\d+)\s+Character\s+(\S+).*pos=\(([-\d.]+), ([-\d.]+)'); if ($m.Success -and $hostPos.ContainsKey($m.Groups[1].Value)) { $h = $hostPos[$m.Groups[1].Value]; $d = [math]::Sqrt([math]::Pow($h[0] - [double]$m.Groups[3].Value, 2) + [math]::Pow($h[1] - [double]$m.Groups[4].Value, 2)); if ($d -gt $worst) { $worst = $d; $worstName = $h[2] } } }
        "     $k; corpses max diff host/client = $([math]::Round($worst, 2)) ($worstName)"
    }
    $prev = $room; $visited += $ra
    if (-not $ok) {
        "     door transition failed"; ((DevA 'log 6 [Transition]') -split "`n" | Select-Object -Last 3); ((DevA 'log 4 [Lock]') -split "`n" | Select-Object -Last 3)
        if ($d) { $bad["$room|$($d.Name)"] = 1 }
        DevA 'tap p2 down' | Out-Null; DevB 'tap p1 down' | Out-Null; Start-Sleep 1
    }
    if ($Until -and $ra -match $Until) { "     reached $ra — stop"; break }
}
"--- resync: host $(@(((DevA 'log 2000 resync') -split "`n") | Where-Object { $_ -match 'asks resync' }).Count), client $(@(((DevB 'log 2000 resync') -split "`n") | Where-Object { $_ -match 'resync requested' }).Count)"
foreach ($pair in @(@('A', 47800), @('B', 47801))) {
    $errs = Get-Errors $pair[1]
    "--- $($pair[0]) errors: $(@($errs).Count) kinds"
    $errs | Select-Object -First 8 | ForEach-Object { "  x$($_.Count) $($_.Name.Substring(0, [Math]::Min(200, $_.Name.Length)))" }
}
