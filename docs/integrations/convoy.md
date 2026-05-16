# Integration: Convoy

PVEDamageGuard treats [Convoy](https://codefling.com/plugins/convoy) (Adem) as a server-wide event flag. While a convoy is active anywhere on the map, the rule-matrix context flips to whatever `GlobalEventTriggers.TriggerContext` specifies (default `AtPvpEvent`).

## Why server-wide, not positional

The convoy is a moving fleet of multiple Bradleys, scientists, and vehicles. There's no single canonical "convoy position" to do a proximity check against. Server-wide flag semantics match the gameplay intent ("PvP allowed during convoys"); admins who want positional convoy detection should add the convoy-spawned `BradleyAPC` entities to `EventTracker.Events` instead.

## How detection works

- We register `OnConvoyStart()` and `OnConvoyStop()` hook handlers.
- When the convoy plugin fires `OnConvoyStart`, we add `"Convoy"` to the `_activeGlobalEvents` HashSet.
- When it fires `OnConvoyStop`, we remove the entry.
- `ResolveContext` consults `_activeGlobalEvents` as the fourth fallback (after ZoneManager, RaidableBases domes, and entity-based EventTracker).

## Configuration

```jsonc
"RuleMatrix": {
  "Enabled": true,
  "Contexts": {
    "Default": { "Rules": { "RealPlayer -> RealPlayer": "block" } },
    "AtPvpEvent": {
      "Inherits": "Default",
      "Rules": { "RealPlayer -> RealPlayer": "allow" }
    }
  },
  "ContextProviders": {
    "GlobalEventTriggers": {
      "Enabled": true,
      "TriggerContext": "AtPvpEvent",
      "Events": ["Convoy", "ArmoredTrain"]
    }
  }
}
```

## Verifying detection

1. Start a convoy (or wait for one to auto-spawn).
2. Run `/pdg events`. You should see an `Active global events:` line with `Convoy`.
3. Run `/pdg context` from anywhere on the map. It should report `AtPvpEvent`.
4. When the convoy ends, `/pdg events` clears it and `/pdg context` returns to `Default`.

## Recipe: convoy-only PvP

```jsonc
"RuleMatrix": {
  "Enabled": true,
  "Contexts": {
    "Default": {
      "Rules": {
        "RealPlayer -> RealPlayer": "reflect:1.0",
        "HumanNpc -> RealPlayer":   "scale:{Bullet:0.25,Default:0.5}",
        "VehicleNpc -> Building":   "scale:0.5"
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
    "GlobalEventTriggers": { "Enabled": true, "TriggerContext": "AtPvpEvent", "Events": ["Convoy"] }
  }
}
```

PvP reflects normally except while a convoy is active. During convoys, free-for-all PvP server-wide.

## Troubleshooting

| Symptom | Cause | Fix |
|---|---|---|
| Convoy starts but `/pdg events` shows no global events | The convoy plugin doesn't fire `OnConvoyStart()` in your version | Check Convoy version; some forks use different hook names. Open an issue at the PVEDamageGuard GitHub. |
| Context stays `AtPvpEvent` after convoy ends | `OnConvoyStop` not firing | Restart the server; verify your Convoy version. |
| PvP works during convoy but Bradley damage still scaled at PVE rates | `AtPvpEvent` inherits Default which still scales VehicleNpc damage | Override that rule in `AtPvpEvent` if you want convoy Bradleys to do full damage. |
