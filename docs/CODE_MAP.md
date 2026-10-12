# Lucky Tower Ultimate — code map for co-op

Game: Unity 6000.0.67f1, Mono. Paths are relative to `decompiled/Assembly-CSharp/`.
Status: step 3 (local co-op) is implemented, see README.md. Below is a map of the game's code; claims were checked against the decompilation, key ones at runtime.

## Player
| What | Where |
|---|---|
| Class | `LuckyTower/CharacterSystem/Player.cs` — `Player : Character` |
| Player slots | `static Player[] VonWansts = new Player[4]`, `CurrentPlayer => VonWansts[0]` |
| Registration | `Player.OnEnable` (:128) — takes the first free slot, `Stats.PlayerNumber = i+1`; with `RespawnSettings.Respawn` it forces slot 0 |
| Input | `Player.PlayerInput` (Rewired.Player, private set) = `GetPlayer(0)` at :60 (activeSceneChanged), :141, :150 |
| Spawn | `GameManager.SpawnPlayer(Vector3, bool firstSpawn, bool resetRespawn, bool spawnMonster)` (`GameManager.cs:1366`), prefab `m_playerPrefab` (:84) |
| Scene initialization | `GameManager.Initialize` (:774) → `Player.GameplayInit()` (:1076) |
| Between scenes | GameManager lives in the scene; the player survives scene loads via `DontDestroyOnLoad` in `EnterDoorCommand.cs:184` |
| References "to the player" | `Player.CurrentPlayer` ~1300, `GameManager.Instance.Player` ~900 (≈220 files) |

## Input (Rewired, 1 player `Player0` + System — checked at runtime)
- `CharacterUtilities.HandleInput()` (:3846): buttons via `(m_character as Player).PlayerInput.GetButton(...)` — per-instance.
- Axes are **global**: `CommandPattern.GetHorizontalAxis/GetVerticalAxis` (`CommandPattern.cs:366-387`) read `Player.CurrentPlayer.PlayerInput`.
- `CommandPattern` is a shared ScriptableObject that caches commands for the first character (:167-364) → P2 needs an `Instantiate`.
- Action IDs: Jump 3, Attack 6, Throw 7, PickUp 8, Use 9, Swap 10, Up/Door 21, Down 22, Drop 26, MoveLeft 2, MoveRight 23.
- Second Rewired player: InputManager initializes before BepInEx, so P2 uses the Rewired System player (we load the layouts ourselves; the game resets them, so we restore them once a second).
- Saving layouts: `TestStoreInput` — player 0 only.

## Tower / generation / RNG
- A segment is generated in one go: `SegmentManager.CreateNextSegment` (`SegmentManager.cs:565`) → `RoomGenerator.GenerateSegmentFromConfig(cfg, seed)` (`RoomGeneration/RoomGenerator.cs:825`) → `GenerateFloor` → `GenerateRoom` → `HandleDoors` → `ConnectDoors`.
- The next segment is created on first entry into the exit room (`Room.OnPlayerEntersFirst`).
- RNG: global `UnityEngine.Random`, seed from `SegmentManager.ResetSeed()` (:511; `DateTime.Now.Ticks` if not set), `SetSeed(int)` (:559).
- Non-determinism (important for online): `new System.Random()` in `RoomGenerator.cs:755`, randomizers in rooms' `Start/OnEnable`, `Time.time`.

## Doors
Chain: `EnterDoorTrigger` (prompt) → `HandleInput` → `EnterDoorCommand.GetKeyDown` (:59, action 21) → `DoorDownAction()` (:95, private) → `GameManager.EnterDoor(Door)` (`GameManager.cs:1479`) → `EnterDoorRoutine` (:1979):
freeze + animation (:2044-2047) → companions (:2095) → fade (:2152) → **room change** (:2259-2261) → **teleport** `Player.transform.position` (:2283) → `TeleportCompanionsToPlayer` (:2359) → `LayerManager.MoveToLayer` (:2393) → camera (:2397-2399) → `UnFreeze`/`FadeIn`.
- Special doors (scene/segment/bubble) bypass this: `EnterDoorCommand.EnterSpecialDoor` (:284), `EnterSegmentRoutine`, `EnterDoorSideRoutine` (:1769).
- Voting hooks: prefix on `EnterDoorCommand.DoorDownAction` (vote), prefix on `GameManager.EnterDoor` (commit), transfer template — `CharacterUtilities.TeleportCompanionsToPlayer` (:4563).

## Death / damage / enemies
- Damage: `CharacterAction.GetDamaged(...)` (`CharacterAction.cs:2037`); sources `DamageOnTouch`, `Weapon` — filtered by layers, so P2 on the Player layer takes damage.
- Death: `CharacterAction.Die()` (:1088); the "run over" decision is at the tail (:1618-1633) → `GameManager.ShowDeathUI(float, bool)` (:1539) — the single entry point for every game over.
- Respawn bottle: `RespawnRoutine` (:1819) destroys `GameManager.Player` and spawns into slot 0 → unusable for P2 directly, only as a template. `Resurrect()` (:2021) does not work.
- Enemy death + killer: `AchievementManager.TrackDeath(Character, Damagable, IDamageSource)` (`AchievementManager.cs:218`), killer = `damageSource.DamagingCharacter`.
- AI targets `VonWansts[0]`: `CharacterUtilities` (:3202-3677), `PatrolingEnemyUtilities` (:242-1429). The only AI drivers are `Enemy.Update` and `NPC.Update` → swap `VonWansts[0]` for the nearest living player for the duration of the update.
- `CombatManager.AddEnemy` (:55) looks at P1's health; `EnemyDetectionRange` hangs on the player; `FlyingBehaviour.m_player`.

## Items (two hands, coins, belt)
- Everything is per-instance in `CharacterStats`: main hand `CurrentWeapon` (:1001), second hand `ItemInSecondHand` (:2360), belt pouch `CurrentGoldSack` (:2358), armor/helmet (:993-1005), teeth `TeethInventory`.
- Coins live in `GoldSack.CurrentGold` (max 20); the total is `CharacterStats.CurrentGoldCount()` (:2946).
- Bugs for P2: `GoldSack` (`Items/GoldSack.cs:94-261`) and `CoinSpecialItem.Pickup/Equip` use `CurrentPlayer`/`GameManager.Player`.
- Inventory persistence between scenes is a single slot (`DataManager.GameData.Inventory`), P1 only.
- No HUD: everything is drawn on the character itself.

## Camera
- `CameraManager` (`LuckyTower/CameraManager.cs`): `CinemachineVirtualCamera` + `CinemachineConfiner` + `FramingTransposer`.
- Follow: `CameraManager.Initialize` (:814-825) → `Player.CurrentPlayer.Components.CameraFocusPoint`; re-applied in `SetFramingComposerValues` (:956) on room change.
- The game has no `CinemachineTargetGroup` → we add our own.

## Scenes
MainMenu, MasterScene (tower), VillageScene, Tutorial, FinalTower, … Loading is `SceneLoader.LoadSceneByName` (single mode), preceded by `GameManager.DestroyAll()`.
