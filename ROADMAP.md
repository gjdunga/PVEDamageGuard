# Roadmap

Planned versions through v2.0 (the marketplace-launch milestone). Nothing here is a commitment; priorities shift based on user feedback and Facepunch's monthly forced wipes. The cadence target is one minor release per month, aligned with Rust's first-Thursday forced wipe.

## Shipped

### v1.0.0 (2026-05-15)
Initial public release. Type-based NPC classifier, per-attacker damage scaling, PvP reflect, five-tier logging, public API, TruePVE companion mode, `/pdg test` diagnostic.

### v1.0.1 (2026-05-16)
DamageControl load-time conflict warning escalated to error.

### v1.1.0 (2026-05-16)
Restored four Damage Control parity features as additive, no-op-by-default config blocks:
- Time-of-day damage modifiers (Global / PvP / NpcToPlayer / NpcToStructure, 24-hour arrays, Game or Real source)
- Per-victim subtype scaling (Bear, Wolf, Heli, Bradley, Minicopter, Barrel, etc.)
- Building grade multipliers (Twigs / Wood / Stone / Metal / TopTier)
- Per-attacker structure scaling (replaces `Heli_bypass` with a more flexible model)

Also: `/pdg hour`, expanded `/pdg test`, `API_ClassifySubtype`, `API_GetCurrentHour`.

### v1.2.0 (2026-05-16)
Declarative rule matrix (opt-in) with context providers:
- Rule actions: `allow` / `block` / `reflect:N` / `scale:N` / `scale:{type:N,...}`
- Contexts with `Inherits` chains and 9-tier precedence lookup
- ZoneManager integration (zone flag -> context)
- Built-in event tracker (Bradley / Heli / Cargo proximity)
- `/pdg context`, `/pdg history [N]`, `/pdg test fire <type> <amount>`
- `API_GetActiveContext`, `API_IsPvpAt`, `API_IsAllowed`

### v1.3.0 (2026-05-16) - Onboarding

- `/pdg import damagecontrol` - reads DamageControl.json, maps to PDG fields, backs up current config
- `/pdg preset <name>` - four presets: pvepure, pvereflect, pvevehicleraids, pvphoursevents
- `/pdg help [subcommand]` - interactive help (closest practical equivalent to tab completion in Rust chat)
- `/pdg validate` - on-demand config validation
- Automatic config validation at load: Inherits cycles, dangling provider targets, malformed rule actions, TOD array length, scaling bounds, unknown DamageType
- Status block now reports config-issue count

### v1.4.0 (2026-05-16) - Ecosystem integration

- RaidableBases integration: tracks dome positions via OnRaidableBaseStarted/Ended hooks, proximity-checks victim position
- Convoy integration: tracks server-wide active flag via OnConvoyStart/Stop hooks
- Armored Train integration: same pattern as Convoy (supports both OnTrainEvent* and OnArmoredTrainEvent* hook signatures)
- New GlobalEventTriggers context provider for server-wide event flags
- Discord webhook output with configurable URL, min level, rate limit, prefix, username/avatar override
- `/pdg events` - list all active entity events, domes, global events
- `/pdg webhook [test|on|off]` - status, test message, runtime toggle
- Integration recipes in docs/integrations/ for RaidableBases, Convoy, ArmoredTrain, Discord webhooks

### v1.5.0 (2026-05-16) - Performance, reliability, Carbon

- Per-entity classification cache (10000 entries, invalidated on OnEntityKill)
- Startup self-test verifies Rust type resolution; surfaces breakage as load-time errors
- Hook timing telemetry with rolling 1000-entry Stopwatch buffer
- `/pdg timing [on|off|clear]`, `/pdg selftest`, `/pdg cache [clear]` commands
- Carbon framework declared compatible (manifest + .umod.yaml updated; uses only Oxide-stable hooks)
- GitHub Actions CI for JSON/YAML/C# syntax checks on PRs
- Language stubs added: French, German, Chinese Simplified, Portuguese-Brazil
- New docs: docs/performance.md, docs/carbon.md

## Planned (CUI slice toward v2.0)

The v2.0 marketplace-launch milestone calls for an in-game CUI admin panel. To avoid that being one large risky release, the CUI is sliced into four read-only-first then editable minors, one per monthly wipe cycle. Each minor also ships one non-CUI feature so the version has standalone value. v2.0 itself becomes pure non-code launch logistics.

### v1.6.0 (2026-05-16) - CUI foundation + Status tab (shipped)

CUI:
- CUI helper module: panel open/close, tab system, theme constants, perm gating
- `/pdg ui` opens the panel for `pvedamageguard.admin` holders
- Status tab (read-only) - renders the `/pdg` status text as a styled CUI panel
- Auto-close on player disconnect; survives plugin reload (close-all on Unload)
- `pdgui.tab <name>` console command handles tab switches

Non-CUI:
- Per-event-context override. New `PerEventContext` dict under both `EventTracker` and `GlobalEventTriggers`. Admins can map specific event names to specific contexts ("Bradley -> AtBradleyEvent", "Heli -> AtHeliEvent") with `TriggerContext` as fallback. Resolves the limitation called out in docs/integrations/armored-train.md.

### v1.7.0 (2026-05-16) - Logging + History CUI tabs + per-player stats (shipped)

CUI:
- Logging tab: live-streaming view of recent log lines, color-coded by level (None/Reflects/Scaled/All/Trace)
- History tab: paginated view of the `_history` ring buffer with sortable columns (time, attacker, victim, damage, action, context)
- Filter controls in both tabs: toggle log levels visible, search history by attacker/victim name

Non-CUI:
- Per-player damage statistics counters (damage dealt, reflected, taken from NPCs, taken from PvP). Persisted to `oxide/data/PVEDamageGuard/stats.json` with periodic writes.
- `API_GetPlayerStats(BasePlayer)` public hook for integration with stats plugins.

### v1.8.0 - Rules tab (read-only browser) + custom NPC categories (target: 2026-08-06 forced wipe)

CUI:
- Rules tab: tree view of contexts -> rules with color-coded action types (green=allow, red=block, yellow=reflect, blue=scale)
- Inheritance chain visualization (shows full Inherits walk)
- "Active at my position" indicator highlighting which context resolves where the admin is standing

Non-CUI:
- Custom NPC category registration API: `API_RegisterCategory(string name, Func<BaseEntity, bool> matcher)`. Third-party plugins (custom NPC mods, Frontier-style packs) can register subtypes that flow through the existing classifier and rule matrix. Registered names appear in `/pdg test` output and are valid in rule matrix keys.

### v1.9.0 - Editing CUI + Backpacks integration (target: 2026-09-03 forced wipe)

CUI:
- Rules tab edit mode (toggle): dropdowns to change action types, add/remove rules, edit context Inherits
- Scaling tab: sliders for NPC->Player per-damage-type, NPC->Structure, BuildingGradeMultipliers; toggle buttons for boolean configs; dropdowns for Logging and TimeOfDaySource
- Live config save with debounce; runs validation on every edit

Non-CUI:
- Backpacks-on-death integration via `[PluginReference]`. Detect popular Backpacks plugins (Backpacks by WhiteThunder, or Backpacks 4 by Whispers88) and respect "no drop in PVE zone" semantics. PVE servers commonly run one of these and want backpacks preserved through reflect-induced kills.

### v2.0.0 - Codefling launch (target: 2026-10-01)

Pure non-code. Pushed one month from the original September target so v1.6-v1.9 each get a full wipe cycle of real-world CUI use before the listing goes live.

- Codefling listing at $15-20 one-time, unlimited-server
- Free uMod listing remains; Codefling adds curated downloads + priority support + Discord channel
- Five-wipe battle-tested promise (May v1.5 through September v1.9) verified
- Marketing assets: 5-8 screenshots, 60-90s feature-tour GIF, comparison spreadsheet vs DamageControl and TruePVE+DynamicPVP+ReflectDamage stack
- Support tier setup: Discord support channel, response-time SLO documented

## Why slice CUI across four minors

Single big v2.0 risk: CUI bugs that only surface under real-world load (open during heli combat, panel left open during plugin reload, edits while another admin is also editing, screen-resolution variance) would all hit on day one of the paid listing. Slicing means each tab gets a full wipe cycle of admin use before v2.0 ships, and reviewers see mature code at launch instead of a v2.0 that just rolled out.

The tradeoff is one month of revenue deferred (October instead of September). Worth it for the much-reduced reputation risk.

## Post-2.0 (no fixed dates)

These are real features but not gating for marketplace launch. They land when there's time, in response to user requests, or when an interesting third-party PR arrives.

### v2.1 - Leaderboard tooling
- **`/pdg stats top [N]`** - leaderboards: top damage dealers, top reflectors, top NPC slayers. (Per-player counters and `API_GetPlayerStats` already shipped in v1.7.)
- **Top-damage Discord weekly digest** via existing webhook infrastructure.

### v2.2 and beyond - TBD
Driven by user feedback after v2.0 launch. Likely candidates: webhook plug-ins beyond Discord (Slack, generic HTTP POST), per-context damage budgets ("PvP zone deals at most X total damage per hour"), historical heatmaps of where most PvP happens, NPC behavior tweaks (e.g. scientist morale, retreat radius) if Facepunch exposes hooks.

## Won't do

- **Full TruePVE replacement.** TruePVE is the rule-matrix-with-zones plugin. We are the classifier + scaling + reflect-as-service companion. Even after v2.0 we remain a companion, not a competitor. Admins who want our matrix without TruePVE can use ours alone; admins who want TruePVE's specific feature set should keep using TruePVE and let us layer scaling on top.
- **Per-server license model.** Codefling and Lone.Design convention is one-time unlimited-server licenses tied to an account. We follow that convention.
- **Subscription pricing.** Same reasoning. Buyers expect perpetual access to the version they paid for plus reasonable free updates.
- **Web admin panel.** Out of scope for a plugin. RCON, config files, and the in-game CUI panel cover the use cases.
- **Mobile remote-management apps.** Same reason.
- **AI / ML-powered anything.** No genuine product fit; would be a marketing gimmick.
- **Standalone Discord / Slack / Telegram bots.** Webhook output (v1.4) is the right scope. Standalone bots are a different product.
- **Feature-gated paid edition.** GPL-3.0 makes this incoherent; even if it didn't, the buying public for Rust plugins rejects it. Paid offering is convenience + support, not features.

## Maintenance promise

- **Monthly forced wipe** (first Thursday of each month at 18:00 UTC): same-week patch for any Facepunch breaking change. Goal: < 48 hours from broken wipe to fixed release on GitHub and uMod. Codefling buyers (post-v2.0) get a forced-update push as soon as the patch is signed.
- **Critical bugs** (data loss, server crash, security): hotfix within 48 hours regardless of wipe schedule.
- **Feature requests**: triaged on a monthly cadence aligned with each minor release. Open one issue per request on GitHub; PRs welcome with tests.
- **Pull requests**: reviewed within one week. Changes that include tests and don't break the type-based classification contract are merged faster.
- **API stability**: the public API surface (everything prefixed `API_`) is considered stable from v1.0. Breaking changes require a major version bump and one major-version cycle of deprecation warnings.
- **Classifier contract**: NPC detection will never regress to prefab-name string matching. If a future Facepunch change makes type-based detection impossible for some subclass, we add a typed fallback and document it; we do not paper over it with `ShortPrefabName.Contains`.

## Marketplace strategy

Two paths to consider; not yet decided.

**Path A - Polish to v2.0 then launch.** Free on GitHub + uMod through v1.5. Codefling listing goes live with v2.0 in September 2026 at $15-20. Pro: launch with a finished product, strong reviews. Con: no revenue for ~4 months.

**Path B - Ship v1.2 to Codefling now as $5-10 introductory, raise to $15-20 at v2.0.** Pro: revenue earlier, real user feedback during v1.3-v1.5. Con: early reviews on unpolished product can sink the listing; raising prices later annoys early buyers.

Recommendation: **Path A**. The research showed $5 reads as "toy" and early-buyer goodwill matters less than the first month of reviews on a $15+ listing. Spend the next four months on polish, ecosystem integration, and the CUI panel; launch with a strong product.

Counter-recommendation: if revenue urgency is high, Path B with a clearly-labeled "Early Access" tag and the explicit promise of free upgrades through v2.0 can work. Codefling supports this with the "Early Access" badge.
