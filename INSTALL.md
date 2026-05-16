# Installation and Configuration Guide

This is the comprehensive install + configure document for PVEDamageGuard. If you just want the three-command quick install, jump to [Quick start](#quick-start). If you're migrating from Damage Control, jump to [Migrating from Damage Control](#migrating-from-damage-control). Otherwise read top to bottom.

For specific topics after install: [Configuration reference](docs/configuration.md), [Rule matrix guide](docs/rule-matrix.md), [Commands](docs/commands.md), [API reference](docs/api.md), [Performance](docs/performance.md), [Carbon support](docs/carbon.md), [CUI panel](docs/cui.md), and per-plugin integration recipes in [docs/integrations/](docs/integrations/).

---

## Table of contents

1. [Quick start](#quick-start)
2. [Prerequisites](#prerequisites)
3. [Detailed installation](#detailed-installation)
4. [Verifying it loaded](#verifying-it-loaded)
5. [First-time configuration](#first-time-configuration)
6. [Picking a preset](#picking-a-preset)
7. [Migrating from Damage Control](#migrating-from-damage-control)
8. [Integration setup](#integration-setup)
9. [Common admin tasks](#common-admin-tasks)
10. [Updating](#updating)
11. [Troubleshooting](#troubleshooting)
12. [Uninstalling](#uninstalling)

---

## Quick start

For admins who already know what they're doing:

```bash
# 1. Drop PVEDamageGuard.cs into oxide/plugins/
# 2. In game console:
oxide.grant user <your-steamid> pvedamageguard.admin

# 3. In chat:
/pdg preset pvereflect      # pick the closest preset, then tune
```

Done. Read the rest of this doc when you want to do anything beyond defaults.

---

## Prerequisites

### Server requirements

- A Rust dedicated server running [Oxide / uMod](https://umod.org/) or [Carbon](https://carbonmod.gg/). Both are supported equally; the plugin uses only standard hooks.
- Server admin access. You'll need at least one of:
  - Filesystem access to `oxide/plugins/` and `oxide/config/`
  - RCON access for `oxide.*` commands
  - In-game admin flag (`ownerid` or `moderatorid` in users.cfg)
- Rust server build no older than the most recent **forced wipe** (first Thursday of each month at 18:00 UTC). Older builds may work but the type-based NPC classifier is tested only against current Rust types.

### Optional but recommended companions

PVEDamageGuard works standalone, but composes with these popular plugins. None are required:

| Plugin | What it adds | Where to get |
|---|---|---|
| [TruePVE](https://umod.org/plugins/true-pve) | Zone-based PvP rule sets. PVEDamageGuard yields PvP allow/block decisions to TruePVE when both are loaded. | uMod (free) |
| [ZoneManager](https://umod.org/plugins/zone-manager) | Lets the rule matrix flip context based on zone flags. | uMod (free) |
| [RaidableBases](https://codefling.com/plugins/raidable-bases) | Auto-detect raid base domes and flip to `InRaidableBaseDome` context. | Codefling (paid) |
| [Convoy](https://codefling.com/plugins/convoy) | Auto-detect convoy events and flip context server-wide. | Codefling (paid) |
| [Armored Train](https://codefling.com/plugins/armored-train) | Same pattern as Convoy. | Codefling (paid) |
| [DynamicPVP](https://umod.org/plugins/dynamic-pvp) | Alternative event-based context provider. Works alongside our built-in EventTracker. | uMod (free) |

### What gets installed where

After the first plugin load, you'll see these files appear:

```
oxide/
├── plugins/
│   └── PVEDamageGuard.cs              # the plugin itself
├── config/
│   └── PVEDamageGuard.json            # auto-generated default config
├── lang/
│   ├── en/PVEDamageGuard.json         # English (default)
│   ├── ru/PVEDamageGuard.json         # Russian
│   ├── es/PVEDamageGuard.json         # Spanish
│   ├── la/PVEDamageGuard.json         # Latin
│   ├── fr/PVEDamageGuard.json         # French
│   ├── de/PVEDamageGuard.json         # German
│   ├── zh/PVEDamageGuard.json         # Chinese Simplified
│   └── pt/PVEDamageGuard.json         # Portuguese (BR)
├── data/
│   └── PVEDamageGuard/
│       └── stats.json                 # per-player damage stats (v1.7+)
└── logs/
    └── PVEDamageGuard/
        └── damage-YYYY-MM-DD.txt      # rotating audit log if LogToFile=true
```

You don't need to create any of these. The plugin sets them up.

---

## Detailed installation

### Step 1: Download the plugin

Three options:

**Option A — Latest release (recommended for production):**
```
https://github.com/gjdunga/PVEDamageGuard/releases/latest/download/PVEDamageGuard.cs
```

**Option B — `main` branch HEAD (recommended for dev/test servers):**
```
https://raw.githubusercontent.com/gjdunga/PVEDamageGuard/main/oxide/plugins/PVEDamageGuard.cs
```

**Option C — uMod listing:** search for "PVE Damage Guard" once published. (For Codefling paid listing, follow the link in your Codefling purchase email after we go live in October 2026.)

### Step 2: Copy the file to your server

Drop `PVEDamageGuard.cs` into `oxide/plugins/` on the Rust server. Oxide watches that directory and will auto-compile and load the plugin within a few seconds.

If you're on Carbon, the plugins directory is `carbon/plugins/`. Same file, different path.

### Step 3: Watch the console

You should see output similar to:

```
[INFO] [PVE Damage Guard] Self-test: 11/11 checks passed.
[INFO] [PVE Damage Guard] PVE Damage Guard v1.7.0 loaded. Reflect=True, NPC->Structure default=0.50x, Features: TOD=False, VictimSub=False, BuildingGrade=False, PerAttackerStruct=False, RuleMatrix=False, Logging=None, YieldToTruePVE=False
```

If you see warnings or errors, jump to [Troubleshooting](#troubleshooting).

### Step 4: Grant admin permission

PVEDamageGuard has two permissions:

- **`pvedamageguard.admin`** — chat command access. Without this, `/pdg` reports "no permission" to non-server-admins.
- **`pvedamageguard.bypass`** — damage immunity for testing. The holder takes full vanilla damage from every source (PVE rules are skipped for them). Use only for staged testing on a dev server.

Grant the admin permission to your moderators:

```
oxide.grant user 76561198000000000 pvedamageguard.admin
oxide.grant group admin pvedamageguard.admin
oxide.grant group moderator pvedamageguard.admin
```

Server admin flag (`ownerid`/`moderatorid`) implicitly grants `pvedamageguard.admin`. You don't need to also grant the explicit permission for server owners.

### Step 5: Open the in-game admin panel

In game, as an admin:

```
/pdg ui
```

The CUI panel opens with five tabs (Status, Logging, History, Rules, Scaling). Click around to verify it renders correctly. Close with the X button or `/pdg close`.

---

## Verifying it loaded

After install, run these in order:

```
oxide.plugins
```
You should see `PVE Damage Guard (1.7.0) by Gabriel Dungan (DunganSoft Technologies)` (or your version).

```
/pdg
```
Should print the status block. If you get "no permission", recheck step 4 above.

```
/pdg selftest
```
Re-runs the type-resolution test. Should report `Self-test: 11/11 checks passed.` On older Rust builds it may soft-fail TOD_Sky; that's fine.

```
/pdg test
```
Aim at any entity (player, scientist, animal, building) and run this in chat. Should print classification, subtype, distance, and the rules that would apply.

```
/pdg events
```
Lists currently tracked events. Empty initially.

```
/pdg ui
```
Opens the CUI panel. Click Status — should mirror what `/pdg` printed.

If any of these fail, see [Troubleshooting](#troubleshooting).

---

## First-time configuration

PVEDamageGuard ships with safe defaults. On first load it writes `oxide/config/PVEDamageGuard.json` with the entire config tree at default values. Out of the box behavior:

- PvP damage **reflects** at 1.0x back to the attacker
- NPCs deal **50% damage to players** by default, **25% for bullets**
- NPCs deal **50% damage to structures**
- All time-of-day modifiers are 1.0x (effectively off)
- Per-victim subtype scaling is all 1.0x (off)
- Building grade multipliers are all 1.0x (off)
- Rule matrix is **disabled** (the case-based scaling logic from v1.0 handles everything)
- TruePVE companion mode is **on** if TruePVE is loaded
- Logging is **None**, file logging **off**
- Discord webhook **off** (no URL set)

For a strict PVE server with PvP reflect, these defaults are very close to what you want. For other server types, see [Picking a preset](#picking-a-preset).

To open the config in an editor:

```
nano oxide/config/PVEDamageGuard.json
```

(or use any text editor — it's plain JSON.)

After editing, apply changes with either:

```
oxide.reload PVEDamageGuard
```

or in chat:

```
/pdg reload
```

The config is validated on every reload. Errors print to console as `[Warning] PVE Damage Guard` lines and surface as `Config issues: N` in the `/pdg` status block. Common validation issues:

- Multiplier out of `[0, 100]` range
- TOD array with wrong length (must be exactly 24 elements)
- Rule matrix `Inherits` cycle
- Provider target context name doesn't exist in `Contexts`
- Unknown `DamageType` value

Run `/pdg validate` any time to see the full list of current issues.

---

## Picking a preset

PVEDamageGuard ships four named presets that overwrite your config with a known-good complete snapshot. Pick the closest to your server style and iterate from there.

```
/pdg preset <name>
```

| Preset | Use case |
|---|---|
| **`pvepure`** | Hardcore PVE. All PvP blocked. NPCs deal 25% damage to players (10% for bullets). NPCs cannot damage structures at all. No time-of-day variation. |
| **`pvereflect`** | Standard PVE with consequences. PvP reflects at 1.0x. NPCs deal 50% damage to players (25% for bullets). NPCs deal 50% damage to structures. |
| **`pvevehicleraids`** | PVE but heli/Bradley events still meaningful. PvP reflects. NPCs cannot damage structures **except** patrol helicopter and Bradley (full damage). |
| **`pvphoursevents`** | Hybrid. PvP blocked by default. **Rule matrix enabled.** PvP allowed only when player is within 200m of a Bradley, patrol heli, or cargo ship event. |

The preset overwrites your existing tuning. If you've spent hours fine-tuning multipliers, **don't** apply a preset on top — it will replace everything.

Recommended workflow:
1. Apply the preset closest to your goal
2. Read the resulting `oxide/config/PVEDamageGuard.json`
3. Use `/pdg scale <type> <mult>` to tune per-damage-type values without editing the file
4. Edit the JSON directly for everything else
5. `/pdg reload` after JSON edits

---

## Migrating from Damage Control

If you're moving from the legacy [Damage Control](https://umod.org/plugins/damage-control) plugin (Wulf / MSpeedie v2.5.14 or similar), use the built-in importer.

### Pre-migration

1. Stop the server gracefully or take a known-good backup of `oxide/config/DamageControl.json`.
2. Install PVEDamageGuard per the steps above. **Both plugins can coexist briefly** — PVEDamageGuard will print a `[ERROR]` line at load saying Damage Control is also loaded and the two will conflict. That's expected during migration.

### Run the import

```
/pdg import damagecontrol
```

What happens:
1. PVEDamageGuard reads `oxide/config/DamageControl.json`.
2. It backs up your **current** PVEDamageGuard config to `oxide/config/PVEDamageGuard.backup.YYYYMMDDHHMMSS.json` (you can revert if you don't like the import).
3. It maps every supported field:

   | Damage Control | PVEDamageGuard |
   |---|---|
   | `APC_Multipliers`, `Heli_Multipliers`, `Bear_Multipliers`, etc. | `PerVictimSubtypeScaling[BradleyAPC / PatrolHelicopter / Bear / ...]` |
   | `BuildingBlock_Multipliers` | averaged into `NpcToStructureScaling` |
   | `Building_Grade_Multipliers` | `BuildingGradeMultipliers` |
   | `Bypasses.Heli_bypass = true` | `PerAttackerStructureScaling["PatrolHelicopter"] = 1.0` |
   | `Time.Time_Type` | `TimeOfDaySource` |
   | `Global_Time_Multipliers` | `TimeOfDayMultipliers["Global"]` |
   | `Player_Time_Multipliers` | `TimeOfDayMultipliers["PvP"]` (closest match) |
   | `NPC_Time_Multipliers` | `TimeOfDayMultipliers["NpcToPlayer"]` |
   | `Building_Time_Multipliers` | `TimeOfDayMultipliers["NpcToStructure"]` |

4. Fields with no direct equivalent (per-piece building protection, separate Animal/Heli/Bradley/Other time multipliers) are **skipped with explicit reports** referencing the migration mapping table.
5. Prints a summary line: `Damage Control import complete. Imported: N, skipped: M.`

### Post-migration

Once the import succeeds:

```
oxide.unload DamageControl
```

Move `DamageControl.cs` out of `oxide/plugins/` so it doesn't auto-load on restart.

The error line from PVEDamageGuard about Damage Control being loaded will clear automatically (DetectCompanions reacts to the unload).

Verify your imported config:

```
/pdg validate    # confirm no validation errors
/pdg             # confirm the feature flags reflect what you imported
/pdg test fire Bullet 100   # aim at a scientist, dry-run a bullet hit, confirm the number is what you expect
```

If something's off, you can revert by deleting `PVEDamageGuard.json` and restoring `PVEDamageGuard.backup.<timestamp>.json` to that name.

---

## Integration setup

PVEDamageGuard auto-detects the popular companion plugins via `[PluginReference]`. You don't need to do anything if you just have them installed; they'll work. This section is for **wiring** them into the rule matrix so contexts switch automatically.

### TruePVE

If TruePVE is loaded when PVEDamageGuard starts, you'll see:

```
[INFO] [PVE Damage Guard] TruePVE detected. Yielding allow/block to TruePVE; PVEDamageGuard will only classify, scale, and reflect-on-request.
```

In this mode:
- TruePVE decides whether PvP is allowed (via its own ruleset and zones)
- PVEDamageGuard layers per-damage-type scaling on top
- PVEDamageGuard does NOT reflect by default (set `PvP - Reflect damage to shooter` to `false` to be explicit, or leave TruePVE's reflect plugin handling it)

To opt out of this companion behavior (run both as full damage handlers): set `Yield allow/block decisions to TruePVE if it is loaded` to `false` in config. Test carefully — both plugins hooking `OnEntityTakeDamage` can produce surprising results.

### ZoneManager

For per-zone rule overrides, enable the rule matrix and configure the ZoneManager provider:

```jsonc
"RuleMatrix": {
  "Enabled": true,
  "ContextProviders": {
    "ZoneManager": {
      "Enabled": true,
      "ZoneFlagToContext": {
        "pvp": "AtPvpEvent",
        "arena": "ArenaZone",
        "raid_zone": "InRaidableBaseDome"
      }
    }
  }
}
```

Then map your ZoneManager zones to have those flags. PVEDamageGuard will check victim position against ZoneManager zones on every hit and flip context accordingly.

### RaidableBases

Auto-detected. With the rule matrix enabled, ensures the `InRaidableBaseDome` context exists. PVEDamageGuard captures `OnRaidableBaseStarted/Ended` hooks and tracks dome positions automatically.

Verify in game by walking into an active raid dome and running `/pdg context` — should report `InRaidableBaseDome`.

See [docs/integrations/raidablebases.md](docs/integrations/raidablebases.md) for the full recipe.

### Convoy and Armored Train

Auto-detected. PVEDamageGuard listens for `OnConvoyStart/Stop` and `OnTrainEventStart/Stop` (and `OnArmoredTrainEventStart/Stop` for newer versions). When any event is active, the rule matrix flips to `AtPvpEvent` context server-wide.

To customize per-event contexts (Bradley vs heli vs convoy each having different rules), use the v1.6 `PerEventContext` dict:

```jsonc
"GlobalEventTriggers": {
  "Enabled": true,
  "TriggerContext": "AtPvpEvent",
  "Events": ["Convoy", "ArmoredTrain"],
  "PerEventContext": {
    "Convoy": "AtConvoyEvent",
    "ArmoredTrain": "AtTrainEvent"
  }
}
```

See [docs/integrations/convoy.md](docs/integrations/convoy.md) and [docs/integrations/armored-train.md](docs/integrations/armored-train.md).

### Discord webhooks

For moderation notifications (every reflect, every block):

1. In Discord, create a webhook: Server Settings → Integrations → Webhooks → New Webhook. Copy the URL.
2. Edit `oxide/config/PVEDamageGuard.json`:

   ```jsonc
   "DiscordWebhook": {
     "Enabled": true,
     "Url": "https://discord.com/api/webhooks/...",
     "MinLevel": "Reflects",
     "RateLimitPerMinute": 20,
     "MessagePrefix": "[YourServerName] ",
     "Username": "PVE Damage Guard",
     "AvatarUrl": ""
   }
   ```

3. `/pdg reload`
4. `/pdg webhook test` — should post a test message to your Discord channel.

See [docs/integrations/discord-webhooks.md](docs/integrations/discord-webhooks.md).

---

## Common admin tasks

### Adjust NPC damage at runtime

```
/pdg scale Bullet 0.1        # NPCs do 10% bullet damage to players
/pdg scale Explosion 0       # NPCs cannot hurt players with explosions
/pdg scale Default 0.5       # fallback for any damage type not explicitly set
```

Changes persist to config immediately. No reload needed.

### Toggle logging while debugging

```
/pdg log Scaled              # log every damage event we modify
/pdg log Reflects            # only reflects
/pdg log None                # silence
/pdg logfile on              # also write to oxide/logs/PVEDamageGuard/damage-YYYY-MM-DD.txt
```

### Watch live damage in the panel

```
/pdg ui                      # open panel
# click Logging tab
# set level filter to All or Scaled
# damage events stream in live (refreshes every 2s)
```

### Browse recent damage history

```
/pdg history 20              # last 20 hits in chat
# OR
/pdg ui                      # open panel
# click History tab, page through with prev/next
```

### Verify a specific entity classification

```
/pdg test                    # aim at any entity, run /pdg test
# reports: classification, subtype, current context, applicable rules

/pdg test fire Bullet 100    # dry-run a 100-bullet hit, see final damage
```

### Check active events

```
/pdg events
# lists tracked Bradley/Heli/Cargo entities, RaidableBases domes, Convoy/Train globals
```

### Per-player damage stats

```
/pdg stats                   # your stats
/pdg stats <other_player>    # another player's stats (admin only)
```

Or use `API_GetPlayerStats(BasePlayer)` from another plugin.

---

## Updating

PVEDamageGuard targets **same-week patches** after every monthly forced wipe (first Thursday of each month). Update procedure:

1. **Watch for the new release.** Check [github.com/gjdunga/PVEDamageGuard/releases](https://github.com/gjdunga/PVEDamageGuard/releases). Codefling buyers get a forced-update push post-v2.0.
2. **Read the CHANGELOG.** [CHANGELOG.md](CHANGELOG.md) describes what changed and any migration notes. No breaking changes have shipped from v1.0 through v1.7; check the latest release notes for the version you're going to.
3. **Replace the file:**
   ```bash
   cp PVEDamageGuard.cs oxide/plugins/
   ```
4. **Reload:**
   ```
   oxide.reload PVEDamageGuard
   ```
   Or in chat: `/pdg reload`.
5. **Verify:** `/pdg selftest` should pass, `/pdg validate` should be clean.

### Backwards compatibility commitment

API methods (everything prefixed `API_`) are stable from v1.0. Breaking changes require a major version bump and one major-version cycle of deprecation warnings. Config schemas are additive within minor versions — new fields appear with safe defaults; old fields are not removed.

### Wipe-day procedure

On forced-wipe day (first Thursday of each month):

1. Server wipes per Facepunch's schedule
2. If a Rust update broke something, PVEDamageGuard releases a patch within 48 hours (often same-day)
3. Update the plugin file and reload
4. `/pdg selftest` verifies type resolution; if anything broke, it'll be reported as an error

Because PVEDamageGuard uses type-based NPC classification (not prefab name strings), most wipes do not require any patch.

---

## Troubleshooting

### Plugin won't load

**Symptom:** `[ERROR]` line during startup, plugin doesn't appear in `oxide.plugins`.

**Common causes:**
- Outdated Oxide version. Update Oxide.
- Outdated Rust server build. Update via SteamCMD.
- A type that PVEDamageGuard depends on was removed/renamed in a recent Rust update. Check `/pdg selftest` output — failed checks will be named explicitly.
- File permissions. The plugin file needs read access for the server process.

### `/pdg` says "no permission"

You don't have the `pvedamageguard.admin` permission. Either:
- Grant it: `oxide.grant user <your-steamid> pvedamageguard.admin`
- Add yourself to server admin: edit `oxide.users` or use `ownerid`/`moderatorid` cvars

### Config validation errors at load

Run `/pdg validate` to see the full list. Most common:

- **`NpcToPlayerScaling['Bullet']: 150.0 out of [0, 100]`** — you typed 150 meaning 150%, but the field is a multiplier. Use `1.5` for 150%.
- **`RuleMatrix.Contexts[X]: Inherits target 'Y' does not exist`** — typo in the Inherits field. Check `Contexts` for the correct name.
- **`TimeOfDayMultipliers[Global]: must have 24 elements (got 23)`** — the array got truncated. Restore it to 24 elements.
- **`EnvironmentalDamageTypes: 'XYZ' is not a valid DamageType`** — typo in damage type name. Valid names are the Rust `DamageType` enum (Bullet, Slash, Stab, etc.).

### CUI panel doesn't open

- Check `/pdg ui` requires `pvedamageguard.admin`. Server console says "must be run by an in-game player" if you ran it from console/RCON; that's expected, the CUI only renders for in-game players.
- If panel doesn't appear but no error: try `/pdg close` then `/pdg ui` again. Stuck panels are rare but happen.
- Check console for `[Error]` lines from PVEDamageGuard. CUI rendering errors usually print there.

### Damage is not being scaled as expected

Run `/pdg test fire <DamageType> <amount>` aimed at the entity in question. The output shows:
- Classification (RealPlayer / HumanNpc / etc.)
- Subtype (if any)
- Active context (if rule matrix enabled)
- Final scaled damage

If the number is wrong, the breakdown shows you which multiplier layer is responsible. Most common: a per-victim subtype scaling you forgot was set, or a TOD multiplier active right now.

Also check the Logging tab in `/pdg ui` with filter set to `Scaled` — every modified hit appears live so you can correlate damage events to the rules that fired.

### Damage Control is also loaded

`[ERROR] [PVE Damage Guard] Damage Control (legacy) is loaded alongside PVEDamageGuard...` — this is the expected warning. Unload:

```
oxide.unload DamageControl
```

And move `DamageControl.cs` out of `oxide/plugins/` so it doesn't auto-load on next restart.

### Other plugins conflict

Known plugins that hook `OnEntityTakeDamage`:
- TruePVE — handled in companion mode
- PVEMode — load-time warning, test carefully
- NextGenPVE — load-time warning, test carefully
- ReflectDamage — overlaps with our PvP reflect. Disable one.
- PunishAttacker — overlaps with our PvP reflect. Disable one or have it call our `API_ReflectDamage`.

### Discord webhook 429 errors

You exceeded Discord's 30 messages/minute cap. Lower `RateLimitPerMinute` in config, or raise `MinLevel` to reduce the volume.

### Stats file is empty

`oxide/data/PVEDamageGuard/stats.json` is written every 60 seconds and on plugin unload. If you just installed v1.7+, it may not exist yet — wait a minute, or `/pdg reload` to force a save.

### Cache hit rate is low

`/pdg cache` shows current entries. If you see frequent flushes (cache hits the 10000-entry cap), your server has high entity churn. Either raise the cap (requires source edit) or ignore — the performance impact is minor.

### Hook timing is high

`/pdg timing on` then play through some combat, then `/pdg timing`. If mean/p95 is much higher than the target (50us/120us for 100-player), check:
- Trace logging on? Drop to Reflects or Scaled.
- Many other plugins hooking damage? Check `oxide.plugins`.
- Validate config: `/pdg validate` — exception-throwing config issues add per-hit overhead.

---

## Uninstalling

### Soft uninstall (keep config)

```
oxide.unload PVEDamageGuard
```

Move `PVEDamageGuard.cs` out of `oxide/plugins/` to prevent auto-reload on restart.

Config, lang files, data files, and logs are left in place. If you reinstall later, your configuration is preserved.

### Hard uninstall (remove everything)

After soft uninstall:

```bash
rm oxide/config/PVEDamageGuard.json
rm oxide/config/PVEDamageGuard.backup.*.json
rm -rf oxide/lang/*/PVEDamageGuard.json
rm -rf oxide/data/PVEDamageGuard/
rm -rf oxide/logs/PVEDamageGuard/
```

(On Windows, use `del` and `rd /s` instead.)

Permissions granted via `oxide.grant` persist independently; revoke them if you want a clean slate:

```
oxide.revoke group admin pvedamageguard.admin
oxide.revoke user 76561198000000000 pvedamageguard.admin
oxide.revoke user 76561198000000000 pvedamageguard.bypass
```

---

## Where to get help

- **Issues / bug reports:** [github.com/gjdunga/PVEDamageGuard/issues](https://github.com/gjdunga/PVEDamageGuard/issues)
- **Feature requests:** open a GitHub issue with the `enhancement` label
- **Documentation:** browse [docs/](docs/) for topic-specific guides
- **Codefling support channel** (post-v2.0): linked from the Codefling listing

---

## License

GPL-3.0. See [LICENSE](LICENSE) for full terms. Free to use, modify, and redistribute under GPL-3.0.
