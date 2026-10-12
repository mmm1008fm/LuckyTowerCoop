# Online on one PC: two game copies over localhost.
# HOST on the left (keyboard), CLIENT on the right (gamepad if connected; otherwise each copy only listens for input while focused).
# After both copies are closed, the game's display settings (fullscreen/resolution) are restored.
param(
    [string]$GameDir = 'D:\game\Lucky.Tower.Ultimate.v1.1.4',
    [ValidateSet('auto', 'keyboard', 'gamepad', 'all')][string]$HostDevice = 'auto',
    [ValidateSet('auto', 'keyboard', 'gamepad', 'all')][string]$ClientDevice = 'auto',
    [switch]$DevServer,   # debug ports 47800/47801 (for autotests)
    [switch]$Steam,       # via Steam: host is the main Steam, client is a second Steam account in a Sandboxie sandbox (box $Box)
    [string]$Box = 'Steam2',
    [switch]$Force        # close an already running game without asking
)
$ErrorActionPreference = 'Stop'
$exe = Join-Path $GameDir 'Lucky Tower Ultimate.exe'
if (-not (Test-Path $exe)) { throw "game not found: $exe" }

$running = Get-Process 'Lucky Tower Ultimate' -ErrorAction SilentlyContinue
if ($running) {
    if (-not $Force) {
        $a = Read-Host "The game is already running ($($running.Count) instance(s)). Close it and launch two copies? [y/N]"
        if ($a -notmatch '^y') { return }
    }
    $running | Stop-Process -Force; Start-Sleep -Milliseconds 800
}

# Unity stores the window mode in the registry on exit - back it up so fullscreen can be restored after the test
$key = 'HKCU:\Software\Studio Seufz\Lucky Tower Ultimate'
$backup = @()
if (Test-Path $key) {
    $item = Get-Item $key
    foreach ($name in $item.GetValueNames() | Where-Object { $_ -like 'Screenmanager*' -or $_ -like 'UnitySelectMonitor*' }) {
        $backup += [pscustomobject]@{ Name = $name; Kind = $item.GetValueKind($name).ToString(); Value = $item.GetValue($name) }
    }
}
$backupFile = Join-Path $env:TEMP "ltcoop_screen_$(Get-Date -Format yyyyMMdd_HHmmss).json"
$backup | ConvertTo-Json -Depth 3 | Set-Content $backupFile -Encoding utf8

$extra = if ($DevServer) { ' --coop-devserver' } else { '' }

# ---------- Steam: two accounts on one PC ----------
$sbStart = 'C:\Program Files\Sandboxie-Plus\Start.exe'
$steamExe = (Get-ItemProperty 'HKCU:\Software\Valve\Steam' -ErrorAction SilentlyContinue).SteamExe
function Get-BoxSteamUser {
    # while anything runs in the box, its registry is mounted at HKU\Sandbox_<user>_<box>
    $hive = Get-ChildItem 'Registry::HKEY_USERS' -ErrorAction SilentlyContinue | Where-Object { $_.PSChildName -like "Sandbox_*_$Box" } | Select-Object -First 1
    if (-not $hive) { return 0 }
    $p = "Registry::$($hive.Name)\user\current\software\Valve\Steam\ActiveProcess"
    $v = (Get-ItemProperty $p -ErrorAction SilentlyContinue).ActiveUser
    if ($v) { return [int64]$v } else { return 0 }
}
if ($Steam) {
    if (-not (Test-Path $sbStart)) { throw "Sandboxie-Plus is required (not found: $sbStart)" }
    if (-not $steamExe) { throw 'Steam not found' }
    $sbIni = 'C:\Program Files\Sandboxie-Plus\SbieIni.exe'
    if (-not ((& $sbIni query $Box Enabled) -match 'y')) {
        & $sbIni set $Box Enabled y | Out-Null
        & $sbStart /reload | Out-Null
        "created Sandboxie box '$Box'"
    }
    # main Steam (host account)
    if (-not ((Get-ItemProperty 'HKCU:\Software\Valve\Steam\ActiveProcess' -ErrorAction SilentlyContinue).ActiveUser)) {
        "starting the main Steam..."
        Start-Process $steamExe -ArgumentList '-silent'
        for ($i = 0; $i -lt 90 -and -not (Get-ItemProperty 'HKCU:\Software\Valve\Steam\ActiveProcess' -ErrorAction SilentlyContinue).ActiveUser; $i++) { Start-Sleep 1 }
        if (-not (Get-ItemProperty 'HKCU:\Software\Valve\Steam\ActiveProcess' -ErrorAction SilentlyContinue).ActiveUser) { throw 'the main Steam did not log in within 90 s - log in and run again' }
    }
    # second Steam in the box (client account)
    if ((Get-BoxSteamUser) -eq 0) {
        "starting the second Steam in sandbox '$Box'..."
        & $sbStart "/box:$Box" $steamExe | Out-Null
        for ($i = 0; $i -lt 60 -and (Get-BoxSteamUser) -eq 0; $i++) { Start-Sleep 1 }
        if ((Get-BoxSteamUser) -eq 0) {
            "the second Steam is not logged in yet: in the Steam window with the yellow border, log in with the second account (it must be a friend of the main one),"
            "tick 'Remember me' and run this script again."
            return
        }
    }
    "both Steam instances are online"
}
# Until the mod has loaded, an unfocused game stays paused - so the copies are launched one after another:
# the client starts once the host has loaded (the mod renames the window to '... HOST ...').
Add-Type @"
using System; using System.Text; using System.Runtime.InteropServices;
public static class LtWin {
    public delegate bool Proc(IntPtr h, IntPtr l);
    [DllImport("user32.dll")] static extern bool EnumWindows(Proc p, IntPtr l);
    [DllImport("user32.dll")] static extern uint GetWindowThreadProcessId(IntPtr h, out uint pid);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] static extern int GetWindowText(IntPtr h, StringBuilder s, int n);
    public static string Titles(int pid) {
        var all = new StringBuilder();
        EnumWindows((h, l) => { uint p; GetWindowThreadProcessId(h, out p); if (p == pid) { var t = new StringBuilder(256); GetWindowText(h, t, 256); all.Append(t).Append('|'); } return true; }, IntPtr.Zero);
        return all.ToString();
    }
}
"@
function Wait-Title($proc, $text, $sec) {
    for ($i = 0; $i -lt $sec; $i++) {
        Start-Sleep -Seconds 1
        if ($proc.HasExited) { throw "the game copy exited during startup (pid $($proc.Id))" }
        if ([LtWin]::Titles($proc.Id) -match $text) { return $true }
    }
    return $false
}
$hostArgs = if ($Steam) { '--coop-steam-host' } else { '--coop-host' }
$h = Start-Process $exe -WorkingDirectory $GameDir -PassThru -ArgumentList "$hostArgs --coop-window left --coop-device $HostDevice$extra"
"starting host (pid $($h.Id))..."
if (-not (Wait-Title $h 'HOST' 90)) { "host is taking longer than usual to load - starting the client anyway" }
if ($Steam) {
    # client runs in the sandbox: its Steam (second account), saves and display settings stay in the box
    & $sbStart "/box:$Box" $exe --coop-steam-join --coop-window right --coop-device $ClientDevice $extra.Trim() | Out-Null
    $c = $null
    for ($i = 0; $i -lt 30 -and -not $c; $i++) { Start-Sleep 1; $c = Get-Process 'Lucky Tower Ultimate' -ErrorAction SilentlyContinue | Where-Object { $_.Id -ne $h.Id } | Select-Object -First 1 }
    if (-not $c) { throw 'the game copy in the sandbox did not start' }
} else {
    $c = Start-Process $exe -WorkingDirectory $GameDir -PassThru -ArgumentList "--coop-join 127.0.0.1 --coop-window right --coop-device $ClientDevice$extra"
}
"starting client (pid $($c.Id)) - do not switch away from its window until it snaps to the right..."
if (Wait-Title $c 'CLIENT' 90) { "both copies are ready: host on the left, client on the right" } else { "client did not load within 90 s - click its window" }

# Background watcher: waits for both copies to close and restores the display settings
$restore = @"
`$ids = @($($h.Id), $($c.Id))
while (Get-Process -Id `$ids -ErrorAction SilentlyContinue) { Start-Sleep -Seconds 2 }
Start-Sleep -Seconds 1
`$key = '$key'
foreach (`$v in (Get-Content '$backupFile' -Raw | ConvertFrom-Json)) {
    `$val = if (`$v.Kind -eq 'Binary') { [byte[]]`$v.Value } else { `$v.Value }
    New-ItemProperty -Path `$key -Name `$v.Name -PropertyType `$v.Kind -Value `$val -Force | Out-Null
}
Remove-Item '$backupFile' -Force
"@
$enc = [Convert]::ToBase64String([Text.Encoding]::Unicode.GetBytes($restore))
Start-Process powershell -WindowStyle Hidden -ArgumentList "-NoProfile -ExecutionPolicy Bypass -EncodedCommand $enc" | Out-Null
"display settings will be restored automatically after both copies are closed"
