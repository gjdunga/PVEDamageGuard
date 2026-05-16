# Changelog

All notable changes to PVEDamageGuard are documented here. Format follows [Keep a Changelog](https://keepachangelog.com/en/1.1.0/). Versioning is [SemVer](https://semver.org/).

## [1.6.0] - 2026-05-16

CUI foundation release. First of four CUI minors leading up to v2.0; ships the panel framework, tab system, theme, and a read-only Status tab. Other tabs are present as placeholders with helpful "coming in vX.Y" messages. Plus per-event-context overrides for the rule matrix.

### Added

- **CUI admin panel** rendered via Oxide's standard `CuiHelper` infrastructure. Opens with `/pdg ui` (or `pdgui.tab status` from console). Closes with the X button, `/pdg close`, or `pdgui.close`. Requires `pvedamageguard.admin` permission or server-admin flag.
- **Panel layout**: title bar with version + close button, left-side tab strip (Status, Logging, History, Rules, Scaling), main content area. Coral accent (#ee7d5a) on active tab and section headings; dark gray content background for readability.
- **Status tab** (read-only): renders the same fields as `/pdg`'s status block in CUI form, with section headings color-coded by topic. Reflects current configuration at panel open; close and reopen to refresh.
- **Placeholder tabs** for Logging / History / Rules / Scaling - each shows the feature title plus a one-line "arrives in v1.X" hint that points to the existing CLI equivalent (e.g. Rules placeholder mentions `/pdg context`).
- **`pdgui.tab <name>`** console command - registered for in-CUI tab-switch buttons. Players can invoke from F1 console too.
- **`pdgui.close`** console command - dual of `/pdg close` for the close button.
- **OnPlayerDisconnected** hook removes the player's open-panel state so the dictionary doesn't accumulate stale entries.
- **Unload** hook calls `HideAllPanels()` so all open panels close cleanly when the plugin is unloaded (otherwise the CUI elements would linger as orphaned overlays).
- **Per-event-context override** for both `EventTracker` and `GlobalEventTriggers`. New `PerEventContext` dict in each provider config maps specific event names to specific context names. Example: `EventTracker.PerEventContext = { "BradleyAPC": "AtBradleyEvent", "BaseHelicopter": "AtHeliEvent" }` flips to different contexts based on which event the player is near, rather than the previous single `TriggerContext` for all. `TriggerContext` remains the fallback for events not listed.

### Changed

- `ResolveContext` now consults `PerEventContext` lookups before falling back to `TriggerContext` for both providers. Order unchanged (ZoneManager -> RaidableBases -> EventTracker -> GlobalEventTriggers -> Default).
- `UsageRoot` lang string extended with `ui` and `close` subcommands.

### Notes

- The CUI tab strip shows all five tabs from v1.6 onward, but only Status is functional in this release. Placeholder messages explicitly call out the version where each tab becomes functional (v1.7 / v1.8 / v1.9). This is intentional: it lets admins see the destination roadmap from inside the game and primes muscle memory for tab locations.
- Per-event-context overrides have no effect if the config keeps default empty dicts. v1.4 / v1.5 configs continue to use the single `TriggerContext` as they always did.
- The CUI uses standard Oxide CuiHelper - it works on both Oxide and Carbon (Carbon ships the same Cui namespace).

## [1.5.0] - 2026-05-16

Performance, reliability, and framework expansion. All additive; no breaking changes.

### Added

- **Per-entity classification cache.** New `_classifyCache` dictionary keyed by `net.ID.Value` stores `(NpcCategory, subtype)` per entity for its lifetime. Both `ClassifyEntity` and `ClassifySubtype` now consult the cache first; results populate it on first miss. Bounded at 10000 entries (full clear when reached). Entries invalidated automatically on `OnEntityKill` via the existing hook. The hot path goes from N type-check branches per call to a single dictionary lookup once an entity has been seen.
- **Startup self-test.** New `RunSelfTest` method verifies that the Rust types PVEDamageGuard depends on resolve correctly at runtime (BasePlayer, BaseNpc, NPCPlayer, BaseHelicopter, BradleyAPC, BuildingBlock, Door, DecayEntity, LootContainer). Also checks `DamageType.LAST`, the cached `_allDamageTypes` array, and `TOD_Sky.Instance` (soft-fail since TOD may not be ready at OnServerInitialized). Failures print as errors with the dependent feature noted (e.g. "BradleyAPC used for: Bradley APC"); plugin continues to run with whatever passed.
- **Hook timing telemetry.** `OnEntityTakeDamage` now optionally wraps in a `Stopwatch`; elapsed microseconds recorded in a 1000-entry rolling buffer. Toggle via `/pdg timing on`. Compute mean / p95 / max on demand via `/pdg timing`. Useful for diagnosing performance regressions and for setting a SLO ("the hook should be under 100us on average").
- **`/pdg timing [on|off|clear]`** command.
- **`/pdg selftest`** command - re-run the type self-test on demand (useful after a Facepunch update to verify nothing broke).
- **`/pdg cache [clear]`** command - show cache size, flush on demand.
- **GitHub Actions CI** (`.github/workflows/validate.yml`) - validates JSON/YAML syntax on every push/PR. Roslyn syntax check on the .cs file. Catches basic breakage before it ships to uMod.
- **Carbon framework declared as compatible.** `manifest.json` and `.umod.yaml` both list `oxide` and `carbon` in `compatible_frameworks`. PVEDamageGuard uses only standard Oxide hooks (`OnEntityTakeDamage`, `OnEntitySpawned`, etc.) and CovalencePlugin patterns that Carbon supports natively. See [docs/carbon.md](docs/carbon.md) for verification steps and known differences (none material so far).
- **More language stubs.** French (fr), German (de), Chinese Simplified (zh-CN), Portuguese Brazil (pt-BR). Translations are reasonable machine-quality with a note welcoming native-speaker contributions via PR.
- **Performance documentation** at [docs/performance.md](docs/performance.md): cache mechanism, hook timing methodology, benchmark targets, and a tuning guide for high-population servers.

### Changed

- `OnEntityTakeDamage` split into a thin timing wrapper and `OnEntityTakeDamageInner` containing the original body. No behavior change when `_hookTimingEnabled = false` (the default).
- `OnEntityKill` now also removes the entity from `_classifyCache`.
- `OnServerInitialized` now calls `RunSelfTest` after `ValidateConfig`.
- `UsageRoot` lang string trimmed to a single-line command list (full usage moved to `/pdg help`).

### Notes

- The cache trades memory for CPU. On a 200-player server with high entity churn, expect ~5-10 KB of cache memory and a measurable hot-path speedup. Hit rate on /pdg cache typically settles around 95%+ within minutes of a wipe.
- The self-test is intentionally conservative: it only checks that the types resolve. It does not exercise the classification logic itself (which would require synthetic entities). If you want functional verification, use `/pdg test` aimed at known entities.
- The hook timing buffer is fixed at 1000 entries. On a heavy server (e.g. a Bradley fight with 30 simultaneous combatants) the buffer fills in seconds; check stats during the activity, not after.
- Carbon support is declared but not extensively battle-tested at v1.5 release. If you run Carbon and find issues, please open a GitHub issue with the error log.

## [1.4.0] - 2026-05-16

Ecosystem integration release. Hooks into popular PVE/event plugins so the rule matrix flips contexts automatically based on what is happening on the server. Adds Discord webhook output for moderation visibility. No breaking changes; integrations are opt-in via config.

### Added

- **RaidableBases integration** via `[PluginReference]`. New `RaidableBasesProviderConfig` block under `RuleMatrix.ContextProviders.RaidableBases`. Listens to `OnRaidableBaseStarted(Vector3, int)` and `OnRaidableBaseEnded(Vector3, int)` hooks (both with and without the mode parameter for cross-version compatibility). Tracks dome center positions and radii in a separate `_activeDomes` dictionary; `ResolveContext` proximity-checks the victim position and flips to `InRaidableBaseDome` (configurable `TriggerContext`) when inside any dome.
- **Convoy integration** via `[PluginReference]`. Listens to `OnConvoyStart()` and `OnConvoyStop()` hooks. Because the convoy is a moving fleet without a single position marker, Convoy is tracked as a server-wide global event flag.
- **Armored Train integration** via `[PluginReference]`. Listens to `OnTrainEventStart` / `OnTrainEventStop` and `OnArmoredTrainEventStart` / `OnArmoredTrainEventStop` (different versions of the plugin use different hook names). Tracked as a server-wide global event flag.
- **`GlobalEventTriggersConfig`** under `RuleMatrix.ContextProviders.GlobalEventTriggers`. When any listed global event is active anywhere on the map, every victim position resolves to the configured `TriggerContext` (default `AtPvpEvent`). Useful for events that span the whole map and have no single positional marker.
- **Discord webhook output** via `DiscordWebhookConfig` block. Configurable webhook URL, minimum log level (default `Reflects`), per-minute rate limit (default 20 to leave headroom under Discord's 30/min cap), message prefix, username override, avatar URL override. Token-bucket rate limiting via a sliding 1-minute window. Webhook messages are queued with Oxide's `webrequest` so they don't block the hook.
- **`/pdg events`** command: lists all active context-affecting events - entity events (Bradley/Heli/Cargo), RaidableBases domes, and global events (Convoy/ArmoredTrain) - with positions, modes, and ages.
- **`/pdg webhook [test|on|off]`** command: status display with `/pdg webhook`, send a test message with `/pdg webhook test`, toggle Enabled flag without editing config with `on`/`off`.
- **Integration recipes** in `docs/integrations/`: dedicated short docs for RaidableBases, Convoy, ArmoredTrain, and Discord webhooks covering setup, troubleshooting, and how PVEDamageGuard composes with each.

### Changed

- `ContextProvidersConfig` extended with two new fields: `GlobalEventTriggers` and `RaidableBases`. Existing `ZoneManager` and `EventTracker` fields unchanged.
- `ResolveContext` now consults providers in this priority order: ZoneManager (positional zone flags) -> RaidableBases (dome proximity) -> EventTracker (entity proximity) -> GlobalEventTriggers (server-wide flags) -> DefaultContext.
- `Log()` now also forwards messages to the Discord webhook when `DiscordWebhook.Enabled=true` and the message's level meets `DiscordWebhook.MinLevel`.
- Status block (`/pdg`) now reports dome count, global event count, and Discord webhook enabled state alongside the existing fields.
- `LoadDefaultMessages` gains 13 new lang keys for events / webhook / help text.

### Notes

- All four ecosystem plugins (RaidableBases, Convoy, ArmoredTrain, Discord) are optional. PVEDamageGuard works exactly as in v1.3.0 if none are installed and the new config blocks are at their defaults.
- The RaidableBases dome radius defaults to 75m when the plugin doesn't supply one and `RadiusOverrideMeters=0` in config. Adjust per your raid base sizes.
- Discord webhooks are off by default and require an explicit URL plus `Enabled=true`. Test with `/pdg webhook test` after setup.
- Convoy and Armored Train are tracked as server-wide flags rather than positional markers because their entities (multiple Bradleys, multiple scientists, multiple cars) don't have a single canonical position. If you want positional convoy detection, add the convoy-spawned Bradley entities to `EventTracker.Events` instead.

## [1.3.0] - 2026-05-16

Onboarding release. Reduces friction for new admins and Damage Control migrators with config import, presets, validation, and an interactive help system. No breaking changes; all additions are additive commands and a passive validation pass.

### Added

- **`/pdg import damagecontrol`** - reads `oxide/config/DamageControl.json` and maps compatible fields into PVEDamageGuard's config. Backs up the current PDG config to `PVEDamageGuard.backup.YYYYMMDDHHMMSS.json` first. Maps: per-victim damage tables (APC/Heli/Bear/Wolf/etc -> PerVictimSubtypeScaling), Building_Grade_Multipliers, Bypasses.Heli_bypass (-> PerAttackerStructureScaling[PatrolHelicopter]), Time.Time_Type, and the four overlapping Time_Multipliers categories. Reports per-mapping success and explicitly skipped fields (Animal_Time, Heli_Time, Bradley_Time, Other_Time, Building per-piece) with rationale.
- **`/pdg preset <name>`** - applies a known-good complete config. Four presets ship:
  - **pvepure** - block all PvP, severe NPC damage scaling (0.25x default, 0.1x bullet), full building invulnerability to NPCs.
  - **pvereflect** - reflect PvP at 1.0x, NPC damage scaled to 0.5x default with 0.25x bullet, NPC->Structure at 0.5x.
  - **pvevehicleraids** - reflect PvP, NPCs cannot damage structures EXCEPT PatrolHelicopter and BradleyAPC (both 1.0x, preserving the heli/Bradley raid event experience).
  - **pvphoursevents** - PvP blocked by default, rule matrix enabled with Default+AtPvpEvent contexts and EventTracker active; PvP only allowed near Bradley/Heli/Cargo.
- **`/pdg validate`** - runs the config validator on demand and reports issues with line-by-line detail.
- **`/pdg help [subcommand]`** - interactive help. With no arg, lists all subcommands with one-line descriptions. With a subcommand arg, prints full usage and an example. Closest practical equivalent to tab-completion since Rust chat does not support client-side completion.
- **Config validation at load.** Runs after `RebuildCaches` in `OnServerInitialized` and on every `/pdg reload`. Checks: EnvironmentalDamageTypes parse to `Rust.DamageType`, all scaling multipliers in `[0, 100]`, TOD arrays have 24 elements, TimeOfDaySource is `Game` or `Real`, rule matrix Inherits chains have no cycles or dangling references, rule action strings parse, provider target context names exist. Issues surface as `PrintWarning` lines at load and in the `/pdg` status block.
- **Status block now reports `Config issues: N`** so admins notice validation problems without needing to scroll back through console output.

### Changed

- `CmdReload` now also runs validation and reports `ConfigReloadedWithIssues` if any are found.
- `UsageRoot` lang string expanded to include the four new subcommands.

### Notes

- The DamageControl importer is one-way and does its best with the v2.5.14 schema. Fields without a direct PVEDamageGuard equivalent (per-piece building protection, separate animal/heli/Bradley/other time multipliers) are explicitly skipped with a report line referencing the migration mapping in `docs/configuration.md`. Use the backup file to roll back if the result is not what you wanted.
- Presets are deliberately complete config snapshots, not deltas. Applying a preset overwrites your existing tuning. Recommended workflow: `/pdg preset <closest-match>`, then iterate from there.
- Validation is non-fatal. Issues are warnings only; the plugin continues to run with whatever config it managed to load.

## [1.2.0] - 2026-05-16

Architectural addition: optional declarative rule matrix as an alternative to the case-based scaling logic. Context providers integrate with ZoneManager and a built-in event tracker so rules switch automatically when players enter PvP zones or get within range of Bradley/Heli/Cargo events. Adds dry-run damage simulation and a history ring buffer for live diagnostics. Fully backward compatible: existing v1.0.x and v1.1.x configs continue to use the legacy scaling path because `RuleMatrix.Enabled` defaults to `false`.

### Added

- **Declarative rule matrix.** New `RuleMatrix` config block defines named `Contexts` containing `(AttackerCategory|AttackerSubtype) -> (VictimCategory|VictimSubtype) -> Action` rules. Actions: `allow`, `block`, `reflect:<mult>`, `scale:<mult>`, `scale:{Bullet:0.25,Default:0.5}`. Contexts support `Inherits` to compose; lookup precedence walks specific-to-general (9 candidate patterns including `*` wildcards). Default ships with `Default`, `AtPvpEvent`, and `InRaidableBaseDome` contexts.
- **ZoneManager integration.** Optional context provider; when ZoneManager is loaded and `RuleMatrix.ContextProviders.ZoneManager.Enabled=true`, zone flags (e.g. `pvp`) map to context names so per-zone rules apply automatically.
- **Built-in event tracker.** `OnEntitySpawned`/`OnEntityKill` track BradleyAPC, BaseHelicopter, and CargoShip; victim positions within `RadiusMeters` of any tracked event flip context to `TriggerContext` (default `AtPvpEvent`). Configurable event list, trigger context, and radius.
- **`/pdg context`** command: reports the currently active context at the player's position and the count of tracked events.
- **`/pdg history [N]`** command: shows the last N (up to 100) classified hits with timestamp, classification, context, action, and damage. Ring buffer is filled regardless of console log level (the history is a separate diagnostic).
- **`/pdg test fire <DamageType> <amount>`**: dry-runs a synthetic hit through the full modifier stack (rule matrix path if enabled, legacy scaling path otherwise) and reports the final damage that would land - without actually hurting anything.
- **Public API additions**:
  - `API_GetActiveContext(Vector3 pos)` returns the active context name at a position (or null if rule matrix is disabled).
  - `API_IsPvpAt(Vector3 pos)` returns true if PvP is allowed at that position under current rules.
  - `API_IsAllowed(BaseEntity attacker, BaseEntity victim)` returns true unless the rule matrix would block this pairing. Lets other plugins (RaidableBases, Convoy, Backpacks-on-death, etc.) query PVEDamageGuard cleanly instead of duplicating PvP detection.

### Changed

- `OnEntityTakeDamage` now dispatches between two code paths based on `_ruleMatrixEnabled`. Legacy scaling path (`HandleViaScaling`) is the v1.1 code unchanged. Rule matrix path (`HandleViaRuleMatrix`) resolves context, looks up the rule, applies the action, and composes v1.1 modifiers (TOD, victim subtype, building grade) on top of `scale` and `reflect` actions.
- `LogHit` now also appends to a 100-entry ring buffer regardless of console log level so `/pdg history` always has recent data to show.
- Status block (`/pdg`) reports rule matrix enabled flag and active tracked event count alongside the existing feature flags.

### Notes

- No breaking changes. `RuleMatrix.Enabled` defaults to `false` and existing v1.1.x configs load unchanged.
- When rule matrix is enabled, the v1.1 PvP reflect / block / yield-to-TruePVE settings still apply as overrides for the PvP-vs-PvP combination (before the rule matrix is consulted) so the most common admin tweak does not require touching the matrix.
- The shipped default rule matrix reproduces v1.1 behavior approximately. Admins who want to flip on the matrix can do so with no other config changes; the only effective shift is that rules become explicit and inspectable via `/pdg test`.

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
