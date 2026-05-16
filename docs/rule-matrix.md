# Rule Matrix Guide (v1.2)

The rule matrix is an optional, declarative alternative to PVEDamageGuard's case-based scaling logic. When enabled, every hit is resolved against a `(AttackerCategory|AttackerSubtype) -> (VictimCategory|VictimSubtype) -> Action` table within the currently active context. ZoneManager and a built-in event tracker can switch contexts automatically.

Opt-in: defaults to disabled. With `RuleMatrix.Enabled = false`, PVEDamageGuard runs the v1.1 scaling logic unchanged.

## Why this exists

The case-based logic in v1.1 is powerful but rigid. Admins who want rules like "during a Bradley event, PvP is allowed in a 200m radius" had to use a separate plugin (DynamicPVP) or write code. The rule matrix lets them express it declaratively:

```jsonc
"Contexts": {
  "Default": {
    "Rules": {
      "RealPlayer -> RealPlayer": "reflect:1.0"
    }
  },
  "AtPvpEvent": {
    "Inherits": "Default",
    "Rules": {
      "RealPlayer -> RealPlayer": "allow"
    }
  }
}
```

Combined with the EventTracker provider, this gives you Bradley-active PvP carve-outs for free.

## Action types

| Action | Meaning |
|---|---|
| `allow` | Vanilla damage. Other modifiers (TOD, victim subtype, building grade) still compose. |
| `block` | Cancel the hit entirely. No further modifiers. |
| `reflect:1.0` | Reflect to the attacker at the given multiplier (only meaningful for player-vs-player). Composes with Global TOD and PvP TOD multipliers. |
| `scale:0.5` | Uniform scaling applied to every damage type in the hit. Composes with all other modifier layers. |
| `scale:{Bullet:0.25,Default:0.5}` | Per-damage-type scaling. `Default` applies to types not explicitly listed. |

## Categories and subtypes

The lookup is performed in a 9-tier precedence order, from most specific to most general:

1. `<AttackerSubtype> -> <VictimSubtype>`
2. `<AttackerSubtype> -> <VictimCategory>`
3. `<AttackerCategory> -> <VictimSubtype>`
4. `<AttackerCategory> -> <VictimCategory>`
5. `<AttackerSubtype> -> *`
6. `<AttackerCategory> -> *`
7. `* -> <VictimSubtype>`
8. `* -> <VictimCategory>`
9. `* -> *`

If no rule matches in the current context, the `Inherits` chain is walked. If still no match, the default action is `allow`.

**Categories** (from `NpcCategory` enum): `RealPlayer`, `HumanNpc`, `AnimalNpc`, `VehicleNpc`, `OwnedTrap`, `Building`, `Deployable`, `Environment`, `Other`.

**Subtypes** (from `ClassifySubtype`): `Bear`, `Wolf`, `Boar`, `Chicken`, `Stag`, `Horse`, `RidableHorse`, `Minicopter`, `ScrapHelicopter`, `HotAirBalloon`, `BradleyAPC`, `PatrolHelicopter`, `SamSite`, `Barrel`, `Zombie`, `Scientist`.

Use subtypes for fine-grained tuning ("helis can raid but Bradleys cannot"). Use categories for broad rules ("all NPCs do half damage").

## Contexts

A context is a named ruleset. The default config ships with three:

- **Default**: Normal server state. Reproduces v1.1 PVE behavior.
- **AtPvpEvent**: Inherits Default but flips PvP to allow. Triggered by ZoneManager `pvp` zone flag or proximity to a Bradley / Heli / Cargo event.
- **InRaidableBaseDome**: Inherits Default but allows PvP and full building damage. Triggered by ZoneManager (your RaidableBases plugin should add a `raid_zone` flag to its domes; map it in `ZoneFlagToContext`).

Add your own contexts as needed:

```jsonc
"Contexts": {
  "ArenaZone": {
    "Description": "Duel arena - free PvP, no reflect",
    "Inherits": "Default",
    "Rules": {
      "RealPlayer -> RealPlayer": "allow"
    }
  }
}
```

Then map a ZoneManager flag (e.g. `arena`) to `ArenaZone` in `ContextProviders.ZoneManager.ZoneFlagToContext`.

## Context providers

Two providers ship in v1.2:

### ZoneManager

Detects when the victim is inside a ZoneManager zone with a specific flag. Map flags to context names:

```jsonc
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
```

Requires the ZoneManager plugin to be installed. PVEDamageGuard auto-detects ZoneManager via `[PluginReference]` and tolerates the API not being present (silently ignored).

### Event Tracker

Watches for `BradleyAPC`, `BaseHelicopter`, and `CargoShip` entities. When any is within `RadiusMeters` of the victim, the configured `TriggerContext` is activated:

```jsonc
"ContextProviders": {
  "EventTracker": {
    "Enabled": true,
    "TriggerContext": "AtPvpEvent",
    "Events": ["BradleyAPC", "BaseHelicopter", "CargoShip"],
    "RadiusMeters": 200
  }
}
```

Hook into `OnEntitySpawned` and `OnEntityKill` for tracked types; the active-events dictionary is seeded at server initialization for entities already spawned.

To add tracking for Convoy or Armored Train entities, extend the `Events` list in config to include their root entity class names (subject to those plugins exposing them by class).

## Provider order

For each hit, the active context is resolved in this order:

1. **ZoneManager** zone-flag check (victim inside a flagged zone)
2. **EventTracker** proximity check (victim within radius of a tracked event)
3. **DefaultContext** fallback

The first match wins.

## Composing with v1.1 modifiers

When a rule resolves to `scale:N` or `scale:{...}`, the v1.1 modifier stack (Global TOD, category TOD, per-victim subtype, building grade) **still applies on top**. This means you can write a single rule "NPCs do half damage to players" and still get time-of-day variation and per-victim toughness from the existing config blocks.

`block` and `reflect` skip the v1.1 modifier stack since their meaning is non-numeric.

`reflect` composes with Global TOD and PvP TOD as multipliers on the reflect amount.

## Inspecting rules at runtime

- **`/pdg context`** shows which context is active at your current position.
- **`/pdg test`** aimed at an entity shows the rule that would fire against it from both a `RealPlayer` and a `HumanNpc` attacker.
- **`/pdg test fire <DamageType> <amount>`** dry-runs the entire pipeline including rule matrix evaluation and modifier composition, reports the final damage.
- **`/pdg history [N]`** shows the last N classified hits with their context and action.

## Performance

When `RuleMatrix.Enabled = false`, the rule matrix code path is never entered. Hot-path overhead is zero.

When enabled, rule resolution is a dictionary lookup against ~9 candidate keys with a small Inherits walk. Context resolution is at worst one ZoneManager API call plus a linear scan of active events (typically 0-3 entities). The implementation avoids per-hit allocations beyond a `List<string>` of candidate keys (which can be optimized later if benchmarks show it matters).

## Public API

Other plugins can query the matrix without touching damage:

- `(string)PVEDamageGuard?.Call("API_GetActiveContext", position)` returns the active context name (or null if matrix is off).
- `(bool)PVEDamageGuard?.Call("API_IsPvpAt", position)` returns true if PvP is allowed there.
- `(bool)PVEDamageGuard?.Call("API_IsAllowed", attacker, victim)` returns true if the matrix would not block this pairing.

This lets RaidableBases, Convoy, Backpacks-on-death, and other plugins delegate PvP detection to PVEDamageGuard instead of reimplementing it.

## Worked example: PvP during Bradley events only

```jsonc
"RuleMatrix": {
  "Enabled": true,
  "DefaultContext": "Default",
  "Contexts": {
    "Default": {
      "Rules": {
        "RealPlayer -> RealPlayer": "block",
        "HumanNpc -> RealPlayer": "scale:{Bullet:0.25,Default:0.5}",
        "VehicleNpc -> RealPlayer": "scale:{Bullet:0.25,Explosion:0.5}",
        "VehicleNpc -> Building": "scale:0.5",
        "AnimalNpc -> Building": "block"
      }
    },
    "AtPvpEvent": {
      "Inherits": "Default",
      "Rules": {
        "RealPlayer -> RealPlayer": "allow"
      }
    }
  },
  "ContextProviders": {
    "ZoneManager": { "Enabled": false },
    "EventTracker": {
      "Enabled": true,
      "TriggerContext": "AtPvpEvent",
      "Events": ["BradleyAPC", "BaseHelicopter"],
      "RadiusMeters": 200
    }
  }
}
```

PvP is blocked everywhere by default. When a Bradley or patrol heli is active and a player is within 200m of either, PvP is allowed. NPC damage is scaled regardless of context.
