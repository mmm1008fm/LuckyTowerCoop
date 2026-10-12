# Lucky Tower Ultimate — карта кода для коопа

Игра: Unity 6000.0.67f1, Mono. Пути относительно `decompiled/Assembly-CSharp/`.
Статус: шаг 3 (локальный кооп) реализован, см. README.md. Ниже — карта кода игры; утверждения проверены по декомпиляции, ключевые — в рантайме.

## Игрок
| Что | Где |
|---|---|
| Класс | `LuckyTower/CharacterSystem/Player.cs` — `Player : Character` |
| Слоты игроков | `static Player[] VonWansts = new Player[4]`, `CurrentPlayer => VonWansts[0]` |
| Регистрация | `Player.OnEnable` (:128) — берёт первый свободный слот, `Stats.PlayerNumber = i+1`; при `RespawnSettings.Respawn` насильно слот 0 |
| Ввод | `Player.PlayerInput` (Rewired.Player, private set) = `GetPlayer(0)` в :60 (activeSceneChanged), :141, :150 |
| Спавн | `GameManager.SpawnPlayer(Vector3, bool firstSpawn, bool resetRespawn, bool spawnMonster)` (`GameManager.cs:1366`), префаб `m_playerPrefab` (:84) |
| Инициализация сцены | `GameManager.Initialize` (:774) → `Player.GameplayInit()` (:1076) |
| Между сценами | GameManager живёт в сцене; игрок переживает загрузку через `DontDestroyOnLoad` в `EnterDoorCommand.cs:184` |
| Ссылки «на игрока» | `Player.CurrentPlayer` ~1300, `GameManager.Instance.Player` ~900 (≈220 файлов) |

## Ввод (Rewired, 1 игрок `Player0` + System — проверено в рантайме)
- `CharacterUtilities.HandleInput()` (:3846): кнопки через `(m_character as Player).PlayerInput.GetButton(...)` — per-instance.
- Оси — **глобальные**: `CommandPattern.GetHorizontalAxis/GetVerticalAxis` (`CommandPattern.cs:366-387`) читают `Player.CurrentPlayer.PlayerInput`.
- `CommandPattern` — общий ScriptableObject, кеширует команды на первого персонажа (:167-364) → для P2 нужен `Instantiate`.
- Action ID: Jump 3, Attack 6, Throw 7, PickUp 8, Use 9, Swap 10, Up/Door 21, Down 22, Drop 26, MoveLeft 2, MoveRight 23.
- Второй игрок Rewired: InputManager инициализируется раньше BepInEx, поэтому P2 использует системного игрока Rewired (раскладки грузим сами, игра их сбрасывает — восстанавливаем раз в секунду).
- Сохранение раскладок: `TestStoreInput` — только player 0.

## Башня / генерация / RNG
- Сегмент генерируется целиком: `SegmentManager.CreateNextSegment` (`SegmentManager.cs:565`) → `RoomGenerator.GenerateSegmentFromConfig(cfg, seed)` (`RoomGeneration/RoomGenerator.cs:825`) → `GenerateFloor` → `GenerateRoom` → `HandleDoors` → `ConnectDoors`.
- Следующий сегмент — при первом входе в exit-комнату (`Room.OnPlayerEntersFirst`).
- RNG: глобальный `UnityEngine.Random`, сид `SegmentManager.ResetSeed()` (:511; `DateTime.Now.Ticks` если не задан), `SetSeed(int)` (:559).
- Недетерминизм (важно для онлайна): `new System.Random()` в `RoomGenerator.cs:755`, рандомайзеры в `Start/OnEnable` комнат, `Time.time`.

## Двери
Цепочка: `EnterDoorTrigger` (подсказка) → `HandleInput` → `EnterDoorCommand.GetKeyDown` (:59, action 21) → `DoorDownAction()` (:95, private) → `GameManager.EnterDoor(Door)` (`GameManager.cs:1479`) → `EnterDoorRoutine` (:1979):
freeze + анимация (:2044-2047) → компаньоны (:2095) → fade (:2152) → **смена комнаты** (:2259-2261) → **телепорт** `Player.transform.position` (:2283) → `TeleportCompanionsToPlayer` (:2359) → `LayerManager.MoveToLayer` (:2393) → камера (:2397-2399) → `UnFreeze`/`FadeIn`.
- Особые двери (сцена/сегмент/bubble) идут в обход: `EnterDoorCommand.EnterSpecialDoor` (:284), `EnterSegmentRoutine`, `EnterDoorSideRoutine` (:1769).
- Хуки голосования: префикс `EnterDoorCommand.DoorDownAction` (голос), префикс `GameManager.EnterDoor` (коммит), шаблон переноса — `CharacterUtilities.TeleportCompanionsToPlayer` (:4563).

## Смерть / урон / враги
- Урон: `CharacterAction.GetDamaged(...)` (`CharacterAction.cs:2037`); источники `DamageOnTouch`, `Weapon` — фильтр по слоям, P2 на слое Player будет получать урон.
- Смерть: `CharacterAction.Die()` (:1088); решение «конец забега» в хвосте (:1618-1633) → `GameManager.ShowDeathUI(float, bool)` (:1539) — общая точка всех game over.
- Respawn-бутылка: `RespawnRoutine` (:1819) уничтожает `GameManager.Player` и спавнит в слот 0 → для P2 не годится напрямую, только как шаблон. `Resurrect()` (:2021) неработоспособен.
- Смерть врага + убийца: `AchievementManager.TrackDeath(Character, Damagable, IDamageSource)` (`AchievementManager.cs:218`), killer = `damageSource.DamagingCharacter`.
- AI целится в `VonWansts[0]`: `CharacterUtilities` (:3202-3677), `PatrolingEnemyUtilities` (:242-1429). Драйверы AI — только `Enemy.Update` и `NPC.Update` → подмена `VonWansts[0]` на ближайшего живого на время апдейта.
- `CombatManager.AddEnemy` (:55) смотрит на здоровье P1; `EnemyDetectionRange` висит на игроке; `FlyingBehaviour.m_player`.

## Предметы (две руки, монеты, пояс)
- Всё per-instance в `CharacterStats`: основная рука `CurrentWeapon` (:1001), вторая `ItemInSecondHand` (:2360), пояс-кошель `CurrentGoldSack` (:2358), броня/шлем (:993-1005), зубы `TeethInventory`.
- Монеты живут в `GoldSack.CurrentGold` (макс 20); сумма `CharacterStats.CurrentGoldCount()` (:2946).
- Баги для P2: `GoldSack` (`Items/GoldSack.cs:94-261`) и `CoinSpecialItem.Pickup/Equip` используют `CurrentPlayer`/`GameManager.Player`.
- Сохранение инвентаря между сценами — один слот (`DataManager.GameData.Inventory`), только P1.
- HUD нет: всё рисуется на самом персонаже.

## Камера
- `CameraManager` (`LuckyTower/CameraManager.cs`): `CinemachineVirtualCamera` + `CinemachineConfiner` + `FramingTransposer`.
- Follow: `CameraManager.Initialize` (:814-825) → `Player.CurrentPlayer.Components.CameraFocusPoint`; переприменяется в `SetFramingComposerValues` (:956) при смене комнаты.
- `CinemachineTargetGroup` в игре нет → добавляем свой.

## Сцены
MainMenu, MasterScene (башня), VillageScene, Tutorial, FinalTower, … Загрузка — `SceneLoader.LoadSceneByName` (single mode), перед ней `GameManager.DestroyAll()`.
