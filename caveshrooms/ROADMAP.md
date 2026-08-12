# Caveshrooms — Roadmap

Task list organized by milestone. Checked items are confirmed done;
unchecked items still need work. See `README.md` for how the mod works
and where to make tweaks, and `NOTES.local.md` for full design-decision
reasoning — this file is just the "what's left."

## 1.0 — being tested

- [x] Regrowth cycle (`empty → flowering → ripe`) confirmed advancing
      over time, no replanting needed; natural worldgen spawns confirmed.
- [x] Correct drops/tooling for all three states — shovel-fast breaking,
      right-click harvest on ripe (2–3 quantity), breaking ripe drops
      both the mushroom and the substrate block.
- [x] Teal/cyan glow scaling with growth stage, color-matched to the
      vanilla Temporal Gear.
- [x] Mushroom is edible: poisonous and mildly psychedelic.
- [x] Eating drains the eater's temporal stability and makes them glow,
      cumulatively, decaying over in-game time — required going from a
      content mod to a real C# code mod (`CaveshroomsCode/`). Check
      current values in-game with `.temporalstatus`.
- [x] Chopping (knife/cleaver), cooking (firepit/oven, 3 bake states, raw
      or chopped in either order), and Temporal Pie support.
- [x] Alchemy mod compatibility — crafts into its basic potion base,
      fully optional, no hard dependency.
- [x] Full handbook entries, including a dedicated "Game Mechanic:
      Caveshrooms" guide page.

Per-feature implementation detail, dead ends, and verification steps for
all of the above are in `NOTES.local.md`.

## 1.1

- [ ] Caveshrooms can only grow in the dark.
- [ ] Caveshrooms grow faster when temporal stability is low or rift
      activity is high. Needs the growth logic to read player/world state
      at tick time — almost certainly not achievable through
      `entityClass: "BerryBush"` as-is; likely needs a custom growth
      behavior, i.e. real C# code, not JSON config.

## 1.2

- [ ] Caveshrooms can grow on walls and ceilings. Requires an
      orientation-aware attachment model (`BlockRequireSolidGround` only
      checks the block below, floor-only), an orientation variant axis
      multiplying against the existing growth states, placement logic to
      pick the right orientation, and shape/UV rework per orientation.
      Vanilla's tree-growing mushrooms (directional `-north` etc. variants)
      are the closest existing precedent worth studying first.
