# The Unknowing — Roadmap

Task list organized by milestone. Checked items are confirmed done;
unchecked items still need work. See `README.md` for how the mod works
so far.

## 0.1 — scaffold

- [x] Mod project builds and loads (`TheUnknowingModSystem`).
- [x] `/unknowing-storm <playerName>` resolves every claim owned by that
      (possibly offline) player and reports the chunk columns they
      cover. Confirmed end-to-end on a throwaway dedicated server,
      including the no-claims-found error path.
- [x] Confirmed the claims-found path against a real claim: targeting
      and claim removal both work (verified via `/land list` before
      and after). Found and fixed a real bug along the way —
      `LandClaim.LastKnownOwnerName` isn't populated at claim-creation
      time, so targeting now resolves through `api.PlayerData` instead
      (see README's "Claim targeting" section).
- [x] Confirmed `/wgen regen <radius>` works from a bare console caller
      with no connected player (reads `Caller.Pos`, not
      `Caller.Player`) — de-risks reusing it later instead of
      hand-rolling chunk deletion.

## 0.2 — the storm actually does something

- [x] `UnknowingConfig` (mirroring Caveshrooms' `CaveshroomsConfig`
      pattern — loaded via `api.LoadModConfig`/`api.StoreModConfig` into
      `ModConfig/TheUnknowing.json`, single source of truth, admin-tunable
      without a rebuild). Every tunable this milestone (and future ones)
      introduces is a config field from the start, not a hardcoded
      constant retrofitted later. First field: `StormDurationHours`.
- [x] Claim suppression: remove the target claim(s) the moment the
      storm starts. Confirmed live via `/land list` before/after.
- [x] `UnknowingStormManager` (plain class, owned/driven by
      `TheUnknowingModSystem` rather than a second `ModSystem`): tracks
      active storms and persists to save-game data. Persistence hit a
      real bug (`ISaveGame.StoreData`/`GetData` is protobuf-net, not
      JSON — needs explicit `[ProtoContract]`/`[ProtoMember]`, unlike
      `LoadModConfig`), now fixed and confirmed both directions live:
      a storm saved cleanly (no exception) and was read back
      successfully on the next server boot.
      `Active -> Collapsing` (duration-based) is wired up but not yet
      manually verified (would need to wait out/fast-forward
      `StormDurationHours`). `Collapsing -> Regenerating -> Done` isn't
      implemented yet (nothing to do there until 0.4's regen call
      exists).
- [x] Containment: periodically despawn any storm-owned entity found
      outside the storm's chunk bounds. Runs on the existing 10s
      `OnGameTick` (no new tick needed) - checks every tracked entity's
      live position against `ChunkColumns` via the new
      `ClaimChunkMath.ToChunkColumn` (same floor/shift math as claim
      targeting, but for a fractional entity position instead of a
      claim's integer bounds), despawns anything outside, and also
      prunes already-dead entities as a second safety net alongside
      `OnSpawnTick`'s own pruning (the two run on different intervals,
      so an entity can die between one tick and the next). Confirmed
      live with `.debug wireframe chunk` for a visual boundary: drifters
      spawned near a chunk edge wandered out and got despawned within
      one tick (explains why spawns can seem to silently not happen -
      they did, just briefly), and manually baiting one across the
      boundary confirmed the despawn.
- [x] Mob spawning: periodic timed spawns within the chunk set while
      `Active`. Its own tick (`EnemySpawnIntervalSeconds`, default 30s),
      spawns a random config-listed entity (`game:drifter-normal`/
      `game:drifter-deep` by default, reusing the game's existing
      Drifter rather than a custom creature) at a random position within
      a random covered chunk column, ground height via
      `GetTerrainMapheightAt`. Capped per storm by `MaxConcurrentEnemies`
      (default 6), which prunes dead/despawned entities from tracking
      first so it reflects what's actually alive.
      First live test spawned nothing - found and fixed a real bug:
      `EnemyEntityCodes` defaulted to `survival:drifter-normal`, but
      entity type codes for the built-in survival/creative content are
      registered under the shared default `game` domain regardless of
      which `assets/<folder>/` they ship from, confirmed by dumping
      `api.World.EntityTypeCodes` live.
      Also found and fixed a second, unrelated bug while cleaning up the
      live config: Newtonsoft.Json appends to (rather than replaces) a
      JSON array deserialized onto a property with a non-empty default
      `List<T>` - `EnemyEntityCodes` was silently duplicating by 2 on
      every server restart. Fixed with
      `[JsonProperty(ObjectCreationHandling.Replace)]`.
      Confirmed working end-to-end live: a clean single-storm test
      spawned drifters normally, no warnings/exceptions anywhere in
      `server-main.log`.
- [x] `/unknowing-storm-clear` (debug/testing only, not part of the
      real lifecycle) - despawns every entity every tracked storm owns
      and clears all storm state. Added because storms never end on
      their own yet (`Collapsing` is still a dead end), so every storm
      from every prior test session was still alive and still spawning;
      confirmed live catching 3 leftover storms / 9 entities from
      earlier sessions in one shot.

## 0.3 — presentation

- [ ] Per-player VFX/audio when near/inside the storm bounds — likely
      piggybacking on the existing temporal-stability visual hooks
      rather than a renderer built from scratch.

## 0.4 — regen

- [ ] Wire up the `/wgen regen` call: compute the minimum enclosing
      radius around the claim's chunk set, construct a `Caller` with
      `Pos` at the center and `Player = null`, call it via
      `sapi.ChatCommands.ExecuteUnparsed`.
- [ ] Full lifecycle test: summon on a real claim, let it run, confirm
      the claim area is gone and the land is regenerated clean.

## Ideas (not scheduled)

- **Demo/RP mode** — a separate command (e.g. `/unknowing-storm-demo
      <radius>`) that summons the storm's presence (spawns, VFX) around
      an arbitrary point with **no claim involved at all** — doesn't
      touch any land claim, doesn't regen anything after. Intended use:
      Dream Realms RP's first planned community event (a town-hall-style
      player meetup) — summon it live as a storytelling beat, where
      players narratively "drive it away" by shouting out things they
      remember, ending the encounter without any real consequence to
      anyone's base. Genuinely a different mode from the main lifecycle
      (no suppression, no regen), not just a flag on the real one —
      revisit as its own small feature once the main storm loop is
      solid.
