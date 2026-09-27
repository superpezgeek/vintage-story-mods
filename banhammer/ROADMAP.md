# Banhammer Roadmap

Nothing built yet — see `README.md` for the full design and the open
questions that should be settled before or during milestone 2.

## Milestone 1 — scaffold

- [ ] `modinfo.json` (modid `banhammer`)
- [ ] `BanhammerCode/BanhammerCode.csproj` (copy Caveshrooms' `.csproj` as
      the starting template — same `VintagestoryDir` HintPath setup)
- [ ] `BanhammerModSystem.cs`
- [ ] `.gitignore` (build output, `releases/`, `*.local.md`) — copy from
      an existing mod
- [ ] `scripts/release.ps1` — copy from an existing mod and retarget

## Milestone 2 — the item, no admin gate yet

- [ ] `assets/banhammer/itemtypes/banhammer-item.json` — no
      `creativeinventory`, no recipe anywhere
- [ ] `ItemBanhammer.cs` with mode stored per-itemstack, shift+right-click
      cycling Smite → Kick → Ban
- [ ] Held-item info text showing current mode (confirm the itemstack
      tooltip mechanism — see README "Needs testing")
- [ ] Smite mode: lethal `ReceiveDamage` on hit, verified in a local test
      world (does NOT require server privilege plumbing yet — just prove
      the hit hook and damage path work)

## Milestone 3 — kick and ban modes

- [ ] Prove out `ChatCommands.ExecuteUnparsed("/ban ...")` from mod code
      against a real dev server — this is the biggest unknown in the whole
      plan (see README "Needs testing")
- [ ] Kick mode wired to a short ban duration (config-driven)
- [ ] Ban mode wired to a long ban duration (config-driven)
- [ ] Fallback path implemented if `/ban` invocation doesn't pan out:
      direct `Disconnect()` + mod-maintained ban list checked on join

## Milestone 4 — access control

- [ ] Runtime gate in `OnAttackingWith`: wielder must be in Creative mode
      and hold the chosen privilege (see README open question #1)
- [ ] Chat error to the wielder when the gate blocks the swing
- [ ] Decide and implement self-hit behavior (README open question #5)

## Milestone 5 — flavor and config

- [ ] Lightning strike on all three modes via
      `WeatherSystemServer.SpawnLightningFlash`
- [ ] ConfigLib support for kick/ban durations, following the
      Caveshrooms/TheUnknowing pattern
- [ ] Reason text shown to struck players (README open question #6)

## Milestone 6 — release

- [ ] `README.md` current-state pass once built (this file currently
      describes the plan, not shipped behavior)
- [ ] First `-rc.0` build via `release.ps1`, manual multiplayer test as an
      op account (confirm all three modes end-to-end against a real
      second account, not just single-player)
