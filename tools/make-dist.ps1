# Сборка архива для раздачи: Release-плагин + CoopNet + BepInEx + установщик.
# Результат: dist\LuckyTowerCoop_<версия>.zip  (внутри: Установить.cmd, Удалить.cmd, installer\, payload\)
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

$enc = [System.Text.Encoding]::GetEncoding(866)  # .cmd читается в OEM-кодировке консоли
[IO.File]::WriteAllText("$stage\Установить.cmd", "@echo off`r`npowershell -NoProfile -ExecutionPolicy Bypass -File `"%~dp0installer\install.ps1`"`r`n", $enc)
[IO.File]::WriteAllText("$stage\Удалить.cmd", "@echo off`r`npowershell -NoProfile -ExecutionPolicy Bypass -File `"%~dp0installer\install.ps1`" -Uninstall`r`n", $enc)
@"
LuckyTowerCoop $version — кооп для Lucky Tower Ultimate

Установка: распакуйте архив куда угодно и запустите «Установить.cmd».
Он сам найдёт игру (или спросит папку), поставит/обновит мод и сохранит ваши настройки.
Удаление: «Удалить.cmd» (файлы игры не меняются).

В игре: F7 — онлайн-меню. Хост: «Создать игру». Друг: «Открыть список друзей (Steam)» → «Войти».
Нужен запущенный Steam у обоих. У обоих должна стоять одна и та же версия мода.
"@ | Set-Content "$stage\ПРОЧТИ.txt" -Encoding utf8

$zip = Join-Path $root "dist\LuckyTowerCoop_$version.zip"
if (Test-Path $zip) { Rename-Item $zip "LuckyTowerCoop_${version}_old_$(Get-Date -Format HHmmss).zip" }
Compress-Archive -Path "$stage\*" -DestinationPath $zip
"dist: $zip ($([math]::Round((Get-Item $zip).Length / 1MB, 1)) MB)"
