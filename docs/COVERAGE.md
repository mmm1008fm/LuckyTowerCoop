# Game Mechanics Coverage Matrix (co-op mod)

Analysis date: 2026-10-12. Method: static review of `decompiled/Assembly-CSharp/` (1086 files, 1135 classes) and `src/LuckyTowerCoop/`; the game was not run. `QUEUE-REPORT.md` does not exist, so "verified" is taken from `docs/PLAN.md` (the "Status" section), `docs/TASKS.md` and `README.md`.

## How to read
- **GM / CP** — the number of occurrences of `GameManager.Instance.Player` (also `InstanceNoWarnings`, `?.`) and `Player.CurrentPlayer` in the class file; for rows with several classes, the sum over unique files. These are occurrences, not lines (`grep -c` gives slightly fewer). A class nested in another file shares the counters with that file. `Player.VonWansts[0]` appears in the game 41 more times (mostly in `CharacterUtilities`, `PatrolingEnemyUtilities`).
- **Coverage** — which mod mechanism applies:
  - *muted on client* — the class's Update/FixedUpdate/LateUpdate, triggers and collisions are disabled on the client (`WorldPatches`: all descendants of `Trap` except `FlyingBehaviour` and `SpecialItem`, plus `s_extraTypes`; `NetPatches`: Player, Enemy, NPC, FlyingBehaviour);
  - *P2 swap* — for the duration of the call the "current player" = P2 (`TargetSwap`): P2's coroutines and those inherited from them, physics callbacks when P2's collider enters, Update of components on the P2 object, `GetDamaged`, `TransformAction`, weapon hit, purse; for AI, the nearest player (`Enemy`, evil `NPC`, `FlyingBehaviour`, `EnemyDetectionRange`);
  - *forwarded* — `Window`, `Talk`, `LightsOut`, `PlayerState`, `SceneLoad` messages and object snapshots from `NetHost.WorldObjects` (everything with `Animator`, `Rigidbody2D`, `Trap`, `Lever`, `Damagable`, `TreasureChest`, `FlyingBehaviour`, `Character`, `PickupItem`);
  - *seeded* — `RandomizerPatches` (16 types), `CaveSeedPatch`, the shared run seed (`SegmentManager.ResetSeed`);
  - *none* — there is no dedicated code.
- **Status**: ✅ covered by a mechanism and named as verified in PLAN/TASKS/README; 🟡 covered by a mechanism, not verified; 🔴 by the code, probably does not work for P2 or on the client (reason in 5–10 words).
- Key point: the `Update` of traps and managers on the host is **not** wrapped in the swap. The swap only applies on P2's objects, in P2's callbacks and in coroutines started under the swap. Anything that reads `Player.CurrentPlayer` from someone else's `Update` sees P1.
- The README line in "Known limitations" about the transformation potion is outdated: PLAN (phase 5) lists the monster potion for P2 as verified; the line about the respawn bottle is correct.

**Summary (105 mechanics): ✅ 10 · 🟡 67 · 🔴 28.**

| Section | ✅ | 🟡 | 🔴 | total |
|---|---|---|---|---|
| 1. Traps | 2 | 18 | 7 | 27 |
| 2. Special rooms and challenges | 4 | 16 | 3 | 23 |
| 3. NPCs and dialogues | 1 | 7 | 5 | 13 |
| 4. Evil mage Evelius | 0 | 1 | 4 | 5 |
| 5. Companions | 0 | 3 | 3 | 6 |
| 6. Special items and potions | 2 | 10 | 2 | 14 |
| 7. Death and revival | 0 | 5 | 0 | 5 |
| 8. Village | 0 | 2 | 0 | 2 |
| 9. Endings and cutscenes | 0 | 1 | 2 | 3 |
| 10. Achievements | 0 | 1 | 0 | 1 |
| 11. Common systems | 1 | 3 | 2 | 6 |

## 1. Traps

| Mechanic | Classes | What it does | GM / CP | Mod coverage | St. | Notes |
|---|---|---|---|---|---|---|
| Spikes: floor, connector, stakes | `TrapSpike`, `TrapSpikeConnector`, `StakeTrap` | Spikes extend on a timer/trigger and hit the Player layer | 0 / 0 | muted on client; in snapshot; physics callbacks under P2 swap | 🟡 | damage on the Player layer: P2 suffers like P1 |
| Stompers and crushers | `Stomper`, `CrusherTrap`, `CrusherManagerSemi`, `CrusherManager`, `DungeonEndCrusherMisc` | Falling presses, the crusher at the end of the dungeon | 0 / 0 | muted on client; in snapshot; physics callbacks under P2 swap | 🟡 | stomper dust is not registered (cosmetic) |
| Anvil | `Anvil` | Falls when passed; breaks on a sharp weapon | 0 / 0 | muted on client; in snapshot; physics callbacks under P2 swap | 🟡 |  |
| Arrow wall and punching wall | `ArrowWall`, `PunchWall` | Shoots arrows / punches out of the wall | 0 / 0 | muted on client; in snapshot; physics callbacks under P2 swap | 🟡 |  |
| Pendulum | `TrapPendulum` | Swinging pendulum | 0 / 0 | muted on client; in snapshot; physics callbacks under P2 swap | 🟡 |  |
| Hanging bomb | `HangingBomb` | Bomb on a rope: gets cut and explodes | 0 / 3 | muted on client; in snapshot; physics callbacks under P2 swap | 🟡 | the "weapon in hand" check in OnTriggerEnter2D runs under the P2 swap |
| Bridge platform | `BridgePlatform` | Bridge raises/lowers via a lever | 0 / 0 | muted on client; in snapshot; physics callbacks under P2 swap | 🟡 |  |
| Crumbling floor and gaps | `TrapCrumblingGap`, `DungeonEndCrumblingStep`, `ParkourGapInFloorTrap`, `TrapGap` | Floor falls under weight, parkour gaps, a maw grabs the character | 0 / 0 | muted on client; in snapshot; physics callbacks under P2 swap | 🟡 | the "floor falls late" complaint was closed in phase 2, not re-checked live |
| Monsters from gaps and walls | `PotentialMonster`, `MonsterInGap`, `MonsterInGapTrigger`, `MonsterInWall`, `MonsterInWallTrigger`, `MonsterInWallGrab` …(+1) | A monster jumps out of the gap/wall as you pass | 0 / 0 | muted on client; in snapshot; physics callbacks under P2 swap; objects created on the fly are created on the client from the prefab | 🟡 | "present in 0.3.2 — to check" (PLAN, phase 2) |
| Hooks | `Hook`, `HookOnTouch`, `HookedItemParent` | A hook catches an item or a character | 1 / 0 | muted on client; in snapshot; physics callbacks under P2 swap | 🟡 |  |
| Wind zone | `TrapWindArea` | Wind blows the player away | 0 / 4 | muted on client; in snapshot; callbacks under P2 swap, **Update without swap (target = P1)** | 🟡 | the trigger condition uses CurrentPlayer (P1), the zone catches both colliders |
| Hammer guy (TrapHammerGuy) | `TrapHammerGuy`, `Hammerguy`, `TrapHammerguyFallSmallGap` | The hammer guy chases the player and their companions | 0 / 40 | muted on client; in snapshot; callbacks under P2 swap, **Update without swap (target = P1)** | 🔴 | chases and hits only Player.CurrentPlayer (P1) |
| Hammer guy in the wall | `HammerGuyWall` | Grabs the player's companions out of the wall | 4 / 0 | muted on client; in snapshot; callbacks under P2 swap, **Update without swap (target = P1)** | 🔴 | takes only P1's companion group |
| Sleeping giant (Sleeper, TrapSleeper) | `Sleeper`, `TrapSleeper` | Stealth: you must not run, jump or attack nearby, or it wakes up | 0 / 30 | muted on client; in snapshot; callbacks under P2 swap, **Update without swap (target = P1)** | 🔴 | woken by P1's input (Input.GetKey of its keys) |
| Orchestra (TrapBigBand) | `TrapBigBand` | The musicians wake up if the player makes noise nearby; drums | 0 / 18 | muted on client; in snapshot; callbacks under P2 swap, **Update without swap (target = P1)**; world sound is synchronized | 🔴 | stealth check uses P1's input and position |
| Lights out (TrapLightsOut) | `TrapLightsOut`, `LightsOutLever`, `LightsOutTrigger`, `DoorLightEvents`, `TorchLightHandler` | Darkness in the room; a lever or the exit brings the light back | 6 / 9 | muted on client; in snapshot; physics callbacks under P2 swap; Msg.LightsOut is forwarded | ✅ | "lights out" verified in both directions by autotest (PLAN) |
| Monster in the dark | `LightsOutMonster` | A monster appears in the dark and strangles the player | 1 / 1 | not muted (MonoBehaviour outside the list); in snapshot | 🔴 | m_currentPlayer caches P1 |
| Powder keg | `TrapPowderKeg`, `SpecialItemPowderKeg`, `KegActivation`, `PowderKegEquipHelper` | Tower explosion: video, player death, end of the run | 13 / 4 | muted on client; in snapshot; physics callbacks under P2 swap; the item version is not muted | 🔴 | kills only slot0, the video is not sent to the client (Task D) |
| Levers and puzzles | `Lever`, `PuzzleLever`, `PuzzleLeverManager`, `TriggerCounter`, `PuzzlePortculis`, `PuzzleWeightedStep` …(+2) | Levers open portcullises, spikes, the puzzle timer | 0 / 0 | muted on client; in snapshot; physics callbacks under P2 swap; P2's hit — weapon under swap | ✅ | "locked rooms with levers" — autotest 12.10 |
| Weighted plates | `WeightedStepParent`, `GamblerWeightedStep`, `ContactSpikesWeightedStep`, `IndianaWeightedStep`, `SpecialWeightTrigger` | Pressure plates open doors and traps | 1 / 1 | muted on client; in snapshot; physics callbacks under P2 swap (subclasses with their own methods are not on the list) | 🟡 | `m_playerInArea = CurrentPlayer` |
| Bats and bees | `BatBehaviour`, `BeeHaviour`, `FlyingBehaviour` | Flying trap enemies, track the player | 1 / 0 | muted on client; target — nearest player (AiTargetPatches) | 🟡 | fixed the BatBehaviour.OnDisable crash on the client |
| Suction pipe | `SuctionPipe` | The pipe sucks the player in and carries them along the pipe | 3 / 0 | not muted on client; host: callbacks under P2 swap | 🔴 | sucks in only GameManager.Player |
| Goo on the floor | `GooOnFloor`, `GooOnFloorTrigger`, `GooMidAir` | Slippery goo: slows down and blocks input | 7 / 0 | muted on client; in snapshot; physics callbacks under P2 swap | 🟡 | speed/input are changed on GM.Player outside P2's callback |
| Holes, oven, ropes, ceiling crushers | `HoleInWall`, `Oven`, `Clothesline`, `CeilingCorpseCrusher`, `AimingTrapBehaviour`, `TrapTrashWindow` …(+2) | Assorted mechanisms: hole in the wall, oven, laundry line, trash window, a hand from a gap | 0 / 0 | not muted on client; in snapshot; host: callbacks under P2 swap | 🟡 | the client runs the logic in parallel with the host; damage is blocked on the client |
| Moles, rhythm, tongue, strength tester | `Mole`, `MoleHole`, `MoleHoleTrap`, `MoleTriggers`, `RythmTrap`, `Tongue` …(+2) | Mini-mechanisms: moles, rhythm trap, tongue, Indiana's spikes | 0 / 0 | not muted on client; in snapshot | 🟡 | Random in OnEnable/Start is not seeded |
| Trap doors | `TrapFakeDoor`, `CardBoardDoor`, `DialogueFakeDoor`, `IronMaidenDoor`, `MonsterDoor`, `ToiletDoor` …(+2) | Fake doors: cardboard, iron maiden, toilet, monster, door to doom | 1 / 1 | BalconyFakeDoor is muted; the rest are not muted; in snapshot | 🟡 | "trap doors are not visible" closed in phase 2/0.3.4 |
| Contact damage | `DamageOnTouch` | Common damage component: spikes, projectiles, enemies | 0 / 5 | client: GetDamaged is muted; host: in place | 🟡 | 5 references to CurrentPlayer |

## 2. Special rooms and challenges

| Mechanic | Classes | What it does | GM / CP | Mod coverage | St. | Notes |
|---|---|---|---|---|---|---|
| Generation: Regular, HUB, Exit, EnemyOnly, SegmentEnd | `RoomGenerator`, `SegmentManager`, `Room` | A segment is built entirely from the seed, doors are connected | 13 / 0 | seeded (shared run seed, SegmentSeed); RoomCheck and resync | ✅ | `[Gen]` fingerprints of segments 1–3 matched (PLAN 12.10) |
| Unique: Gambler | `GamblerBehavior`, `GamblerDialogue`, `Gambler`, `GamblerAction`, `GamblerUtilities`, `PuzzleMonsterGamble` …(+2) | Dice game with a monster, prizes, a fight if you lose | 39 / 2 | P2 swap (dialogue, coroutines); Gambler is an Enemy (nearest); the window is forwarded | 🟡 | 36 GM.Player in DialogueLogic: correct only while the chain runs under the P2 swap |
| Unique: DoorToDoom | `TrapDoorToDoom` | Door to "doom": a special branch in GameManager.EnterDoorRoutine | 0 / 0 | not muted; transition is a door vote | 🟡 | the DoorToDoom branches (GameManager :2163, :2331, :2477) are not verified |
| Unique: IndianaJones | `IndianaJonesRoomGenerator`, `IndianaJonesSpikes`, `IndianaWeightedStep` | Idol room: weighted plate, spikes, cave-in | 0 / 0 | plate is muted; the generator uses unseeded Random | 🟡 | IndianaJonesRoomGenerator is not on the seeding list |
| Unique: HiddenTreasure | `HiddenRoom`, `SpawnTreasure`, `FillChest`, `LockedItems` | Hidden room with treasure | 1 / 0 | coins arrive as host objects; SpawnTreasure is not seeded | 🟡 |  |
| Unique: SantaRoom | `SnowyPufflesDoors`, `SnowyPufflesHelper` | Santa's room with puffle doors | 4 / 0 | not muted; the segment takes GM.Player.Stats.SegmentID | 🟡 |  |
| Unique: BonusRoom, BonusRoomNoCompanion | `BonusRoomDoor` | Bonus room through a special door | 2 / 0 | seeded; door vote | ✅ | bonus rooms verified by autotest 12.10 |
| Companion, CompanionHUB | `CompanionSpawnSpot`, `CompanionAutomat`, `CompanionRandomizer`, `CompanionBalconyTrap` | Companion vending machine room | 4 / 0 | CompanionRandomizer is seeded; CompanionBalconyTrap is muted; payment under P2 swap | 🟡 | payment `GM.Player.Stats.CurrentGoldCount`; see the "Companions" section |
| Cauldron: puzzle cauldron | `PuzzleCauldron`, `PuzzleMakePotion`, `CauldronPullLever`, `CauldronPump`, `IngredientSlot`, `PuzzlePotion` | Brewing a potion from ingredients, lever, pump | 0 / 5 | levers are muted; IngredientSlot is unseeded | 🟡 |  |
| LockedChest, ChestKey | `TreasureChest`, `TreasureKey`, `ChestSpawner`, `OpenChestHint` | Chests, keys, locked chests | 0 / 8 | chest and ChestSpawner are muted; ChestSpawner is seeded; despawn is forwarded | 🟡 | "chest can't be broken" fixed in phase 2, not marked verified |
| Crusher, Crumbling (dungeon end) | `CrusherManager`, `DungeonEndCrumblingStep`, `DungeonEndCrusherMisc` | Final crusher and crumbling steps | 0 / 0 | muted on client; in snapshot; physics callbacks under P2 swap | 🟡 |  |
| Torture: iron maiden | `IronMaidenDoor`, `TrapFakeDoor` | Torture room: iron maiden door | 0 / 0 | not muted; in snapshot | 🟡 |  |
| Lights out (room) | `TrapLightsOut`, `LightsOutTrigger`, `LightsOutLever`, `LightsOutMonster` | Darkness, light lever, monster | 7 / 10 | muted; Msg.LightsOut | ✅ | light verified; the monster is a separate 🔴 in the traps section |
| Balcony, BalconyEntrance, BalconyExit | `BalconyGenerator`, `BalconyBird`, `BalconyFakeDoor`, `BalconyDropTrigger`, `BalconyHatch`, `BalconyWindManager` …(+3) | Outer wall: windows, birds, trap doors, wind, hatch | 4 / 3 | muted (bird, door, drop zone); seeded (generator, wind); in snapshot | ✅ | 458/458 objects (PLAN 12.10); depth layers are shared (README) |
| LabHUB, LabRegular, LabKey, LabExit | `LabHubManager`, `LabAlarm`, `PatrolingEnemy`, `PatrolingEnemyUtilities`, `LabEnemyStates` | Laboratory: multi-layer hub, patrols, per-layer alarm | 0 / 29 | AI targets the nearest (Enemy.Update); the depth layer is shared | 🔴 | the static CurrentPlayerLayer is one for both |
| Cave, CaveUnique | `CaveMadnessTrigger`, `CaveDoorSwitcher`, `MonsterCaveSpot`, `CaveConfiguration`, `MonsterSpawnSpot` | Caves after the tower: madness, monsters, doors | 2 / 0 | seeded (CaveSeedPatch) | 🟡 | "awaiting verification" (PLAN); queue task E |
| SewerEntrance, SewerExit | `DrippingPipe`, `Critters`, `Snail`, `Rat`, `BaseAnimal` | Sewer: decor and small creatures; logic is RoomGenerator flags | 0 / 0 | seeded (shared seed); decor is not registered | 🟡 | there are no dedicated sewer classes |
| TowerExit, EndGameSpawn, tower exit | `EndgameExitroomTrigger`, `ExitAutomatHelper`, `EndgameSettings`, `EveliusBasement` | Tower exit, final spawn, Evelius's heart in hand | 6 / 0 | not muted; the heart is looked up on GM.Player | 🔴 | the heart is looked up only in slot0's hands |
| Tutorial | `TutorialEvelius`, `CommandEvents`, `IntroHelper`, `SkipIntro` | Tutorial scene and hints | 3 / 0 | none | 🟡 | not needed for P2; CommandEvents.m_player = GM.Player |
| ConnectRoomStart, ConnectRoomTarget | `RoomGenerator` | Connecting rooms between segments | 7 / 0 | seeded | 🟡 | in the generator, not verified separately |
| Head rooms: ItemVault, Pawnshop, PillarWalk, BarberShop, HairMirror | `VonWanstHeadManager`, `ItemVault`, `PillarWalk`, `HeadRoom`, `HeadRoomPedestal`, `HeadroomExitDoor` …(+3) | The player goes into "Vanst's head" with a copy for a limited time | 18 / 9 | none (single manager, disables slot0) | 🔴 | disables Player.CurrentPlayer, replaces GM.Player with a copy |
| Hideouts (HideOut) | `HideOut`, `HideoutSpecialItem` | Hide in a barrel/wardrobe, companions come out nearby | 3 / 1 | P2 swap on trigger | 🟡 |  |
| Mirror platform | `MirrorPlatform` | The mirror platform works when a player enters | 0 / 0 | P2 swap (physics callbacks) | 🟡 | "mirror for the host only" — phase 3, not marked verified |

## 3. NPCs and dialogues

| Mechanic | Classes | What it does | GM / CP | Mod coverage | St. | Notes |
|---|---|---|---|---|---|---|
| Dialogue base: DialogueTrigger + DialogueManager | `DialogueTrigger`, `DialogueManager`, `TalkCommand`, `FirstOptionCommand`, `SecondOptionCommand`, `ThirdOptionCommand` | The NPC trigger queues itself in the manager and opens the menu | 15 / 14 | P2 swap (callbacks, P2 manager's Update); Talk is forwarded | 🔴 | the trigger caches P1's DialogueManager (Awake/Start) |
| Choice window (InteractionWindow) | `InteractionWindow` | Menu 1/2/3: dialogues, shops, djinn, bank | 1 / 12 | forwarded (Msg.Window); local opening is muted on the client | ✅ | opening and closing on the client — autotest 12.10 |
| Lines: subtitles and voice | `CharacterAudioComponents`, `TextboxPositionHandler`, `ReparentTextbox` | Lines above the head, voice-over | 2 / 0 | forwarded (Msg.Talk); the client does not start its own | 🟡 | DialogueManager.Initialize for P2 is called while GM.Player = P1 |
| Vending machines: Automat, AutomatenManager | `Automat`, `AutomatenManager`, `AutomatWindowAnimation`, `PriceTagValue` | Vending machine: lever, price, item dispensing | 3 / 0 | lever is muted on the client; P2's hit under swap; item and price are not seeded | 🟡 | Task A (P2 payment) is in the queue; the client may see a different item |
| Pawnshop | `Pawnshop`, `PawnshopMain`, `PawnshopSell`, `PawnshopDisplayItems`, `Pawnshopevents`, `PawnshopBaseItems` | Selling and buying back items, appraisal, payment in coins | 36 / 0 | P2 swap in the trigger/window; manager cache as in the base | 🔴 | the menu and payment may go to P1's window and purse |
| Barber | `Barber` | Haircut and beard for coins, coupon | 27 / 0 | P2 swap in the trigger/window | 🔴 | hairstyle and payment are taken from GM.Player |
| Pipefitter: Pipefitter, PipeSystem | `Pipefitter`, `PipeSystem`, `PipeSystemManager` | Paid item delivery over the pipe network | 21 / 0 | P2 swap; PipeSystem.m_player = GM.Player | 🔴 | payment and player cache are P1's |
| Banker, ATM, coins | `Banker`, `ATMLogic`, `MoneyTrigger`, `DropAllCoinsDialogue` | Bank deposit, coin bubble, dropping all coins | 14 / 14 | P2 swap; MoneyTrigger shows GM.Player's purse | 🟡 | bubble and coin text are P1's |
| Mayor, bartender, villagers | `Mayor`, `Barkeeper`, `Bard`, `AlvaVillage`, `SobuVillage`, `HubertsMom` …(+8) | Village NPCs: tax, drinks, music, quests, tattoos | 108 / 0 | P2 swap; dialogue base risk | 🟡 | up to 27 GM.Player per class; village progress is P1's |
| Riddle master (RiddleMaster) | `RiddleMaster`, `RiddleMasterAction`, `RiddleMasterUtilities`, `RiddleDatabase` | Riddles with a prize | 9 / 1 | P2 swap; questions use Random | 🟡 |  |
| Mirror talker (MirrorTalk) | `MirrorTalk` | The mirror's line, entry to the mirror's head room | 7 / 0 | P2 swap | 🟡 | leads to a head room (see section 2) |
| Djinn and lamp | `DjinnData`, `MagicLampSpecialItem` | The lamp summons a djinn: wishes, monkey, storage | 16 / 0 | P2 swap; TransformAction under swap | 🟡 | "djinn for the second player" — phases 3/5, not verified live |
| Hut and item save | `PlayerHutItemSave`, `WanstHouseHandler`, `HouseUpgradeHelper`, `ItemListCurator` | Items in the hut, house upgrades | 10 / 0 | the client's save is disabled; the host's save uses GM.Player | 🔴 | saves only P1's inventory |

## 4. Evil mage Evelius

| Mechanic | Classes | What it does | GM / CP | Mod coverage | St. | Notes |
|---|---|---|---|---|---|---|
| EveliusController | `EveliusController` | Evelius in hubs: appearance, mood, dialogues, tasks | 12 / 6 | muted on client; in snapshot; the extra one on the client is removed | 🔴 | m_player = GM.Player: only one conversation partner |
| Tasks, mood, small talk | `EvTasks`, `EvReturn`, `EvSmalltalk`, `EvMediumMood`, `EvAnyMood`, `EvGiveItem` …(+2) | Tasks, item return, reward and punishment | 35 / 1 | host; not muted separately | 🔴 | coins and items are deducted from GM.Player |
| EveliusHunter: the hunt | `EveliusHunter`, `HunterActions`, `HunterMovement`, `HunterCloud`, `HunterAnimationEvents` | The hunter chases the player and the princess | 21 / 9 | muted like FlyingBehaviour; the target in Update is the nearest | 🔴 | FixedUpdate, attack, lines — by CurrentPlayer |
| Evelius's heart and the finale | `EveliusHeartSpecialItem`, `FinalEvelius`, `RuinEvelius`, `EveliusDropArea` | The heart, the final tower, the ruins, the fight with Evelius | 37 / 0 | ScenePatches takes P2 to FinalTower; no P2 logic | 🔴 | 31 GM.Player; fight and scene — slot0 |
| Evelius's potion and body | `EveliusPotion` | Turns you into Evelius (Status.Evelius) | 0 / 0 | TransformAction under P2 swap | 🟡 | not verified live |

## 5. Companions

| Mechanic | Classes | What it does | GM / CP | Mod coverage | St. | Notes |
|---|---|---|---|---|---|---|
| Following and AI (NPC + HandleFollow) | `NPC`, `CharacterUtilities` | Follows the player, waits, jumps between layers | 30 / 107 | client: muted; host: no swap (swap only for evil NPCs) | 🔴 | everyone follows Player.VonWansts[0] = P1 |
| Conversation and recruiting | `CompanionDialogueTrigger` | "Follow/stay" window, gifts, loyalty, release | 16 / 43 | P2 swap (menu); window is forwarded | 🔴 | P2's group is not the one the AI follows (43 CurrentPlayer) |
| Companion vending machine | `CompanionAutomat`, `CompanionSpawnSpot` | Buying a companion for coins | 3 / 0 | lever is muted; payment under P2 swap | 🟡 |  |
| Princess | `PrincessAction`, `PrincessUtilities`, `PrincessVillage` | Princess companion: eating, rescue, lines | 5 / 12 | none (AI is bound to CurrentPlayer) | 🔴 | 8+4 CurrentPlayer, behavior follows slot0 |
| Moving companions on room change | `EnterDoorCommand`, `CoopTransitions` | Teleports companions to the player on transition | 9 / 2 | CoopTransitions: P1 executes the transition, P2 is carried over later | 🟡 | TeleportCompanionsToPlayer runs from P1; P2's group was not verified |
| Companions disarming traps | `UseTrapSpot`, `MultiDismantle`, `CompanionBalconyTrap` | A companion disarms a trap at a marker | 1 / 0 | UseTrapSpot and MultiDismantle are muted on the client | 🟡 | the companion's trap works on the host only (0.3.4) |

## 6. Special items and potions

| Mechanic | Classes | What it does | GM / CP | Mod coverage | St. | Notes |
|---|---|---|---|---|---|---|
| Coins and purse | `CoinSpecialItem`, `GoldSack`, `GoldSackSpecialItem`, `GoldStash`, `FillGoldSack` | A 20-coin purse, pickup, throwing | 12 / 38 | P2 swap (GoldSack*, Coin Equip) | 🟡 | queue Tasks A/B: payment and coin transfer |
| Armor, helmet, shield, mannequin | `ArmorSpecialItem`, `HelmetSpecialItem`, `ShieldSpecialItem`, `MannequinSpecialItem`, `HatStandSpecialItem`, `HaircutBustSpecialItem` | Wearable items | 0 / 0 | P2 swap (the item's Update on P2's object) | 🟡 |  |
| Hive helmet | `HiveSpecialItem` | Bees sting the wearer | 5 / 2 | P2 swap while worn | 🔴 | Awake: m_character = CurrentPlayer (P1) |
| Banana peel | `TrapItemBananaPeel`, `NutritiousSpecialItem` | Slippery peel, sliding in a coroutine | 9 / 2 | P2 swap (coroutines) | ✅ | the banana on the second player ends — verified (PLAN, phase 3) |
| Bear traps, mousetrap, rake, goose | `TrapItemBearTrap`, `TrapItemMouseTrap`, `TrapItemRake`, `TrapItemHonk` | Trap items | 0 / 2 | P2 swap (callbacks) | 🟡 | `hitFrom == null → CurrentPlayer` |
| Explosive plant | `TrapItemExplosivePlant` | Explosion based on distance to the player | 4 / 1 | P2 swap | 🟡 | the radius is computed from CurrentPlayer |
| Wands and spells | `MagicWandSpecialItem`, `MagicDevWandSpecialItem`, `RangedSpecialItem`, `SpellSpecialItem`, `SpellHelper` | Spells, ranged attack | 0 / 12 | P2 swap (item in hand) | 🟡 | 12 CurrentPlayer per wand |
| Hourglass | `HourglassSpecialitem`, `HeadroomHourglass` | Head room timer | 4 / 0 | none | 🟡 | tied to the head room |
| Respawn bottle | `RespawnBottleSpecialItem`, `RespawnSettings`, `RespawnBottleBurner` | Respawns at the bottle's location (RespawnSettings is global) | 1 / 1 | none (only the slot is fixed on OnEnable) | 🔴 | RespawnRoutine recreates slot0 (CODE_MAP) |
| Teeth, hammer, sticky, hard food | `ToothSpecialItem`, `Tooth`, `HammerSpecialItem`, `StickySpecialItem`, `HardFoodSpecialItem` | Teeth as "lives", hammer, sticky item, food | 0 / 0 | P2 swap | 🟡 |  |
| Readable items, prophecies | `ReadableSpecialItem`, `ProphecyScrollSpecialItem`, `ProphecyLibrary`, `ReadProphecyHint` | Scrolls and prophecies | 1 / 18 | P2 swap | 🟡 |  |
| Puzzle potions | `PuzzlePotion`, `AntiPotion`, `DamagePotion`, `DeathPotion`, `GooPotion`, `LockengoldPotion` | Throwable potions: damage, goo, gold, antidote | 0 / 4 | P2 swap (throw from the hand) | 🟡 | the throw ray takes CurrentPlayer's localScale |
| Transformation: monster potion | `TransformationPotion`, `TransformTo` | The player's body is replaced by a monster | 0 / 0 | TransformAction under swap + AdoptAsSecond; the client creates a puppet | ✅ | "monster, the client walks as it" — verified (PLAN, phase 5) |
| Transformations: djinn (monkey), minion | `VonWanstHeadManager`, `CharacterAction` | A monkey or servant instead of the player | 66 / 112 | TransformAction under P2 swap | 🟡 | phase 5, not verified live |

## 7. Death and revival

| Mechanic | Classes | What it does | GM / CP | Mod coverage | St. | Notes |
|---|---|---|---|---|---|---|
| P2's death while P1 is alive | `CharacterAction`, `CharacterStats` | The run continues, the corpse stays | 62 / 110 | GetDamaged/Die swap; DeathPatches | 🟡 |  |
| P1's death while P2 is alive | `GameManager`, `CoopPlayers` | P2 becomes the main player (PromoteSecondToMain) | 0 / 28 | CoopPlayers.PromoteSecondToMain; camera and fog | 🟡 |  |
| Both die, Game over, funeral | `FuneralEndscreen`, `VillageManager`, `UIManager` | End of the run, death screen | 13 / 0 | ShowDeathUI/Funeral are muted while the partner is alive; client — GameOver over the network | 🟡 |  |
| Revival (mod): kills or a new floor | `CoopLife` | The dead player revives after N kills or on a new floor | 0 / 0 | mod; the counter is on the host | 🟡 | not named verified in PLAN |
| Client camera, fog and light after death | `CameraManager`, `UberFogSystem`, `CoopCamera` | Camera and light follow the survivor | 17 / 1 | SetCurrent: the "player" = the partner | 🟡 | phase 4: done, not verified |

## 8. Village

| Mechanic | Classes | What it does | GM / CP | Mod coverage | St. | Notes |
|---|---|---|---|---|---|---|
| Scenes: tower, village, finale | `SceneLoader`, `VillageManager`, `GameManager` | Transition via door, scene loading, carrying over players | 0 / 28 | ScenePatches: DontDestroyOnLoad(P2); SceneLoad is forwarded | 🟡 | the client's save is disabled |
| Taxes, village saves, house | `VillageData`, `DataManager`, `HouseUpgradeHelper`, `WanstHouseHandler` | Village progress, house, the mayor's taxes | 0 / 0 | the client's save is disabled; host — as in the game | 🟡 | inventory in the save is P1's only |

## 9. Endings and cutscenes

| Mechanic | Classes | What it does | GM / CP | Mod coverage | St. | Notes |
|---|---|---|---|---|---|---|
| Endings: throne, monster cave, bar, funeral | `ThroneEndscreen`, `FuneralEndscreen`, `MonsterCave`, `Barkeeper`, `HubertThrone` | Scripted endings that move the player | 28 / 0 | the client receives SceneLoad/GameOver; no logic of its own | 🔴 | the scene moves only GM.Player, P2 is outside the script |
| Video: tower explosion, bubble transition | `VideoLibrary`, `CameraManager` | Video on the camera: tower explosion, bubble transition | 9 / 1 | none (Task D in the queue) | 🔴 | the video plays on the host only |
| Intro and splash screen | `IntroHelper`, `SkipIntro`, `IdleScreen` | Intro videos, idle mode | 1 / 0 | none | 🟡 | outside a run |

## 10. Achievements

| Mechanic | Classes | What it does | GM / CP | Mod coverage | St. | Notes |
|---|---|---|---|---|---|---|
| AchievementManager and 102 trackers | `AchievementManager`, `AchievementData`, `EnemyDeathDummyAchievementTracker`, `BarenudleBeastAchievementTracker`, `HardyPartyAchievementTracker` | Kills, deaths, purchases, transformations, escape | 7 / 1 | TrackDeath hook (kill counter); the client's save is muted | 🟡 | counted by CharacterTypes.Player; TrackEscape takes GM.Player |

## 11. Common systems

| Mechanic | Classes | What it does | GM / CP | Mod coverage | St. | Notes |
|---|---|---|---|---|---|---|
| Doors, segment hatch, side passage | `EnterDoorCommand`, `EnterDoorTrigger`, `EnterDoorSideTrigger`, `StartNewSegmentTrigger`, `Door` | Transition: vote, teleport, camera | 9 / 2 | P2 swap (CoopTransitions vote); muted on the client | ✅ | route-test passes rooms automatically (TASKS) |
| Depth layers (LayerManager, DepthSwitch) | `LayerManager`, `DepthSwitch`, `ObjectLayerSet` | Depth layers (balcony, laboratory), shared CurrentPlayerLayer | 30 / 3 | none | 🔴 | P2 on a switch changes P1's layer too |
| Combat (CombatManager, enemy detector) | `CombatManager`, `EnemyDetectionRange`, `Enemy` | List of enemies in combat, detectors, AI targets | 10 / 35 | swap: Enemy.Update → nearest; detector — the owner | 🟡 | CombatManager.InCombat is shared: blocks conversations for both |
| Fog (UberFog) | `UberFogSystem`, `UberFogPlayerOccluder`, `UberFogHallwayOccluder` | Fog in rooms, transparency around the player | 12 / 16 | none | 🔴 | the occluder is bound to P1 |
| Hints (HintManager) | `HintObject`, `HintManager`, `HintCollection` | Tutorial hints on screen | 5 / 26 | none | 🟡 | 36 HintObject subclasses (497 CurrentPlayer in total) |
| Shadows and eyes | `TwoDShadow`, `TwoDShadow_outdoor`, `EyeTrackingRangeLimiter` | Shadows and eye tracking | 0 / 13 | shadows are not registered | 🟡 | cosmetic |

## Top 15 risky 🔴 (by importance for the player)

Order: how often it comes up in a run × how badly it breaks the game for the second player. Line numbers refer to `decompiled/Assembly-CSharp/`. Live checking is via the dev commands from `docs/TASKS.md` (`tp p2 x y`, `tap p2 talk|opt1|attack`, `log N <filter>`); they were not run here.

| # | Risk | Why it breaks | How to fix (in one sentence) | Game files and methods |
|---|---|---|---|---|
| 1 | NPCs, shops and menus for P2: the trigger stores P1's dialogue manager | `DialogueTrigger` caches the first player's `DialogueManager` (`Awake`, `Start`), P2 ends up in the `OverlappingTriggers` queue of P1's manager; after a synchronous start the dialogue is continued by P1's manager Update outside the swap (`MustLookAtTrigger`, `CancelThis` by P1). The likely common cause of Task A (P2's payment is deducted from P1) | Patch the `DialogueTrigger.DialogueManager` getter (and `Awake`/`Start`): return the manager of whoever is currently the "player" (`GameManager.Player` under the swap) and keep queues per player; also call `DialogueManager.Initialize` for P2 under the swap | `DialogueTrigger.cs` :106, :111–117, :134–160; `DialogueManager.cs` :369–372, :753, :831–900; `Player.cs` :72, :114; `Pawnshop*.cs`, `Barber.cs`, `Pipefitter.cs`, `Banker.cs` |
| 2 | Companions always follow P1 | `NPC.Update` is swapped only for evil NPCs; `HandleFollow` takes `Player.VonWansts[0]` (P1); companions recruited by P2 get P2's group but walk to P1 | Prefix `NPC.Update`: substitute the "leader" (the player whose `GetCurrentPlayerGroup()` contains the NPC), the nearest one for free NPCs | `NPC.cs` :258–262; `CharacterUtilities.cs` :2676–2780, :3288; `CompanionDialogueTrigger.cs` :339–348, :991–1018 |
| 3 | Respawn bottle | `RespawnSettings` is global; `Die` schedules `RespawnRoutine`, which recreates slot0; the mod mutes only `ShowDeathUI` | If a co-op pair member dies and the partner is alive, do not arm `RespawnSettings.Respawn` for that death (keep `CoopLife` revival), or spawn the respawned one as P2 via `CoopPlayers.SpawnP2` | `CharacterAction.cs` :1584–1602, :1755, :1819; `RespawnBottleSpecialItem.cs` :46, :97; `Player.OnEnable` |
| 4 | The hammer guy chases only P1 | `TrapHammerGuy` reads `Player.CurrentPlayer` everywhere; the trap's `Update` on the host runs without the swap; `HammerGuyWall` grabs only P1's companions | Wrap `Update`/`FixedUpdate`/coroutines of `TrapHammerGuy` and `HammerGuyWall` in `TargetSwap.SwapTo(NearestFor(trap))`, as with `Enemy.Update` | `TrapHammerGuy.cs` :237, :303–355, :455, :615–:862; `HammerGuyWall.cs` :133, :244, :290 |
| 5 | Stealth traps (Sleeper, TrapSleeper, TrapBigBand) | Woken by P1's input and position, including `Input.GetKey` of its keys; P2 can make noise with no reaction | Swap to the nearest "noisy" player in their `Update` and replace `Input.GetKey(CommandPattern…KeyCode)` with the `PlayerInput` of the pattern's owner | `Sleeper.cs` :491, :579, :657–735; `TrapSleeper.cs` :461–465; `TrapBigBand.cs` :559–608 |
| 6 | Evelius: a single conversation partner | `EveliusController.m_player` = `GameManager.Player` at spawn; tasks and coin deductions are P1's; the hunter attacks `CurrentPlayer` in `FixedUpdate` | Run `EveliusController` (`OnTriggerEnter2D`, coroutines) under a swap to the player in the trigger/the nearest, and `EveliusHunter.FixedUpdate` to the nearest | `EveliusController.cs` :706, :756, :843, :1581, :1680, :1932; `EvTasks.cs` :741, :933; `EveliusHunter.cs` :256–300 |
| 7 | Powder keg | `DestroyTowerRoutine` and `SpecialItemPowderKeg` kill `GameManager.Player` (slot0) and play the video on the host only | A new message `Msg.TowerExplosion` (Task D) and killing both players in turn | `TrapPowderKeg.cs` :317–346; `SpecialItemPowderKeg.cs` :127–164 |
| 8 | Depth layers are shared (laboratory, balcony) | `LayerManager.CurrentPlayerLayer` is static; P2's layer switch changes P1's layer, the camera and AI follow it | A per-player layer (dictionary), or ignore the switch if the player is not slot0; drive the `Lab` camera by the sum of both | `LayerManager.cs` :13, :198–231; `LabHubManager.cs` :68, :152; `LabAlarm.cs` :123; `CharacterUtilities.cs` :2665, :3391 |
| 9 | Fog (UberFog) is bound to P1 | `UberFogPlayerOccluder` and `UberFogSystem` hold a single occluder on `Player.CurrentPlayer`; around P2's body the fog is solid | A second occluder for P2 (a clone of `Components.UberFogPlayerOccluder`) and registration in `UberFogSystem` | `UberFogPlayerOccluder.cs` :86, :136–156, :195–246; `UberFogSystem.cs` :327–357 |
| 10 | Inventory and hut are P1's only | The save and the carry-over between scenes take `GetPlayerInventory()` from `GameManager.Player`; `PlayerHutItemSave` too | A second inventory slot in `GameData` for P2, or record P2's items on leaving the tower | `GameManager.cs` :733, :758, :1116; `EnterDoorCommand.cs` :173; `PlayerHutItemSave.cs` |
| 11 | Head rooms (djinn vault, Evelius, pawnshop, barber, mirror) | `VonWanstHeadManager` disables `Player.CurrentPlayer`, replaces `GameManager.Player` with a copy and restores it; a single manager | Forbid entry while P2 is alive, or freeze the partner and run entry/exit under one swap with the camera on whoever entered | `VonWanstHeadManager.cs` :264–273, :337, :405–414; `Djinn.cs` :427; `EvSmalltalk.cs` :38–40 |
| 12 | Finale: Evelius's heart and FinalTower | The heart check and the finale loading go by `GameManager.Player`; scripted endings move only it | Run `EveliusHeartSpecialItem`, `EndgameExitroomTrigger`, `ThroneEndscreen` under a swap to the heart's owner, carry P2 over and freeze them | `EveliusHeartSpecialItem.cs` :224–257, :337–345; `EndgameExitroomTrigger.cs` :74–77; `ThroneEndscreen.cs` :104–119 |
| 13 | Suction pipe and the pipe network | `SuctionPipe` sucks in `GameManager.Player`; `PipeSystem.m_player` caches P1 | Remember the character who entered and run the suction/payment under their swap | `SuctionPipe.cs` :198, :246, :306; `PipeSystem.cs` :133 |
| 14 | Monster in the dark and death by `LightsOut` | `LightsOutMonster.m_currentPlayer` caches P1; `TrapLightsOut` kills/freezes `GameManager.Player` | The victim is the player in the dark room/the nearest; call these methods under the victim's swap | `LightsOutMonster.cs` :150, :210; `TrapLightsOut.cs` :394–431 |
| 15 | Vending machines: item and price are not seeded | `Automat.Initialize` picks the item with `WeightedElement.GetRandomElement` and the discount without a seed; on the client the sign and price may differ from what the host dispenses | Add `Automat`, `AutomatenManager`, `CompanionAutomat` to `RandomizerPatches.s_types` | `Automat.cs` :264, :286, :423; `AutomatenManager.cs` :100–177; `CompanionAutomat.cs` :136 |

## Observations affecting many rows

1. **The swap does not cover other classes' `Update`.** Traps, managers and AI components that are not on P2's object run with `CurrentPlayer = P1` (the swap exists only for `Enemy`, evil `NPC`, `FlyingBehaviour`, `EnemyDetectionRange`). The 🔴 rows with "target = P1" are mostly due to this.
2. **Player caches.** The game stores the "player" in a field in `Awake`/`Start` and changes it only via `GameManager.UpdatePlayer` (called by mod code only when P2 is promoted). Fields: `DialogueTrigger.m_dialogueManager`, `FlyingBehaviour.m_player` (fixed), `PipeSystem.m_player`, `EveliusController.m_player`, `LightsOutMonster.m_currentPlayer`, `DepthSwitch.m_playerCollider`, `HiveSpecialItem.m_character`, `HintObject.m_currentPlayer`, `CommandEvents.m_player`, `InteractionWindow.m_player`.
3. **P2's DialogueManager.** `Player.GameplayInit` → `DialogueManager.Initialize` reads `GameManager.Player`, and `CoopPlayers.SpawnP2` calls `GameplayInit` without the swap: `m_character`, audio and animator of P2's manager point to P1. P2's lines are voiced and animated on P1 (check via `[Window]`/`Talk` in the log).
4. **Not muted on the client, but has Update/triggers:** `Automat`, `CompanionAutomat`, `Pawnshop*`, `PipeSystem`, the `DialogueTrigger` family, `LightsOutMonster`, `SuctionPipe`, `HoleInWall`, `Oven`, `Clothesline`, `CeilingCorpseCrusher`, `AimingTrapBehaviour`, `PuzzleDeathTimer`, `Mole`, `RythmTrap`, the `TrapFakeDoor` family. Damage on the client is blocked (`GetDamaged`), but animations and spawns may run twice.
5. **Unseeded random in Awake/Start/OnEnable:** `Automat`, `AutomatenManager`, `CompanionAutomat`, `ItemVault`, `HideOut`, `LabHubManager`, `IngredientSlot`, `SpawnTreasure`, `IndianaJonesRoomGenerator`, `Mole`, `MoleHoleTrap`, `PuzzleMonsterGamble`, `MonsterInWall`, `ConveyorBeltTrapProbability`, `TrapsInletProbaility`, `GooOnFloorActiveProbability`, `MonsterInGapActiveProbability`. Part of it is evened out by "activeSelf mask + object count check", but the item type, price and individual sprites are not.
6. **Confirmations from the docs.** README: "Some traps, hints and companion lines react only to the main player"; "Depth layers are shared"; "the respawn bottle is correct only for the main player". PLAN: the powder keg (video), `leftover` debris, caves and the rare S03 desync are open; phases 1b and 6 are not started.
7. **Limits of the analysis.** Only occurrences in the decompiled text were counted; accesses through intermediate variables (`var p = GameManager.Instance.Player;`) and through `Character` fields are invisible to grep. The 🔴 status is set by reading the code, without running the game; items 1, 2 and 11 should be confirmed live (P1 far from the NPC, P2 enters the trigger).
