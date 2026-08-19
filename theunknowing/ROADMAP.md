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
- [x] Two-phase escalation replacing the old flat `Active` status:
      `GatheringStrength -> EnteringReality -> Collapsing -> Done`.
      Considered a third opening phase ("Gaining Foothold"/"The
      Thinning") but decided against it - the mod's actual purpose is a
      pacing tool for admins to regenerate abandoned land, not a
      narrative engine, so kept it to the two phases that actually
      matter. `StormDurationHours` split into
      `GatheringStrengthDurationHours`/`EnteringRealityDurationHours`
      (still snapshotted onto the storm at creation, not read live, so
      a config change never retroactively alters a storm already in
      progress). `EnteringReality` scales `MaxConcurrentEnemies` and
      `EmberParticlesPerColumn` by a new `EnteringIntensityMultiplier`
      (default 2x) rather than a second full set of phase-specific
      configs. `EnteringReality` also swaps enemy pools entirely - a new
      `EnteringEnemyEntityCodes` (default the stronger drifter variants
      deliberately excluded from the base pool since 0.2: tainted,
      corrupt, nightmare, double-headed) replaces `EnemyEntityCodes`
      rather than adding to it, so the escalation is a real threat
      upgrade, not just more of the same enemy. `UnknowingStormStatus`
      is now explicitly numbered, since
      protobuf-net serializes enums by ordinal and inserting/reordering
      values would silently reinterpret already-persisted storms as the
      wrong phase - any future change to this enum needs a
      `/unknowing-storm-clear` before redeploying.
      Confirmed live via `server-main.log`: shortened both phase
      durations to 1h each for testing and watched a real storm
      transition on schedule - `GatheringStrength -> EnteringReality`
      and `EnteringReality -> Collapsing` both fired within seconds of
      the expected in-game-hours-to-real-time math (2 real min/in-game
      hour at this world's default calendar speed). `BroadcastStormPhase`
      added so both transitions get the same server-wide chat
      notification treatment as the original storm-start message
      (`BroadcastStormUnleashed`), not just a server-log line - players
      have no other way to know the threat just escalated.
- [x] Real bug found and fixed live, right after deploying the above:
      ran `/unknowing-storm-clear` to reset for testing (per the note
      above) while standing near a storm's chunks but not inside them.
      Log showed `despawned 4 tracked entity/entities` and 1 storm
      cleared - but the cloud entities were still standing afterward,
      and a second run immediately after reported 0 storms/0 entities
      (nothing left to even search). Root cause: `GetEntityById` only
      finds entities in currently-loaded chunks, and both
      `ClearAllStorms` and `RespawnClouds` discarded their tracking
      unconditionally regardless of whether a `null` back meant "gone"
      or just "not loaded right now" - so any cloud in an unloaded
      chunk got silently orphaned, un-despawned and now un-trackable
      since the record pointing at it was wiped anyway. Very likely
      the real explanation for the earlier "stacked clouds" bug
      (0.3/reversal) too, not pure z-fighting. Fixed both commands to
      only drop tracking once a despawn is actually confirmed -
      `ClearAllStorms` now marks a storm `Done` immediately (stops
      spawning/fog/self-healing) but keeps any unresolved entity IDs
      tracked for a rerun instead of forgetting them.
      Added `/unknowing-storm-kill-nearby [range]` to clean up clouds
      already orphaned by the old behavior (or any other stray found
      by eye) - despawns any `theunknowing:stormcloud` entity in range
      of the caller that isn't currently owned by a tracked storm, so
      it can't accidentally kill an active storm's landmark.

## 0.3 — presentation

- [ ] Per-player VFX/audio when near/inside the storm bounds.
      First pass (light-grey ambient fog, see below) felt like "spooky
      weather," not "The Unknowing is actively consuming this ground
      into nothingness" - the actual point per the original pitch
      (inky blackness descending from the heavens, devouring the
      world). Asked for direction; got all three candidates back,
      audio marked as a must:
      - Void-black particle color + downward "descending from above"
        motion instead of light-grey ambient drift.
      - Denser/darker wall specifically at the boundary vs. the
        interior, layered on top of the above (not instead of it).
      - Dedicated dread/ambient audio.
      All three now built - not yet tested live:
      - `SpawnFogParticles` reworked: near-black color
        (`ColorUtil.ColorFromRgba(15, 12, 20, ...)`), spawns 10-20
        blocks above ground falling downward (negative Y velocity +
        light `gravityEffect`) rather than drifting at ground level.
        Boundary columns (touching a chunk outside the storm, via a
        `HashSet` neighbor check) get 3x the particle count and higher
        opacity than interior columns, so the edge reads as a sealed
        wall.
      - `OnAmbientAudioTick` (own tick, `AmbientAudioIntervalSeconds`,
        default 15s) plays `game:sounds/effect/rift.ogg` - the game's
        existing Rift sound, closest thematic match already in the
        game for "localized temporal wrongness" - from roughly the
        center of each storm. Confirmed live (empirically, via
        `api.Assets.Exists`) that sound assets share the same
        collapsed-to-`game`-domain quirk as entity codes, despite the
        file living under `assets/survival/` - would have silently
        failed otherwise, same class of bug as the entity-code domain
        issue earlier.
      Feedback after seeing it live: the particles read as "more smoke,"
      not real fog (the atmospheric, view-distance-limiting kind).
      Fair - particles alone can't do that.

      **Real fog, via `IAmbientManager`.** Confirmed live this is a
      general-purpose blended-modifier system (`CurrentModifiers`, an
      `OrderedDictionary<string, AmbientModifier>` of `FogDensity`/
      `FogColor`/`FogBrightness`/etc.) - not owned by Temporal
      Stability specifically, so pushing our own entry under our own
      key doesn't reopen any of the risk that approach carried. First
      client-side code this mod has: added a network channel
      (`InStormPacket`) - server tracks per-player storm membership in
      `OnGameTick` and sends the packet only on transition
      (entering/leaving, not every tick), client pushes/pops an
      `AmbientModifier` keyed `theunknowing:storm-fog` in response.
      Color/density/brightness values are a first-pass guess. Builds
      clean, not yet tested live.

      **Attempted and abandoned: reusing Temporal Stability.** Tried
      piggybacking on the game's own system rather than a custom
      renderer - pinning
      `EntityBehaviorTemporalStabilityAffected.OwnStability` down for
      any player in a storm's chunk bounds, since that behavior already
      drives fog/glitch sounds/particles off that value for free. Ruled
      out `ModSystemDevastationEffects` as the reuse target along the
      way - its location/radius fields are singular, not a list, so it
      looks built for one global effect at a time and would likely
      break with more than one concurrent storm.
      Live testing surfaced two real problems before this got dropped:
      (1) a timing bug - the 10s reapply tick was far slower than the
      entity's own continuous drift-back-to-ambient, producing a
      sawtooth that was barely noticeable (fixed by moving to a ~1s
      tick, matching Caveshrooms' own decay-tick pattern) - and (2) the
      dealbreaker - pushing the value low enough to feel dramatic
      crossed real vanilla thresholds: unavoidable player damage below
      12% stability, and uncontrolled tier-4 bowtorn spawns below 5%,
      neither of which was ever part of the intended design, and not
      safely tunable around on a real server. (One positive finding
      from this detour worth keeping: temporarily swapping
      `EnemyEntityCodes` to a single visually-distinct entity
      (`game:locust-bronze`) as a diagnostic confirmed vanilla's own
      spawn conditions do *not* independently react to lowered ambient
      stability - nothing but locusts ever appeared.)

      **Current approach: our own particles + audio**, fully decoupled
      from Temporal Stability - all spawning/sound is our own
      responsibility via `IWorldAccessor.SpawnParticles`/`PlaySoundAt`
      directly, no coupling to any vanilla system that could hurt a
      player or spawn something we don't control. Referenced the
      "Spreading Devastation" mod
      (mods.vintagestory.at/show/mod/37072) for validation early on -
      it uses a "stormwall" fog effect at the boundary of its
      devastated areas for the same kind of ambiance, no source
      available to inspect but confirmed the general approach was
      proven; the first (light-grey, non-boundary-aware) version
      already confirmed live that the effect really is visible from
      outside the chunk, which carries over to this revision too.

      **First live playtest feedback (a small group demo):** strongly
      positive - the particles alone already got an "oh, that's cool,"
      and stepping inside the fog got an "oh, that's COOOOOL." Three
      follow-up requests came out of it, all now built, none yet
      confirmed live:
      - **Smooth fog fade.** The real fog (`AmbientModifier` pushed via
        `InStormPacket`) was snapping fully on/off on
        entering/leaving a storm - "flips like a switch" instead of
        the intended dread-creeping-in effect. Root cause: the
        modifier was being added to/removed from
        `IAmbientManager.CurrentModifiers` outright, so removal
        snapped the blended ambient straight back to the base outdoor
        values with nothing to interpolate through.
        `AmbientModifier` does have a `LerpSpeed` field ("Transition
        speed for interpolating between ambient states"), but its
        actual units/semantics live in the closed-source client
        assembly and couldn't be confirmed by reading `vsapi` alone,
        so rather than guess at an undocumented engine knob, the fix
        drives the fade ourselves: the modifier now stays registered
        under its key permanently (from mod load, not from first
        entering a storm), and only its `FogDensity`/`FogColor`/
        `FogBrightness` `.Weight` fields move, ramped once per client
        tick (`OnFogFadeTick`, a new ~20Hz `RegisterGameTickListener`)
        toward 1 (in storm) or 0 (out) over `FogFadeSeconds`
        real-time seconds (config, default 2.5, sent to the client
        per-transition via `InStormPacket` since the client has no
        config file of its own). Confirmed live via reflection on the
        installed game assemblies that `WeightedFloat.Weight` is a
        plain mutable public field, so ticking it every frame is
        safe; the fade *feel* itself (timing, whether the blend reads
        as smooth in practice) is not yet confirmed live.
      - **Clickable location link on the broadcast.** Storm start was
        previously only ever reported back to whichever admin ran
        `/unknowing-storm`, via the command's own `TextCommandResult`
        - nobody else on the server was told anything. `StartStorm`
        now also calls `sapi.BroadcastMessageToAllGroups` so every
        online player sees it, with the storm's center chunk as a
        clickable VTML link. Confirmed via the wiki's VTML page that
        there's no `worldmap://`-style protocol to open the map at a
        coordinate - closest available is `command:///...`, which
        types and runs a command outright. Used that to run
        `/waypoint addati` directly, so one click drops a pinned
        waypoint right on the storm's location - same practical
        outcome as "open the map so people can mark it," without a
        protocol that doesn't exist. Icon/color
        (`spiral`/`#B4145A`) are a first-pass guess, not yet seen
        live.
      - **Giant smoke marker ("beacon of inky blackness").** Went
        through two different implementations before landing on one
        that actually works:

        *Attempt 1: solid mesh entity.* A `theunknowing:stormsmoke`
        entity with a custom shape - first a 32x32x32 translucent
        cube, later a 16-wide/256-tall column - spawned once per
        storm and tracked via a (since-removed)
        `UnknowingStorm.SmokeEntityId` field. Two real bugs surfaced
        and got fixed along the way (wrong `class` value throwing
        `Don't know how to instantiate entity of type 'Entity'` at
        spawn time - fixed to `EntityAgent`; and that exception, at
        the time, aborting the rest of `StartStorm` after the claim
        was already deleted but before the storm was ever persisted -
        fixed by reordering + try/catch), and a missing
        `ShapeElement.RenderPass` meant the mesh rendered fully
        opaque regardless of the texture's alpha channel. But even
        with all of that fixed, live testing (twice, with
        screenshots) showed the fundamental problem: a continuous
        alpha-blended surface can't produce real gaps no matter how
        low its alpha goes - it read as a flat tinted wall, not
        smoke, and its geometry was clipping nearby particles outright
        rather than blending with them (visibly cutting off particle
        sprites in the second screenshot). That's an optical
        limitation of "one big translucent quad," not a tuning
        problem - discussed directly, decided to drop the mesh
        approach entirely rather than keep tuning around a ceiling.

        *Attempt 2 (current): particle column.* Removed the entity
        type, shape, texture, and `SmokeEntityId` field entirely.
        `UnknowingStormManager.SpawnSmokeColumn`, called from the
        existing `OnFogTick`, spawns a tall dense burst of the same
        void-black particles as `SpawnFogParticles` (color
        `15,12,20`), concentrated in a narrow column at the storm's
        center rather than spread across every chunk column.
        Particles have real gaps between them, so this both keeps the
        landmark visible from a distance and lets other particles/
        scenery show through, unlike the mesh. Reuses a particle
        system already confirmed live rather than new rendering
        surface area.

        Liked live, with two follow-up requests: the boundary
        "stormwall" (`SpawnFogParticles`, `isBoundary` columns) reads
        too low/knee-high, and the beacon column should visibly grow
        over the storm's lifetime rather than stay a fixed size -
        "representing the unknowing's strength in the realm." Both
        built, neither yet confirmed live:
        - Boundary columns now spawn their falling fog particles from
          up to `FogWallHeight` (config, default 80 blocks) instead of
          the interior's fixed +20, so the edge reads as a proper
          towering wall.
        - `SmokeColumnRadiusMin`/`SmokeColumnRadiusMax` (config,
          default 4/16 blocks) replace the old fixed `SmokeColumnRadius`
          - the column's horizontal radius now lerps from Min to Max
          over `elapsed / DurationHours` (clamped to [0,1], computed in
          `SpawnSmokeColumn` from `storm.StartTotalHours`), and particle
          count scales proportionally with the current radius so
          density per unit area stays roughly constant as it widens
          rather than thinning out. Height (`SmokeColumnHeight`)
          deliberately does not grow - only width, per the request.

      **Reversed: ground fog and the smoke column beacon both removed.**
      All prior testing had been against empty land claims with nothing
      else nearby, where every layer reads clearly against open sky/flat
      terrain. Called out that a real target is a lived-in settlement -
      buildings, foliage, other players' builds all around - and the
      ground-level haze plus a thin distant smoke column will both get
      lost in that visual clutter rather than being the obvious "this
      base is being consumed" signal they were meant to be. The boundary
      "stormwall" (`SpawnFogParticles`, `isBoundary` columns, up to
      `FogWallHeight`) stays as the sole atmospheric signal - it's tall
      enough to read above rooftops regardless of what's built nearby,
      which neither of the removed layers could guarantee. Removed:
      `UnknowingStormManager.SpawnGroundFog`/`SpawnSmokeColumn` and their
      calls from `OnFogTick`, and the now-unused config fields
      (`GroundFogParticlesPerColumn`, `GroundFogLifeLengthSeconds`,
      `SmokeColumnRadiusMin`/`Max`, `SmokeColumnHeight`,
      `SmokeColumnParticles`). Interior fog (non-boundary columns) and
      embers (`SpawnEmberParticles`) both stay - interior fog is part of
      the same `SpawnFogParticles` call as the wall, and embers are a
      separate, cheap, close-range "danger" cue rather than a
      landmark/haze layer, so neither has the same "gets lost from a
      distance" problem the removed two did.
- [x] Real crash found and fixed via a live server crash report right
      after the above removal: `System.IndexOutOfRangeException` in (the
      now-deleted) `SpawnSmokeColumn`, from `storm.ChunkColumns[Count /
      2]` on an empty list. Root cause was two bugs stacked together:
      1. `ClaimChunkMath.GetCoveredChunkColumns` built its chunk range
         from `area.Start.X`/`area.End.X` (raw `Cuboidi.X1`/`X2`), not
         `area.MinX`/`MaxX`. `Cuboidi` doesn't guarantee `X1 <= X2` - a
         claim area dragged from its "high" corner to its "low" corner
         has `Start.X > End.X`, which made the min/max chunk loop run
         zero iterations. Confirmed this is what actually happened: the
         crashing storm was targeted at a real, valid, non-empty claim
         (self-tested), which should be impossible to produce a
         zero-column result from - fixed by using `.MinX`/`.MaxX`/etc.
         throughout.
      2. `StartStorm` never validated `columns.Count > 0` before
         removing the target's claim(s) and persisting the storm (only
         `claims.Count == 0` was checked) - so a zero-column result
         wasn't just miscomputed, it got permanently saved to disk and
         crashed every later tick that indexed into it. Added the
         missing check, before any destructive side effect, as defense
         in depth even with bug 1 fixed - and confirmed no other
         `ChunkColumns` indexing site was unguarded
         (`OnAmbientAudioTick`/`TrySpawnOne` already skip empty-column
         storms). See `GOTCHAS.md` in vs-source for the general
         `Cuboidi.Start`/`.End` write-up.

## 0.4 — regen

- [ ] Wire up the `/wgen regen` call: compute the minimum enclosing
      radius around the claim's chunk set, construct a `Caller` with
      `Pos` at the center and `Player = null`, call it via
      `sapi.ChatCommands.ExecuteUnparsed`.
- [ ] Full lifecycle test: summon on a real claim, let it run, confirm
      the claim area is gone and the land is regenerated clean.

## 0.5 — polish & compatibility

- [ ] ConfigLib support (mods.vintagestory.at/show/mod/9551) - already
      installed as a dependency for other mods on the server, but
      `TheUnknowing.json` isn't hooked into it yet.
- [ ] Faster/separate tick for `UpdatePlayerStormMembership`. Currently
      runs off the shared 10s `OnGameTick` (same tick as Active ->
      Collapsing and containment), so `InStormPacket` - and therefore
      the client-side fog fade - can lag up to ~10s behind a player
      actually crossing the storm boundary in either direction: leave
      right after a tick fires and you stay "blinded" for up to 10s
      after you're already clear; enter right after a tick fires and
      you go up to 10s without fog before it catches up. Explore
      splitting membership tracking onto its own faster tick (candidate
      interval: something closer to `FogParticleIntervalSeconds`, or
      faster) so the fade starts promptly on the real crossing instead
      of on the next 10s boundary-check.
- [ ] Cloud entity spawn animation.
- [ ] Cloud entity expansion animation (grows over time, similar to
      what the old particle-based beacon did via
      `SmokeColumnRadiusMin`/`Max` before it was removed - see 0.3).
- [ ] Darker cloud entity (texture alpha tune - currently ~90/255) - OR
      as an alternative worth trying first, a starfield texture instead
      of solid void-black: white star-dots on a dark background, same
      shape/renderPass, just a new PNG. Noted live that the "tear in
      reality" read kind of already works with how the cloud currently
      renders, so this might get further toward that than just going
      darker would. A literal see-through hole to the real sky behind
      it (rather than a starfield pattern painted on the surface) was
      also discussed and deliberately ruled out - would need render-to-
      texture/portal tricks the shape+texture system doesn't expose,
      same "reads as a picture, not a real gap" lesson already learned
      from the smoke-column mesh attempt (0.3/reversal) - not worth the
      engine-level effort it'd take.
- [ ] More erratic ember particle motion.
- [ ] New ambient audio - currently still reusing the game's own
      `game:sounds/effect/rift.ogg`, never replaced with something
      custom.
- [ ] Test underground claim (does terrain-height-based positioning
      for the cloud/particles/spawns still make sense if the claim
      itself is underground?).
- [ ] Test multiple claims on one player. `ClaimChunkMath.
      GetCoveredChunkColumns` already unions chunk columns across every
      claim a player owns, but this has never actually been confirmed
      live against a player with more than one claim.

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
