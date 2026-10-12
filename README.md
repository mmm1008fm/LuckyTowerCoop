# LuckyTowerCoop

Two-player co-op mod for **Lucky Tower Ultimate**: a second Vanst on the same PC or online, over Steam or by direct IP.
It is a BepInEx mod. It does not modify game files and uninstalls with one click.

Current version: **0.3.7**. Both players must run the same version.

[Русская версия](README.ru.md)

---

## For players

### Installation
1. Extract `LuckyTowerCoop_<version>.zip` anywhere.
2. Run `Установить.cmd` (Install). It finds the game or asks for its folder, then installs BepInEx, the mod and the Steam module. Your settings are kept.
3. To uninstall, run `Удалить.cmd` (Uninstall).

### Playing online
Both players need Steam running. The mod connects through a separate `CoopNet.exe` process, which Steam shows as "Spacewar".

- **Host:** press **F7** in game, then "Create game". A friends-only lobby is created.
- **Friend:** press **F7**, open the Steam friends list and click "Join" next to the host. You can also join by lobby code or by direct IP on port 27015.
- The client does not need to do anything in the game menu. After connecting it loads the host's run automatically.
- The status line at the bottom shows ping, input delay and Steam connection quality: packet loss, queue time, direct or relayed through Valve.
- **Disconnects.** The host's game pauses with a "Wait / Play alone / Close session" dialog. The partner who left drops dead and their items can be looted. While the session is open, the partner can rejoin.
- The client's save files are not changed online: the host's run is what you play.

### Playing on one PC
The second player spawns automatically and uses the most recently connected gamepad by default.
To control player 2 with the keyboard, open F7, go to Settings and set the player 2 device to keyboard.
Keyboard layout for player 2: arrows to move, K jump, J attack, L pick up, U throw, I use, O swap hands, P drop, 7/8/9 dialogue options.

### Co-op rules
- **Doors, passages and hatches work by vote.** Whoever enters first waits and "1/2" appears above the entrance. The transition happens when the second player enters too. Press down or jump to cancel waiting.
- **Death.** If your partner is alive, the run continues. A dead player revives after the survivor kills enough monsters or reaches a new floor. If both die, the run is over.
- **Each player has their own items and money:** hands, coins and belt purse. In shops and vending machines, the buyer pays.
- **Coins can be given to the partner:** F7, Players tab, "Give 1 / 10 / all" buttons.
- After you die, the camera follows your partner.

### F7 menu
| Tab | Contents |
|---|---|
| Online | create game, join, resync, disconnect |
| Settings | enemy health and extra monsters, revive conditions, camera, name tags, player 2 device, pause on disconnect, movement prediction, smooth world display |
| Players | state of both players, coin transfer, "Pull partner to me", "Revive now", add or remove player 2 |

Online, the host sets the rules. The client sees them read-only.

### Known limitations
The full table of game mechanics is in [docs/COVERAGE.md](docs/COVERAGE.md) (in Russian). The main points:
- Companions follow player 1.
- The respawn bottle only works for player 1.
- The hammer guy and stealth traps target player 1.
- The evil mage Evelius talks to one player only.
- Depth layers on the balcony and in the lab are shared by both players.
- Leaving Limbo through the bubble door breaks online sync for now.
- The pawnshop, barber and pipefitter have not been tested live with player 2.
- Real gamepads and Steam between two different accounts have not been covered by automated tests.

### Troubleshooting
- The client's world drifted from the host: F7, Online, "Resync". Usually the client detects the drift and asks for it on its own.
- Steam does not connect: check that both players have the same mod version and are Steam friends.
- Log for bug reports: `<game folder>\BepInEx\LogOutput.log`. Lines starting with `[Steam]` describe connection quality.

---

## For developers

Working docs, code comments and in-game UI are in Russian. Commit messages are in English.

### Layout
| Path | Contents |
|---|---|
| `src/LuckyTowerCoop/` | the plugin: `Coop/` local co-op, `Net/` online, `Patches/` Harmony patches, `Dev/` debug command server |
| `src/CoopNet/` | Steam module: a separate .NET 8 process with Steamworks.NET on Spacewar (AppID 480) |
| `tools/` | launch, test and release scripts |
| `docs/` | plan, task queue, code map, online design, coverage matrix, reports |
| `decompiled/` | decompiled game code, local only, not in git |

Docs:
- [docs/PLAN.md](docs/PLAN.md): plan and current state.
- [docs/TASKS.md](docs/TASKS.md): task queue written for an implementer.
- [docs/CODE_MAP.md](docs/CODE_MAP.md): map of the game's classes.
- [docs/ONLINE.md](docs/ONLINE.md): online design.
- [docs/COVERAGE.md](docs/COVERAGE.md): which mechanics work for two players.
- [docs/QUEUE-REPORT.md](docs/QUEUE-REPORT.md): report on the latest task queue.

### Building
```powershell
dotnet build src\LuckyTowerCoop\LuckyTowerCoop.csproj -c Debug
```
The plugin is copied into the game automatically. The game path is the `GameDir` property. Close the game before building, otherwise the dll is locked.

```powershell
dotnet publish src\CoopNet\CoopNet.csproj -c Release
```
The Steam module is copied to `BepInEx\plugins\LuckyTowerCoop\CoopNet\`.

```powershell
pwsh tools\make-dist.ps1
```
Builds a release and an installer archive in `dist\`. The version number lives in `Plugin.cs` and `LuckyTowerCoop.csproj`.

### How online works
- **The host is authoritative, the client mirrors it.** The host runs the game. On the client, characters, enemies and items replay the host's snapshots, and trap logic is disabled.
- **The client's character** is player 2 on the host, driven by input sent over the network.
- **"Current player" swap.** Game code treats "the player" as a single character. While player 2 acts, the mod substitutes player 2. Enemies get the nearest player.
- **Smoothness.** The client renders the world slightly in the past and interpolates between snapshots using the host's clock. Its own character is predicted and reconciled with the host by input sequence number.
- **Generation.** Both sides build the tower from the same seed. Random objects are seeded by their path in the hierarchy.

### Testing and debugging
The debug command server is enabled with the `--coop-devserver` game argument. The host listens on port 47800, the client on 47801.
```powershell
. tools\devlib.ps1
DevA 'where'
DevB 'net status'
```

| Script | What it does |
|---|---|
| `Онлайн на одном ПК.cmd` / `tools\local-online.ps1` | two game instances over localhost, host on the left, client on the right |
| `tools\route-test.ps1 -Rooms 10` | automated route through doors: rooms, entities, corpses, resyncs, errors |
| `tools\net-quality.ps1` | network quality bench: emulated bad links and a table of delays and hitches |
| `Онлайн Steam на одном ПК.cmd` | Steam on one PC: a second account in a Sandboxie-Plus sandbox |
| `tools\soak.ps1` | soak test with random input |

Useful dev commands:
- **State:** `where`, `doors`, `rooms`, `netents`, `ent <id>`, `ls <path>`, `log N <filter>`. Always request logs with a filter.
- **Control:** `load tower`, `tp p1 x y`, `tap p1 up`, `walkto p1 x`, `patrol p1 x1 x2 sec`, `killall`, `god`, `unlock`, `free`, `gold p1 N`, `givecoins p1 N`.
- **Network:** `net link wifi|bad|off`, `net quality [reset]`, `net interp buffer|legacy`, `net resync`, `net drop`.

Game launch arguments:
- `--coop-host`, `--coop-join IP[:port]`, `--coop-steam-host`, `--coop-steam-join [lobby]`: create or join a game right away.
- `--coop-window left|right`, `--coop-device auto|keyboard|gamepad|all`: window placement and input device.
- `--coop-devserver`, `--coop-link <profile>`: debug server and link emulation.

### Git
- The main branch is `main`. Releases are tagged `vX.Y.Z`.
- Work happens in separate branches, one commit per task.
- Every task must end with a clean `route-test`.

### Config
File: `<game>\BepInEx\config\local.luckytower.coop.cfg`. Almost everything can also be changed in the F7 menu.

| Section | Keys |
|---|---|
| General | Enabled, ShowNames |
| Difficulty | EnemyHealthMultiplier, ExtraEnemies |
| Spawn | AutoSpawnP2, SpawnInVillage, SpawnDelaySeconds |
| Input | P2Device |
| Camera | LeashSeconds |
| Revive | KillsToRevive, ReviveOnNewFloor |
| Net | Transport, DirectPort, DirectJoinAddress, PauseOnDisconnect, MenuKey, ClientPrediction, InterpolationBuffer |
| Dev | CommandServer, CommandPort, ForceWindowed, RunInBackground |
