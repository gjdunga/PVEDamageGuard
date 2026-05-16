# Commands

All commands are subcommands of `/pdg`. Server console can run them as `pdg <args>` (no slash).

Access requires either:
- the `pvedamageguard.admin` permission, or
- server admin flag (`IsAdmin`), or
- server console (always allowed)

## /pdg

No arguments: prints current config state and command list.

```
PVE Damage Guard v1.0.0
  Reflect: True (1.00x)
  Block-PvP-if-not-reflecting: True
  Teammate damage allowed: False
  NPC->Player default mult: 0.50
  NPC->Structure mult: 0.50
  Traps treated as PvP: True
  Logging: None (file=False)
  Yield to TruePVE: True
  Commands: /pdg reload | log <lvl> | logfile <on|off> | scale <type> <mult> | test
```

## /pdg reload

Re-reads `oxide/config/PVEDamageGuard.json` and lang files. Use after editing config by hand. Equivalent to `oxide.reload PVEDamageGuard` but faster from in-game.

## /pdg log &lt;level&gt;

Sets the log verbosity level. Valid values:

| Level | Logs |
|---|---|
| `None` | nothing |
| `Reflects` | only PvP reflect events |
| `Scaled` | every hit PVEDamageGuard modifies (reflects + NPC scaling + structure scaling/blocks) |
| `All` | also passthroughs (env damage, Player->NPC, bypass-perm, teammate-allow) |
| `Trace` | All + full HitInfo dump (Initiator type, Weapon type, HitBone) - very noisy |

Examples:
```
/pdg log Reflects
/pdg log Scaled
/pdg log None
```

No argument: shows the current level.

## /pdg logfile &lt;on|off&gt;

Toggles whether log lines are also written to `oxide/logs/PVEDamageGuard/damage-YYYY-MM-DD.txt`. Oxide rotates these files daily automatically.

Use cases:
- **`on`** for permanent audit trail (recommended for moderation evidence)
- **`off`** for live tuning where you only need console output

## /pdg scale &lt;DamageType&gt; &lt;multiplier&gt;

Sets the NPC -> Player scaling multiplier for a single damage type at runtime, without editing the JSON config. Persists immediately (writes to config file).

Damage type names must match Rust's `DamageType` enum (case-insensitive). Multiplier must be 0 to 100.

Examples:
```
/pdg scale Bullet 0.1     # NPCs deal 10% bullet damage to players
/pdg scale Explosion 0    # players are immune to NPC explosions
/pdg scale Default 1.0    # NPCs deal vanilla damage for any type not otherwise overridden
```

## /pdg hour (v1.1.0)

Reports the current hour from the configured time-of-day source and the four TOD multipliers at that hour. Use to verify TOD rules are loading correctly.

Example:
```
Hour 14 (Game). TOD multipliers: Global=1.00, PvP=1.00, NpcToPlayer=1.00, NpcToStructure=1.00.
```

## /pdg context (v1.2.0)

Reports the rule-matrix context active at your current position. Only meaningful when `RuleMatrix.Enabled=true`.

Example:
```
Active context at your position: 'AtPvpEvent' (default fallback: 'Default'). Active tracked events: 2.
```

## /pdg history [N] (v1.2.0)

Shows the last `N` classified hits (default 10, max 100). The ring buffer is filled regardless of console log level, so this command works even when `/pdg log None` is set.

Example:
```
Showing last 5 of 17 hits (buffer capacity 100):
  [14:32:18] npc->player-scaled HumanNpc(scientistnpc_heavy) -> RealPlayer(player) | Bullet 5.2 -> scaled
  [14:32:20] env-passthrough Environment(<none>) -> RealPlayer(player) | Bleeding 1.0 -> passthrough
  [14:32:25] rule[AtPvpEvent]-allow RealPlayer(player) -> RealPlayer(player) | Bullet 35.0 -> allow ctx=AtPvpEvent
  [14:32:28] npc->structure-0.50x VehicleNpc(patrolhelicopter) -> Building(wall.stone) | Explosion 50.0 -> scale:0.50
  [14:32:30] rule[Default]-reflect RealPlayer(player) -> RealPlayer(player) | Bullet 28.0 -> reflect:1.00 ctx=Default
```

The action column shows what PVEDamageGuard did: `allow`, `block`, `reflect:N`, `scale:N`, `passthrough`, `yield`.

## /pdg test

**The diagnostic command.** Aim at any entity in the world and run `/pdg test`. PVEDamageGuard raycasts from your crosshair, classifies the target, and reports which rule would apply, including all v1.1.0 layered modifiers.

Example output, aiming at a scientist:
```
Target: scientistnpc_full_any (type=ScientistNPC) classified as HumanNpc, subtype=Scientist
Distance: 18.3m
Hour: 14 (Game). Global TOD multiplier: 1.00x
If this entity damages a player: NPC->Player scaling (Default 0.50x) * NpcToPlayer TOD (1.00x) * Global TOD (1.00x). If it damages a structure: attacker struct scaling 0.50x * NpcToStructure TOD (1.00x).
Per-victim subtype scaling: 'Scientist' Default 1.00x (stacks on top of attacker rules).
```

Example output, aiming at a wooden wall:
```
Target: wall.wood (type=BuildingBlock) classified as Building, subtype=<none>
Distance: 4.2m
Hour: 22 (Game). Global TOD multiplier: 1.00x
If an NPC damages this building: NpcToStructure default 0.50x, Building grade multiplier 1.50x (grade=Wood).
```

Example output, aiming at a patrol heli with rule matrix enabled:
```
Target: patrolhelicopter (type=BaseHelicopter) classified as VehicleNpc, subtype=PatrolHelicopter
Distance: 87.2m
Hour: 14 (Game). Global TOD multiplier: 1.00x
Active context: 'AtPvpEvent'
Rules at this context: RealPlayer -> VehicleNpc_victim = 'allow', HumanNpc -> VehicleNpc_victim = 'allow'
Per-victim subtype scaling: 'PatrolHelicopter' Default 1.00x (stacks on top of attacker rules).
```

This is the fastest way to debug "why is this NPC hurting my players so hard" or "why isn't this trap kill reflecting" - aim at the actor in question, run `/pdg test`, and you get a definitive answer including every modifier layer.

## /pdg import damagecontrol (v1.3.0)

Reads `oxide/config/DamageControl.json` and maps compatible fields into PVEDamageGuard's config. Before applying, backs up the current PVEDamageGuard config to `oxide/config/PVEDamageGuard.backup.YYYYMMDDHHMMSS.json`.

Mapping highlights (full list in [configuration.md](configuration.md#migrating-from-damage-control)):
- `APC_Multipliers`, `Heli_Multipliers`, `Bear_Multipliers`, etc. -> `PerVictimSubtypeScaling[BradleyAPC / PatrolHelicopter / Bear / ...]`
- `BuildingBlock_Multipliers` -> averaged into `NpcToStructureScaling`
- `Building_Grade_Multipliers` -> `BuildingGradeMultipliers`
- `Bypasses.Heli_bypass = true` -> `PerAttackerStructureScaling["PatrolHelicopter"] = 1.0`
- `Time.Time_Type` -> `TimeOfDaySource`
- `Global_Time_Multipliers` -> `TimeOfDayMultipliers["Global"]`
- `Player_Time_Multipliers` -> `TimeOfDayMultipliers["PvP"]`
- `NPC_Time_Multipliers` -> `TimeOfDayMultipliers["NpcToPlayer"]`
- `Building_Time_Multipliers` -> `TimeOfDayMultipliers["NpcToStructure"]`

Explicitly skipped (no direct equivalent): `Animal_Time_Multipliers`, `Heli_Time_Multipliers`, `Bradley_Time_Multipliers`, `Other_Time_Multipliers`, `Building` (per-piece protections).

Example:
```
/pdg import damagecontrol

Damage Control import complete. Imported: 18, skipped: 5.
Backed up current config to oxide/config/PVEDamageGuard.backup.20260516153022.json
Mapped APC_Multipliers -> PerVictimSubtypeScaling["BradleyAPC"] (24 damage types)
Mapped Heli_Multipliers -> PerVictimSubtypeScaling["PatrolHelicopter"] (24 damage types)
...
SKIPPED Heli_Time_Multipliers: no direct PVEDamageGuard equivalent. See docs/configuration.md for the migration mapping table.
SKIPPED Building: no direct PVEDamageGuard equivalent.
```

## /pdg preset <name> (v1.3.0)

Applies a known-good complete config. Overwrites your current PVEDamageGuard config.

| Preset | Behavior |
|---|---|
| `pvepure` | Blocks all PvP, severe NPC damage scaling (0.25x default, 0.1x bullet), NPCs cannot damage structures at all. |
| `pvereflect` | Reflects PvP at 1.0x, NPC damage scaled to 0.5x (0.25x bullet), NPCs do half structure damage. |
| `pvevehicleraids` | Reflects PvP, NPCs cannot damage structures EXCEPT PatrolHelicopter and BradleyAPC which raid at full power. |
| `pvphoursevents` | Rule matrix enabled. PvP blocked by default. PvP allowed only when within 200m of Bradley/Heli/Cargo (EventTracker active). |

Example:
```
/pdg preset pvereflect

Applied preset 'pvereflect'. Config saved and reloaded. Review with /pdg status.
```

Recommended workflow: pick the closest preset, then iterate with `/pdg scale`, edits to `oxide/config/PVEDamageGuard.json`, and `/pdg reload`.

## /pdg validate (v1.3.0)

Runs the config validator on demand and reports issues. Validation also runs automatically on plugin load and after `/pdg reload`; any issues surface as `[Warning] PVE Damage Guard` lines in the console and as a `Config issues: N` line in the `/pdg` status output.

Checks performed:
- `EnvironmentalDamageTypes` names parse as `Rust.DamageType`
- All scaling multipliers in `[0, 100]`
- `TimeOfDayMultipliers` arrays have exactly 24 elements
- `TimeOfDaySource` is `Game` or `Real`
- Rule matrix `Inherits` chains have no cycles or dangling references
- Every rule action string parses
- ZoneManager `ZoneFlagToContext` and EventTracker `TriggerContext` reference existing contexts
- `RuleMatrix.DefaultContext` exists in `Contexts`

Example output with issues:
```
/pdg validate

Config validation found 3 issue(s):
  - NpcToPlayerScaling['Bullet']: 150.0 out of [0, 100]
  - RuleMatrix.Contexts[CustomZone]: Inherits target 'NoSuchContext' does not exist
  - TimeOfDayMultipliers[Global]: must have 24 elements (got 23)
```

## /pdg help [subcommand] (v1.3.0)

Interactive help. With no argument, lists all subcommands. With a subcommand name, shows full usage and an example.

Examples:
```
/pdg help
  PVEDamageGuard subcommands. Use /pdg help <subcommand> for details on each:
    reload   - reload config from disk
    log      - set console log verbosity
    ...

/pdg help scale
  /pdg scale <DamageType> <multiplier 0-100> - tune the NPC->Player scaling for one damage type. Persists to config. Example: /pdg scale Bullet 0.1

/pdg help preset
  /pdg preset <name> - apply a known-good config. Available presets: pvepure, pvereflect, pvevehicleraids, pvphoursevents. Each preset is a complete config snapshot; existing fine-tuning is overwritten.
```

This is the closest practical equivalent to tab completion - Rust chat does not support client-side completion, so we settle for explicit help on demand.

## /pdg test fire <DamageType> <amount> (v1.2.0)

Dry-runs a synthetic hit through the full modifier stack and reports what the final damage would be, **without actually applying any damage**. Use to confirm that your config produces the numbers you expect.

Example, aiming at a scientist and firing 100 bullet damage:
```
/pdg test fire Bullet 100

Dry-run: Bullet 100.0 damage to scientistnpc_heavy (HumanNpc / subtype=Scientist).
Legacy scaling path. Final damage if applied: 100.0.
```

Example with rule matrix enabled, aiming at a wall during a Bradley event:
```
/pdg test fire Explosion 250

Dry-run: Explosion 250.0 damage to wall.stone (Building / subtype=<none>).
Rule matrix: context='AtPvpEvent', action=allow. Final damage if applied: 250.0.
```

Damage type must match a value from `Rust.DamageType` (Bullet, Slash, Stab, Bite, Blunt, Explosion, Arrow, Generic, etc.).

Limitations:
- Must be run in-game.
- Simulates the modifier stack only. It does not literally call `Hurt()`, so the actual values shown are what would land before the engine's own protections (armor, etc.).

This is the fastest way to debug "why is this NPC hurting my players so hard" or "why isn't this trap kill reflecting" - aim at the actor in question, run `/pdg test`, and you get a definitive answer including every modifier layer.

Limitations:
- Must be run in-game (server console can't aim at things).
- Raycast distance is 250m.
- If you aim at terrain or a surface without an attached entity, you get "raycast hit a surface but no game entity".

## Console / RCON

Every command above works from server console without the slash:
```
pdg reload
pdg log Scaled
pdg logfile on
```

The `/pdg test` command does not work from console (no crosshair to raycast from).
