# Marketplace Listing Copy

Ready-to-paste content for the Codefling listing. Adjust as needed before submission.

---

## Title

**PVE Damage Guard**

## Short tagline (under 100 chars)

The future-proof PVE damage handler. Type-based NPC classifier, rule matrix, reflect-as-a-service, CUI admin panel.

## Category tags

`pve` `damage` `npc` `classifier` `reflect` `cui` `rule-matrix` `truepve-companion` `discord-webhook`

## Suggested price

**$15.00 USD one-time, unlimited-server.**

Rationale (don't paste this into the listing; it's reasoning for your own pricing decision):
- Codefling comparable plugins: SimplePVE ($15), PunishAttacker ($15), Customizable Protection ($20), War Mode ($24.99), Real PvE ($24.99)
- Per the May 2026 market research, $15 is the credibility floor for non-trivial PVE plugins; $5 reads as "toy" and undercuts perceived value
- Codefling convention is one-time unlimited-server tied to account; never per-server or subscription

---

## Long description (paste into Codefling description field)

**The PVE damage plugin that doesn't break every wipe.**

Every Rust PVE plugin on the market — DamageControl, TruePVE, NextGenPVE, SimplePVE — classifies NPCs by matching prefab name substrings like `"scientist"` or `"human"`. Every time Facepunch ships a new NPC subclass (HumanNPCNew, vendor guard variants, frontier bandits, future types), those checks silently break and players take vanilla damage from NPCs the plugin no longer recognizes.

PVE Damage Guard classifies NPCs by **base type** (`BasePlayer.IsNpc`, `BaseNpc`, `NPCPlayer`, `BaseHelicopter`, `BradleyAPC`). New subclasses are caught automatically because they inherit from these bases. The classifier is the technical moat: it does not require a patch for most monthly forced wipes, while the competition does.

### What you get

**The classifier**
- Future-proof type-based NPC detection that catches every Facepunch NPC variant including ones that don't exist yet
- Per-attacker, per-damage-type damage scaling (NPC bullets at 0.25x, slash at 0.5x, etc.)
- Per-victim subtype scaling (Bear, Wolf, Heli, Bradley, Minicopter, Barrel, individually tunable)
- Building grade multipliers (Twigs / Wood / Stone / Metal / TopTier)
- Time-of-day modifiers (24-hour arrays for Global / PvP / NpcToPlayer / NpcToStructure)

**The rule matrix**
- Declarative `(Attacker -> Victim) -> Action` rule sets per context
- Actions: `allow`, `block`, `reflect:N`, `scale:N`, `scale:{type:N,...}`
- Context inheritance with cycle detection and validation
- 9-tier precedence lookup (subtype-to-subtype most specific, wildcards supported)

**Context auto-switching**
- ZoneManager integration: zone flags map to context names
- RaidableBases dome proximity detection
- Built-in event tracker for Bradley / Helicopter / Cargo Ship
- Convoy and Armored Train server-wide flags
- Per-event context overrides

**The reflect system**
- PvP reflect with re-entrancy guard and configurable multiplier
- **Foreign-structure reflect**: damage to other players' bases bounces back to the griefer (own/team/TC-authorized structures unaffected)
- Trap-owner attribution (auto-turret kills reflect to the turret's owner)
- Reflect-as-a-service API for other plugins to call safely

**In-game CUI admin panel**
- `/pdg ui` opens a tabbed panel: Status, Logging, History, Rules, Scaling
- Live-streaming Logging tab with level filters
- Paginated History tab (last 100 hits)
- Read-only Rules browser with inheritance visualization and color-coded actions
- Edit-mode for Rules: per-rule action cycle and delete
- Scaling tab with multiplier sliders, boolean toggles, log-level and TOD-source dropdowns

**Onboarding tools**
- `/pdg import damagecontrol` — one-shot migration from Damage Control 2.5.x with backup
- Four ready-to-use presets: `pvepure`, `pvereflect`, `pvevehicleraids`, `pvphoursevents`
- Automatic config validation at load with explicit per-issue reporting
- Interactive `/pdg help [subcommand]` system

**Discord webhook output**
- Configurable URL, minimum log level, per-minute rate limit (Discord cap honored)
- Username and avatar overrides
- Token-bucket rate limiter
- Async via Oxide webrequest (non-blocking)

**Per-player stats**
- Damage dealt / taken / reflected / NPC kills / PvP deaths per player
- Persists to `oxide/data/PVEDamageGuard/stats.json`
- `API_GetPlayerStats(BasePlayer)` for stat plugin integration
- `/pdg stats [player]` chat command

**Public API for ecosystem integration**
- `API_Classify(BaseEntity) -> string` — entity category lookup
- `API_ClassifySubtype(BaseEntity) -> string` — fine-grained subtype
- `API_IsNpcAttacker(HitInfo) -> bool`
- `API_ReflectDamage(BasePlayer attacker, BasePlayer victim, HitInfo info, float multiplier) -> bool`
- `API_GetActiveContext(Vector3 pos) -> string`
- `API_IsPvpAt(Vector3 pos) -> bool`
- `API_IsAllowed(BaseEntity attacker, BaseEntity victim) -> bool`
- `API_GetPlayerStats(BasePlayer) -> Dictionary`
- `API_RegisterCategory(string name, Func<BaseEntity, bool> matcher)` — extend the classifier from your plugin
- `API_IsPveDeath(BasePlayer victim) -> bool` — Backpacks-on-death integration hook

**Diagnostics that nobody else ships**
- `/pdg test` — aim at any entity, see classification + active rule + final damage breakdown
- `/pdg test fire <DamageType> <amount>` — dry-run a synthetic hit through the full modifier stack
- `/pdg validate` — on-demand config validator with detailed issue list
- `/pdg events` — list all currently-tracked events (entity / dome / global)
- `/pdg timing` — Stopwatch-based hook timing telemetry (mean / p95 / max in microseconds)
- `/pdg selftest` — type-resolution self-test against current Rust build
- `/pdg cache` — inspect / flush the per-entity classification cache

**Ecosystem integrations** (auto-detected via [PluginReference])
- TruePVE (companion mode: yield allow/block to TruePVE, layer scaling on top)
- ZoneManager (per-zone context switching)
- RaidableBases (dome detection and PvP carve-out)
- Convoy / Armored Train (server-wide event flags)
- Backpacks / Backpacks 4 (PVE-death detection so reflect-kills don't drop loot)
- Compatible with: PunishAttacker, ReflectDamage, NextGenPVE, PVEMode (with load-time warnings)

### Framework support

- Oxide / uMod (primary, tested every wipe)
- Carbon (declared compatible; uses only standard Oxide hooks)

### Languages

English, Russian, Spanish, Latin, French, German, Chinese Simplified, Portuguese (Brazil). Translations welcome via GitHub PR.

### The promise

PVE Damage Guard targets **same-week patches** after every monthly forced wipe (first Thursday of each month). For critical bugs (data loss, server crash, security), the SLO is < 48 hours from report to release. Because classification is type-based rather than prefab-string-based, **most wipes do not require a patch at all** — the plugin keeps working through Facepunch's content additions automatically.

### What you don't get (and won't)

Honest about scope:
- This is a damage handler. It does not implement zones, raid base spawning, convoy events, or backpack-on-death itself. It integrates cleanly with the plugins that do.
- It does not replace TruePVE. It coexists with TruePVE in companion mode (TruePVE handles allow/block, we layer scaling on top).
- No subscription pricing, no per-server licensing, no feature-gated edition. GPL-3.0 license, one-time purchase, unlimited servers, free upgrades for life of the major version.

### Compatibility

- **Game**: Rust dedicated server, current build (forced-wipe-tested through the most recent first Thursday).
- **Framework**: Oxide / uMod 2.x or Carbon current release.
- **License**: GPL-3.0. Free to fork and modify under GPL terms. Purchase here = convenience (curated downloads, priority support, Discord channel).

### What this costs you

$15 one-time. Unlimited servers tied to your Codefling account. Free upgrades for the entire v2.x lifetime. Major version bumps (v3.0+) may require a separate purchase at that time.

### Free alternative

The plugin is fully open source on [GitHub](https://github.com/gjdunga/PVEDamageGuard). You can clone, build, and run it for free. The Codefling purchase exists for admins who want:
- Curated download (no scraping the GitHub release page)
- Priority support in the Codefling support channel
- Early-access patches the same Thursday as the forced wipe
- A direct line to the maintainer for feature requests

If you're comfortable doing your own GitHub-watching, go that route — it's the same plugin.

---

## Screenshots section (Codefling expects 5-8 images)

See [docs/marketing/screenshots.md](docs/marketing/screenshots.md) for the capture guide. Recommended set:

1. CUI admin panel with Status tab open showing live config state
2. CUI Logging tab with live damage events streaming (color-coded)
3. CUI History tab paginated
4. CUI Rules tab with inheritance chain visible and color-coded actions
5. CUI Rules tab in edit mode with cycle/del buttons visible
6. CUI Scaling tab with multiplier rows and toggles
7. `/pdg test` output in chat aimed at a scientist
8. Damage Control side-by-side comparison: vanilla NPC hit at 47 dmg, with PVEDamageGuard at 11.75 dmg (showing the actual scaling working)

---

## Video / GIF

See [docs/marketing/feature-tour.md](docs/marketing/feature-tour.md) for the 60-90 second tour script. Capture with OBS Studio or LICEcap, compress to under 5 MB for Codefling upload.

---

## Listing metadata

- **Plugin version**: 2.0.0
- **Last update**: 2026-05-16 (verify date at submission time)
- **Game**: Rust
- **Framework**: Oxide / uMod (Carbon-compatible)
- **License**: GPL-3.0
- **Source**: https://github.com/gjdunga/PVEDamageGuard
- **Documentation**: https://github.com/gjdunga/PVEDamageGuard/blob/main/INSTALL.md
- **Support**: https://github.com/gjdunga/PVEDamageGuard/issues

## Support promise on the listing

> **Maintenance commitment**: same-week patches after Rust's first-Thursday forced wipe. Critical bugs (data loss / crash / security): < 48 hours. Open issues at github.com/gjdunga/PVEDamageGuard/issues. Codefling buyers get priority Discord support.

## Refund policy

> All sales final per Codefling default. If the plugin fails to load on your supported server (Oxide 2.x or Carbon current), open a GitHub issue within 7 days and I'll either fix it or refund.

---

## Pre-submission checklist

Before clicking submit on Codefling:

- [ ] Replace `gjdunga` in URLs if your Codefling account uses a different handle
- [ ] Capture all 8 screenshots per `docs/marketing/screenshots.md`
- [ ] Record and compress the 60-90s feature tour per `docs/marketing/feature-tour.md`
- [ ] Verify v2.0.0 is the latest GitHub release and `PVEDamageGuard.cs` is attached as an asset
- [ ] Confirm Discord support channel is set up and the invite link is ready
- [ ] Set up your Codefling support response routine (target: respond to threads within 48h)
- [ ] Double-check the listing price and currency match your intent ($15 USD)
- [ ] Tag the listing with all relevant categories (pve, damage, npc, etc.)

## Post-launch follow-up

First 30 days after launch:
- Monitor Codefling support thread daily; reply within 24h
- Track installs and any installer feedback
- Watch GitHub issues for bug reports from new buyers
- Note any feature requests for v2.1 (leaderboards) or post-2.0 backlog

Months 2-3:
- Aim for first review milestone (5+ ratings)
- Iterate on documentation gaps surfaced by support questions
- Plan v2.1 leaderboard release if there's demand
