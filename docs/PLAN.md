# Online rework plan (after the first live run of 0.3.1/0.3.2)

## What was reported broken

Shops (the second player cannot buy); the banana "forever"; levers not visible and not pressable; trap doors not visible; the evil mage not visible;
drummers never stop; the floor collapses late; monsters from pits are out of sync; the minion's spell; the camera does not follow the friend,
blackness, lighting; the mirror works for the host only; companions; the chest cannot be broken; traps lag behind; the camera freezes after death;
input delay ("like a second" at ping 50); a client restart breaks everything; damage to enemies is not visible; the genie/monkey.

## Three root causes

1. **The client does not simulate its own character.** Input → host → snapshot → smoothing. The chain at ping 50:
   50 (network) + 2×(5–10) (`CoopNet` sleeps in both directions) + ~25 (snapshot at 20 Hz) + 70–100 (exponential smoothing, k = 1−e^(−15·dt))
   + 2 frames ≈ 200–250 ms. Our own server would not help: the delay is in the pipeline, not in the transport.
2. **Only `Character` and `PickupItem` are synchronized.** The world (28 `Trap` subclasses, `Lever`/`PuzzleLever`/`LightsOutLever`,
   `TrapCrumblingGap`/`ParkourGapInFloorTrap`, `TreasureChest`, `TrapBigBand`, `TrapLightsOut`, `PotentialMonster`, `EveliusController`)
   lives on the two machines independently. The client's traps react to its lagging puppet.
3. **The game everywhere takes "the player" = `Player.CurrentPlayer` / `GameManager.Instance.Player`.** `TargetSwap` exists only on Update/FixedUpdate/
   GetDamaged/purse. These slip through: coroutines (banana: the end of the slide is in a coroutine), `PawnshopMain` (11 references), `Djinn`, `MirrorPlatform`,
   `TrapLightsOut` (15), `Sleeper` (18), `TrapPowderKeg` (11).

## Phases

### 0. Measurement (S)
- The client numbers input packets; the host returns the number of the last applied one in the snapshot; the client computes three segments:
  input→applied on host, snapshot→received, received→visible (after smoothing). Show them in the HUD (F7 → Online).
- Check the send flags in `CoopNet` (snapshots/input: Unreliable, NoNagle).

### 1a. Quick latency wins (S)
- `CoopNet`: `Thread.Sleep(10/5)` → 1 ms or an event wait.
- Input: send immediately when the mask changes + a 60 Hz heartbeat.
- Snapshots of seats A/B at 60 Hz, other objects at 20 Hz.
- Smoothing: k based on the time since the last snapshot (dead reckoning by velocity), not an exponential.
- Goal: ~250 → ~120 ms.

### 2. Synchronizing the whole world (L) — the core
- The host registers all room objects with `Animator`, `Rigidbody2D`, `Trap`, `Lever`, `TreasureChest`, `IDamageable`
  (extend `Scan`; IDs by path, as now).
- Object snapshot: animator (exists), transform (exists), `enabled`/`activeSelf` (partly exists), health/damage (new).
- On the client the `Trap`/`Lever`/`…` components are disabled (`Behaviour.enabled = false`): display only; damage to the client comes from the host.
- Damage to enemies: the host sends health, the client calls the standard texture swap (`ReplaceColor`/damage sprite).
- Despawn of any registered objects (chest).
- Objects outside the room hierarchy (mage, monsters from pits, companions): registration by room boundaries + creation from a prefab (exists in 0.3.2; to be verified).
- Closes: traps, trap doors, floor, drums, levers (appearance), chest, lighting (LightsOut), the purple ones, mage, damage textures.

### 3. "Who is the player right now", systematically (M, risky)
- A resolver instead of spot `TargetSwap`s: a transpiler for calls to `Player.get_CurrentPlayer` / `GameManager.get_Player` in the types
  `CharacterUtilities`, `CharacterAction`, `PickupItem`+descendants, `Trap`+descendants, `PawnshopMain/Sell`, `Djinn`, `MirrorPlatform`,
  `InteractionWindow` → "the character we are working with" (the component's owner / whoever entered the trigger / the current swap).
- Character coroutines: an `IEnumerator` wrapper, every `MoveNext` under the owner's swap (a `MonoBehaviour.StartCoroutine` patch for `Character`).
- Closes: banana, the mirror for the second player, genie/monkey for the second player, shop logic for the second player, hints.

### 1b. The client moves its own character itself (L)
- The client's own character is simulated locally (Update/FixedUpdate/physics/animation enabled), the pose goes to the host at 60 Hz;
  on the host the client's character is a puppet driven by the pose, but actions (attack, pickup, lever, door, purchase) are events from the client, which the host confirms.
- Divergences: the host may "roll back" the client (teleport at >2 m); rare if phase 2 is done.
- Do this only after phase 2.

### 4. Second player's interface (M)
- Forwarding `InteractionWindow` (the single window for dialogues/shop/genie/bank/companions): Open(options, state) → Choice → Close.
- Client restart → request to the host + a vote on the host; a local restart on the client is blocked with a message.
- Death on the client: the partner becomes the "current player" for the game's systems (camera, `UberFog` fog, lighting, `SnapCameraToCollider`).
- Closes: purchases, dialogues with choices, restart, camera/blackness after death.

### 5. Body swap (M)
- The monkey (genie), the minion and the monster potion replace the player object (`CharacterAction` ~6588/6639).
- A `Transform(seat, form)` message; `CoopPlayers` rebinds P1/P2; the client replaces the puppet with the same prefab.

### 6. Stabilization (M)
- `route-test.ps1`: hatches, balcony, outer wall, bonus rooms; checksums for all registered objects.
- A 10+ minute endurance run; Steam on two accounts; release 0.4.

## Order
0 → 1a → 2 → 3 → 1b → 4 → 5 → 6.

## Status (2026-10-12)
- Phase 0 — done: the "input→screen" HUD (client), `net sim N` for ping emulation.
- Phase 1a — done: delay = ping + ~26 ms (measured: ping 100 → 132 ms, ping 240 → 265 ms).
- Phase 2 — mostly done: world objects (animator/physics/Trap/Lever/Damagable/chest) are registered and included in snapshots;
  on the client the trap/lever/damageable logic is muted (47 types); enemy health → damage texture; world sound (drums);
  decoration alignment by path hashes (via a list of disabled objects); "lights out" is forwarded; objects created by the host
  on the fly (monsters from pits, the hammer guy) are created on the client from a prefab.
  Closed on Oct 12 (evening): object paths — names containing "/" ("Window 6/4") and "#" ("Door #1") broke path parsing, the separators were replaced
  with service characters → the balcony synchronizes completely (458/458 objects), hubs without losses; the deleting randomizers are seeded.
- Phase 3 — done: "current player" swap for the second character's coroutines (and for coroutines started during the swap — the banana),
  for physics callbacks (171) with the second character's collider, for Update of components on its object (142); weapon hit on "player only" targets.
  Verified: the banana on the second player ends.
- Phase 4 — partly: the choice window (InteractionWindow) is forwarded to the client (open/close, appearance, binding); client restart →
  a request to the host with a "Start over / No" window (verified). Not done: camera/fog on the client after death = partner.
- Phase 4 (camera): done — after death, the client's game treats the partner as "the player" (camera, fog, lighting), and its own character again after revival.
- Phase 5 — done: the second character's transformation (monster potion, genie, Evelius) runs under the swap, and the new body is rebound as the second player;
  the client receives the body form in PlayerState and creates a puppet from the same prefab (verified: monster, the client walks with it).
- Randomizers (jars, bombs, monsters in windows): before each decision the generators (UnityEngine.Random and ThreadAwareRandom)
  are reseeded with "room+path+run seed", and the state afterwards is restored (otherwise tower generation broke).
- Oct 12 (evening), 0.3.4: balcony — bird, trap doors, drop zone, the companion trap on the host only, wind from the shared seed;
  character shadows, stomper dust, fx_* effects are not registered (cosmetics, −80 objects per room);
  Evelius in hubs — his logic does not run on the client (previously he self-destructed based on the puppet's position), a surplus Evelius
  is removed if the host did not register him. Verified by the autotest: dialogue windows open/close on the client,
  "lights out" both ways, segments 1–3 are generated identically (`[Gen]` fingerprints match), bonus rooms, locked
  rooms with levers. Once, the segment 3 room was not found on the client — the cause was not caught (the fingerprint is now written to the log).
  Diagnostics (dev): `[Gen]` generation fingerprint, `[Lock]` who locks movement; dev commands `ls` ("|" as a separator for
  names with a slash), `ent <id>`, `rooms`, `unlock`, `free`, `doors` with state; route-test repeats the door after killall/unlock.
- Not closed: the powder keg (tower explosion video) — the video is not shown on the client, the run is over for both;
  "leftover" objects that fell off the map fall forever (extra traffic); movement prediction (NetPredict) is unverified;
  cave generation uses an unseeded System.Random (RoomGenerator.GenerateCave) — awaiting verification.
- Phases 1b, 6 — not started.

## Network lab and smoothness (0.3.6, Oct 12)
The `tools\net-quality.ps1` bench: two copies over localhost, both with link emulation (`net link <profile>` / `--coop-link`,
profiles lan/good/wifi/bad/awful: delay, jitter, loss, bandwidth), both characters walk back and forth, the client measures
input delay (p50/p95), the partner's hitches (frames where the displayed shift diverges from the velocity), stream gaps,
corrections of its own character. The table goes to the console and to `docs\net-quality-last.csv` (not in git).
What was done based on its measurements:
- Interpolation buffer (`Net.InterpolationBuffer`, on by default): the client shows the partner and the world at the moment
  "host time − (snapshot interval + jitter)", the host clock is estimated from snapshot timestamps, display time
  is continuous (0.94…1.06 of real time), and when data runs short — extrapolation up to 100 ms.
- Timestamps: the host's real time; player snapshots — the time of the last physics step (characters move
  in 50 Hz physics steps, frames are 60 — the frame timestamp produced "freezes").
- Send schedule counted from the previous deadline: previously at 60 frames/s snapshots and input went out 30–45 times a second.
- Prediction of its own character is reconciled with the host by press number (the position on the host when it applied press N,
  versus the position where we showed ourselves when sending N): no more sharp corrections at high ping.

| Link | Partner hitches: before → after | Own character corrections: before → after |
|---|---|---|
| lan | 0.5% → 0.4% | 0 → 0 |
| good (25±6 ms, 0.5%) | 0.9% → 0.2% | 0 → 0 |
| wifi (40±30 ms, 2%) | 2.8% → 0.4% | 0 → 0 |
| bad (90±60 ms, 5%, 96 kB/s) | 8.0% → 1.9% | 3 → 0 |
| awful (160±100 ms, 10%, 48 kB/s) | 10.0% → 1.2% | 3–4 → 0 |

The cost: the partner is shown 30–60 ms later (about 45 ms on a good link). Input delay did not change,
and on a LAN it dropped (28 → 21 ms on average) thanks to the even send pace.
Further on quality: animations are applied on receipt, while the position is applied with the buffer delay (a mismatch of ~45 ms);
enemies (also physics) travel in 20 Hz world snapshots with the frame's timestamp.

## Steam segment (0.3.5, Oct 12)
Code review and measurements without a second Steam account:
- CoopNet slept 15.5 ms instead of 1 ms: a .NET process without timeBeginPeriod gets the coarse Windows timer, and LiteNetLib ticked on it as well.
  Every packet waited up to two such ticks in each CoopNet: an estimated +30…60 ms of delay and hitches. Fixed: timeBeginPeriod(1)
  in CoopNet and in the game for the duration of the session, TriggerUpdate after sending. The self-check at CoopNet start writes `timer: Sleep(1) = 1.6 ms` to the log.
- The game–CoopNet segment was entirely ReliableOrdered: unreliable snapshots became reliable and queued up on a stall.
  Now reliability is preserved in both directions (messages that are too large go reliable).
- Reliable messages in Steam went out with Nagle (up to 5 ms); now ReliableNoNagle.
- Steam segment measurements: once a second CoopNet sends `stats` (ping, loss in both directions, kB/s, Steam's rate limit, bytes in the queue,
  time in the queue, relay or direct). Visible in the status line (F7), in `net status`, in the log as `[Steam]` every 10 s and immediately on
  loss > 2% or queue > 50 ms.
- Host traffic (Direct, measured): ordinary rooms 5–8 kB/s, balcony 29 on average, peak 42 kB/s; balcony registration ≈ 22 packets of ~1 kB each.
  Compressing paths into hashes is not needed yet; come back to it if `[Steam]` shows a queue when entering rooms.
- Not verified live: a connection between two accounts (needs a second account + Sandboxie, `Online Steam on one PC.cmd`).

## About the transport
Steam (Spacewar) stays: lobbies/invitations/NAT with no dependency on our own server. Our own relay in the US would lengthen the path.
A possible replacement inside Steam: `SteamNetworkingSockets` instead of `SteamNetworkingMessages` (better control over reliability/buffers) — after phase 0, if measurements show losses.
