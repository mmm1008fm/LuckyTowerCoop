# Network quality bench: two copies over localhost, both with link simulation enabled (NetLinkSim),
# both characters walk back and forth, the client measures input lag and how smoothly the partner is displayed.
# Each link profile is run in two display modes: legacy (snapshot catch-up) and buffer (interpolation buffer).
#   pwsh tools\net-quality.ps1                       # all profiles
#   pwsh tools\net-quality.ps1 -Profiles wifi,bad -Seconds 20 -NoLaunch
param(
    [string[]]$Profiles = @('lan', 'good', 'wifi', 'bad', 'awful'),
    [string[]]$Modes = @('legacy', 'buffer'),
    [int]$Seconds = 12,
    [switch]$NoLaunch
)
$Profiles = @($Profiles | ForEach-Object { $_ -split ',' }); $Modes = @($Modes | ForEach-Object { $_ -split ',' })
$t = $PSScriptRoot
. "$t\devlib.ps1"
$inv = [Globalization.CultureInfo]::InvariantCulture

if (-not $NoLaunch) {
    & pwsh -NoProfile -File "$t\local-online.ps1" -DevServer -Force | Select-Object -Last 1
    for ($i = 0; $i -lt 40; $i++) { Start-Sleep 1; if ((DevB 'net status') -match 'state=Connected') { break } }
    DevA 'load tower' | Out-Null
    for ($i = 0; $i -lt 40; $i++) { Start-Sleep 1; if ((DevB 'log 5 room ->') -match 'TopOfTower') { break } }
    Start-Sleep 3
}
DevA god | Out-Null
$m = [regex]::Match((DevA where), 'P1 .*?pos=\(([-\d.]+), ([-\d.]+)')
if (-not $m.Success) { throw 'host player position not found' }
$x0 = [double]::Parse($m.Groups[1].Value, $inv)
$a = ($x0 - 3).ToString('F2', $inv); $b = ($x0 + 3).ToString('F2', $inv)

$rows = @()
foreach ($p in $Profiles) {
    DevA "net link $p" | Out-Null; DevB "net link $p" | Out-Null
    foreach ($mode in $Modes) {
        DevB "net interp $mode" | Out-Null
        DevA "patrol p1 $a $b $($Seconds + 4)" | Out-Null; DevB "patrol p1 $a $b $($Seconds + 4)" | Out-Null
        Start-Sleep 3   # link and jitter estimate settle
        DevB 'net quality reset' | Out-Null
        Start-Sleep $Seconds
        $q = DevB 'net quality'
        $f = @{}; foreach ($kv in ($q -split '\s+')) { $parts = $kv -split '=', 2; if ($parts.Count -eq 2) { $f[$parts[0]] = $parts[1] } }
        $rows += [pscustomobject]@{
            link = $p; mode = $mode
            'lag50' = $f['lag50']; 'lag95' = $f['lag95']
            'hitch%' = $f['hitch'] -replace '%', ''; 'extrap%' = $f['extrap'] -replace '%', ''
            gaps = $f['gaps']; 'maxGap' = $f['maxGap']; delay = $f['delay']; jitter = $f['jitter']
            snaps = $f['snaps']; dropped = $f['dropped']; stalls = $f['stalls']; jumps = $f['jumps']; ownErr = $f['ownErr']
        }
        "{0,-6} {1,-6} lag50={2,4} lag95={3,4} hitch={4,5}% extrap={5,5}% gaps={6,3} maxGap={7,4} delay={8,3} jitter={9,3} snaps={10} stall/jump={11}/{12} ownErr={13}" -f $p, $mode, $f['lag50'], $f['lag95'], ($f['hitch'] -replace '%', ''), ($f['extrap'] -replace '%', ''), $f['gaps'], $f['maxGap'], $f['delay'], $f['jitter'], $f['snaps'], $f['stalls'], $f['jumps'], $f['ownErr']
    }
}
DevA 'net link off' | Out-Null; DevB 'net link off' | Out-Null; DevB 'net interp buffer' | Out-Null
$out = Join-Path $t '..\docs\net-quality-last.csv'
$rows | Export-Csv $out -NoTypeInformation -Encoding utf8
"table: $out"
"lag - from the client's key press to the host's response, ms; hitch - share of frames with a partner jerk; extrap - frames where the buffer ran out of data;"
"gaps - snapshot stream gaps > 50 ms; delay - buffer display delay, ms; jitter - latency spread estimate, ms; snaps - sharp corrections of the own character"
