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
- [x] Flowering: shovel-faster. Added `"*-flowering": "Soil"` to
      `blockmaterialByType`.
- [x] Ripe: tune harvest quantity to a strict 2–3 range. Changed both
      `harvestedStack` and the `*-ripe` `dropsByType` entry to
      `avg: 2.5, var: 0.5` (was `avg: 2, var: 1`, which allowed 1 or 4 at
      the edges).
- [x] Ripe: breaking (not harvesting) now drops **both** the mushroom
      item and the substrate block — added a second `dropsByType`
      entry for `*-ripe`.
- [x] Ripe: shovel-faster when broken. Added `"*-ripe": "Soil"` to
      `blockmaterialByType`.

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

### Items — Chopping, cooking, and pie

Standalone, pure-vanilla mechanics — no dependency on any other mod (the
user has Expanded Foods installed and asked for chop/cook support; that
mod's own chopped/cooked mushroom items turned out to be a deeply
variant-driven system tied to its own shared mushroom species registry,
so rather than hook into that, this mirrors the same underlying vanilla
mechanics — `knife-*`/`cleaver-*` grid recipes and `bakingProperties` —
entirely within the `caveshrooms` domain, same low-coupling philosophy as
the Alchemy compat above). **Confirmed working in-game**, full matrix:
chopping with both knife and cleaver, eating raw chopped mushroom,
cooking whole and chopped over a fire through all three bake states,
chopping an already-cooked/charred whole mushroom, baking a Temporal
Pie, and eating the pie — all tested and correct. A plain vegetable pie
was also baked alongside as a sanity check that nothing else broke.

- [x] `choppedtemporalmushroom-item` — chop a `caveshroom-item` with a
      knife or cleaver (`recipes/grid/choppedtemporalmushroom.json`).
      Custom shape (`shapes/item/food/choppedtemporalmushroom-item.json`)
      went through two failed attempts before landing: a first pass
      (small diced chunks) rendered as a shapeless "blob," and a second
      pass (four copies rotated around a shared pivot, wrapped in an
      extra zero-size rotation node) looked right as a dropped-item
      preview but flew apart when actually held in-hand — turned out
      nested zero-size wrapper elements don't survive tpHand hold
      animation correctly. Fixed by adapting Expanded Foods' own
      `greencrackedrussula` chopped-mushroom shape directly (exact
      geometry/rotation values, since no vanilla "chopped pile" shape
      exists to reference) — its four `stemN`/`capN` pieces carry their
      own rotation directly rather than through a wrapper, which is what
      actually works under hold-animation. `guiTransform`/
      `tpHandTransform`/`groundTransform` also reused verbatim from
      Expanded Foods' `greencrackedrussula` values for the same reason.
      Carries `lightHsv: [32, 7, 6]` (same glow as the raw whole
      mushroom) since it's still uncooked — cooked forms below have none.
- [x] `cookedtemporalmushroom-item` (whole, cooked) and
      `cookedchoppedtemporalmushroom-item` (chopped, cooked) — both
      `partbaked`/`perfect`/`charred`, baked via `bakingProperties` in a
      firepit/oven, following the exact vanilla `bread.json`/`dough.json`
      convention (checked directly against the local game install). Six
      new textures generated by recoloring the existing
      `caveshroom-jim(-gills)` PNGs progressively browner/darker per
      state. Health/psychedelic effects scale down 100% → 75% → 50% → 25%
      raw → partbaked → perfect → charred, matching the ratio Expanded
      Foods uses for its own hallucinogenic mushroom cooking (checked
      their source as a reference point, though nothing here depends on
      that mod being installed). No glow — cooking burns off the residual
      temporal instability.
- [x] Temporal Pie — restored pie support, retargeted to the **chopped**
      mushroom as filling (raw whole mushroom is no longer a valid pie
      filling; this was a deliberate choice on revisit, matching vanilla's
      own convention of chopped/prepared fillings rather than whole raw
      ingredients). `inPieProperties`/`nutritionPropsWhenInMeal` live on
      `choppedtemporalmushroom-item.json` now instead of
      `caveshroom-item.json`. New `fill-choppedtemporalmushroom.png`
      texture, recolored teal from the same vanilla source as before
      (`fill-mushroomblue.png`). Four `pie-single-caveshrooms:
      choppedtemporalmushroom-item-*` lang keys (`raw`/`partbaked`/
      `perfect`/`charred`) name it "Temporal Pie". `nutritionPropsWhenInMeal`
      carries the **full** `health: -1, psychedelic: 0.8` (unreduced) —
      checked against vanilla's own `mushroom.json`, which does the same
      for its hallucinogenic species (e.g. Laughing Jim keeps its full
      psychedelic value when eaten via meal/pie, not a diminished one).
      The cooking-reduces-potency scaling above only applies to the
      separate firepit/oven cooking path, not to pie filling — a
      deliberate distinction, not an inconsistency.

### Items — Alchemy mod compatibility

Reference: <https://mods.vintagestory.at/alchemy> (adds herb/potion
crafting; mushrooms can be ground into "basic potion base").

- [x] Make `caveshroom-item` usable to craft Alchemy's basic potion base.
      Confirmed there's no generic tag/attribute system — Alchemy's own
      compat for other mods (Mycodiversity, Material Needs: Flowers) works
      by shipping real grid recipes plus a jsonpatch that disables them
      when the target mod is absent (checked their actual unpacked mod
      files, e.g. `recipes/grid/ingredient/compatibility/
      mycodiversity-basicbase.json` + `patches/compatibility/
      mycodiversity-compat.json`). Mirrored that pattern from our own
      side instead of patching theirs:
  - `assets/caveshrooms/recipes/grid/potionbase-basic-caveshroom.json` —
    a new grid recipe: `caveshroom-item` + `game:flower-horsetail-free` +
    `alchemy:mortarpestle-*` → `alchemy:potionbase-basic`, matching the
    exact ingredient pattern Alchemy uses for every other compatible
    mushroom.
  - `assets/caveshrooms/patches/compatibility/alchemy-potionbase.json` —
    a jsonpatch that disables that recipe (`enabled: false`) specifically
    when the `alchemy` modid is *not* present, so Caveshrooms doesn't
    hard-depend on Alchemy and stays fully standalone without it.
  - Not added to `modinfo.json` `dependencies` — that field only supports
    hard/required dependencies (confirmed via `ModDependency` in the API
    doc, no optional-dependency concept exists there); the jsonpatch
    `dependsOn` condition is the actual mechanism for "only if present."
  - **Confirmed working in-game** — successfully crafted a basic potion
    base from a caveshroom.

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
