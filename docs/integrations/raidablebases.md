# Integration: RaidableBases

PVEDamageGuard detects [RaidableBases](https://codefling.com/plugins/raidable-bases) (nivex) domes automatically and flips the rule-matrix context to `InRaidableBaseDome` (or whatever context you configure) while a player is inside one. Inside the dome, the default rule set allows PvP and full building damage to the raid base.

## How detection works

- PVEDamageGuard registers itself with the `[PluginReference]` system as a RaidableBases listener.
- When a raid base spawns, RaidableBases fires `OnRaidableBaseStarted(Vector3 pos, int mode)`. We capture the position and store it in `_activeDomes`.
- When it despawns, RaidableBases fires `OnRaidableBaseEnded(Vector3 pos, int mode)`. We remove the entry.
- On every hit, `ResolveContext` proximity-checks the victim position against every active dome. First match wins.

Cross-version compatibility: we accept both `(Vector3, int)` and `(Vector3)` signatures of those hooks, so older and newer RaidableBases versions both work.

## Configuration

```jsonc
"RuleMatrix": {
  "Enabled": true,
  "Contexts": {
    "Default": { "Rules": { "RealPlayer -> RealPlayer": "block" } },
    "InRaidableBaseDome": {
      "Inherits": "Default",
      "Rules": {
        "RealPlayer -> RealPlayer": "allow",
        "RealPlayer -> Building":   "allow",
        "RealPlayer -> Deployable": "allow"
      }
    }
  },
  "ContextProviders": {
    "RaidableBases": {
      "Enabled": true,
      "TriggerContext": "InRaidableBaseDome",
      "RadiusOverrideMeters": 0
    }
  }
}
```

`RadiusOverrideMeters: 0` uses whatever radius PVEDamageGuard inferred from the RaidableBases hook (default 75m if nothing supplied). Set to a positive number to override per your raid base sizes.

## Verifying detection

1. Wait for a raid base to spawn (or use RaidableBases' admin command to spawn one).
2. Walk inside the dome.
3. Run `/pdg context`. You should see `Active context at your position: 'InRaidableBaseDome'`.
4. Run `/pdg events`. You should see a `Tracked RaidableBases domes:` section with the dome listed.

## Troubleshooting

| Symptom | Cause | Fix |
|---|---|---|
| `/pdg events` shows zero domes while one is clearly spawned | RaidableBases loaded after PVEDamageGuard and we missed the start hook | Reload PVEDamageGuard once a base is active; the hook fires on every new base going forward. Or wait for the next spawn. |
| Context stays `Default` inside the dome | Position check radius too small | Set `RadiusOverrideMeters` to a larger value (e.g. 100), `/pdg reload`. |
| PvP allowed everywhere, not just in the dome | `Default` context's `RealPlayer -> RealPlayer` rule is `allow` | Set it to `block` or `reflect:1.0` so PvP is restricted outside dome. |

## Recipe: PVE + raidable raid bases

```jsonc
"RuleMatrix": {
  "Enabled": true,
  "Contexts": {
    "Default": {
      "Rules": {
        "RealPlayer -> RealPlayer": "block",
        "RealPlayer -> Building":   "allow",
        "RealPlayer -> Deployable": "allow",
        "HumanNpc -> RealPlayer":   "scale:{Bullet:0.25,Default:0.5}"
      }
    },
    "InRaidableBaseDome": {
      "Inherits": "Default",
      "Rules": {
        "RealPlayer -> RealPlayer": "allow"
      }
    }
  },
  "ContextProviders": {
    "RaidableBases": { "Enabled": true, "TriggerContext": "InRaidableBaseDome" }
  }
}
```

Server is full PVE everywhere except inside raidable base domes, where PvP turns on so contestants can fight over the loot. Players can damage their own structures (the `Building`/`Deployable` rules in Default) but can't hurt each other unless inside a dome.
