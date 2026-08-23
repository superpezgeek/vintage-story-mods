# Vintage Story Mods

A collection of [Vintage Story](https://www.vintagestory.at/) mods, developed and versioned together in this repo. Each mod lives in its own top-level folder with its own `README.md` (design + current state), `ROADMAP.md` (what's left, by milestone), and `modinfo.json`.

## Mods

| Mod | What it does | Status |
| --- | --- | --- |
| [Caveshrooms](caveshrooms/) | A mushroom that fruits from patches of temporally unstable substrate deep in caves. Grows on its own over time, glows, and eating it drains your temporal stability while making you glow — cumulatively. | Release candidate, in multiplayer testing |
| [The Unknowing](theunknowing/) | Admin-summoned storm of forgetting that consumes an abandoned land claim, then regenerates the land beneath it. | Early scaffold — claim targeting only, no storm mechanics yet |

## Patterns worth reusing for the next one

Established while building Caveshrooms — not enforced anywhere, just a
sensible starting point rather than reinventing each one from scratch:

- **Folder layout**: `<ModName>/modinfo.json` + `assets/<modid>/...` for
  content, `<ModName>/<ModName>Code/` as its own C# project for anything
  that needs real code (see Caveshrooms' `CaveshroomsCode/` for the
  `.csproj` setup — references `VintagestoryAPI`/`VintagestoryLib`/
  `0Harmony` by `HintPath` into the local game install, builds straight
  out to the mod root).
- **`scripts/release.ps1`** per mod: bumps `modinfo.json`'s version
  (patch/minor/major, or an `-rc.N`/`-pre.N` prerelease tag using Vintage
  Story's own recognized version-tag convention), builds any C# project
  in Release, packages a clean distributable zip, and deploys it to the
  local `Mods` folder for testing. Copy Caveshrooms' version as a
  starting point.
- **`.gitignore`** each mod's own build output (compiled DLL/pdb/
  `obj/`/`bin/`, packaged `releases/`) and any `*.local.md` scratch/notes
  files — those pile up fast once a code project is involved.
- **Docs split**: `README.md` stays a practical reference (current
  status, folder structure, where the tunable values live, the handful
  of mechanics that need real explaining); `ROADMAP.md` stays a terse
  shipped/left checklist; anything longer — debugging sagas, dead ends,
  full design-decision reasoning — goes in a gitignored `NOTES.local.md`
  instead of bloating the two files someone actually reads day to day.

## Contributing

Commit messages follow [Conventional Commits](https://www.conventionalcommits.org/):

```text
<type>(<scope>): <summary>
```

- **type**: `feat`, `fix`, `refactor`, `docs`, `chore`, `test`
- **scope**: the mod folder, lowercased (`caveshrooms`, `theunknowing`), or
  omitted for repo-wide changes (tooling, this README)
- **summary**: imperative mood, no trailing period

Examples:

```text
feat(caveshrooms): add temporal milestone particle burst
fix(theunknowing): correct claim radius check at chunk borders
chore: update .gitignore for build output
```
