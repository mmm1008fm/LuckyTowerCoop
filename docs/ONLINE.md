# Online co-op: design

## Status (v0.2.0)
Verified with two game copies on one PC (Direct UDP over localhost):
connection and handshake; loading the host's scene with its seed and difficulty (generation matched); synchronization of rooms,
players, enemies (positions and animations), items; client input → its character on the host; door voting with client input,
"1/2" on the client; moving between rooms; connection loss (`net drop`) → pause, host window, character death with item drop →
auto-reconnect → a fresh character; client process crash and reconnecting mid-run;
corpse synchronization. Steam: `CoopNet.exe` initializes Spacewar and creates a lobby (connecting a second account has not been verified).

Differences from the original design: the reconnect window is unlimited - as long as the host has not closed the session, the partner can come back;
on connection loss their character dies (items can be picked up), and on return it spawns anew without items
(if it was dead, it stays dead until resurrected).

## Model
**Host authority + client mirror.** Only the host simulates the game (AI, damage, doors, loot, death).
The client's character on the host is the second player (P2) from local co-op, and its input arrives over the network
(the same `InputOverrides` mechanism as scripted input). The client runs the same scene with the same seed,
but simulates nothing locally: all characters, enemies and items are "puppets" that replay the host's snapshots.

Pros: no logic desync (loot, damage, AI live in one place), minimal changes to the game.
Con: client input latency = ping (tolerable for the unhurried Lucky Tower; prediction later).

## Transport
| | How | Why |
|---|---|---|
| **Steam (Spacewar, AppID 480)** | separate `CoopNet.exe` process (.NET 8, Steamworks.NET 20.1.0 + Valve `steam_api64.dll`), friends lobby, `SteamNetworkingMessages` (Valve NAT/relay). Plugin ↔ CoopNet over LiteNetLib on 127.0.0.1 | connecting without IPs and ports |
| **Direct UDP** | LiteNetLib in the plugin, IP:port | LAN / Tailscale / tests with two copies on one PC |

Why a separate process: within one process the Steam API works under a single AppID, and the game has already initialized its own.

## Session and connection loss
- `Hello(protocol version, name, token)` → `Welcome(session token, run seed)`. A version mismatch is rejected.
- Heartbeat 2 Hz, timeout 6 s.
- **Client connection loss**: the host pauses the game (`PauseOnDisconnect`) and shows a window "Wait / Play solo / Close session". The client's character dies (items can be picked up). The client reconnects by itself with the same token (or anew after restarting the game), the host does a full resync (scene → seat states → room → entities), and the character spawns anew without items.
- **Host connection loss**: the client freezes with a message and keeps reconnecting; the run lives on the host as long as its game is open.

## Synchronization
1. **Run**: the host picks a seed and sends `SceneLoad(scene, seed)`; both force the `SegmentManager` seed → identical generation.
   Non-random spots of the generator (`new System.Random()`) are switched to the seed.
2. **Room**: `RoomEnter(name, positions, bitmask of activeSelf over the whole room hierarchy)` - the client performs
   the room change itself (like `EnterDoorRoutine`, without the logic) and aligns enabled objects (randomizers).
3. **Entities**: ID = hash of the path in the room hierarchy; objects created during play (loot, throws) - `Spawn(id, ItemLibrary name)`.
4. **Snapshots** 20 Hz (unreliable): position, rotation, flip, animator state per layer, layer weights.
5. **Events** (reliable): death, equipment, "1/2" at doors, resurrection counter, game over, scene change.
6. **Client input** 60 Hz (unreliable): mask of held buttons + mask of presses since the last packet (a tap is not lost).

## What is disabled on the client
Enemy/NPC AI, damage and death (except host events), room transitions (except host events),
hatch/passage triggers, game over, **saves** (the client's save is untouched), loading inventory from the save,
animation events on puppets.

## Risks
- Generation may diverge (randomness in rooms' `OnEnable`) → activeSelf mask + object count check, mismatches go to the log.
- Objects created by enemy spawners during play may not be found by path on the client → logged, no puppet is created.
- Equipment on puppets calls the game's equip methods - side effects are possible.
- Not synchronized (yet): lines/subtitles, puppet animation sounds, village state (the client has its own), shops/bank, cutscenes.
- The Steam transport can only be verified live with two accounts.
