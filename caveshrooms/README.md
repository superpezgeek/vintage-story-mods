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

Playable and feature-complete except for two effects that need a C# code
mod (see `ROADMAP.md`). Confirmed in-game: growth cycle (`empty →
flowering → ripe`, self-sustaining over time, no replanting needed),
natural underground worldgen spawns, correct drops/tooling for all three
states, teal/cyan glow scaling with growth stage (color-matched to the
game's own Temporal Gear), the item being edible (poisonous + mildly
psychedelic), full handbook entries including a dedicated "Game
Mechanic: Caveshrooms" guide page, crafting into Alchemy's basic potion
base when that mod is installed (fully optional, no hard dependency),
and chopping/cooking/Temporal Pie support (knife and cleaver chopping,
firepit/oven cooking through all three bake states in either order, and
baking/eating a Temporal Pie — see `ROADMAP.md`'s "Chopping, cooking,
and pie" section for the full design writeup).

## Folder structure

```
Caveshrooms/
├── modinfo.json
├── README.md                  # this file — design + current state
├── ROADMAP.md                 # what's left, by milestone (1.0 / 1.5 / 2.0)
└── assets/caveshrooms/
    ├── blocktypes/plant/caveshroom.json       # the block: 3 growth states
    ├── itemtypes/caveshroom-item.json         # the harvested/edible item
    ├── itemtypes/choppedtemporalmushroom-item.json          # chopped, raw
    ├── itemtypes/cookedtemporalmushroom-item.json           # cooked, whole
    ├── itemtypes/cookedchoppedtemporalmushroom-item.json    # cooked, chopped
    ├── worldgen/blockpatches/caveshrooms.json # underground cave spawn rules
    ├── lang/en.json                           # display names + handbook text
    ├── config/handbook/gamemechanicinfo-caveshrooms.json  # guide page
    ├── recipes/grid/potionbase-basic-caveshroom.json       # Alchemy recipe
    ├── recipes/grid/choppedtemporalmushroom.json            # knife/cleaver chopping
    ├── recipes/grid/cookedchoppedtemporalmushroom.json      # chop a cooked mushroom
    ├── patches/compatibility/alchemy-potionbase.json        # disables the
    │                                          # recipe above if Alchemy isn't installed
    ├── shapes/block/plant/caveshroom-{empty,flowering,ripe}.json
    ├── shapes/item/caveshroom-item.json
    ├── shapes/item/food/choppedtemporalmushroom-item.json   # diced chunks, new geometry
    └── textures/{block/plant,item,block/food/pie}/*.png     # procedurally generated, see below
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

Or, from a WSL/Linux terminal (e.g. VS Code's integrated terminal set to WSL) — same
result, verified to produce an identical archive structure:

```bash
cd "/mnt/c/Users/<you>/.../Caveshrooms" && zip -r -X -q /mnt/c/Users/<you>/AppData/Roaming/VintagestoryData/Mods/Caveshrooms.zip .
```

Requires `zip` installed in the WSL distro (`sudo apt install zip` if missing).

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
  shipped assets. Perishable (`transitionableProps`, `Perish` →
  `game:rot`), matching vanilla mushroom/vegetable food items. Kept as a
  genuinely separate drop item (vegetable-style) rather than the block
  dropping itself (how vanilla mushrooms work) — that's what lets the
  block stay in its growth-cycle states rather than being consumed.
  Note: `game:rot` is correct despite `rot.json` physically living under
  `assets/survival/` on disk — survival-mod content registers under the
  `game` domain at runtime, not `survival` (confirmed against a real
  third-party mod's own cross-references; see `NOTES.local.md`).

- **Poisonous and psychedelic**: `nutritionProps` set to `health: -1,
  psychedelic: 0.8` — the same fields vanilla uses for its own
  hallucinogenic mushrooms (Laughing Jim itself is `health: -10,
  psychedelic: 0.6`). A JSON-achievable stand-in for the original
  "lowers temporal stability on eat" idea, not the same effect — the
  real thing needs a C# code mod (see `ROADMAP.md`).

- **Worldgen placement:** `placement: "Underground"`, `maxY: 0.35`
  (roughly below sea level, so it doesn't show up right at surface cave
  entrances). `chance`/`quantity` are unvalidated starting guesses.

- **Light/glow:** `lightHsvByType` set to hue `32`, saturation `3/5/7`,
  value `3/7/12` across empty/flowering/ripe — deliberately matching the
  real Temporal Gear item's own light value (`"gear-temporal": [32, 5,
  2]`) exactly, since the mod's premise is that the substrate is a
  visible concentration of the same temporal instability. Confirmed
  in-game as a genuine teal/cyan glow, both underground in full dark and
  above ground. **Important, easy to get wrong**: `lightHsv` is *not*
  the 0–255 scale `ColorUtil.HsvToRgb`'s doc comment describes — that's
  a generic conversion helper, not proof of this specific field's range.
  Every real vanilla/mod usage caps saturation at `7` and hue never
  exceeds `54`; sending higher values (as an earlier pass here did)
  produces corrupted, wrong-looking colors rather than an error. Value
  (brightness) does behave as a small int, vanilla examples stay in the
  2–22 range (torch `14`, oil lamp `11`, gas lamp `22`, glow worms `7`).
  Full debugging history in `NOTES.local.md`.

- **Textures recolored to match:** `caveshroom-slime`/`saddle`/`jim`
  were all originally a green/tan/orange palette (a nod to the
  bioluminescent-mushroom idea the concept started from) and were later
  hue-shifted to the same teal-green family as the Temporal Gear glow,
  for visual consistency between lit and unlit appearance.

- **Alchemy mod compatibility:** no hard dependency — Alchemy isn't
  listed in `modinfo.json`'s `dependencies` (that field only supports
  hard requirements; there's no optional-dependency mechanism). Instead,
  a normal grid recipe (`caveshroom-item`, `game:flower-horsetail-free`,
  and `alchemy:mortarpestle-*` combine into `alchemy:potionbase-basic`)
  is shipped unconditionally, plus a `jsonpatches` file that disables it
  specifically when the `alchemy` modid is *not* present. Mirrors the
  exact pattern Alchemy's own mod uses internally to support other
  mushroom mods (checked their real source,
  github.com/Llama3013/vsmod-Alchemy). Confirmed working in-game.

- **Pie support was built, then deliberately removed** — caveshroom
  would have been the only mushroom species usable in a pie at all,
  and it didn't feel worth the added surface area given the Alchemy
  ingredient use case already answers "what's this good for." Easy to
  revisit: see the note in `ROADMAP.md` for exactly what was removed.

## Reference

- Vintage Story wiki: [Simple World Generation tutorial](https://wiki.vintagestory.at/Modding:Content_Tutorial_Simple_Worldgen)
- Vanilla conventions were cross-checked directly against the local game
  install: `blocktypes/plant/mushroom.json`,
  `blocktypes/creature/glowworms.json`,
  `blocktypes/legacy/bigberrybush.json` + `smallberrybush.json` (the
  `Harvestable`/`BerryBush` model this mod's regrowth is built on),
  `blocktypes/plant/barrelcactus.json` (the `BlockRequireSolidGround`
  model), `blocktypes/plant/saguarocactus.json`,
  `itemtypes/food/vegetable.json`. These files live on disk under
  `assets/survival/`, but the content they define registers under the
  **`game`** domain at runtime, not `survival` — worth remembering
  before assuming folder name tells you the right cross-domain prefix
  for a reference (see `NOTES.local.md` for how this one bit us).
- Alchemy mod source (for the potion base recipe/jsonpatch pattern):
  [github.com/Llama3013/vsmod-Alchemy](https://github.com/Llama3013/vsmod-Alchemy)
- Full blow-by-blow investigation history (screenshots, pixel samples,
  dead-end theories, exact debugging steps) lives in `NOTES.local.md` —
  gitignored, not committed, not required reading. This file and
  `ROADMAP.md` are the only documents meant to stay current.
