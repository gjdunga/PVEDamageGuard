# Configuration

The config file lives at `oxide/config/PVEDamageGuard.json`. Edit with a text editor, then either restart the server, run `oxide.reload PVEDamageGuard`, or use `/pdg reload`.

Modifiers compose by multiplication. The order applied per hit is:

1. Case-specific scaling (NpcToPlayerScaling, PerAttackerStructureScaling/NpcToStructureScaling, PvP reflect)
2. Global time-of-day multiplier (`TodGlobal`)
3. Category time-of-day multiplier (`TodPvp`, `TodNpcToPlayer`, `TodNpcToStructure`)
4. Per-victim subtype scaling (`PerVictimSubtypeScaling[subtype]`)
5. Building grade multiplier (`BuildingGradeMultipliers[grade]` if victim is BuildingBlock)

Any modifier set to `1.0` for all entries is detected as inactive and skipped on the hot path. Leave the new v1.1.0 fields at their defaults to get exactly v1.0.x behavior.

## PvP

### `"PvP - Reflect damage to shooter (master switch)"` (bool)
Turn PvP reflect on or off. When on, any PvP hit is cancelled and the same damage is dealt to the attacker via `BasePlayer.Hurt()`.

### `"PvP - Reflect multiplier"` (float)
Scales reflected damage. `1.0` = same damage dealt back to attacker, `0.5` = half, `2.0` = double.

### `"PvP - If reflect is disabled, block PvP damage outright instead of letting it through"` (bool)
If reflect is off, choose between blocking PvP entirely (`true`) or letting it through vanilla (`false`). On a PVE server you almost certainly want `true`.

### `"PvP - Allow teammates to damage each other"` (bool)
If `true`, players on the same Rust team can damage each other freely.

## NPC -> Player

### `"NPC -> Player - Per-damage-type scaling"` (object)
Dictionary of damage types to multipliers. When any NPC hits a player, each damage type in the hit is scaled by the configured value, falling back to `Default`.

```json
{
  "Default":   0.5,
  "Bullet":    0.25,
  "Slash":     0.5,
  "Stab":      0.5,
  "Bite":      0.5,
  "Blunt":     0.5,
  "Explosion": 0.5,
  "Arrow":     0.5,
  "Generic":   1.0
}
```

`0` for any type makes players immune to that damage type from NPCs.

## NPC -> Structure

### `"NPC -> Structure - Default uniform scaling"` (float)
Single scalar used when no per-attacker override applies. Default `0.5`. Set `0` to fully block NPC damage to player structures.

### `"NPC -> Structure - Per-attacker overrides"` (object)
**This replaces Damage Control's `Heli_bypass` flag.** Empty dict (default) = use the single Default scalar above for everyone. Populate with attacker subtype keys to override per-attacker:

```json
{
  "Default":          0.0,
  "PatrolHelicopter": 1.0,
  "BradleyAPC":       0.75,
  "HumanNpc":         0.0,
  "AnimalNpc":        0.0
}
```

The above blocks all NPC damage to structures **except** patrol helicopter (full damage) and Bradley (75%). This is the "heli bypass" pattern.

Recognized keys: `PatrolHelicopter`, `BradleyAPC`, `HumanNpc`, `AnimalNpc`, `VehicleNpc`, `Default`. Lookup order: specific subtype -> broad category -> Default -> single scalar above.

## Building grade multipliers

### `"Building grade multipliers"` (object)

```json
{
  "Twigs":   2.0,
  "Wood":    1.5,
  "Stone":   1.0,
  "Metal":   0.5,
  "TopTier": 0.25
}
```

Stacks on top of NPC -> Structure scaling. Applies **only to BuildingBlock entities** (foundations, walls, doors built via the building plan), not deployables. All-ones (default) disables the feature.

Example: with the above plus `"NPC -> Structure default": 0.5`:
- Twigs takes `0.5 * 2.0 = 1.0x` damage (vanilla)
- Stone takes `0.5 * 1.0 = 0.5x` damage
- TopTier takes `0.5 * 0.25 = 0.125x` damage

## Per-victim subtype scaling

### `"Per-victim subtype scaling"` (object)

Per-damage-type multipliers applied when the **victim** matches a specific subtype. Stacks on top of attacker-based rules.

```json
{
  "Bear":          { "Default": 1.5 },
  "Wolf":          { "Default": 1.0 },
  "Minicopter":    { "Default": 1.0, "Explosion": 2.0 },
  "Barrel":        { "Default": 0.5 },
  "PatrolHelicopter": { "Default": 1.0, "Bullet": 1.5 }
}
```

The above makes bears 50% tougher (take less damage), barrels die in half the hits, minicopters take double damage from explosions, and the patrol heli is 50% more vulnerable to bullets.

Recognized subtypes: `Bear`, `Wolf`, `Boar`, `Chicken`, `Stag`, `Horse`, `RidableHorse`, `Minicopter`, `ScrapHelicopter`, `HotAirBalloon`, `BradleyAPC`, `PatrolHelicopter`, `SamSite`, `Barrel`, `Zombie`, `Scientist`. Inner dict supports any `Rust.DamageType` enum value plus `Default`.

Entities that don't match any subtype skip this layer entirely.

## Time of day

### `"Time of day - source for hour lookup"` (string)
- `"Game"` reads from `TOD_Sky.Instance.Cycle.Hour` (in-game day/night cycle, integer 0-23 floor).
- `"Real"` reads from `System.DateTime.Now.Hour` (server wall clock, integer 0-23).

### `"Time of day multipliers"` (object)
Per-category 24-element arrays, indexed by hour (0-23). Applied multiplicatively to the relevant damage path. All-ones disables the feature.

```json
{
  "Global":          [1,1,1,1,1,1, 1,1,1,1,1,1, 1,1,1,1,1,1, 1,1,1,1,1,1],
  "PvP":             [0,0,0,0,0,0, 1,1,1,1,1,1, 1,1,1,1,1,1, 1,1,1,1,1,0],
  "NpcToPlayer":     [0.5,0.5,0.5,0.5,0.5,0.5, 1,1,1,1,1,1, 1,1,1,1,1,1, 1,1,1,0.5,0.5,0.5],
  "NpcToStructure":  [1,1,1,1,1,1, 1,1,1,1,1,1, 1,1,1,1,1,1, 1,1,1,1,1,1]
}
```

The above example:
- **Global** is all-ones (no global TOD effect).
- **PvP** is blocked from midnight to 6 AM and from 11 PM to midnight (PvP only allowed during the day).
- **NpcToPlayer** is at half damage at night, full damage during the day (sleep safer).
- **NpcToStructure** is unchanged.

`/pdg hour` shows the current hour and all four multipliers at a glance for debugging.

## Misc

### `"Treat traps owned by a player as PvP"` (bool)
When an auto-turret, shotgun trap, or flame turret damages another player, attribute the damage to the trap's owner (so PvP reflect bounces to the trap owner instead of being lost).

### `"Damage types to NEVER touch"` (array of strings)
Damage types that PVEDamageGuard never modifies. Default: fall, bleed, hunger, cold, drown, suicide, etc. Type names must match Rust's `DamageType` enum.

### `"Yield allow/block decisions to TruePVE if it is loaded"` (bool)
When `true` (default) and TruePVE is loaded, PVEDamageGuard yields PvP allow/block to TruePVE and only applies scaling on top. When `false` or TruePVE absent, PVEDamageGuard handles PvP itself.

### `"Log verbosity"` (string enum)
`None`, `Reflects`, `Scaled`, `All`, or `Trace`. See [commands.md](commands.md) for what each emits.

### `"Also write log entries to oxide/logs/PVEDamageGuard/"` (bool)
Mirrors console log output to a daily-rotated file at `oxide/logs/PVEDamageGuard/damage-YYYY-MM-DD.txt`.

## Migrating from Damage Control

Mapping of Damage Control config blocks to PVEDamageGuard equivalents:

| Damage Control | PVEDamageGuard |
|---|---|
| `Heli_Multipliers`, `APC_Multipliers`, etc. (per-victim damage type tables) | `PerVictimSubtypeScaling[subtype]` |
| `Player_Multipliers`, `Scientist_Multipliers` | Use `NpcToPlayerScaling` (we are attacker-side, not victim-side) |
| `Building_Grade_Multipliers` | `BuildingGradeMultipliers` |
| `*_Time_Multipliers` arrays (8 of them) | `TimeOfDayMultipliers` (4 categories: Global, PvP, NpcToPlayer, NpcToStructure) |
| `Heli_bypass` (true/false flag) | `PerAttackerStructureScaling["PatrolHelicopter"] = 1.0` (more flexible) |
| `Time_Type` (game/real) | `TimeOfDaySource` (Game/Real) |

PVEDamageGuard does **not** replicate the 8 separate TOD categories. Four cover the meaningful cases; if you specifically need Heli-only-at-night, set `PerAttackerStructureScaling["PatrolHelicopter"]` and use `NpcToStructure` TOD to scale all NPC->Structure together at the desired hour.

## Reloading without restart

- In game: `/pdg reload`
- Server console: `oxide.reload PVEDamageGuard`

## Resetting to defaults

Delete `oxide/config/PVEDamageGuard.json` and reload the plugin. A fresh default config will be written.
