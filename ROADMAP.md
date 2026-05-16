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

## Planned

### v1.3.0 - Onboarding (target: 2026-06-04 forced wipe)

Reducing friction for new admins and Damage Control migrators. Low complexity, high adoption value.

- **`/pdg import damagecontrol`** - reads `oxide/config/DamageControl.json`, generates an equivalent PVEDamageGuard config with comments noting which DC fields mapped to which PDG fields and which were dropped.
- **`/pdg preset <name>`** - applies a known-good preset config. Initial presets: `pvepure` (block all PvP, full NPC scaling, full base protection), `pvereflect` (reflect PvP, NPC scaling), `pvevehicleraids` (allow heli/Bradley to damage bases, block other NPC->structure), `pvphoursevents` (PvP allowed only during events or PvP-flagged zones).
- **Tab autocomplete** for `/pdg` subcommands, damage type names, context names, and preset names.
- **Config validation at load** - warn on malformed rule strings, unreachable contexts (no provider routes to them), Inherits cycles, unknown DamageType values, etc. Validation surfaces in a single `Puts` block at startup so admins see issues immediately.
- **Documentation polish** - migration guide updated with side-by-side DC -> PDG examples, common-pattern recipes (PVE with events, arena zones, RaidableBases), troubleshooting FAQ.

### v1.4.0 - Ecosystem integration (target: 2026-07-02 forced wipe)

The Codefling-readiness push. Integrate with the plugins admins already run.

- **RaidableBases integration** via `[PluginReference]`. Auto-detect dome presence at victim position; flip to `InRaidableBaseDome` context. Configurable RaidableBases context name in `ContextProviders`.
- **Convoy event support** - extend EventTracker with the Convoy plugin's spawn/kill hooks. Add `"Convoy"` to the default `Events` list.
- **Armored Train event support** - same pattern, listen to Armored Train plugin's events.
- **Discord webhook output** for `Reflects` and higher log levels. Configurable webhook URL, per-event-type filtering, rate limiting (Discord caps at 30/min). Useful for moderation channels watching for PvP attempts on PVE servers.
- **Integration recipes** in `docs/integrations/` - one short page per popular plugin (RaidableBases, Convoy, Armored Train, PunishAttacker, ReflectDamage) showing how PVEDamageGuard composes with each.

### v1.5.0 - Performance, reliability, Carbon (target: 2026-08-06 forced wipe)

Invisible-but-essential work plus framework expansion.

- **Per-entity classification cache** keyed by `net.ID` for the entity's lifetime, cleared on `OnEntityKill`. The hot path currently calls `ClassifyEntity` and `ClassifySubtype` on every hit; caching avoids repeated type checks for the same entity. Benchmark target: < 50us per hit on a 100-player server.
- **Self-test on startup** - validate that classification works against current Rust types by checking a few canonical type references (`BasePlayer`, `BaseNpc`, `BaseHelicopter`, `BradleyAPC`). If any fail, log an error and disable that branch gracefully rather than throwing per-hit exceptions.
- **Hook timing telemetry** - optional Trace-level dump of `OnEntityTakeDamage` execution time (mean, p95, max over a rolling 1000-hit window). Surfaces via `/pdg history --timing`.
- **GitHub Actions CI** - compile-check against pinned Oxide stub assemblies on every PR. Catches type-name drift before it ships.
- **Carbon framework support** - test under Carbon, document the differences (mostly none; both support the same hooks), publish on Carbon's plugin index alongside uMod.
- **More languages** - French (fr), German (de), Chinese (zh-CN), Portuguese-Brazil (pt-BR). Sourced from native-speaker contributors via PR.

### v2.0.0 - CUI admin panel + marketplace launch (target: 2026-09-03 forced wipe)

The major-version milestone. Justification for the version bump: substantial UX shift (in-game UI for non-technical admins) and external commitment (Codefling listing, support tier setup).

- **In-game CUI admin panel** - opens with `/pdg ui`. Tabs: Status (current feature flags, active context, recent history), Rules (browse contexts and rules, toggle context provider settings), Scaling (slider controls for NPC->Player damage types and building grades), Logging (toggle log level and file output), History (live tail of last 50 hits). Read-write for admins with the `pvedamageguard.admin` perm.
- **Codefling listing** at $15-20 one-time unlimited-server. Free uMod listing stays at the GitHub release. Codefling buyers get:
  - Curated download (signed releases, no need to scrape GitHub)
  - Priority support in a dedicated Discord channel
  - Early-access patches the same Thursday as the forced wipe
- **Battle-tested promise** - three consecutive monthly forced wipes survived without breakage between v1.2 (May 2026) and v2.0 (September 2026). If this promise fails, the v2.0 listing is held until it's met.
- **Marketing assets** - listing copy, 3-5 screenshots of CUI panel and `/pdg test` output, a 60-second feature-tour GIF, one-page feature comparison vs DamageControl and TruePVE+DynamicPVP+ReflectDamage stack.

## Post-2.0 (no fixed dates)

These are real features but not gating for marketplace launch. They land when there's time, in response to user requests, or when an interesting third-party PR arrives.

### v2.1 - Stats & observability
- **`/pdg stats <player>`** - per-player damage-dealt, damage-reflected, damage-taken-from-NPCs, damage-taken-from-PvP counters.
- **`/pdg stats top [N]`** - leaderboards: top damage dealers, top reflectors, top NPC slayers.
- **Stats persistence** to `oxide/data/PVEDamageGuard/stats.json` with periodic writes.
- **`API_GetPlayerStats(BasePlayer)`** for integration with stat plugins (PlayerStats, ServerInfo, Discord stat bots).

### v2.2 - Custom NPC category registration API
- **`API_RegisterCategory(string name, Func<BaseEntity, bool> matcher)`** - lets other plugins register custom subtype names backed by their own classification logic (e.g. a Frontier mod registering `FrontierBandit` for its custom NPCs).
- **Composition**: registered matchers run after built-in `ClassifySubtype` checks; first match wins. Registered names appear in `/pdg test` output and rule matrix lookups.

### v2.3 and beyond - TBD
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
