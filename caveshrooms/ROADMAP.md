# Caveshrooms — Roadmap

Task list organized by milestone. Checked items are confirmed done;
unchecked items still need work. See `README.md` for the "why" behind
design decisions — this file is just the "what's left."

## 1.0

### Foundational — verify before anything else

These are the two biggest unknowns in the whole mod. Nothing else here
matters if either of these doesn't actually work.

- [x] **Confirm the regrowth cycle actually advances over time.** Confirmed
      in creative: placed `empty` (Unstable Substrate) on the ground, ran
      `/time add 8 days`, saw it switch to `flowering`; ran `/time add 8
      days` again, saw it switch to `ripe`. `entityClass: "BerryBush"`
      works correctly on a non-`BlockBerryBush` block, no class-cast error.
      Not separately re-verified: harvesting a `ripe` caveshroom exchanging
      back to `empty` — lower risk, since that's the unmodified vanilla
      `exchangeBlock`/`harvestedBlockCode` mechanic, not the repurposed
      part.
- [x] **Confirm natural worldgen spawns work.** Confirmed: flew to a
      freshly generated chunk, went underground, found natural spawns.

### States — drops & tooling

- [x] Empty (Unstable Substrate): breaks (not right-click) to drop itself;
      shovel-faster via `blockmaterialByType: {"*-empty": "Soil"}`.
- [x] Flowering: breaking drops substrate.
- [ ] Flowering: shovel-faster. Needs `blockmaterialByType` entry (currently
      only `*-empty` is set to `"Soil"`; flowering still falls back to
      `"Plant"`, so no shovel bonus).
- [ ] Ripe: tune harvest quantity to a strict 2–3 range (currently
      `avg: 2, var: 1`, which allows 1 or 4 at the edges).
- [ ] Ripe: breaking (not harvesting) should drop **both** the mushrooms
      and the substrate block. Currently `dropsByType` for `*-ripe` only
      has the mushroom item.
- [ ] Ripe: shovel-faster when broken. Same `blockmaterialByType` gap as
      flowering.

### Effects — glow

Recolored in v0.1.13 to match the vanilla Temporal Gear's own light
value exactly (`hue: 32, sat: ≤7`, the real 0-63/0-7 scale `lightHsv`
actually uses — see `NOTES.local.md` for the debugging saga), brightness
scaling with growth stage:

- [x] Substrate: faint glow (`v: 3`).
- [x] Flowering: slightly stronger glow (`v: 7`).
- [x] Ripe: stronger glow, "almost torch level" (`v: 12`, vs. vanilla
      torch's `v: 14`).

Confirmed working and correctly teal/cyan-colored — verified underground
at a natural spawn in full dark and above ground surrounded by chalk
stone. Textures (`caveshroom-slime`/`saddle`/`jim`) were also recolored
in this pass to the same temporal-gear teal-green family so the block's
lit and unlit appearance match.

### Items — Caveshroom effects

- [x] Caveshroom item is poisonous and psychedelic — `nutritionProps`
      set to `health: -1, psychedelic: 0.8`, the same JSON fields vanilla
      uses for its own hallucinogenic mushrooms (e.g. Laughing Jim itself
      is `health: -10, psychedelic: 0.6`). A plain JSON-achievable stand-in
      for the original "lowers temporal stability" idea, not the same
      effect — see below.

Two effects from the original spec are confirmed to require a real code
mod — no JSON-only path exists for either. Checked: no vanilla food item
modifies temporal stability via JSON anywhere; `VintagestoryAPI.xml` (the
public modding API doc) has zero references to "Stability" at all,
meaning the mechanic is internal to `VSSurvivalMod.dll`, not exposed as
data-driven config; and player light emission isn't one of the fixed
keys the engine's generic `statModifiers`/`statModifiersByType` system
supports (`walkSpeed`, `hungerrate`, `healingeffectivness`,
`rangedWeaponsAcc`/`Speed`, etc. — a closed set, not extensible via JSON
to new stats). Path forward: flip `modinfo.json` from `"type": "content"`
to `"type": "code"` and add a compiled C# behavior — per `EnumModType`'s
own doc comment, `Code` is a strict superset of `Content` (can still do
everything the JSON/assets side already does), so this doesn't require
restructuring anything we've already built.

- [ ] Eating a Caveshroom specifically lowers the eating player's
      temporal stability (distinct from generic health damage).
      **Needs C# code mod** (see above).
- [ ] Eating a Caveshroom makes the player glow, cumulatively with
      repeated eating. **Needs C# code mod** (see above).
- [ ] A pie containing caveshroom filling glows. **Needs C# code mod**:
      `pie.json` is a single blocktype where fillings are stored as
      freeform block-entity data (`ucontents`), not a variant — there's
      no `lightHsvByType` key to hang a filling-specific glow off of.
      The hook that would make this possible:
      `CollectibleObject.GetLightHsv(blockAccessor, pos, stack)`, which
      exists precisely for computed-at-render-time light values — read
      the pie's `ucontents` off its block entity, check for a caveshroom
      filling, return HSV accordingly.

### Items — Alchemy mod compatibility

Reference: <https://mods.vintagestory.at/alchemy> (adds herb/potion
crafting; mushrooms can be ground into "basic potion base").

- [ ] Make `caveshroom-item` usable as an Alchemy ingredient.
      **Not yet investigated in depth** — initial research found:
  - Alchemy ships dedicated compatibility patches for specific mods
    (e.g. "Mycodiversity", "Material Needs: Flowers") rather than a
    generic tag/attribute any mushroom can opt into. No documented
    plugin/tagging architecture for third-party items.
  - Likely path: **we** ship a `jsonpatches` file inside Caveshrooms that
    targets Alchemy's own recipe/ingredient JSON (standard VS cross-mod
    patch mechanism, via an optional/soft dependency on the `alchemy`
    modid) — same effect as Alchemy's own Mycodiversity compat, just
    authored on our side instead of theirs.
  - Still need: the exact JSON file/path inside Alchemy that lists valid
    "basic potion base" ingredients, so we know what to patch. Not yet
    located — requires pulling the actual Alchemy mod files (from
    `github.com/Llama3013/vsmod-Alchemy` or the installed mod zip) and
    reading the real recipe JSON, not just the mod page description.
  - Fallback if no clean patch point exists: reach out to the Alchemy
    author for a native compat entry, same as Mycodiversity got.

## 1.5

- [ ] Caveshrooms can only grow in the dark.
- [ ] Caveshrooms grow faster when temporal stability is low or rift
      activity is high. Needs the growth logic to read player/world state
      at tick time — almost certainly not achievable through
      `entityClass: "BerryBush"` as-is; likely needs a custom growth
      behavior, i.e. real C# code, not JSON config.

## 2.0

- [ ] Caveshrooms can grow on walls and ceilings. Requires an
      orientation-aware attachment model (`BlockRequireSolidGround` only
      checks the block below, floor-only), an orientation variant axis
      multiplying against the existing growth states, placement logic to
      pick the right orientation, and shape/UV rework per orientation.
      Vanilla's tree-growing mushrooms (directional `-north` etc. variants)
      are the closest existing precedent worth studying first.
