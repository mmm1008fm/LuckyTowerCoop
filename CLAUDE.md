# CLAUDE.md — LuckyTowerCoop

Two-player co-op mod (BepInEx 5 + HarmonyX) for **Lucky Tower Ultimate** (Studio Seufz, Unity 6, Mono).
Local co-op on one PC and online co-op: host-authoritative, the client mirrors the host.
Transports: Steam (separate `CoopNet.exe` process on Spacewar, AppID 480) or direct UDP (LiteNetLib).

Start here: [README.md](README.md) (user + developer overview), [docs/PLAN.md](docs/PLAN.md) (state and history),
[docs/TASKS.md](docs/TASKS.md) (task queue), [docs/COVERAGE.md](docs/COVERAGE.md) (which game mechanics work for two
players, top risks), [docs/CODE_MAP.md](docs/CODE_MAP.md) (map of the game's classes), [docs/ONLINE.md](docs/ONLINE.md).

## Working rules
- **Talk to the user in Russian.** Everything in the repo is English: code, comments, UI, logs, docs, commit messages.
  The only Russian file is `README.ru.md` (a translation of README.md — keep both in sync).
- **No attribution of Claude anywhere.** No `Co-Authored-By: Claude`, no "Generated with Claude Code" in commits,
  PR descriptions or files. The user explicitly does not want Claude listed as a contributor.
- **The repo is public.** Never commit game files or anything personal: no decompiled game code, game DLLs, saves,
  logs, screenshots, local usernames, e-mails, IPs, Steam IDs, tokens. `.gitignore` already excludes `decompiled/`,
  `dist/`, `save_backup/`, `bin/`, `obj/`, `*.zip`.
- One branch per task, one commit per finished task, plain English commit messages. Releases are tagged `vX.Y.Z`
  on `main`; the version lives in `src/LuckyTowerCoop/Plugin.cs` and `LuckyTowerCoop.csproj` (both peers must match).
- Verify with observed results (numbers from dev commands, logs, the route test), not "it compiles".
  State plainly what was not verified.
- Mechanical, long iterate-until-green work (translation, repeated test runs, release checklists) is a good fit
  for a cheaper model with a precise task spec (see the style of `docs/TASKS.md`).

## What works where
| Activity | Cloud session (Linux, no game) | User's Windows PC (game installed) |
|---|---|---|
| Read/modify code, docs, scripts | yes | yes |
| Build `src/CoopNet` (NuGet only) | should work: `bash tools/cloud-setup.sh` installs .NET 8 and builds it (not yet tried on Linux) | yes |
| Build the plugin `src/LuckyTowerCoop` | **no** — needs the game's assemblies (`Assembly-CSharp`, `UnityEngine.*`, Rewired, BepInEx) from `GameDir` | yes (auto-deploys into the game) |
| Look up game code | **no** — `decompiled/` is local only; ask the user or a local session | yes: `decompiled/Assembly-CSharp/` |
| Run the game, two-instance tests, Steam | no | yes |

In a cloud session: write the change carefully (no compiler for the plugin), keep it small, and end with exact
commands for the user to build and test on the PC. If you need game internals you don't have, say which class or
method you need instead of guessing signatures.

## Build and test on the PC (PowerShell)
```powershell
Get-Process 'Lucky Tower Ultimate','CoopNet' -ErrorAction SilentlyContinue | Stop-Process -Force   # dll is locked while the game runs
dotnet build src\LuckyTowerCoop\LuckyTowerCoop.csproj -c Debug        # deploys to <GameDir>\BepInEx\plugins\LuckyTowerCoop
dotnet publish src\CoopNet\CoopNet.csproj -c Release                  # deploys CoopNet next to the plugin
pwsh tools\route-test.ps1 -Rooms 10                                    # regression: host/client entities equal, resync 0, no new mod errors
pwsh tools\net-quality.ps1                                             # network bench: emulated links, input lag, partner hitches
pwsh tools\make-dist.ps1                                               # release zip with installer in dist\
```
`GameDir` defaults to `D:\game\Lucky.Tower.Ultimate.v1.1.4` (override with `-p:GameDir=...`).
Two instances on one PC: `tools\local-online.ps1 -DevServer` (host left, client right). Dev command server:
host port 47800, client 47801; `. tools\devlib.ps1` then `DevA 'where'`, `DevB 'net status'`.

## Architecture in brief
- **Host authority.** Only the host simulates. On the client, world logic (all `Trap` subclasses and the list in
  `Patches/WorldPatches.cs`) is suppressed; entities are puppets driven by snapshots (`Net/NetHost.cs`, `Net/NetClient.cs`).
- **Player 2 is a second instance of the player prefab.** Its input comes from Rewired's *system* player (local) or
  from the network (`Net/NetInput.cs`).
- **"Current player" swap.** Game code assumes one player (`GameManager.Instance.Player`, `Player.CurrentPlayer`,
  `Player.VonWansts[0]`). `TargetSwap` substitutes P2 while P2 acts: coroutines, triggers and component updates owned
  by P2 (`Patches/ContextPatches.cs`), weapons, transformations, purchases (`ActingAsPatches.cs`, `PurchasePatches.cs`);
  enemies target the nearest player (`AiTargetPatches.cs`). Anything still reading the single player is P1-only —
  see the risk list in `docs/COVERAGE.md`.
- **Entity identity** is the path in the room hierarchy (`NetPaths` in `Net/NetEntities.cs`). Path separators are
  control characters because game object names contain `/` and `#`; use `NetPaths.Pretty` for logs.
- **Same world on both sides.** Tower generation uses the run seed; randomizers are re-seeded per object by path
  (`Patches/RandomizerPatches.cs`, restoring RNG state afterwards); caves are seeded via `CaveSeedPatch`.
- **Smoothness** (`Net/NetInterp.cs`): the client renders the partner and the world at host time minus an adaptive
  delay; seat snapshots are stamped with the last physics step time (players move at 50 Hz physics); own character is
  predicted and reconciled by input sequence number. Send schedules advance from the previous deadline (`NetConst.NextTick`).
- **Network lab** (`Net/NetLinkSim.cs`): receive-side emulation of latency/jitter/loss/bandwidth for any transport
  (`net link wifi|bad|off`, `--coop-link`); metrics via `net quality`.
- **Steam path**: plugin ⇄ CoopNet over LiteNetLib on localhost (reliability preserved per message), CoopNet ⇄ Steam via
  `SteamNetworkingMessages`; CoopNet reports connection stats (`[Steam]` log lines).

## Known gotchas
- Never set Rewired `ignoreInputWhenAppNotInFocus = true`: it kills the networked P2 input on an unfocused host.
- .NET processes without `timeBeginPeriod(1)` sleep 15.6 ms for `Thread.Sleep(1)` on Windows (fixed in CoopNet and
  during sessions in the plugin).
- Dev command `log N` replies over UDP and is truncated around 64 KB — always pass a filter: `log 50 [NetClient]`.
- PowerShell: `[IO.File]` APIs ignore `cd`/`Set-Location` — use absolute paths. `Remove-Item`/`git rm` may be blocked
  by the agent harness; use bash for `git rm`.
- `.ps1` files are UTF-8 with BOM, C# files UTF-8 without BOM.
- The game itself logs `Loading standard input` and sometimes `... has no linked item spawner` — not mod errors.
- Test runs change save profiles in `StreamingAssets\SaveFiles` — back them up (`save_backup/`, local only).

## Open work
See `docs/TASKS.md` and the top risks in `docs/COVERAGE.md`: companions follow P1, respawn bottle is P1-only,
hammer guy / stealth traps target P1, Evelius talks to one player, shared depth layer, Limbo bubble door reseeds the
tower on the host only. Steam between two different accounts and real gamepads still need a live test by the user.
