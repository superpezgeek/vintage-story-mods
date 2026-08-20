# The Unknowing — Roadmap

Task list organized by milestone. Checked items are confirmed done;
unchecked items still need work. See `README.md` for how the mod works
so far, and `NOTES.local.md` (gitignored, not part of the release) for
the full story behind any of these — bugs found, live-test results,
things tried and reversed.

## 0.1 — scaffold

- [x] Mod project builds and loads (`TheUnknowingModSystem`).
- [x] `/unknowing-storm <playerName>` resolves every claim owned by that
      (possibly offline) player and reports the chunk columns they
      cover.
- [x] Claim targeting and removal confirmed against a real claim.
- [x] Confirmed `/wgen regen <radius>` works from a bare console caller
      with no connected player.

## 0.2 — the storm actually does something

- [x] `UnknowingConfig` — admin-tunable via `ModConfig/TheUnknowing.json`,
      no rebuild required.
- [x] Claim suppression: remove the target claim(s) the moment the storm
      starts.
- [x] `UnknowingStormManager` tracks active storms and persists them
      across server restarts.
- [x] Containment: despawns any storm-owned entity found outside the
      storm's chunk bounds.
- [x] Mob spawning: periodic timed spawns within the chunk set while
      active, capped by `MaxConcurrentEnemies`.
- [x] `/unknowing-storm-clear` (debug/testing only) — despawns every
      entity every tracked storm owns and clears all storm state.
- [x] Two-phase escalation: `GatheringStrength -> EnteringReality ->
      Collapsing -> Done`, with `EnteringReality` scaling enemy
      cap/ember density and swapping in a stronger enemy pool.
- [x] `/unknowing-storm-kill-nearby [range]` — cleans up cloud entities
      orphaned from earlier bugs/test sessions.

## 0.3 — presentation

- [x] Per-player VFX/audio near/inside the storm: a boundary "stormwall"
      of falling void-black fog particles (tall enough to read above
      rooftops), interior fog, ember particles, real atmospheric fog via
      `IAmbientManager`, and ambient rift audio.
- [x] Smooth fog fade in/out on crossing the storm boundary instead of
      snapping on/off.
- [x] Clickable waypoint link on the storm broadcasts (superseded by the
      0.5 broadcast rewrite).
- [x] Ground fog and a growing smoke-column beacon were tried, then
      reversed — got lost in visual clutter around a real base. The
      boundary stormwall stayed as the sole atmospheric landmark.

## 0.4 — regen

- [x] Wired up the actual regen via `/wgen regenrange` once a storm
      reaches `Collapsing` — despawns every mob/cloud, regenerates the
      claimed chunks, then drops the storm from tracking.
- [x] Full lifecycle confirmed live: summon -> gathering -> entering
      reality -> collapsing -> regen, land confirmed clean afterward.
- [ ] `[StoryEvent]` log entry at the moment a storm's land is actually
      wiped, for admin/story tracking. Backlogged.

## 0.5 — polish & compatibility

- [ ] ConfigLib support (mods.vintagestory.at/show/mod/9551) - already
      installed as a dependency for other mods on the server, but
      `TheUnknowing.json` isn't hooked into it yet.
- [x] Faster/separate 1s tick for player storm membership, so the
      client-side fog fade can't lag up to 10s behind a player actually
      crossing the boundary.
- [x] Cloud entity spawn animation - a "beam from the sky" ignite effect
      (the column's height keyframes in from near-zero, anchored at the
      top so it reads as descending rather than growing up from the
      ground). Confirmed live after four rounds of live-tested fixes
      (invalid enum value, an engine null-ref on partially-set stretch
      keyframes, an off-by-one on quantityframes, and an animation
      easing quirk unrelated to spawn/anim call order) - see
      `NOTES.local.md` for the full sequence.
- [x] Cloud entity expansion animation - widens from 12 to 32 blocks
      (chunk width) the moment a storm enters `EnteringReality`, applied
      to every cloud the storm owns. Confirmed live. The starfield
      texture visibly stretches as it widens (static UV mapping, no
      re-tiling) - kept deliberately, reframed as in-theme spacetime
      distortion rather than fixed.
- [x] Starfield cloud texture — void-black base with scattered
      star-dots, aspect-matched per face so stars don't smear. Confirmed
      live: "looks great." (A WBOIT transparency artifact at box corners
      was investigated and left alone as an engine-level limitation, not
      a bug in our shape/texture.)
- [x] Entity display name ("The Unknowing") and hover-tooltip
      description, via a lang entry and a small generic
      `EntityBehaviorInfoText`.
- [x] Storm broadcast messages rewritten for a consistent escalating
      voice across all four lifecycle moments, each with its own
      clickable waypoint link. Waypoint coordinates went through
      several rounds of live-tested fixes (chunk centroid math, then a
      missing `=` absolute-position marker on the `/waypoint` command,
      then a snap-vs-seam precision issue) — confirmed live, including
      against an L-shaped claim (the concave case the centroid fallback
      exists for).
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
