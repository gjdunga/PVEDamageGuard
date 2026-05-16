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

Example output, aiming at a patrol heli:
```
Target: patrolhelicopter (type=BaseHelicopter) classified as VehicleNpc, subtype=PatrolHelicopter
Distance: 87.2m
Hour: 14 (Game). Global TOD multiplier: 1.00x
If this entity damages a player: NPC->Player scaling (Default 0.50x) * NpcToPlayer TOD (1.00x) * Global TOD (1.00x). If it damages a structure: attacker struct scaling 1.00x * NpcToStructure TOD (1.00x).
Per-victim subtype scaling: 'PatrolHelicopter' Default 1.00x (stacks on top of attacker rules).
```

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
