# Задачи для дешёвой модели (Sonnet/Haiku)

Каждая задача самодостаточна: контекст, файлы, команды, критерий готовности. Делать по одной, после каждой — сборка и автотест.

## Общий контекст
- Проект: `D:\game\LuckyTowerCoop` (BepInEx/HarmonyX-мод кооп-на-двоих для Lucky Tower Ultimate). Игра: `D:\game\Lucky.Tower.Ultimate.v1.1.4`.
  Карта классов игры — `docs/CODE_MAP.md`, декомпилированный код — `decompiled/Assembly-CSharp/`. План и состояние — `docs/PLAN.md`.
- Сборка (деплоит dll в игру; перед ней закрыть обе копии игры):
  ```powershell
  Get-Process 'Lucky Tower Ultimate' -ErrorAction SilentlyContinue | Stop-Process -Force
  dotnet build D:\game\LuckyTowerCoop\src\LuckyTowerCoop\LuckyTowerCoop.csproj -c Debug -nologo -v q
  ```
- Автотест (поднимает хост+клиент на одном ПК через localhost, идёт по комнатам, сравнивает число объектов):
  ```powershell
  pwsh D:\game\LuckyTowerCoop\tools\route-test.ps1 -Rooms 40 -Until Balcony
  ```
  Критерий «всё хорошо»: в каждой строке `entities host=N client=N` равны, в конце `resync: host 0, client 0`, в `errors` только `Loading standard input`.
  Строки `unresolved: ... not found here: <путь>` показывают объекты хоста, которых нет у клиента; ниже печатается `ls host/client` родителя.
- Dev-команды к запущенным копиям (хост порт 47800 → `DevA`, клиент 47801 → `DevB`):
  ```powershell
  . D:\game\LuckyTowerCoop\tools\devlib.ps1; DevA 'where'; DevB 'net status'
  ```
  Полезные: `where` (игроки, комната), `doors`, `exits`, `rooms` (все комнаты башни), `netents` (сущности сети), `ent <id>`,
  `ls <путь от корня комнаты>` (если в именах есть «/», разделитель — «|»), `log N <фильтр>` (ответ режется UDP ~64 КБ — всегда с фильтром),
  `tp p1|p2 x y`, `tap p1 up|down|talk|opt1|cancel`, `killall`, `unlock` (поднять решётки дверей), `free` (снять замки движения),
  `lights out|on`, `net sim <мс>` (имитация пинга), `net resync`.
- Лог мода: `D:\game\Lucky.Tower.Ultimate.v1.1.4\BepInEx\LogOutput.log` (хост); у клиента — своя копия, смотреть через `DevB 'log 50 <фильтр>'`.
- Правила: хост — авторитет, клиент — зеркало. Логика мира у клиента глушится списком типов в `Patches/WorldPatches.cs` (`s_extraTypes`),
  случайности сидируются списком в `Patches/RandomizerPatches.cs`, объекты регистрируются в `Net/NetHost.cs` (`WorldObjects`).
  Пути объектов: `Net/NetEntities.cs` → `NetPaths` (разделители — служебные символы, для логов `NetPaths.Pretty`).

## 1. ~~Релиз 0.3.4~~ — сделано. Релиз: pwsh tools\make-dist.ps1, архив в dist\.

## 2. Обломки «leftover» и объекты, упавшие за карту
Симптом: в пещерах/комнате `Dead` у клиента `not found here: PlantRemains/ArmorLeftOvers/leftover ...`; объект `leftover` с y ≈ −90000 падает вечно.
Сделать: в `Net/NetHost.cs` → `WorldObjects` → локальная `Add(Component c)` добавить исключение: имя начинается с `leftover`
(`go.name.StartsWith("leftover", OrdinalIgnoreCase)`) или среди родителей есть `ArmorLeftOvers`/`PlantRemains`. Это косметика (обломки брони).
Проверка: автотест до пещер `-Until Cave` — предупреждений про `leftover` нет, `entities` равны.

## 3. Бочка с порохом: видео взрыва башни у клиента
Симптом: `TrapPowderKeg.DestroyTowerRoutine` (decompiled `LuckyTower/Traps/TrapPowderKeg.cs:317`) у хоста играет видео
`VideoLibrary.ExplodingTower` и убивает игрока; клиент видео не видит.
Сделать: новый `Msg.TowerExplosion = 31` в `Net/NetTypes.cs`; префикс на `TrapPowderKeg.DestroyTower` (новый `Patches/PowderKegPatches.cs`):
хост → `NetHost.SendTowerExplosion()` (по образцу `SendLights`), клиент → в `NetClient.OnMessage` обработчик запускает тот же ролик
(`CameraManager.Instance.MainVideoPlayer`, `clip = GameManager.Instance.VideoLibrary.ExplodingTower`, `renderMode = CameraNearPlane`,
`PlayingVideo = true` на время), ввод глушить через `Stats.InputAllowed=false` на своих персонажах, после ролика — `PlayingVideo=false`.
Смерть после видео придёт обычным путём (PlayerState). У клиента сам `DestroyTowerRoutine` не должен выполняться (`TrapPowderKeg` — Trap, уже глушится).
Проверка: найти комнату с бочкой (`DevA traps` содержит `TrapPowderKeg`), `DevA 'tp p1 <x> <y>'` к бочке, поджечь ударом (`tap p1 attack`),
дождаться; в логе клиента `[NetClient] tower explosion`, ошибок нет.

## 4. ~~Предсказание движения клиента~~ — сделано в 0.3.6 (сверка по номеру нажатия, стенд tools\net-quality.ps1)
Проверить, что при пинге персонаж клиента начинает идти сразу: на обеих копиях `net sim 80`; `DevB 'tap p1 right'` и сразу `DevB where`
несколько раз подряд (каждые 50 мс): `pos.x` должен меняться в первые ~2 кадра, а не через 160 мс. Если нет — смотреть `NetClient.PredictOwn`
(условия `Blocked`), поправить, записать результат в `docs/PLAN.md` («Фаза 1б»).

## 5. Редкая рассинхронизация сегмента 3
Один раз у клиента не нашлась комната `S03_F04_R02` (генерация разошлась). Теперь при `Dev.CommandServer=true` в лог пишется отпечаток
`[Gen] segment ... seed=... unity=... rooms=...` на обеих сторонах.
Сделать: 5 раз подряд `pwsh tools\route-test.ps1 -Rooms 40 -Until 'S03_'`; после каждого — `DevA 'log 20 [Gen]'` и `DevB 'log 20 [Gen]'`,
сравнить строки `segment DungeonThree`. Если `resync` > 0 или отпечатки разные — сохранить обе строки в `docs/PLAN.md` (раздел «Состояние»)
с указанием, что отличается (seed/unity-состояние/игрок/companions/список комнат).

## 6. Пещеры после башни
Генерация пещер теперь сидирована (`Patches/CaveSeedPatch.cs`). Проверить: автотест `-Rooms 60 -Until Cave` (маршрут идёт через ExitRoom →
Exit → Entrance → Cave). В комнатах `Entrance`/`Cave` `entities host/client` должны совпадать, `ls` по `LevelElements` одинаковый.
Если различаются — в `decompiled/.../RoomGenerator.cs` метод `GenerateCave` искать другие источники случайности
(`UnityEngine.Random` до `ResetSeed`, `Guid`, `DateTime`) и сидировать по образцу `CaveSeedPatch`.

## 7. Не делать без пользователя
Steam с двумя аккаунтами на разных ПК, реальные геймпады — только с участием пользователя.

## Очередь отработки (с 12.10, вечер) — порядок выполнения
Работать в ветке `work/queue` (от `main`), по коммиту на задачу, в конце каждого сообщения коммита строка
`Co-Authored-By: Claude Sonnet 5.5 <noreply@anthropic.com>`. `git add` только своих файлов. В `main` не сливать, на Диск не выкладывать.
Регрессия после каждой задачи: `pwsh tools\route-test.ps1 -Rooms 10` (объекты равны, resync 0, новых ошибок нет).

### A. Покупки вторым игроком платятся его монетами
Причина: проверка и списание цены идут через `GameManager.Instance.Player` / `Player.CurrentPlayer` (всегда P1).
Места: `LuckyTower/RoomGeneration/Automat.cs:356`, `CompanionAutomat.cs:253`, `PipeSystem.cs:423,565`, `Pawnshop.cs:599`,
`LuckyTower/Village/PawnshopMain.cs:455`, `Barber.cs:329`, `Pipefitter.cs:348`, `LuckyTower/Evelius/EvTasks.cs:741`,
`DropAllCoinsDialogue.cs`, `CharacterUtilities.cs:4851,5315` (пути от `decompiled/Assembly-CSharp/`). Найти и методы, которые списывают золото.
Решение: платит тот, кто покупает. Методы покупки/оплаты этих классов выполнять под подменой «текущего игрока» на покупателя —
так же, как уже сделано для других действий: `Patches/ActingAsPatches.cs` (TargetSwap, `OwnerOf`), `Patches/ContextPatches.cs`.
Покупатель = персонаж, чей ввод выбрал вариант в окне (`InteractionWindow` его Components) или кто стоит в триггере.
Проверка: dev-командой выдать монеты P2 (если команды нет — добавить `gold p1|p2 N` в `Dev/DevServer.cs`), найти комнату с автоматом/магазином,
купить вторым: у P2 монет меньше на цену, у P1 без изменений; то же первым. Локально и онлайн (`local-online.ps1 -DevServer`).

### B. Передача монет напарнику
Вкладка «Игроки» меню F7 (`Coop/CoopMenuPages.cs`): кнопки «Передать 1 / 10 / все монеты напарнику» для своего персонажа.
Онлайн у клиента — запрос хосту новым сообщением `Msg` (по образцу `RestartRequest` в `Net/NetTypes.cs`, `NetClient`, `NetHost`), меняет только хост.
Перенос через API `GoldSack` (найти добавление/убавление монет; у получателя без кошелька — создать как при подборе монеты,
посмотреть `CoinSpecialItem`, `GoldStash`). Dev-команда `givecoins p1|p2 N`. Проверка: счётчики `золото:` у обоих игроков на хосте и у клиента.

### C. Обломки «leftover» — задача 2 выше.
### D. Видео взрыва башни у клиента — задача 3 выше.
### E. Пещеры — задача 6 выше; редкий рассинхрон сегмента 3 — задача 5 выше.

### Отчёт
В конце — `docs/QUEUE-REPORT.md`: по каждой задаче что сделано, коммит, как проверено (цифры), что не получилось и почему.