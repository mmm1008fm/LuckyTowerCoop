# Tasks for a cheaper model (Sonnet/Haiku)

Each task is self-contained: context, files, commands, completion criterion. Do them one at a time, with a build and an autotest after each.

## General context
- Project: `D:\game\LuckyTowerCoop` (a BepInEx/HarmonyX two-player co-op mod for Lucky Tower Ultimate). Game: `D:\game\Lucky.Tower.Ultimate.v1.1.4`.
  The map of the game's classes is `docs/CODE_MAP.md`, the decompiled code is in `decompiled/Assembly-CSharp/`. Plan and status are in `docs/PLAN.md`.
- Build (deploys the dll into the game; close both game copies first):
  ```powershell
  Get-Process 'Lucky Tower Ultimate' -ErrorAction SilentlyContinue | Stop-Process -Force
  dotnet build D:\game\LuckyTowerCoop\src\LuckyTowerCoop\LuckyTowerCoop.csproj -c Debug -nologo -v q
  ```
- Autotest (brings up a host + client on one PC over localhost, walks through the rooms, compares object counts):
  ```powershell
  pwsh D:\game\LuckyTowerCoop\tools\route-test.ps1 -Rooms 40 -Until Balcony
  ```
  The "all good" criterion: in every `entities host=N client=N` line the numbers are equal, at the end `resync: host 0, client 0`, and `errors` contains only `Loading standard input`.
  Lines `unresolved: ... not found here: <path>` show host objects that the client does not have; below them `ls host/client` of the parent is printed.
- Dev commands for the running copies (host port 47800 → `DevA`, client 47801 → `DevB`):
  ```powershell
  . D:\game\LuckyTowerCoop\tools\devlib.ps1; DevA 'where'; DevB 'net status'
  ```
  Useful ones: `where` (players, room), `doors`, `exits`, `rooms` (all tower rooms), `netents` (network entities), `ent <id>`,
  `ls <path from the room root>` (if names contain "/", the separator is "|"), `log N <filter>` (the reply is cut at the UDP ~64 KB limit — always use a filter),
  `tp p1|p2 x y`, `tap p1 up|down|talk|opt1|cancel`, `killall`, `unlock` (raise the door gratings), `free` (remove movement locks),
  `lights out|on`, `net sim <ms>` (ping emulation), `net resync`.
- Mod log: `D:\game\Lucky.Tower.Ultimate.v1.1.4\BepInEx\LogOutput.log` (host); the client has its own copy, read it via `DevB 'log 50 <filter>'`.
- Rules: the host is authoritative, the client is a mirror. World logic on the client is muted by a list of types in `Patches/WorldPatches.cs` (`s_extraTypes`),
  randomness is seeded by a list in `Patches/RandomizerPatches.cs`, objects are registered in `Net/NetHost.cs` (`WorldObjects`).
  Object paths: `Net/NetEntities.cs` → `NetPaths` (the separators are service characters, use `NetPaths.Pretty` for logs).

## 1. ~~Release 0.3.4~~ — done. Release: pwsh tools\make-dist.ps1, the archive goes to dist\.

## 2. "leftover" debris and objects that fell off the map
Symptom: in caves / the `Dead` room the client reports `not found here: PlantRemains/ArmorLeftOvers/leftover ...`; a `leftover` object at y ≈ −90000 falls forever.
To do: in `Net/NetHost.cs` → `WorldObjects` → the local `Add(Component c)` add an exclusion: the name starts with `leftover`
(`go.name.StartsWith("leftover", OrdinalIgnoreCase)`) or `ArmorLeftOvers`/`PlantRemains` is among the parents. This is cosmetic (armor debris).
Check: autotest up to the caves `-Until Cave` — no warnings about `leftover`, `entities` equal.

## 3. Powder keg: the tower explosion video on the client
Symptom: `TrapPowderKeg.DestroyTowerRoutine` (decompiled `LuckyTower/Traps/TrapPowderKeg.cs:317`) plays the video
`VideoLibrary.ExplodingTower` on the host and kills the player; the client does not see the video.
To do: a new `Msg.TowerExplosion = 31` in `Net/NetTypes.cs`; a prefix on `TrapPowderKeg.DestroyTower` (new `Patches/PowderKegPatches.cs`):
host → `NetHost.SendTowerExplosion()` (modeled on `SendLights`), client → a handler in `NetClient.OnMessage` plays the same clip
(`CameraManager.Instance.MainVideoPlayer`, `clip = GameManager.Instance.VideoLibrary.ExplodingTower`, `renderMode = CameraNearPlane`,
`PlayingVideo = true` for the duration), mute input via `Stats.InputAllowed=false` on its own characters, after the clip — `PlayingVideo=false`.
The death after the video arrives the usual way (PlayerState). `DestroyTowerRoutine` itself must not run on the client (`TrapPowderKeg` is a Trap, already muted).
Check: find a room with a keg (`DevA traps` contains `TrapPowderKeg`), `DevA 'tp p1 <x> <y>'` to the keg, ignite it with a hit (`tap p1 attack`),
wait; the client log has `[NetClient] tower explosion`, no errors.

## 4. ~~Client movement prediction~~ — done in 0.3.6 (reconciliation by press number, bench tools\net-quality.ps1)
Check that with ping the client's character starts moving immediately: `net sim 80` on both copies; `DevB 'tap p1 right'` and immediately `DevB where`
several times in a row (every 50 ms): `pos.x` must change within the first ~2 frames, not after 160 ms. If not, look at `NetClient.PredictOwn`
(the `Blocked` conditions), fix it, and record the result in `docs/PLAN.md` ("Phase 1b").

## 5. Rare desync of segment 3
Once the client could not find the room `S03_F04_R02` (generation diverged). Now, with `Dev.CommandServer=true`, a fingerprint
`[Gen] segment ... seed=... unity=... rooms=...` is written to the log on both sides.
To do: run `pwsh tools\route-test.ps1 -Rooms 40 -Until 'S03_'` 5 times in a row; after each one — `DevA 'log 20 [Gen]'` and `DevB 'log 20 [Gen]'`,
compare the `segment DungeonThree` lines. If `resync` > 0 or the fingerprints differ, save both lines in `docs/PLAN.md` (the "Status" section)
noting what differs (seed/unity state/player/companions/room list).

## 6. Caves after the tower
Cave generation is now seeded (`Patches/CaveSeedPatch.cs`). Check: autotest `-Rooms 60 -Until Cave` (the route goes through ExitRoom →
Exit → Entrance → Cave). In the `Entrance`/`Cave` rooms `entities host/client` must match, and `ls` over `LevelElements` must be identical.
If they differ, look in `decompiled/.../RoomGenerator.cs`, method `GenerateCave`, for other sources of randomness
(`UnityEngine.Random` before `ResetSeed`, `Guid`, `DateTime`) and seed them following the `CaveSeedPatch` example.

## 7. Do not do without the user
Steam with two accounts on different PCs, real gamepads — only with the user's participation.

## Work queue (from Oct 12, evening) — order of execution
Work in the `work/queue` branch (from `main`), one commit per task, and the line `Co-Authored-By: Claude Sonnet 5.5 <noreply@anthropic.com>`
at the end of each commit message. `git add` only your own files. Do not merge into `main`, do not upload to Drive.
Regression after each task: `pwsh tools\route-test.ps1 -Rooms 10` (objects equal, resync 0, no new errors).

### A. Purchases by the second player are paid with their coins
Cause: the price check and deduction go through `GameManager.Instance.Player` / `Player.CurrentPlayer` (always P1).
Places: `LuckyTower/RoomGeneration/Automat.cs:356`, `CompanionAutomat.cs:253`, `PipeSystem.cs:423,565`, `Pawnshop.cs:599`,
`LuckyTower/Village/PawnshopMain.cs:455`, `Barber.cs:329`, `Pipefitter.cs:348`, `LuckyTower/Evelius/EvTasks.cs:741`,
`DropAllCoinsDialogue.cs`, `CharacterUtilities.cs:4851,5315` (paths relative to `decompiled/Assembly-CSharp/`). Also find the methods that deduct gold.
Solution: whoever buys pays. Run the purchase/payment methods of these classes under a "current player" swap to the buyer,
the same way it is already done for other actions: `Patches/ActingAsPatches.cs` (TargetSwap, `OwnerOf`), `Patches/ContextPatches.cs`.
Buyer = the character whose input chose the option in the window (the `InteractionWindow` of its Components) or who stands in the trigger.
Check: give P2 coins with a dev command (if there is no such command, add `gold p1|p2 N` to `Dev/DevServer.cs`), find a room with a vending machine/shop,
buy as the second player: P2 has coins reduced by the price, P1 unchanged; the same as the first. Both locally and online (`local-online.ps1 -DevServer`).

### B. Giving coins to the partner
The Players tab of the F7 menu (`Coop/CoopMenuPages.cs`): buttons "Give 1 / 10 / all coins to partner" for your own character.
Online, a client sends a request to the host with a new `Msg` (modeled on `RestartRequest` in `Net/NetTypes.cs`, `NetClient`, `NetHost`), and only the host changes anything.
Transfer through the `GoldSack` API (find the coin add/remove; for a recipient without a purse, create one the way it is done on coin pickup,
look at `CoinSpecialItem`, `GoldStash`). Dev command `givecoins p1|p2 N`. Check: the `gold:` counters of both players on the host and on the client.

### C. "leftover" debris — task 2 above.
### D. Tower explosion video on the client — task 3 above.
### E. Caves — task 6 above; the rare segment 3 desync — task 5 above.

### Report
At the end — `docs/QUEUE-REPORT.md`: for each task what was done, the commit, how it was verified (numbers), what did not work and why.