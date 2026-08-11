# Caveshrooms

A Vintage Story content mod. A mushroom that fruits from patches of
"unstable substrate" — a slimy mat that's a visible concentration of
ambient temporal instability, growing in dark cave interiors. The
substrate is the real organism; the mushroom is just its fruiting body.
It's forageable and relocatable: dig up the substrate, carry it home,
replant it, and grow your own patch.

This README is the source of truth for the mod's design and current
state. See `ROADMAP.md` for what's left to build, organized by milestone.

## Current status

Playable, renders correctly in-game, all three growth states have working
geometry and drops. Growth cycle confirmed working: `empty → flowering →
ripe` advances correctly on its own over time (verified in creative with
`/time add 8 days` twice). **Not yet confirmed**: whether natural worldgen
spawns work post-fixes — see `ROADMAP.md`.

## Folder structure

```
Caveshrooms/
├── modinfo.json
├── README.md                  # this file — design + current state
├── ROADMAP.md                 # what's left, by milestone (1.0 / 1.5 / 2.0)
└── assets/caveshrooms/
    ├── blocktypes/plant/caveshroom.json       # the block: 3 growth states
    ├── itemtypes/caveshroom-item.json         # the harvested/edible item
    ├── worldgen/blockpatches/caveshrooms.json # underground cave spawn rules
    ├── lang/en.json                           # display names
    ├── shapes/block/plant/caveshroom-{empty,flowering,ripe}.json
    ├── shapes/item/caveshroom-item.json
    └── textures/{block/plant,item}/*.png      # procedurally generated, see below
```

## Dev workflow — read before testing changes

**Mod must be loaded as a real `.zip` in the `Mods` folder, not a live
directory junction/symlink.** A junction was the original setup for fast
iteration, and it works fine for JSON content (blocktypes, lang — verified
by controlled tests: editing `lightHsvByType`, `selectionbox`, and lang
strings all took effect immediately on client restart, no repackaging
needed). But the texture atlas silently fails to build for a plain-folder
mod — blocks render as flat white/untextured no matter what the JSON or
PNG files say, with zero errors in the client log. Packaging the exact
same files as a `.zip` fixes it immediately. Root cause inside the engine
isn't confirmed (no decompiled source), but the symptom and fix are solid
and were reproduced deliberately.

Rezip after **any** change before testing:

```powershell
Compress-Archive -Path "<repo>\Caveshrooms\*" -DestinationPath "$env:APPDATA\VintagestoryData\Mods\Caveshrooms.zip" -Force
```

Bumping `modinfo.json`'s `version` is not required for reloads to work
(also confirmed), but doesn't hurt.

Textures are procedurally generated with Pillow (per-pixel jitter +
gill-line overlay), not hand-drawn — regenerate/tweak via a similar script
rather than expecting hand-authored art.

## Design decisions and why

- **Modid / asset domain:** `caveshrooms` (plural, matches the mod name).
  All block/item codes live under this domain, e.g.
  `caveshrooms:caveshroom-ripe`.

- **Growth stages:** `variantgroups` with `state: empty, flowering, ripe`,
  cycled over time by `entityClass: "BerryBush"` — repurposed from
  vanilla's berry bushes, which is the only class found anywhere in the
  shipped game install that does timed in-place regrowth. Vanilla loose
  mushrooms, by contrast, don't regrow at all (one-shot forage,
  replenished only by worldgen generating new unexplored chunks) — so
  they were never a viable model for this. The three state *names*
  (`empty`/`flowering`/`ripe`) are a deliberate match to the exact strings
  vanilla's berry bush JSON uses, on the bet that `BerryBush`'s entity
  logic keys off those literal strings, and the bet paid off — confirmed
  working in-game: the cycle advances `empty → flowering → ripe` on its
  own over time with no class-cast error or other issue.

- **`class: "BlockRequireSolidGround"`**, not `BlockPlant`. `BlockPlant`
  (the original choice) requires fertile soil beneath the block — it's
  the generic wildflower/grass class — which silently rejected placement
  on bare cave stone, both for manual placement and for the worldgen
  blockpatch itself (same ground-validity check), and was the actual
  reason zero caveshrooms were found naturally at one point.
  `BlockRequireSolidGround` is the class vanilla uses for things that just
  need *some* solid surface regardless of fertility (desert barrel cactus
  on sand/rock; also carcasses, driftwood, loose sticks).

- **Harvest & tooling, per state:**
  - `empty` (Unstable Substrate): plain block breaking, not a right-click
    `Harvestable` interaction — this is "digging up dirt," not "picking a
    mushroom." Drops itself as a placeable block so it can be relocated.
    Not resource-limited by design. `blockmaterialByType: {"*-empty":
    "Soil"}` makes it shovel-fast to break, via the same
    `miningspeedbytype.soil` mechanism vanilla shovels use against dirt.
  - `flowering` (Caveshroom Saddle): also plain breaking, also drops the
    substrate block (disturbing an unripe fruiting body doesn't destroy
    the patch).
  - `ripe` (Caveshroom): right-click `Harvestable`, gives the edible item
    and exchanges back to `empty` to continue the cycle.
  - **Confirmed not possible via JSON:** making the `ripe` harvest faster
    when holding a knife. Checked every vanilla `Harvestable` usage and
    the relevant API docs — the behavior only supports `harvestTime`,
    `harvestedStack`, `harvestedBlockCode`, `exchangeBlock`,
    `convertFrom`, `transientProps`. No per-tool speed multiplier exists.
    (Vanilla's knife-related `IHarvestable` is an unrelated C# interface
    for entity/carcass harvesting.) Would need a custom C# `BlockBehavior`
    — a real code mod, not a content mod. Not attempted.

- **Item has no explicit `class`** (defaults to plain `Item`), matching
  vanilla `vegetable.json` — `"class": "ItemMushroom"` from the original
  scaffold was removed because that class doesn't exist anywhere in the
  shipped assets. Perishable (`transitionablePropsPerType`, `Perish` →
  `game:rot`), matching vanilla mushroom/vegetable food items. Kept as a
  genuinely separate drop item (vegetable-style) rather than the block
  dropping itself (how vanilla mushrooms work) — that's what lets the
  block stay in its growth-cycle states rather than being consumed.

- **Worldgen placement:** `placement: "Underground"`, `maxY: 0.35`
  (roughly below sea level, so it doesn't show up right at surface cave
  entrances). `chance`/`quantity` are unvalidated starting guesses.

- **Light/glow:** currently switched off entirely (`lightHsvByType`
  removed) — was distracting during testing. Reintroducing it with tiered
  brightness per growth state is on the roadmap. Two facts worth reusing
  rather than rediscovering when that happens: `lightHsv` is genuinely a
  0–255 scale using the standard `ColorUtil.HsvToRgb` algorithm (confirmed
  against the real API doc comments, not guessed) — hue `85` is true
  green on that scale. And there's a real brightness ceiling: community
  reports describe crashes in other mods at `v ≥ ~30`; vanilla's own
  light sources all stay well under that (torch `14`, oil lamp `11`, gas
  lamp `22`, glow worms `7`).

## Reference

- Vintage Story wiki: [Simple World Generation tutorial](https://wiki.vintagestory.at/Modding:Content_Tutorial_Simple_Worldgen)
- Vanilla conventions were cross-checked directly against the local game
  install (`assets/survival/`, not `assets/game/` — that's where vanilla
  content actually lives): `blocktypes/plant/mushroom.json`,
  `blocktypes/creature/glowworms.json`,
  `blocktypes/legacy/bigberrybush.json` + `smallberrybush.json` (the
  `Harvestable`/`BerryBush` model this mod's regrowth is built on),
  `blocktypes/plant/barrelcactus.json` (the `BlockRequireSolidGround`
  model), `blocktypes/plant/saguarocactus.json`,
  `itemtypes/food/vegetable.json`.
- Full blow-by-blow investigation history (screenshots, pixel samples,
  dead-end theories, exact debugging steps) lives in `NOTES.local.md` —
  gitignored, not committed, not required reading. This file and
  `ROADMAP.md` are the only documents meant to stay current.
