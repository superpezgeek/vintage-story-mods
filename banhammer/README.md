# Banhammer

An admin-only creative tool: a melee weapon with three selectable modes that
lands a moderation action on whichever player it strikes, instead of normal
damage.

**Status: design only, nothing built yet.** This document is the plan. See
`ROADMAP.md` for the build checklist and the "Open questions" section below
for decisions that need a human before or during implementation.

## What it does

One item, `banhammer:banhammer-item`, with three modes cycled by the wielder:

| Mode | Effect on the struck player |
| --- | --- |
| **Smite** | Lethal damage — kills them via the normal death pipeline (drops, death message, respawn), not a hidden/silent removal |
| **Kick** | Disconnected immediately and temporarily banned (default: 1 hour, configurable) |
| **Ban** | Disconnected immediately and banned for a very long duration (default: 1000 years — see "Permanent bans" below for why not literally forever) |

All three additionally strike the target with a real lightning bolt
(visual + the engine's own lightning damage/fire behavior) as a flourish.
This is cosmetic, not the mechanism of the kill/kick/ban — if the lightning
call turns out to be unreliable, the mode logic works without it.

## Access control (defense in depth)

This item must never be reachable in survival or by a non-admin, so it's
gated three separate ways rather than trusting any single one:

1. **Not craftable** — no grid/recipe references it anywhere.
2. **Hidden from the creative inventory browser** — no `creativeinventory`
   block in the itemtype JSON, so it doesn't show up in the creative-mode
   search/tabs for anyone who happens to be in creative mode. The only way
   to obtain it is `/giveitem banhammer:banhammer-item`, which itself
   requires the `give` privilege (already an admin-only privilege in
   vanilla).
3. **Runtime check on every swing** — even if a copy ends up in the wrong
   hands (traded, dropped, granted by mistake), `OnAttackingWith` verifies
   the wielder is in Creative mode *and* holds an admin-level privilege
   (currently planned: `Privilege.root`, so ops/owner only — see open
   questions) before doing anything. If either check fails, the swing is a
   no-op (or falls through to ordinary tiny damage — TBD, see below) and
   the wielder gets a chat error explaining why.

## How mode-switching works

Mode is stored per-itemstack (like durability), not a global setting.
Shift+right-click cycles Smite → Kick → Ban → Smite. The current mode shows
in the item's held-item info text (the tooltip-style line under the hotbar
slot, same mechanism The Unknowing uses for its
`EntityBehaviorInfoText` — see `theunknowing/TheUnknowingCode/EntityBehaviorInfoText.cs`
for the existing pattern to reuse/adapt) so the wielder always knows which
mode is armed before swinging.

## Implementation sketch

- **`ItemBanhammer : Item`** (in `BanhammerCode/`)
  - `OnHeldInteractStart` (or `OnHeldAttackStart` guard) handles the
    shift+right-click mode cycle, persisting the mode into the itemstack's
    `Attributes`.
  - `OnAttackingWith(IWorldAccessor world, Entity byEntity, Entity
    attackedEntity, ItemSlot itemslot)` is the actual trigger — this is the
    same hook vanilla weapons use to apply on-hit effects
    (`EntityAgent.hitEntity` calls it once per successful melee hit,
    matching `Collectible.OnAttackingWith`'s durability-damage default
    override). Guard on `world.Side == EnumAppSide.Server` and
    `attackedEntity is EntityPlayer` before doing anything — this hook can
    otherwise fire in contexts we don't want to react to.
  - **Smite**: `attackedEntity.ReceiveDamage(new DamageSource { Source =
    EnumDamageSource.Player, Type = EnumDamageType.Injury, SourceEntity =
    byEntity }, 9999f)` — routes through the normal death pipeline (death
    message, drops, respawn) rather than zeroing health directly, which
    skips those systems.
  - **Kick / Ban**: both reuse vanilla's own `/ban` command rather than
    inventing a parallel ban list, via
    `sapi.ChatCommands.ExecuteUnparsed($"/ban \"{targetName}\" {n} {unit}
    banhammer", callingArgs, callback)` with a Console-level `Caller` so the
    command's own privilege check passes unconditionally. This keeps the
    result visible to `/listbans`, `/unban`, etc. — a real admin using
    those tools later sees a normal ban entry, not a mystery disconnect.
    **This call chain is unverified — see "Needs testing" below.**
  - **Lightning**: `((ICoreServerAPI)api).ModLoader
    .GetModSystem<WeatherSystemServer>().SpawnLightningFlash(targetPos)`
    fires the engine's real lightning strike (visuals + its own
    damage/fire behavior) at the struck player's position. Purely cosmetic
    — call it and ignore the outcome.
- **`itemtypes/banhammer-item.json`**: no `creativeinventory` key, no
  recipe references anywhere, `attackpower`/`attackrange` attributes tuned
  so it registers as a normal melee hit.
- **Config**: kick duration and ban duration should be tunable via
  ConfigLib, following the existing `CaveshroomsConfig.cs` /
  `UnknowingConfig.cs` pattern (`configlib-patches.json` +
  `HookConfigLib()` in the ModSystem) rather than hardcoded.

## Permanent bans aren't literally permanent

Vanilla's `/ban` syntax is `/ban playername timespan (hour|day|week|year)
reason` — there's no "forever" unit. "Ban" mode uses a very large number of
years (planned default: 1000) as a practical stand-in for permanent. This
is a vanilla engine limitation, not something worth working around with a
custom ban list — a custom list would lose `/listbans`/`/unban`
compatibility, which is worse.

## Needs testing (can't verify from source alone)

- **Does `ChatCommands.ExecuteUnparsed` actually work called this way from
  mod code?** I found the `/ban` command's exact syntax and behavior from
  the game's own lang file (`cmdban-*` keys), but the real `/ban` handler
  itself lives in closed-source server code — I can't read its
  implementation, only its user-facing strings. If `ExecuteUnparsed` proves
  unreliable (wrong Caller shape, privilege check fails anyway, etc.), the
  fallback is: call `IServerPlayer.Disconnect(reason)` directly for the
  immediate kick, and maintain the mod's own small persisted ban list
  (checked on player join) instead of leaning on vanilla's. Worse
  ergonomics for other admins, but fully within the public API.
- **Does PvP-off block the hit before `OnAttackingWith` even fires?** If a
  server has player-vs-player damage disabled, it's possible the engine
  refuses the hit earlier in the pipeline (before any item hook runs)
  rather than letting it through and zeroing the damage. Admin accounts
  typically carry every privilege including `attackplayers`, so this is
  unlikely to matter in practice, but it's untested.
- **Held-item info text mechanics** — `EntityBehaviorInfoText` in The
  Unknowing was written for an entity, not an item; confirm the
  itemstack-tooltip equivalent (likely `GetHeldItemInfo` override) behaves
  the same way before assuming the pattern ports directly.

## Open questions for you

1. **Privilege gate**: I've defaulted to requiring `Privilege.root` (owner
   only) rather than `Privilege.ban`/`Privilege.kick` (any moderator with
   those individual privileges). Root-only is safer for something this
   destructive but shuts out ops who only have kick/ban, not full root. Do
   you want a lower bar?
2. **Non-admin/non-creative swing behavior**: if the gate fails, should the
   swing be a complete no-op, or fall through to trivial vanilla damage so
   it doesn't look broken if it's ever picked up by mistake? I've assumed
   no-op with a chat error to the wielder for now.
3. **Kick duration default**: 1 hour was my placeholder — is that the
   right length for "kick," or did you have a specific duration in mind?
4. **Ban duration default**: 1000 years as the "permanent" stand-in — fine,
   or would you rather see a genuinely different mechanism (e.g. an
   IP/UID entry in a mod-maintained permanent list, bypassing `/ban`'s
   year cap entirely)?
5. **Self-hits and friendly fire**: should the Banhammer be able to target
   the wielder themself (e.g. testing Smite mode on your own character),
   or should self-hits always be blocked?
6. **Reason text shown to the struck player**: vanilla shows whatever
   reason string the banning command passes along. Want something flavorful
   ("smitten by the Banhammer") or a plain moderation-log-style reason?
