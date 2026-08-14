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
- [ ] Confirm the claims-found path against a real claim (needs a live
      client session to actually claim land first — dry run only so
      far).
- [x] Confirmed `/wgen regen <radius>` works from a bare console caller
      with no connected player (reads `Caller.Pos`, not
      `Caller.Player`) — de-risks reusing it later instead of
      hand-rolling chunk deletion.

## 0.2 — the storm actually does something

- [ ] `UnknowingConfig` (mirroring Caveshrooms' `CaveshroomsConfig`
      pattern — loaded via `api.LoadModConfig`/`api.StoreModConfig` into
      `ModConfig/TheUnknowing.json`, single source of truth, admin-tunable
      without a rebuild): every tunable this milestone introduces
      (storm duration, enemy severity/spawn rate, spawn count, tick
      intervals, etc.) is a config field from the start, not a
      hardcoded constant retrofitted later.
- [ ] Claim suppression: remove/disable the target claim(s) the moment
      the storm starts, so the base becomes lootable immediately.
- [ ] `UnknowingStormManager` (ModSystem): tracks active storms, ticks
      them through a lifecycle (`Spawning -> Active -> Collapsing ->
      Regenerating -> Done`), persists to save-game data so a server
      restart mid-storm doesn't orphan one.
- [ ] Containment: periodically despawn any storm-owned entity found
      outside the storm's chunk bounds.
- [ ] Mob spawning: periodic timed spawns within the chunk set while
      `Active`.

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
