# The Unknowing

[A Vintage Story mod](https://mods.vintagestory.at/theunknowing) for Dream Realms RP. An admin-summoned storm of
forgetting — our version of The Nothing / the Smoke Monster — that
descends on an abandoned land claim, makes it lootable, and eventually
erases and regenerates the land underneath it. In the server's lore,
this is what happens when a player is forgotten: they quit, and The
Unknowing comes for what they left behind.

## Current status — 1.0.0 (pre-release)

Full lifecycle is built and live-tested end to end:

1. **`/unknowing-storm <playerName>`** erases every land claim that
   player owns (works whether they're online or have quit) and starts a
   storm over the covered chunk columns.
2. **Gathering Strength** — enemies (drifters, by default) spawn
   periodically within the storm's chunk bounds, capped per storm.
   Storm-owned enemies that wander outside the bounds are despawned.
   A translucent storm-cloud landmark and purple ember particles mark
   every covered chunk from a distance; players inside or near the
   storm get a fading atmospheric fog and dedicated storm music.
3. **Entering Reality** — after a configurable duration, the storm
   escalates: enemy cap and ember density multiply, the enemy pool
   swaps to stronger variants, and every cloud landmark widens and
   tints purple.
4. **Collapsing** — after another duration, every storm-owned
   enemy/cloud is despawned and the claimed land is regenerated via
   `/wgen regenrange`. The storm is then dropped from tracking.

Every phase transition, plus the original summon, broadcasts a
clickable waypoint link to the whole server.

## Server commands

All commands require the `controlserver` privilege.

| Command | Purpose |
| --- | --- |
| `/unknowing-storm <playerName>` | Summons a real storm: erases every claim the named player owns and starts the full lifecycle above. |
| `/unknowing-storm-clear` | Debug/admin tool: force-ends every tracked storm, despawning everything it owns without regenerating any land. Use to abort a stuck or unwanted storm. |
| `/unknowing-storm-status` | Lists every tracked storm's target, phase, chunk column count, and spawned/cloud entity counts — useful for checking for stale or overlapping storms. |
| `/unknowing-demo <playerName>` | RP/event tool: spawns the storm cloud + ember particles on that online player's current chunk only. No claim touched, no phase progression, no enemy spawns — fog and music still trigger normally for anyone nearby. Runs until stopped. |
| `/unknowing-demo kill` | Ends the currently running demo (despawns its cloud, no regen). |

## Folder structure

```text
theunknowing/
├── modinfo.json
├── README.md / ROADMAP.md / NOTES.local.md   # NOTES.local.md is gitignored, not part of the release
├── scripts/
│   └── release.ps1                  # bump version, build, package, deploy
├── assets/theunknowing/
│   ├── config/configlib-patches.json  # ConfigLib GUI schema (optional dependency)
│   ├── entities/theunknowing.json     # storm cloud entity definition
│   ├── lang/en.json
│   ├── music/unknowing-storm.ogg      # looping in-storm music track
│   ├── shaders/theunknowing.fsh/.vsh  # scrolling-starfield cloud shader
│   ├── shapes/entity/theunknowing.json
│   └── textures/entity/               # cloud starfield textures
└── TheUnknowingCode/                 # C# source
    ├── TheUnknowingCode.csproj
    ├── TheUnknowingModSystem.cs      # commands, client-side fog/music, ConfigLib integration
    ├── UnknowingStormManager.cs      # storm lifecycle, spawning, containment, VFX ticks
    ├── UnknowingStorm.cs             # persisted storm/chunk-column data
    ├── UnknowingConfig.cs            # every tunable, loaded from ModConfig/TheUnknowing.json
    ├── ClaimChunkMath.cs             # claim areas -> covered chunk columns
    ├── InStormPacket.cs              # server -> client storm-membership packet
    ├── TheUnknowingRenderer.cs       # custom entity renderer (scrolling cloud texture)
    └── EntityBehaviorInfoText.cs     # generic entity hover-tooltip description
```

## Dev workflow

Fastest local iteration on `TheUnknowingCode` changes: build straight
into the live `Mods` folder as an unpacked folder mod (no zip needed —
unlike Caveshrooms, this mod has no texture atlas to worry about, so the
plain-folder-mod texture bug doesn't apply here):

```powershell
dotnet build TheUnknowingCode
New-Item -ItemType Directory -Force "$env:APPDATA\VintagestoryData\Mods\TheUnknowing" | Out-Null
Copy-Item modinfo.json,TheUnknowing.dll "$env:APPDATA\VintagestoryData\Mods\TheUnknowing\" -Force
```

That skips `assets/` entirely, so it's only useful for code-only
changes. For anything touching `assets/theunknowing/` (entity, shapes,
shaders, textures, config, lang, music), use `scripts/release.ps1`
instead — it packages a zip with forward-slash paths, which matters the
moment this mod ships asset files (see Caveshrooms' README for the
exact backslash bug that command avoids).

## Making a release

```powershell
.\scripts\release.ps1               # patch bump
.\scripts\release.ps1 -Minor
.\scripts\release.ps1 -Major
.\scripts\release.ps1 -Version 1.0.0

.\scripts\release.ps1 -Major -Rc    # start a candidate series
.\scripts\release.ps1 -Rc           # bump the counter
.\scripts\release.ps1 -Minor -Pre   # start a preview series
.\scripts\release.ps1 -Pre          # bump the counter
```

Same behavior as Caveshrooms' script (see its README for the full
`-Rc`/`-Pre` versioning explanation) — bumps `modinfo.json`, builds
`TheUnknowingCode` in Release, packages `releases/TheUnknowing-<version>.zip`,
and deploys it to the live `Mods` folder (clearing its stale unpack
cache too). Pass `-SkipDeploy` to skip that last step.

This mod stays on the `1.0.0-pre.x` series (`-Pre`) until told
otherwise — don't bump `-Rc`/`-Major`/`-Minor` unprompted.

## Configuration

Every tunable lives in `UnknowingConfig` and loads from
`VintagestoryData/ModConfig/TheUnknowing.json`, created with these
defaults on first run:

| Field | Default | Meaning |
| --- | --- | --- |
| `GatheringStrengthDurationMinutes` | 30 | Real-life minutes before advancing to EnteringReality. |
| `EnteringRealityDurationMinutes` | 30 | Real-life minutes before collapsing and regenerating the land. |
| `EnteringIntensityMultiplier` | 2 | Multiplies enemy cap and ember density during EnteringReality. |
| `EnemySpawnIntervalSeconds` | 30 | Seconds between enemy spawn attempts per storm. **Restart required to re-tune.** |
| `MaxConcurrentEnemies` | 6 | Per-storm cap on live enemies (before the EnteringReality multiplier). |
| `EnemyEntityCodes` | `game:drifter-normal, game:drifter-deep` | Enemy pool during GatheringStrength. |
| `EnteringEnemyEntityCodes` | `game:drifter-tainted, game:drifter-corrupt, game:drifter-nightmare, game:drifter-double-headed` | Enemy pool during EnteringReality (replaces the above entirely, not additive). |
| `FogParticleIntervalSeconds` | 2 | Seconds between ember particle bursts. **Restart required to re-tune.** |
| `EmberParticlesPerColumn` | 4 | Ember particles spawned per chunk column, per burst. |
| `FogFadeSeconds` | 2.5 | Seconds for the in-storm fog effect to fade in/out crossing a storm boundary. |
| `StormMembershipIntervalSeconds` | 1 | Seconds between per-player storm-boundary checks. **Restart required to re-tune.** |

Durations/caps/ranges apply live to any *new* storm as soon as the file
changes and the server restarts (a storm already in progress keeps the
values it was created with). The three fields marked above only ever
set a tick listener's interval once, at startup, regardless of when the
config is edited.

That file is always the one actually on disk — edit it directly
(server restart required to pick up changes) and the mod works fine
with nothing else installed.

If [ConfigLib](https://mods.vintagestory.at/configlib) is installed
(optional, not a hard dependency — see `modinfo.json`), the same
settings also show up in its in-game GUI, defined in
`assets/theunknowing/config/configlib-patches.json`. ConfigLib isn't a
second config to keep in sync by hand: at startup `TheUnknowing.json`'s
values are pushed into it, so the GUI reflects reality immediately
rather than showing its own schema defaults, and any edit made through
the GUI is written straight back to `TheUnknowing.json`. One file, two
ways to edit it.

**Don't hand-edit `TheUnknowing.json` while the server is running.** It's
only ever read once, at startup — nothing reloads it live. If ConfigLib
is installed and someone saves a change through its GUI at any point
afterward, that save overwrites the whole file with ConfigLib's
in-memory values, silently discarding any hand-edit made to the file in
the meantime. Edit the file, then restart; or use ConfigLib's GUI; don't
mix the two within the same server session.

## Mechanics worth understanding before changing

**Claim targeting.** `LandClaim` has no stable numeric ID in the API —
`/land list` only shows a per-owner index, meaningless for an admin
targeting someone else's claim. `/unknowing-storm` resolves the given
name via `api.PlayerData.GetPlayerDataByLastKnownName` (the persistent
per-player record, populated at first join and kept regardless of
online status) to get a `PlayerUID`, then matches claims on
`LandClaim.OwnedByPlayerUid` — `LandClaim.LastKnownOwnerName` isn't
populated at claim-creation time, so it can't be used directly. A
player can own multiple claims, and a single claim can have multiple
disjoint `Areas` (an L-shaped base) — `ClaimChunkMath.GetCoveredChunkColumns`
unions chunk columns (and each column's claimed Y span, for
underground-base spawning) across all of them rather than assuming one
contiguous cuboid.

**Chunk math.** Chunks are 32×32 blocks. Column index uses `>> 5`
(arithmetic right shift), not `/ 32` — integer division truncates
toward zero, which gives the wrong column for negative coordinates
(`-33 / 32 == -1`, but block `-33` is in column `-2`); the shift rounds
toward negative infinity instead, matching how the engine assigns
blocks to chunks.

**World regen.** `Collapsing` runs `/wgen regenrange <xs> <zs> <xe>
<ze>` (not `/wgen regen <radius>`) over the bounding box of each
connected cluster of the storm's chunk columns, via a synthetic
console-privileged `Caller` — `regenrange` takes explicit chunk
coordinates with no positional fallback, unlike `regen`, whose
chunk-centering math keys off `Caller.Player.Entity.Pos` and silently
regenerates around the world map center if `Player` is null (the case
for a storm targeting a most-likely-offline player). Clustering keeps
each call scoped to one real blob of claimed ground, rather than one
combined box that could sweep in unrelated terrain between two
far-apart claims owned by the same player.

**Client-side fog/music.** The server only ever tracks which chunk
columns are inside a storm; the client renders the actual fog
(`IAmbientManager`'s blended `AmbientModifier` stack) and plays the
storm music track. `InStormPacket` is sent only on an actual
entering/leaving transition, and both effects fade rather than snap.

See `NOTES.local.md` (gitignored, not part of the release) for the full
development history — bugs found, live-test results, and the reasoning
behind anything not covered above.
