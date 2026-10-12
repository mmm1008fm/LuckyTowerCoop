# LuckyTowerCoop installer: finds the game folder itself, installs/updates BepInEx + the mod without touching settings.
# Run: double-click Install.cmd (or Uninstall.cmd, which passes -Uninstall).
param([switch]$Uninstall, [string]$GameDir)

$ErrorActionPreference = 'Stop'
$exeName = 'Lucky Tower Ultimate.exe'
$here = Split-Path -Parent $MyInvocation.MyCommand.Path
$payload = Join-Path (Split-Path -Parent $here) 'payload'

function Say($text, $color = 'Gray') { Write-Host $text -ForegroundColor $color }

function Test-GameDir($dir) { $dir -and (Test-Path (Join-Path $dir $exeName)) }

function Find-GameDir {
    # 1) next to the installer (unpacked straight into the game)
    $parent = Split-Path -Parent $here
    foreach ($d in @($parent, (Split-Path -Parent $parent))) { if (Test-GameDir $d) { return $d } }

    # 2) Steam libraries
    $steamRoots = @()
    foreach ($key in 'HKCU:\Software\Valve\Steam', 'HKLM:\SOFTWARE\WOW6432Node\Valve\Steam', 'HKLM:\SOFTWARE\Valve\Steam') {
        try {
            $p = Get-ItemProperty $key -ErrorAction Stop
            foreach ($v in $p.SteamPath, $p.InstallPath) { if ($v) { $steamRoots += ($v -replace '/', '\') } }
        } catch {}
    }
    $libraries = @()
    foreach ($root in $steamRoots | Select-Object -Unique) {
        $libraries += $root
        $vdf = Join-Path $root 'steamapps\libraryfolders.vdf'
        if (Test-Path $vdf) {
            foreach ($m in Select-String -Path $vdf -Pattern '"path"\s+"([^"]+)"') { $libraries += ($m.Matches[0].Groups[1].Value -replace '\\\\', '\') }
        }
    }
    foreach ($lib in $libraries | Select-Object -Unique) {
        $common = Join-Path $lib 'steamapps\common'
        if (-not (Test-Path $common)) { continue }
        foreach ($d in Get-ChildItem $common -Directory -ErrorAction SilentlyContinue | Where-Object Name -like 'Lucky Tower*') {
            if (Test-GameDir $d.FullName) { return $d.FullName }
        }
    }

    # 3) typical locations
    foreach ($drive in Get-PSDrive -PSProvider FileSystem) {
        foreach ($sub in 'Games', 'game', 'SteamLibrary\steamapps\common', 'Program Files (x86)\Steam\steamapps\common') {
            $base = Join-Path $drive.Root $sub
            if (-not (Test-Path $base)) { continue }
            foreach ($d in Get-ChildItem $base -Directory -ErrorAction SilentlyContinue | Where-Object Name -like 'Lucky*Tower*') {
                if (Test-GameDir $d.FullName) { return $d.FullName }
            }
        }
    }
    return $null
}

function Ask-GameDir {
    Add-Type -AssemblyName System.Windows.Forms
    $dlg = New-Object System.Windows.Forms.FolderBrowserDialog
    $dlg.Description = "Select the game folder (containing $exeName)"
    $dlg.ShowNewFolderButton = $false
    while ($dlg.ShowDialog() -eq [System.Windows.Forms.DialogResult]::OK) {
        if (Test-GameDir $dlg.SelectedPath) { return $dlg.SelectedPath }
        [System.Windows.Forms.MessageBox]::Show("$exeName not found in this folder", 'LuckyTowerCoop') | Out-Null
    }
    return $null
}

try {
    Say "LuckyTowerCoop - $(if ($Uninstall) { 'uninstall' } else { 'install' })" 'Cyan'
    if (-not (Test-GameDir $GameDir)) { $GameDir = Find-GameDir }
    if (-not $GameDir) { Say 'Game not found automatically - please select the folder.' 'Yellow'; $GameDir = Ask-GameDir }
    if (-not $GameDir) { throw 'no game folder selected' }
    Say "Game: $GameDir"

    while (Get-Process 'Lucky Tower Ultimate' -ErrorAction SilentlyContinue) {
        Say 'The game is running - close it and press Enter.' 'Yellow'; [void](Read-Host)
    }
    Get-Process CoopNet -ErrorAction SilentlyContinue | Stop-Process -Force -ErrorAction SilentlyContinue

    if ($Uninstall) {
        foreach ($f in 'winhttp.dll', 'doorstop_config.ini', '.doorstop_version') {
            $p = Join-Path $GameDir $f; if (Test-Path $p) { Remove-Item $p -Force }
        }
        $bep = Join-Path $GameDir 'BepInEx'
        if (Test-Path $bep) {
            $keep = Join-Path $env:TEMP "LuckyTowerCoop_config_$(Get-Date -Format yyyyMMdd_HHmmss)"
            if (Test-Path "$bep\config") { Copy-Item "$bep\config" $keep -Recurse; Say "Settings saved to $keep" }
            Remove-Item $bep -Recurse -Force
        }
        Say 'Mod removed. Game files were not modified.' 'Green'
        return
    }

    if (-not (Test-Path $payload)) { throw "payload folder not found next to the installer ($payload)" }
    $plugin = Join-Path $GameDir 'BepInEx\plugins\LuckyTowerCoop'
    $was = if (Test-Path "$plugin\LuckyTowerCoop.dll") { (Get-Item "$plugin\LuckyTowerCoop.dll").VersionInfo.FileVersion } else { $null }

    # copy everything except already existing settings
    Get-ChildItem $payload -Recurse -File -Force | ForEach-Object {
        $rel = $_.FullName.Substring($payload.Length + 1)
        $dst = Join-Path $GameDir $rel
        if ($rel -like 'BepInEx\config\*' -and (Test-Path $dst)) { return }
        New-Item -ItemType Directory -Force (Split-Path -Parent $dst) | Out-Null
        Copy-Item $_.FullName $dst -Force
    }

    # The Steam transport needs the original steam_api64.dll from Valve: take it from the game if the package does not include it
    $sidecarDll = Join-Path $plugin 'CoopNet\steam_api64.dll'
    if (-not (Test-Path $sidecarDll)) {
        foreach ($cand in "Lucky Tower Ultimate_Data\Plugins\x86_64\steam_api64.dll.valve", "Lucky Tower Ultimate_Data\Plugins\x86_64\steam_api64.dll") {
            $src = Join-Path $GameDir $cand
            if (Test-Path $src) { Copy-Item $src $sidecarDll -Force; break }
        }
    }

    $ok = (Test-Path (Join-Path $GameDir 'winhttp.dll')) -and (Test-Path "$plugin\LuckyTowerCoop.dll") -and (Test-Path "$plugin\CoopNet\CoopNet.exe")
    if (-not $ok) { throw 'files are missing after copying' }
    $now = (Get-Item "$plugin\LuckyTowerCoop.dll").VersionInfo.FileVersion
    Say ($(if ($was) { "Updated: $was -> $now" } else { "Installed: LuckyTowerCoop $now" })) 'Green'
    Say 'Launch the game. Online menu: F7.'
}
catch {
    Say "Error: $($_.Exception.Message)" 'Red'
    exit 1
}
finally {
    Say ''
    Say 'Press Enter to close the window.'
    [void](Read-Host)
}
