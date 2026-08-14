# Caveshrooms

A Vintage Story mod. A mushroom that fruits from patches of
"unstable substrate" — a slimy mat that's a visible concentration of
ambient temporal instability, growing in dark cave interiors. The
substrate is the real organism; the mushroom is just its fruiting body.
It's forageable and relocatable: dig up the substrate, carry it home,
replant it, and grow your own patch.

## Current status - being tested

- Grows on its own over time, no replanting — substrate cycles through
  `empty → flowering → ripe`, and harvesting a ripe mushroom resets it
  back to `empty` to start again.
- Spawns naturally underground. Glows a faint teal/cyan, brighter the
  riper it gets, color-matched to the game's own Temporal Gear.
- The mushroom is edible — and poisonous and mildly psychedelic, so eat
  with some caution.
- Eating it also drains the eater's temporal stability and makes them
  glow, cumulatively with repeated eating (fades slowly over in-game
  time if you stop).
- Can be chopped with a knife or cleaver, cooked over a fire (raw or
  chopped, in either order), or baked into a Temporal Pie. Cooking dulls
  the poison/psychedelic effect the further it's cooked; pie doesn't.
- Crafts into Alchemy's basic potion base if that mod is installed —
  fully optional, no hard dependency either way.
- Full handbook entries, including a dedicated "Game Mechanic:
  Caveshrooms" guide page.

## Folder structure

```text
Caveshrooms/
├── modinfo.json
├── README.md / ROADMAP.md
├── scripts/
│   ├── release.ps1                  # bump version, build, package, deploy
│   └── gen_cooked_textures.py       # regenerate cooked-state + pie textures
├── CaveshroomsCode/                 # C# source (config, stability drain, player glow, nutrition)
│   ├── CaveshroomsCode.csproj
│   ├── CaveshroomsConfig.cs         # ModConfig/Caveshrooms.json shape + defaults
│   ├── CaveshroomsModSystem.cs      # registers everything, loads config, glow decay tick, .temporalstatus command
│   ├── CaveshroomsAssetTuning.cs    # rewrites harvest/glow/worldgen JSON from config at AssetsLoaded
│   ├── CollectibleBehaviorTemporalEffect.cs  # applies the stability/glow effect when eaten
│   ├── ItemTemporalMushroom.cs      # config-driven nutrition + perishability
│   └── EntityPlayerGlowPatch.cs     # Harmony patch that renders the glow
├── Caveshrooms.dll                  # build output (gitignored, see Dev workflow)
└── assets/caveshrooms/
    ├── blocktypes/plant/caveshroom.json                     # the block: 3 growth states
    ├── itemtypes/caveshroom-item.json                       # raw whole mushroom
    ├── itemtypes/choppedtemporalmushroom-item.json          # raw chopped
    ├── itemtypes/cookedtemporalmushroom-item.json           # cooked whole (3 bake states)
    ├── itemtypes/cookedchoppedtemporalmushroom-item.json    # cooked chopped (3 bake states)
    ├── worldgen/blockpatches/caveshrooms.json                # underground spawn rules
    ├── lang/en.json                                          # display names + handbook text
    ├── config/handbook/gamemechanicinfo-caveshrooms.json     # "Game Mechanic" guide page
    ├── recipes/grid/potionbase-basic-caveshroom.json         # Alchemy recipe
    ├── recipes/grid/choppedtemporalmushroom.json             # knife/cleaver chopping
    ├── recipes/grid/cookedchoppedtemporalmushroom.json       # chop a cooked mushroom
    ├── patches/compatibility/alchemy-potionbase.json         # disables the recipe above
    │                                                          # if Alchemy isn't installed
    ├── shapes/block/plant/caveshroom-{empty,flowering,ripe}.json
    ├── shapes/item/caveshroom-item.json
    ├── shapes/item/food/choppedtemporalmushroom-item.json    # chopped-mushroom mesh
    └── textures/{block/plant,item,block/food/pie}/*.png      # procedurally generated
```

## Dev workflow

**The mod must be loaded as a real `.zip` in the `Mods` folder, not a live
directory junction/symlink.** JSON-only edits (blocktypes, lang, etc.)
reload fine from a plain folder, but the texture atlas silently fails to
build for a plain-folder mod — blocks render flat white/untextured with
no error logged. Packaging as a `.zip` fixes it immediately every time.

**Fast iteration on JSON/textures only** (no C# changes): just rezip and
drop it in the live `Mods` folder, no version bump needed for a reload to
pick it up:

```powershell
Compress-Archive -Path "<repo>\Caveshrooms\*" -DestinationPath "$env:APPDATA\VintagestoryData\Mods\Caveshrooms.zip" -Force
```

**Any C# change, or a real version bump**: use the release script instead
(see below) — it builds `CaveshroomsCode`, and a stale `Caveshrooms.dll`
from JSON-only rezipping won't pick up code changes on its own.

Textures are procedurally generated with Pillow (per-pixel jitter + a
recolor pass for cooked states), not hand-drawn —
`scripts/gen_cooked_textures.py` regenerates the cooked/pie textures from
their source PNGs; the original base textures (`caveshroom-jim.png` etc.)
were generated by a similar one-off script that wasn't kept, so tweaking
those specifically means writing a new one in the same spirit.

## Making a release

```powershell
.\scripts\release.ps1               # patch bump: 0.2.3 -> 0.2.4
.\scripts\release.ps1 -Minor        # minor bump: 0.2.3 -> 0.3.0
.\scripts\release.ps1 -Major        # major bump: 0.2.3 -> 1.0.0
.\scripts\release.ps1 -Version 1.0.0

.\scripts\release.ps1 -Major -Rc    # start a candidate series: 0.2.7 -> 1.0.0-rc.0
.\scripts\release.ps1 -Rc           # bump the counter: 1.0.0-rc.0 -> 1.0.0-rc.1
.\scripts\release.ps1 -Minor -Pre   # start a preview series: 1.0.0 -> 1.1.0-pre.0
.\scripts\release.ps1 -Pre          # bump the counter: 1.1.0-pre.0 -> 1.1.0-pre.1
```

Bumps `modinfo.json`'s version, builds `CaveshroomsCode` in Release
config, packages a clean zip (excludes `CaveshroomsCode/` source,
`scripts/`, and `NOTES.local.md`) to `releases/Caveshrooms-<version>.zip`,
and copies it into the live `Mods` folder. Pass `-SkipDeploy` to skip that
last step.

`-Rc`/`-Pre` tag the version using Vintage Story's own recognized
prerelease convention (`major.minor.revision-rc.N` /
`-pre.N` — see `GameVersion.SplitVersionString` in the decompiled
engine), not an arbitrary string — the engine's own version comparison
understands these two specifically and ranks them below a plain stable
version. Used alone (no `-Major`/`-Minor`), they increment the existing
counter if the current version already carries that tag, or start a
fresh `.0` on a patch-bumped base otherwise. `-Version` always overrides
everything, e.g. `-Version 1.0.0` to drop a prerelease tag once it's
final.

## Where the levers are

Nearly every balance/tuning number in the mod lives in a single runtime
config file, `ModConfig/Caveshrooms.json` in the game's data folder
(**not** inside the mod itself — untouched by mod updates, edits take
effect on next start, no rebuild or repo edit needed). It's created with
the defaults listed below the first time the mod starts if it doesn't
already exist. What's left in plain JSON asset files (needs a plain
rezip, no rebuild — see Dev workflow above) is structural: it defines
*how* something works, not a balance number to retune per-server.

### Config file (`ModConfig/Caveshrooms.json`)

This is what the file looks like with every default value, annotated below
— real JSON can't have comments, so the actual file won't have them, but
the structure and property names (including capitalization) are exactly
what you'll see when you open it. Edit any value and restart to apply it;
check current effective glow/stability any time in-game with
`.temporalstatus`.

```jsonc
{
  // Eating: stability lost and glow gained per point of a mushroom's "Psychedelic"
  // strength - scales automatically, so cooked/charred forms (lower Psychedelic)
  // already hit softer without a separate setting.
  "StabilityLossPerPsychedelic": 0.05,
  "GlowGainPerPsychedelic": 2.5,
  "MaxGlow": 20.0,                    // glow can't build up past this
  "GlowDecayPerInGameHour": 2.5,      // 2.5 = full glow fades in ~8 in-game hours of not eating
  "GlowHue": 32,                      // player glow color - also reused by GrowthGlow below
  "GlowSaturation": 6,

  "Nutrition": {
    // Raw mushroom's stats (Health is negative - it's poisonous)
    "RawSatiety": 80.0,
    "RawHealth": -1.0,
    "RawPsychedelic": 0.8,

    // Multipliers on the raw values above per cook stage - note Satiety goes UP as it
    // cooks while Health/Psychedelic go DOWN, so these aren't one shared percentage
    "PartBaked": { "SatietyMultiplier": 1.125, "HealthMultiplier": 0.75, "PsychedelicMultiplier": 0.75 },
    "Perfect":   { "SatietyMultiplier": 1.375, "HealthMultiplier": 0.5,  "PsychedelicMultiplier": 0.5 },
    "Charred":   { "SatietyMultiplier": 1.25,  "HealthMultiplier": 0.25, "PsychedelicMultiplier": 0.25 },

    "PieFillingSatiety": 150.0  // Temporal Pie always uses this, regardless of bake state
  },

  "Perish": {
    // Hours before spoiling starts / hours to fully spoil, raw vs. cooked vs. charred
    "RawFreshHours": 96.0,     "RawTransitionHours": 24.0,
    "CookedFreshHours": 432.0, "CookedTransitionHours": 72.0,
    "CharredFreshHours": 1512.0, "CharredTransitionHours": 120.0
  },

  "Harvest": {
    "QuantityAvg": 2.5,           // mushrooms received from harvesting/breaking a ripe caveshroom
    "QuantityVar": 0.5,
    "SubstrateDropChance": 0.5    // odds (0-1) that breaking the block also drops a
                                   // relocatable Unstable Substrate; right-click harvest
                                   // is unaffected - it always leaves substrate planted
  },

  "GrowthGlow": {
    // Block brightness at each growth stage (color comes from GlowHue/GlowSaturation above).
    // Usable Saturation range is 0-7; Value roughly 2-22.
    "Empty":     { "Saturation": 3, "Value": 3 },
    "Flowering": { "Saturation": 5, "Value": 7 },
    "Ripe":      { "Saturation": 7, "Value": 12 }
  },

  "Worldgen": {
    "Chance": 12.0,       // % chance per eligible spot underground
    "QuantityAvg": 2.0,   // caveshrooms per spawn
    "QuantityVar": 1.0,
    "MaxY": 0.35          // how close to the surface spawns can happen (0-1, lower = deeper only)
  }
}
```

Worldgen/Harvest/GrowthGlow are applied by rewriting the shipped asset
JSON in memory at startup (see Mechanics below) — if that ever fails for
any reason, the shipped JSON values are the fallback, and a warning is
logged.

### Growth & spawning (structural JSON)

- Growth cycle speed (`empty → flowering → ripe`): **not currently
  tunable** — timing is internal to vanilla's `BerryBush` entity class,
  which this block's `entityClass` repurposes as-is.
- Shipped worldgen defaults / fallback values: `worldgen/blockpatches/caveshrooms.json`.
- Shipped harvest-quantity / growth-glow defaults / fallback values:
  `blocktypes/plant/caveshroom.json` (`behaviorsByType."*-ripe"[0].properties.harvestedStack.quantity`,
  `dropsByType."*-ripe"`, `lightHsvByType`).

### Cooking (structural JSON)

- Bake timing/thresholds: `attributesByType.bakingProperties`
  (`temp`, `levelFrom`, `levelTo`) in each cooked itemtype — defines the
  raw→partbaked→perfect→charred progression itself, not a balance knob,
  so this stays plain JSON rather than moving to config.

### Recipes

- Chopping/cooking recipes: `recipes/grid/choppedtemporalmushroom.json`,
  `recipes/grid/cookedchoppedtemporalmushroom.json`.
- Alchemy compat recipe + its enable/disable gate:
  `recipes/grid/potionbase-basic-caveshroom.json` +
  `patches/compatibility/alchemy-potionbase.json`.

### Look & feel

- Held/inventory/ground transforms: `guiTransform` / `tpHandTransform` /
  `groundTransform` in each itemtype.
- Text: all display names, descriptions, and handbook copy live in
  `lang/en.json`.

## Mechanics worth understanding before changing

**Growth cycle.** The block reuses vanilla's `BerryBush` entity class
(via `entityClass: "BerryBush"`) rather than anything mushroom-specific —
vanilla loose mushrooms don't regrow at all, so berry bushes were the
only timed in-place-regrowth class available to repurpose. The three
`variantgroups` state names (`empty`/`flowering`/`ripe`) are a deliberate
match to the exact strings vanilla's own berry bush JSON uses, since
`BerryBush`'s logic keys off those literal strings — rename them and the
cycle breaks.

**Temporal stability drain & glow.** `CollectibleBehaviorTemporalEffect`
hooks `OnHeldInteractStop`, which — despite its own doc comment implying
otherwise — fires on *any* release, not just a completed eat; vanilla's
own `tryEatStop` only actually grants nutrition if held for
`secondsUsed >= 0.95f`, so the behavior checks that same threshold itself
before applying anything, or a quick tap-and-release would drain
stability/add glow without ever really eating the mushroom. Server-side
only, reads the eaten stack's own `Psychedelic` nutrition value to scale
both effects — this is why cooked forms automatically apply less without
needing separate config. Stability
is read/written through vanilla's own
`EntityBehaviorTemporalStabilityAffected.OwnStability` (the same
attribute the game's storm/rift system already uses). The glow is a
custom `caveshrooms:temporalGlow` float stored in the player's
`WatchedAttributes`, rendered by Harmony-patching `EntityPlayer.LightHsv`
(that property isn't virtual, so Harmony is the only way to hook it — the
same technique the Alchemy mod's own glow potion uses). A separate
server-side tick listener decays that float based on elapsed **in-game
calendar hours** (not real time), so `/time add` and calendar-speed
changes both affect it correctly.

**Chopping/cooking/pie.** Four items form a small state graph: raw whole
↔ raw chopped (via knife/cleaver), each of which can be cooked
(`partbaked → perfect → charred`) into the matching cooked item, and a
cooked item can also be chopped directly into the matching cooked-chopped
state. Raw chopped mushroom is the only valid pie filling — pie doesn't
reduce potency the way direct cooking does, matching how vanilla's own
hallucinogenic mushrooms keep their full effect in `nutritionPropsWhenInMeal`
regardless of the pie's own bake state.

**Config-driven nutrition & perishability.** All four edible itemtypes use
a custom item class, `ItemTemporalMushroom` (`"class": "Caveshrooms.ItemTemporalMushroom"`),
instead of the plain JSON-driven base `Item` — the itemtypes JSON no
longer sets `nutritionProps`/`transitionableProps` at all. It overrides
`GetNutritionProperties`/`GetTransitionableProperties` (the same virtual
hooks vanilla's own eating/perish/handbook code already calls) to compute
values from `CaveshroomsModSystem.Config` instead, determining cook stage
from the itemstack's own `state` variant. Pie-filling nutrition is a
special case — it isn't read through either virtual method (vanilla's pie
code reads `nutritionPropsWhenInMeal` directly off the item's `Attributes`
JSON), so `ItemTemporalMushroom.OnLoaded` patches that JSON in place at
load time instead, gated on the itemtype declaring `inPieProperties`.

**Config-driven worldgen & block tuning.** Harvest quantity, growth-stage
glow, and worldgen spawn tuning have no C# hook of their own to intercept
(they're read directly out of JSON by the engine's block/item loader and
world generator) — `CaveshroomsAssetTuning`, called from
`CaveshroomsModSystem.AssetsLoaded`, rewrites the raw bytes of
`blocktypes/plant/caveshroom.json` and `worldgen/blockpatches/caveshrooms.json`
in memory using `Config`, before either the engine's block/item loader or
its worldgen block-patch reader parse them. This mirrors the same
`IAsset.Data` + `IsPatched` mechanism the engine's own jsonpatches loader
uses, just applied imperatively instead of declaratively, and runs at the
documented `AssetsLoaded` window (after jsonpatches, before block/item
loading). Every rewrite is wrapped in a try/catch that logs a warning and
leaves the JSON untouched on failure, so a problem here can only lose
that one piece of config-driven tuning — it can't break asset loading for
the mod as a whole.

**Alchemy compatibility.** No hard dependency — a normal grid recipe is
shipped unconditionally, plus a `jsonpatches` file that disables it
specifically when the `alchemy` modid is *not* present (`dependsOn` with
`invert: true`). Mirrors the exact pattern Alchemy's own mod uses
internally for other mushroom mods it supports.

## Reference

- Vintage Story wiki: [Simple World Generation tutorial](https://wiki.vintagestory.at/Modding:Content_Tutorial_Simple_Worldgen)
- Alchemy mod source (compat recipe pattern, and the reference point for
  the C# effect system above): [github.com/Llama3013/vsmod-Alchemy](https://github.com/Llama3013/vsmod-Alchemy)
- `NOTES.local.md` has the full blow-by-blow: debugging sagas, dead ends,
  and every design decision with its reasoning. Gitignored, not required
  reading, but it's where the "why" lives if this file's short version
  isn't enough.
