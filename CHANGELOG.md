# Changelog

All notable changes to PVEDamageGuard are documented here. Format follows [Keep a Changelog](https://keepachangelog.com/en/1.1.0/). Versioning is [SemVer](https://semver.org/).

## [1.1.0] - 2026-05-16

Restores four features that existed in the legacy MSpeedie/Wulf Damage Control plugin and were intentionally deferred from PVEDamageGuard v1.0. All four are additive and default to no-op behavior, so existing v1.0.x configs upgrade in place without behavior change until the admin chooses to use the new fields.

### Added

- **Time-of-day damage modifiers.** Per-hour multipliers (24-element arrays) for Global, PvP, NpcToPlayer, and NpcToStructure damage categories. Source is configurable as `Game` (Rust day/night cycle via `TOD_Sky.Instance.Cycle.Hour`) or `Real` (server wall clock via `DateTime.Now.Hour`). When all four arrays are all-ones the feature is detected as inactive and skipped on the hot path. Replaces Damage Control's `*_Time_Multipliers` family.
- **Per-victim subtype scaling.** New config block `PerVictimSubtypeScaling` keyed by entity subtype (Bear, Wolf, Boar, Chicken, Stag, Horse, RidableHorse, Minicopter, ScrapHelicopter, HotAirBalloon, BradleyAPC, PatrolHelicopter, SamSite, Barrel, Zombie, Scientist). Inner dict is per-damage-type multipliers with a `Default` fallback. Stacks on top of attacker-based rules. Replaces Damage Control's per-victim arrays (Bear_Multipliers, Heli_Multipliers, etc.).
- **Building grade multipliers.** New config block `BuildingGradeMultipliers` keyed by `Twigs`, `Wood`, `Stone`, `Metal`, `TopTier`. Applies to BuildingBlock entities (foundations, walls, doors built via the building plan) and stacks on top of attacker-based structure scaling. Replaces Damage Control's `Building_Grade_Multipliers`.
- **Per-attacker structure scaling.** New config block `PerAttackerStructureScaling` keyed by attacker subtype (PatrolHelicopter, BradleyAPC, HumanNpc, AnimalNpc, VehicleNpc, or a "Default" key). Replaces Damage Control's `Heli_bypass` flag cleanly: set `"PatrolHelicopter": 1.0` to let helis raid at full damage even when `"Default": 0.0` blocks every other NPC. Empty dict (the v1.1.0 default) falls back to the existing single `NpcToStructureScaling` scalar, so existing configs work unchanged.
- **`ClassifySubtype(BaseEntity)`** method and matching `API_ClassifySubtype` public API hook. Returns a stable subtype string for entities admins want to tune individually. Uses type checks where stable (BaseHelicopter, BradleyAPC) and prefab name matches for known-stable specific entities (Bear, Wolf, Minicopter, etc.). Does NOT introduce prefab-name fragility for NPC humanoid classification - that stays type-based in `ClassifyEntity`.
- **`/pdg hour` command.** Reports current hour and the four TOD multipliers at that hour, for time-of-day rule debugging.
- **Expanded `/pdg test` output.** Now reports entity subtype, current hour and global TOD multiplier, and the per-victim subtype scaling that would apply on top of the attacker-based rule.
- **`API_GetCurrentHour()` public API.** Other plugins can query the same hour PVEDamageGuard uses for TOD lookups, so a Discord plugin can announce "PvP hours start in 30 minutes" using the same source of truth.

### Changed

- `OnEntityTakeDamage` hook restructured to layer modifiers consistently. Order: case-specific scaling -> Global TOD -> category TOD -> per-victim subtype scaling -> building grade multiplier. All multipliers compose by multiplication so behavior is predictable.
- `RebuildCaches()` now detects whether each optional feature (TOD, victim scaling, building grade, per-attacker structure) is actually active, so the hot path can skip the lookup entirely when admins haven't configured the feature.
- Status block (`/pdg`) now reports the active-feature flags and the current hour so admins can see at a glance which modifier layers are in play.

### Notes

- No breaking changes. v1.0.x configs load unchanged. New fields are written to the config file on first v1.1.0 load with all-ones / empty defaults that are functionally no-ops.
- The features added here cover the gaps from Damage Control 2.5.14 that were called out during the v1.0 migration discussion. See ROADMAP.md for what remains.

## [1.0.1] - 2026-05-16

### Added
- Conflict detection for `DamageControl` (Wulf / MSpeedie's legacy plugin). PVEDamageGuard is the replacement; if both are loaded the load-time message escalates from warning to error and explicitly directs the admin to `oxide.unload DamageControl`. Reacts to runtime load/unload events so the message updates if Damage Control is unloaded after PVEDamageGuard.

### Notes
- No code-path changes to the damage hook itself; this is purely a load-time conflict surface improvement. Servers that don't have Damage Control loaded see no behavior change from v1.0.0.

## [1.0.0] - 2026-05-15

Initial public release.

### Added
- Type-based NPC classifier with public `NpcCategory` taxonomy (RealPlayer, HumanNpc, AnimalNpc, VehicleNpc, OwnedTrap, Building, Deployable, Environment, Other).
- Universal NPC detection via `BasePlayer.IsNpc`, `BaseNpc`, `NPCPlayer`, `BaseHelicopter`, `BradleyAPC` (no prefab-name string matching).
- Projectile and explosive attribution via `creatorEntity` and `OwnerID.IsSteamId()` chain walks (correctly identifies heli rockets, Bradley shells, scientist grenades, C4, satchels, trap-owner reflect).
- Per-attacker, per-damage-type scaling for NPC -> Player hits (Bullet, Slash, Stab, Bite, Blunt, Explosion, Arrow, plus a Default fallback).
- Uniform NPC -> Structure scaling (configurable, 0 = invulnerable, default 0.5x).
- PvP reflect with `HashSet<ulong>` re-entrancy guard, configurable multiplier, optional same-team carve-out, optional block-instead-of-reflect mode.
- Public API hooks callable by other plugins: `API_Classify(BaseEntity)`, `API_IsNpcAttacker(HitInfo)`, `API_ReflectDamage(BasePlayer, BasePlayer, HitInfo, float)`, `API_GetNpcScaling(string)`.
- TruePVE companion mode: auto-detects TruePVE at load (via `[PluginReference]`); when present and `YieldToTruePVE=true`, yields PvP allow/block decisions to TruePVE and only applies scaling on top.
- Companion warnings for PVEMode and NextGenPVE when both are loaded alongside.
- `/pdg test` command: raycasts from the admin's crosshair, classifies the target entity, and reports which rule would fire (the killer diagnostic feature).
- Five-tier logging: None / Reflects / Scaled / All / Trace, runtime-switchable via `/pdg log <level>`.
- Optional structured file logging to `oxide/logs/PVEDamageGuard/damage-YYYY-MM-DD.txt`.
- Permission split: `pvedamageguard.bypass` (damage immunity for testing) and `pvedamageguard.admin` (chat command authorization), independently grantable.
- Lang files for English, Russian, Spanish, and Latin per uMod convention.
- `/pdg scale <DamageType> <multiplier>` for live tuning of the NPC -> Player scaling table without editing JSON.

### Notes
- This is a new plugin, not a fork of any existing damage plugin. It is named "PVE Damage Guard" rather than "Damage Control" to avoid confusion with the existing umod.org plugin by MSpeedie/Wulf.
- Tested against Rust forced-wipe builds through 2026-05.
