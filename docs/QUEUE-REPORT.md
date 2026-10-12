# Report on queue A–E (branch work/queue)

Verification everywhere: two copies via `tools\local-online.ps1 -DevServer`, seed `seed 4242` (a new dev command) or random;
regression after each task — `pwsh tools\route-test.ps1 -Rooms 10`: for all rooms `entities host=N client=N` are equal,
`resync: host 0, client 0`, no mod errors (only `Loading standard input`; once the game's own `Torture Chamber ... has no linked item spawner` on both copies).

## A. Purchases by the second player are paid with their coins — `11ee8bd`
What was done:
- `Weapon.ApplyDamage` runs on behalf of the owner when that is the second player (previously only `GetPotentialHitTargets` was under the swap,
  and the hit on the lever was applied outside the swap) → the `Automat`, `CompanionAutomat`, `PipeSystem` vending machines deduct from whoever hit them.
- The coordinator's hint was confirmed and extended: `DialogueTrigger` caches P1's dialogue manager. Now, for all methods of the
  30 `DialogueTrigger` subclasses (221 methods), the `m_dialogueManager` field is replaced with the acting player's manager (the field, not the getter —
  the getter may be inlined by the JIT); the shops' "action" methods (payment, haircut, animator events) run on behalf of the player
  whose manager is running the dialogue (`Patches/PurchasePatches.cs`).
- Found a window forwarding bug: when opened, `InteractionWindow` goes under the NPC's "icon" point, and `OwnerContext.SecondOwnerOf`
  did not recognize it as the second player's window (log `open ... on host window`, the client did not see the window, the window's Update ran outside the swap).
  Now the window is matched against `P2.Components.InteractionWindow`.
- `DropAllCoinsDialogue` (bank/ATM) works with the player standing in its zone (`IsTouching`).
- Dev: `gold p1|p2 [N]`, `shops`, `seed N`, `village bank on`, `find`; `tools\testlib.ps1` (`Start-Pair`, `Enter-Room`); helper `Coop/CoopGold.cs`.
Verified (numbers):
- Companion vending machine (price 4), a hit on the lever by the client's character (seat B): it goes 12 → 8, the host stays 12 → 12. Before the fix the host paid (12 → 8, the client at 12).
- Vending machine in a hub (price 6), hit by the host: 15 → 9, the client 15 → 15.
- ATM in the village (`village bank on`), P1 stands far away (−166), the client's character enters the zone: window `DropCoins on partner window`,
  the client sees it, choice → the client goes 9 → 0, the host 6 → 6, `coinsBank` +9. Then the other way round (host): 6 → 0, the client 7 → 7.
- Pipefitter: on the client a `TalkPrompt` window opens (the partner's, mirrored to the client).
Not verified live: the full payment cycle at `Pawnshop`, `PawnshopMain`, `Barber`, `Pipefitter` (long dialogues / the head room `VonWanstHeadManager`,
they change the village save). The swap on their payment methods is in place (`ShopActionContextPatches` → `DialogueTriggerContextPatches`), but I did not run a live payment by the second player there.
Not touched: `EvTasks` (Evelius takes gold: `GameManager.Player` under the AI swap), `CharacterUtilities.cs:4851/4955`.

## B. Giving coins to the partner — `05907a7`
- Players tab (F7): a "Coins" block — "Give 1 / 10 / all" for your own character (locally — for each).
- `Msg.GiveCoins = 29` (client → host, the amount); only the host changes things (`CoopGold.Transfer`: deduction via `Utilities.RemoveCoins`, crediting
  to the belt, an empty purse is created as in `GameManager`/`EquipGoldSack`; limited by the recipient's free space — 20 per purse).
- `PlayerState` now carries the seat's total coin count, and the client menu shows real numbers (previously the client's puppets have no purses).
- Dev: `givecoins p1|p2 N` (the same as the button; on the client it is a request to the host).
Verified: start host P1=5, client's=8 (the client sees 8/5). The host gives 3: host 2/11, client 11/2. The client gives 4: host 6/7, client 7/6.
The client gives "all": host 13/0, client 0/13. Limit: the host has 18, the client gives 10 → host 20, client 8. Screenshot of the tab on the client: "You: 8 … Give 1/10/all".
I did not click the buttons with the mouse (GUI) — I called the same `CoopGold.GiveToPartner` via the dev command.

## C. "leftover" debris — `82394f8`
- `NetHost.Register` does not register (except characters) objects `leftover*`, `*LeftOvers`, under the parents `PlantRemains`/`*LeftOvers`, `*ArmorDestroyed*`
  (`PeasantArmorDestroyed_NN`). It turned out the debris are `PickupItem`s, which went into the item scan rather than into `WorldObjects`.
Verified: before the fix, on the route (random seed) the client wrote `3 of 4 host objects not found here: leftover (1); leftover (2); leftover (3)` (twice);
after: room `S01_F01_R01`, broke a chest (5 active `leftover`, `rb=True`): `netents` on the host and the client has 0 `leftover` objects, 29/29 entities each,
0 `not found ... leftover` lines in the client log. `PeasantArmorDestroyed_02..06` (5 objects "not found" in `S03_F06_R03`) were added later, not rechecked in a separate run
(the lines no longer appeared in subsequent runs; once a 58/57 difference remained in `S03_F06_R01`).

## D. Tower explosion video on the client — `24af8ba`
- `Msg.TowerExplosion = 31`; `Patches/PowderKegPatches.cs`: a prefix on `TrapPowderKeg.DestroyTower` and `SpecialItemPowderKeg.DestroyTower` —
  the host sends the message, and on the client the local call is blocked. Client: a coroutine (input and movement of both characters off, the tower sound fades as on the host,
  `VideoLibrary.ExplodingTower` on `CameraManager.MainVideoPlayer`, `CameraNearPlane`, `PlayingVideo=true` for the duration of the clip, then false).
  Added a reference to `UnityEngine.VideoModule` in the csproj. Dev: `kegboom`, `itemnames`, `spawnitem Name_with_spaces`.
Verified: `kegboom` on the host (there were no kegs in the scene — an empty `TrapPowderKeg`, a call to the real `DestroyTower`):
host `[NetHost] tower explosion sent`, client `[NetClient] tower explosion` → `video playing` → `video finished`; client screenshot — a frame of the clip (destroyed tower, smoke).
Limitations: I did not ignite a real keg in a room (there are none in segment 1 with seed 4242; they are in later ones); the death is still done by the original code only for `GameManager.Player`
(it does not kill the second player — out of scope, left as is).

## E. Caves and the segment 3 desync — `8cff463`
Task 5 (S03): the cause was found and fixed. Segment generation reads `GameManager.Player.Stats.SegmentID` (room names `S0N_…`, floor type,
the index into the `NonExitRooms` list). The game sets it only when passing through a door; the client enters rooms by the host's message, so its value
lagged behind (1 vs 2) and segment 3 got rooms `S02_F05_R01…` instead of `S03_…`. `[Gen]` fingerprints in the reproduced run:
`segId=2` (host) versus `segId=1` (client), the room lists differ only in the prefix. The fix: `NetClient.SyncPlayerSegment` before entering a room (and `seg-1` when generating the remainder).
Verified: 5 runs of `route-test -Rooms 40 -Until S03_ -AvoidBalcony` (flag added): `resync 0/0`, object counts equal in every room, all `[Gen]` DungeonOne…Four
match (`gencmp`: "same"; in one run only the player name/teeth differed, which does not affect the rooms). Room `S03_F04_R02` is reached and found on the client.
Without the `-AvoidBalcony` flag the route goes through Limbo (see below) — that is a separate phenomenon.

Task 6 (caves): the seeded `GenerateCave` is itself deterministic: dev commands `gencaves` + `cavehash` (create segments 2…cave on both copies and compute a fingerprint
of the rooms and level elements) → the same hash (`26192FAA` on both; in runs through rooms `736169D5`/`E428DCBF` — equal on the host and the client).
But on the real route the "Cave" room on the client was a different one: cave room names repeat in clusters ("Cave 6" ×3, `seg=7/8/9`), and `FindRoom` took the first by name
(host 32 objects, client 15). Now `FindRoom(name, segmentId)`. After the fix, in the run `-Until Cave -AvoidBalcony`: Entrance and Cave without divergences, `resync 0/0`.
Did not work / open:
- Caves without `-AvoidBalcony`, and in general the path through the Balcony → "Sphere BubbleDoor" (Limbo): the host recreates `SegmentManager` with a new seed (the tower-verse),
  the client does not know about it → `EnsureSegments` spins anew on every room (hundreds of `generating segment 1..3`), the client has 0–50 entities against the host's 90, and `resync` grows.
  This needs a separate feature (passing the new seed + repeating `StartTowerverseTransition` on the client) — I did not do it.
- `route-test` without `-AvoidBalcony` also runs into locked "ChestKey/LockedChest" rooms (`no forward exit`) — a route heuristic, not the mod.

## Miscellaneous
- The test save profile `PROFILE_TWO` in `StreamingAssets\SaveFiles` was modified by the tests (village, bank); a copy was made before the tests, the modified files were restored
  (profile `PROFILE_ONE` was not changed). New autosaves labeled `261012_*` remain.
- Not touched: `docs/COVERAGE.md`, the mod version, Google Drive, `main`.
