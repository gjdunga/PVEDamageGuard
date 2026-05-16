# Roadmap

What is planned for future PVEDamageGuard versions. Nothing here is a commitment; priorities shift based on user feedback and Facepunch's monthly forced wipes.

## v1.1 (shipped 2026-05-16)

Done in v1.1.0:
- Time-of-day damage modifiers (Global / PvP / NpcToPlayer / NpcToStructure, 24-element hourly arrays, Game or Real source)
- Per-victim subtype scaling (Bear, Wolf, Heli, Bradley, Minicopter, etc.)
- Building grade multipliers (Twigs/Wood/Stone/Metal/TopTier)
- Per-attacker structure scaling (replaces Damage Control's `Heli_bypass`)
- `/pdg test` expanded with subtype + hour + composed-multiplier diagnostics
- `/pdg hour` command
- `API_ClassifySubtype` and `API_GetCurrentHour` public hooks

Deferred to v1.2:

- **Rule-matrix configuration mode (opt-in).** Optional `RuleMatrix` config block that lets admins write `(AttackerCategory x VictimCategory x Context) -> Action` rules declaratively, similar to TruePVE rulesets. Defaults remain the current per-attacker scaling model so existing configs are not broken.
- **Context providers**: ZoneManager integration for per-zone rule overrides; event tracker that listens to `OnEntitySpawned` / `OnEntityKill` for Bradley/Heli/Cargo/Convoy/Armored Train and flips context automatically.
- **`/pdg test fire <type> <amount>`**: simulate an actual hit (not just classification) to confirm final damage values without needing to swing a weapon.
- **`/pdg history`**: ring buffer of the last N classified hits, queryable in-game for quick debugging.

## v1.3

- **RaidableBases integration**: detect being inside a RaidableBases dome, flip context to allow PvP and full building damage automatically.
- **Discord webhook output for `Reflects` and higher log levels** (admin moderation channel).
- **Per-player damage-dealt and damage-reflected counters**, surfaced via `/pdg stats <player>` and exported via API for stat plugins.

## v2.0 (major)

- **In-game CUI admin panel** for editing the rule matrix and scaling table without leaving Rust. Slash commands remain canonical; CUI is layered convenience for non-technical admins.
- **Carbon framework first-class support** (currently works via Oxide compatibility shim but untested under load).
- **Public NuGet-style "category registration" API** so third-party plugins can register custom entity categories (e.g., a Frontier mod registering its custom NPCs into a `FrontierBandit` category).

## Won't do

- **Become a full TruePVE replacement.** TruePVE is excellent at allow/block rule sets with zones and we explicitly cede that ground. PVEDamageGuard's value is classification, scaling, reflect-as-service, and diagnostic tooling on top of whatever PVE plugin the admin is using.
- **Per-server license model.** Codefling and Lone.Design convention is one-time unlimited-server licenses tied to an account. We will follow that convention.
- **Web admin panel.** Out of scope for a plugin; out-of-game admin is what RCON and config files are for.

## Maintenance promise

Forced wipe occurs the first Thursday of every month. PVEDamageGuard targets same-week patches for any Facepunch breaking change. Because classification is type-based rather than prefab-string-based, most wipes will not require a patch. When they do, the goal is < 48 hours from the broken wipe to a fixed release.
