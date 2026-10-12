# Онлайн на одном ПК: две копии игры через localhost.
# Слева ХОСТ (клавиатура), справа КЛИЕНТ (геймпад, если подключён; иначе каждая копия слушает ввод только в фокусе).
# После закрытия обеих копий настройки экрана игры (полный экран/разрешение) возвращаются как были.
param(
    [string]$GameDir = 'D:\game\Lucky.Tower.Ultimate.v1.1.4',
    [ValidateSet('auto', 'keyboard', 'gamepad', 'all')][string]$HostDevice = 'auto',
    [ValidateSet('auto', 'keyboard', 'gamepad', 'all')][string]$ClientDevice = 'auto',
    [switch]$DevServer,   # отладочные порты 47800/47801 (для автотестов)
    [switch]$Steam,       # через Steam: хост — основной Steam, клиент — второй аккаунт Steam в песочнице Sandboxie (ящик $Box)
    [string]$Box = 'Steam2',
    [switch]$Force        # закрыть уже запущенную игру без вопроса
)
$ErrorActionPreference = 'Stop'
$exe = Join-Path $GameDir 'Lucky Tower Ultimate.exe'
if (-not (Test-Path $exe)) { throw "не найдена игра: $exe" }

$running = Get-Process 'Lucky Tower Ultimate' -ErrorAction SilentlyContinue
if ($running) {
    if (-not $Force) {
        $a = Read-Host "Игра уже запущена ($($running.Count) шт.). Закрыть и запустить две копии? [y/N]"
        if ($a -notmatch '^(y|д)') { return }
    }
    $running | Stop-Process -Force; Start-Sleep -Milliseconds 800
}

# Unity запоминает режим окна в реестре при выходе — сохраняем, чтобы вернуть полный экран после теста
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

# ---------- Steam: два аккаунта на одном ПК ----------
$sbStart = 'C:\Program Files\Sandboxie-Plus\Start.exe'
$steamExe = (Get-ItemProperty 'HKCU:\Software\Valve\Steam' -ErrorAction SilentlyContinue).SteamExe
function Get-BoxSteamUser {
    # пока в ящике что-то запущено, его реестр смонтирован в HKU\Sandbox_<пользователь>_<ящик>
    $hive = Get-ChildItem 'Registry::HKEY_USERS' -ErrorAction SilentlyContinue | Where-Object { $_.PSChildName -like "Sandbox_*_$Box" } | Select-Object -First 1
    if (-not $hive) { return 0 }
    $p = "Registry::$($hive.Name)\user\current\software\Valve\Steam\ActiveProcess"
    $v = (Get-ItemProperty $p -ErrorAction SilentlyContinue).ActiveUser
    if ($v) { return [int64]$v } else { return 0 }
}
if ($Steam) {
    if (-not (Test-Path $sbStart)) { throw "нужен Sandboxie-Plus (не найден $sbStart)" }
    if (-not $steamExe) { throw 'не найден Steam' }
    $sbIni = 'C:\Program Files\Sandboxie-Plus\SbieIni.exe'
    if (-not ((& $sbIni query $Box Enabled) -match 'y')) {
        & $sbIni set $Box Enabled y | Out-Null
        & $sbStart /reload | Out-Null
        "создан ящик Sandboxie «$Box»"
    }
    # основной Steam (аккаунт хоста)
    if (-not ((Get-ItemProperty 'HKCU:\Software\Valve\Steam\ActiveProcess' -ErrorAction SilentlyContinue).ActiveUser)) {
        "запускаю основной Steam…"
        Start-Process $steamExe -ArgumentList '-silent'
        for ($i = 0; $i -lt 90 -and -not (Get-ItemProperty 'HKCU:\Software\Valve\Steam\ActiveProcess' -ErrorAction SilentlyContinue).ActiveUser; $i++) { Start-Sleep 1 }
        if (-not (Get-ItemProperty 'HKCU:\Software\Valve\Steam\ActiveProcess' -ErrorAction SilentlyContinue).ActiveUser) { throw 'основной Steam не вошёл в аккаунт за 90 с — войдите и запустите снова' }
    }
    # второй Steam в ящике (аккаунт клиента)
    if ((Get-BoxSteamUser) -eq 0) {
        "запускаю второй Steam в песочнице «$Box»…"
        & $sbStart "/box:$Box" $steamExe | Out-Null
        for ($i = 0; $i -lt 60 -and (Get-BoxSteamUser) -eq 0; $i++) { Start-Sleep 1 }
        if ((Get-BoxSteamUser) -eq 0) {
            "второй Steam ещё не вошёл в аккаунт: войдите в окне Steam с жёлтой рамкой вторым аккаунтом (он должен быть в друзьях у основного),"
            "отметьте «Запомнить меня» и запустите этот скрипт снова."
            return
        }
    }
    "оба Steam в сети"
}
# Пока мод не загрузился, игра без фокуса стоит на паузе — поэтому копии запускаются по очереди:
# клиент стартует, когда хост уже загрузился (мод переименовывает окно в «… ХОСТ …»).
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
        if ($proc.HasExited) { throw "копия игры закрылась при запуске (pid $($proc.Id))" }
        if ([LtWin]::Titles($proc.Id) -match $text) { return $true }
    }
    return $false
}
$hostArgs = if ($Steam) { '--coop-steam-host' } else { '--coop-host' }
$h = Start-Process $exe -WorkingDirectory $GameDir -PassThru -ArgumentList "$hostArgs --coop-window left --coop-device $HostDevice$extra"
"запускаю хост (pid $($h.Id))…"
if (-not (Wait-Title $h 'ХОСТ' 90)) { "хост грузится дольше обычного — запускаю клиента всё равно" }
if ($Steam) {
    # клиент — в песочнице: его Steam (второй аккаунт), сохранения и настройки экрана остаются в ящике
    & $sbStart "/box:$Box" $exe --coop-steam-join --coop-window right --coop-device $ClientDevice $extra.Trim() | Out-Null
    $c = $null
    for ($i = 0; $i -lt 30 -and -not $c; $i++) { Start-Sleep 1; $c = Get-Process 'Lucky Tower Ultimate' -ErrorAction SilentlyContinue | Where-Object { $_.Id -ne $h.Id } | Select-Object -First 1 }
    if (-not $c) { throw 'копия игры в песочнице не запустилась' }
} else {
    $c = Start-Process $exe -WorkingDirectory $GameDir -PassThru -ArgumentList "--coop-join 127.0.0.1 --coop-window right --coop-device $ClientDevice$extra"
}
"запускаю клиента (pid $($c.Id)) — не переключайтесь из его окна, пока оно не встанет справа…"
if (Wait-Title $c 'КЛИЕНТ' 90) { "обе копии готовы: слева хост, справа клиент" } else { "клиент не загрузился за 90 с — щёлкните по его окну" }

# Фоновый сторож: ждёт закрытия обеих копий и возвращает настройки экрана
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
"после закрытия обеих копий настройки экрана вернутся сами"
