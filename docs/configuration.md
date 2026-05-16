# Configuration

The config file lives at `oxide/config/PVEDamageGuard.json`. Edit it with a text editor, then either restart the server, run `oxide.reload PVEDamageGuard`, or use the in-game `/pdg reload` command.

## Default config

```json
{
  "PvP - Reflect damage to shooter (master switch)": true,
  "PvP - Reflect multiplier (1.0 = full reflect, 0.5 = half)": 1.0,
  "PvP - If reflect is disabled, block PvP damage outright instead of letting it through": true,
  "PvP - Allow teammates (Rust team system) to damage each other": false,

  "NPC -> Player - Per-damage-type scaling. Missing types use 'Default'. Set to 0 to make players immune to that type.": {
    "Default": 0.5,
    "Bullet": 0.25,
    "Slash": 0.5,
    "Stab": 0.5,
    "Bite": 0.5,
    "Blunt": 0.5,
    "Explosion": 0.5,
    "Arrow": 0.5,
    "Generic": 1.0
  },

  "NPC -> Structure - Uniform scaling for heli/Bradley/scientist damage to player-built structures (0 = invulnerable)": 0.5,

  "Treat traps owned by a player (auto-turret, shotgun trap, flame turret) as PvP from that owner": true,

  "Damage types to NEVER touch (always vanilla). Fall, bleed, cold, etc.": [
    "Hunger", "Thirst", "Cold", "Heat", "Drowned",
    "Bleeding", "Poison", "Suicide", "Fall",
    "Radiation", "RadiationExposure", "ColdExposure", "Decay"
  ],

  "Yield allow/block decisions to TruePVE if it is loaded (we only scale and classify)": true,

  "Log verbosity: None | Reflects | Scaled | All | Trace": "None",
  "Also write log entries to oxide/logs/PVEDamageGuard/ files for audit": false
}
```

## Field-by-field

### PvP - Reflect damage to shooter (master switch)
Turn PvP reflect on or off. When on, any PvP hit is cancelled and the same damage is dealt to the attacker via `BasePlayer.Hurt()`. When off, behavior depends on the next field.

### PvP - Reflect multiplier
Scales the reflected damage. `1.0` = same damage dealt back to attacker, `0.5` = half, `2.0` = double (mean, but possible).

### PvP - If reflect is disabled, block PvP damage outright instead of letting it through
If reflect is off, choose between blocking PvP entirely (`true`) or letting it through vanilla (`false`). On a PVE server you almost certainly want `true`.

### PvP - Allow teammates to damage each other
If `true`, players on the same Rust team can damage each other freely. Useful for team training or duel modes. Defaults to `false` (no friendly fire).

### NPC -> Player - Per-damage-type scaling
A dictionary of damage types to multipliers. When any NPC hits a player, every damage type in `info.damageTypes` is scaled by the configured value, falling back to `Default` for any type not listed. `0` = full immunity to that damage type.

Common adjustments:
- Bumping `Bullet` lower makes scientists much less lethal.
- Bumping `Explosion` lower makes heli rockets and Bradley shells less lethal.
- Setting `Bite` to `0` makes wolves and bears unable to hurt players (they can still be killed, just can't damage you).

### NPC -> Structure - Uniform scaling
Single multiplier for NPC -> Player-built-structure damage. Set to `0` to make all player structures invulnerable to NPCs (heli rockets, Bradley shells, scientist grenades all do zero damage to bases). Defaults to `0.5` so heli raids still happen but bases hold longer.

### Treat traps owned by a player as PvP
When an auto-turret, shotgun trap, or flame turret damages another player, attribute the damage to the trap's owner. This means PvP reflect (if enabled) bounces back to the trap owner. Set to `false` if you want trap kills to be vanilla one-way damage.

### Environmental damage types
Damage types that PVEDamageGuard never touches under any circumstance. Fall damage, bleed ticks, hunger, cold, drowning, suicide, etc. Add or remove types here. Type names must match Rust's `Rust.DamageType` enum exactly (case-insensitive).

### Yield allow/block decisions to TruePVE if it is loaded
When `true` and TruePVE is loaded, PVEDamageGuard yields PvP allow/block decisions to TruePVE and only does scaling + classification + reflect-on-API-request. When `false` or TruePVE is not loaded, PVEDamageGuard handles everything itself. Default `true`.

### Log verbosity
One of `None`, `Reflects`, `Scaled`, `All`, `Trace`. See [commands.md](commands.md#pdg-log) for what each level emits.

### Also write log entries to oxide/logs/PVEDamageGuard/
When `true`, every console log line is also appended to `oxide/logs/PVEDamageGuard/damage-YYYY-MM-DD.txt` for offline review or shipping to Loki/Splunk/Discord webhooks. Default `false`.

## Reloading without restart

Three options:
- In game: `/pdg reload`
- Server console: `oxide.reload PVEDamageGuard`
- Edit and save - Oxide does NOT auto-reload on file change. You must reload manually.

## Resetting to defaults

Delete `oxide/config/PVEDamageGuard.json` and reload the plugin. A fresh default config will be written.
