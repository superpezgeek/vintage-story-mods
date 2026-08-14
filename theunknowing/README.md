# The Unknowing

A Vintage Story mod for Dream Realms RP. An admin-summoned storm of
forgetting — our version of The Nothing / the Smoke Monster — that
descends on an abandoned land claim, makes it lootable, and eventually
erases and regenerates the land underneath it. In the server's lore,
this is what happens when a player is forgotten: they quit, and The
Unknowing comes for what they left behind.

## Current status — early scaffold

- `/unknowing-storm <playerName>` exists and is admin-gated
  (`controlserver` privilege), targeting by the claim's
  `LastKnownOwnerName` so it works even though the player is offline.
- Right now it's a **targeting dry run only**: it resolves every claim
  that player owns, computes the chunk columns they cover, and reports
  back. It doesn't touch the claim or the world yet.
- Nothing else — claim suppression/removal, the storm itself (contained
  mob spawning, VFX), and the final `/wgen regen` cleanup — is built.
  See `ROADMAP.md`.

## Folder structure

```text
theunknowing/
├── modinfo.json
├── README.md / ROADMAP.md
├── scripts/
│   └── release.ps1                  # bump version, build, package, deploy
└── TheUnknowingCode/                 # C# source
    ├── TheUnknowingCode.csproj
    ├── TheUnknowingModSystem.cs      # registers /unknowing-storm
    └── ClaimChunkMath.cs             # claim areas -> covered chunk columns
```

No `assets/theunknowing/` yet — there's no content (blocks/items/etc.)
to ship, just code.

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

For anything meant to leave your machine (a real release), use
`scripts/release.ps1` instead — it packages a zip with forward-slash
paths, which matters the moment this mod ships any asset files (see
Caveshrooms' README for the exact backslash bug that command avoids).

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
and deploys it to the live `Mods` folder. Pass `-SkipDeploy` to skip that
last step.

## Mechanics worth understanding before changing

**Claim targeting.** `LandClaim` has no stable numeric ID in the API —
`/land list` only shows a per-owner index, meaningless for an admin
targeting someone else's claim. `/unknowing-storm` instead matches on
`LandClaim.LastKnownOwnerName`, the same offline-safe approach the
engine's own `/land adminfree <playerName>` uses. A player can own
multiple claims, and a single claim can have multiple disjoint `Areas`
(an L-shaped base) — `ClaimChunkMath.GetCoveredChunkColumns` unions
chunk columns across all of them rather than assuming one contiguous
cuboid.

**Chunk math.** Chunks are 32×32 blocks. Column index uses `>> 5`
(arithmetic right shift), not `/ 32` — integer division truncates
toward zero, which gives the wrong column for negative coordinates
(`-33 / 32 == -1`, but block `-33` is in column `-2`); the shift rounds
toward negative infinity instead, matching how the engine assigns
blocks to chunks.

**World regen (planned, de-risked but not wired up yet).** The plan is
to reuse the engine's own `/wgen regen <radius>` via
`sapi.ChatCommands.ExecuteUnparsed`, rather than hand-rolling chunk
deletion. Confirmed on a throwaway dedicated server that `/wgen regen`
runs fine from a bare console caller with no connected player — it
reads `Caller.Pos` rather than requiring `Caller.Player`, so a
mod-constructed `Caller` with `Pos` set to the claim's center and
`Player` left `null` should work the same way. `/wgen regen` takes a
radius around a point, not an arbitrary bounding box, so the actual
call will need the minimum enclosing radius around the claim's chunk
set rather than the exact chunk footprint.
