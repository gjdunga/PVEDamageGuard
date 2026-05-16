# PVE Damage Guard

A future-proof Rust PVE damage classifier, per-attacker damage scaler, and reflect-as-a-service for Oxide / uMod. Designed to live alongside [TruePVE](https://umod.org/plugins/true-pve) as a companion plugin rather than replace it.

## What it solves

Every existing Rust PVE plugin (DamageControl, TruePVE, NextGenPVE, SimplePVE, War Mode, Real PvE) classifies NPCs by matching prefab name substrings like `"scientist"` or `"human"`. Whenever Facepunch ships a new NPC subclass (HumanNPCNew variants, Travelling Vendor guards, Frontier NPCs, future types), those checks silently break and players take vanilla damage from NPCs the plugin no longer recognizes.

PVEDamageGuard classifies NPCs by **base type** (`BasePlayer.IsNpc`, `BaseNpc`, `NPCPlayer`, `BaseHelicopter`, `BradleyAPC`). New NPC subclasses are caught automatically because they all inherit from these bases. It then exposes a public API so other PVE plugins can ask "what category is this entity?" instead of maintaining their own brittle prefab lists.

## Features

- **Type-based NPC classifier** that survives every Facepunch update without code changes
- **Optional declarative rule matrix** with contexts and inheritance: write `(Attacker -> Victim) -> Action` rules and switch contexts automatically based on ZoneManager zones or Bradley/Heli/Cargo event proximity
- **Per-attacker, per-damage-type NPC->Player scaling** (NPC bullets at 0.25x, slash at 0.5x, etc.)
- **Per-attacker NPC->Structure scaling** with subtype overrides (`PatrolHelicopter: 1.0` lets helis raid at full damage even when other NPCs are blocked - replaces Damage Control's `Heli_bypass`)
- **Per-victim subtype scaling** (Bear, Wolf, Minicopter, Barrel, etc.) - per-damage-type, stacks on top of attacker rules
- **Building grade multipliers** (Twigs/Wood/Stone/Metal/TopTier)
- **Time-of-day modifiers** for Global / PvP / NpcToPlayer / NpcToStructure - 24-hour arrays, Game or Real time source
- **PvP reflect** with re-entrancy guard, configurable multiplier, optional team carve-out
- **`/pdg test`** - aim at any entity, see classification, subtype, context, current hour, and every modifier layer that applies
- **`/pdg test fire <type> <amount>`** - dry-run a synthetic hit through the full modifier stack and see the final damage without actually hurting anything
- **`/pdg history [N]`** - inspect the last N classified hits with timestamps, classifications, contexts, and actions
- **`/pdg context`** - show the active rule-matrix context at your current position
- **`/pdg hour`** - current hour and TOD multipliers
- **`/pdg import damagecontrol`** - one-shot migration from Damage Control 2.5.x config files
- **`/pdg preset <name>`** - four known-good presets: pvepure, pvereflect, pvevehicleraids, pvphoursevents
- **`/pdg validate`** + automatic validation at load - catches malformed rules, Inherits cycles, dangling provider targets, out-of-bounds multipliers
- **`/pdg help [subcommand]`** - interactive help system
- **Five-tier logging** (None / Reflects / Scaled / All / Trace) with optional structured file output to `oxide/logs/PVEDamageGuard/`
- **Public API** including `API_Classify`, `API_ClassifySubtype`, `API_IsNpcAttacker`, `API_ReflectDamage`, `API_GetActiveContext`, `API_IsPvpAt`, `API_IsAllowed` for TruePVE / RaidableBases / PunishAttacker / NextGenPVE and other plugins
- **TruePVE companion mode** - auto-detects TruePVE on load and yields allow/block decisions to it, only applying scaling/reflect on top
- **Permission split** - `pvedamageguard.bypass` (damage immunity for testing) and `pvedamageguard.admin` (chat command access) are separate

## Quick install

1. Copy `oxide/plugins/PVEDamageGuard.cs` into your server's `oxide/plugins/` directory.
2. The plugin auto-loads. Default config is written to `oxide/config/PVEDamageGuard.json`.
3. Tweak the config, then `oxide.reload PVEDamageGuard` or `/pdg reload` in chat.
4. Grant admins the chat command perm: `oxide.grant user <steamid> pvedamageguard.admin`.

See [docs/installation.md](docs/installation.md) for full setup, [docs/configuration.md](docs/configuration.md) for every config field, [docs/commands.md](docs/commands.md) for chat commands, and [docs/api.md](docs/api.md) for the plugin API.

## Compatibility

- **Game**: Rust (current stable, as of the most recent forced wipe)
- **Framework**: Oxide / uMod (Carbon is untested but should work because we use only standard Oxide hooks)
- **Best with**: TruePVE (companion mode auto-enables), DynamicPVP, RaidableBases, PunishAttacker
- **Will warn at load if also present**: TruePVE, PVEMode, NextGenPVE (these all hook OnEntityTakeDamage; companion mode handles TruePVE cleanly, the others need manual config review)

## Status

**v1.3.0** - onboarding release. `/pdg import damagecontrol` for migrators, `/pdg preset <name>` for one-line config bootstrapping, automatic config validation with explicit error reports, and an interactive `/pdg help [subcommand]` system.

See [CHANGELOG.md](CHANGELOG.md) for version history and [ROADMAP.md](ROADMAP.md) for planned features.

## License

GPL-3.0. See [LICENSE](LICENSE) for details. Anyone is free to use, modify, and redistribute under GPL-3.0 terms.

---
*A [DunganSoft Technologies](https://dstaftn.net) project. Maintained by [Gabriel Dungan](https://github.com/gjdunga).*
