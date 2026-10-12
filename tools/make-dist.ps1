# Build the distribution archive: Release plugin + CoopNet + BepInEx + installer.
# Result: dist\LuckyTowerCoop_<version>.zip  (contents: Install.cmd, Uninstall.cmd, README.txt, installer\, payload\)
param([string]$GameDir = 'D:\game\Lucky.Tower.Ultimate.v1.1.4')
$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot

$b = dotnet build "$root\src\LuckyTowerCoop\LuckyTowerCoop.csproj" -c Release -nologo -v q -p:GameDir="$GameDir" 2>&1
if ($b -match ': error ') { $b | Select-String ': error '; throw 'build failed' }
$p = dotnet publish "$root\src\CoopNet\CoopNet.csproj" -c Release -nologo -v q -p:GameDir="$GameDir" 2>&1
if ($p -match ': error ') { $p | Select-String ': error '; throw 'publish failed' }

$version = (Get-Item "$GameDir\BepInEx\plugins\LuckyTowerCoop\LuckyTowerCoop.dll").VersionInfo.FileVersion
$stage = Join-Path $root "dist\stage_$(Get-Date -Format yyyyMMdd_HHmmss)"
$pl = "$stage\payload"
New-Item -ItemType Directory -Force "$pl\BepInEx\plugins", "$stage\installer" | Out-Null
Copy-Item "$GameDir\winhttp.dll", "$GameDir\doorstop_config.ini", "$GameDir\.doorstop_version" $pl
Copy-Item "$GameDir\BepInEx\core" "$pl\BepInEx\core" -Recurse
Copy-Item "$GameDir\BepInEx\plugins\LuckyTowerCoop" "$pl\BepInEx\plugins\LuckyTowerCoop" -Recurse
Copy-Item "$PSScriptRoot\installer\install.ps1" "$stage\installer\"

# .cmd files are pure ASCII, so any encoding works
$ascii = [System.Text.Encoding]::ASCII
[IO.File]::WriteAllText("$stage\Install.cmd", "@echo off`r`npowershell -NoProfile -ExecutionPolicy Bypass -File `"%~dp0installer\install.ps1`"`r`n", $ascii)
[IO.File]::WriteAllText("$stage\Uninstall.cmd", "@echo off`r`npowershell -NoProfile -ExecutionPolicy Bypass -File `"%~dp0installer\install.ps1`" -Uninstall`r`n", $ascii)
@"
LuckyTowerCoop $version - co-op mod for Lucky Tower Ultimate

Install: unpack the archive anywhere and run "Install.cmd".
It finds the game by itself (or asks for the folder), installs/updates the mod and keeps your settings.
Uninstall: "Uninstall.cmd" (game files are not modified).

In game: F7 opens the online menu. Host: "Create game". Friend: "Open friends list (Steam)" -> "Join".
Both players need Steam running. Both must have the same mod version.
"@ | Set-Content "$stage\README.txt" -Encoding utf8

$zip = Join-Path $root "dist\LuckyTowerCoop_$version.zip"
if (Test-Path $zip) { Rename-Item $zip "LuckyTowerCoop_${version}_old_$(Get-Date -Format HHmmss).zip" }
Compress-Archive -Path "$stage\*" -DestinationPath $zip
"dist: $zip ($([math]::Round((Get-Item $zip).Length / 1MB, 1)) MB)"
