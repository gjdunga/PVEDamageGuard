# Integration: Armored Train

[Armored Train](https://codefling.com/plugins/armored-train) (Adem) is integrated identically to [Convoy](convoy.md): server-wide global event flag, not positional. See the Convoy doc for the full architecture and rationale.

## Hook compatibility

Adem's Armored Train plugin has used different hook names across versions. PVEDamageGuard listens to all four common forms:

- `OnTrainEventStart()` / `OnTrainEventStop()` (older versions)
- `OnArmoredTrainEventStart()` / `OnArmoredTrainEventStop()` (newer versions)

Whichever your installed version fires, we catch it.

## Configuration

```jsonc
"ContextProviders": {
  "GlobalEventTriggers": {
    "Enabled": true,
    "TriggerContext": "AtPvpEvent",
    "Events": ["Convoy", "ArmoredTrain"]
  }
}
```

The `Events` list controls which global events activate the context flip. Remove `"ArmoredTrain"` to keep PvP off during train events while still flipping for convoy.

## Verifying detection

1. Start an armored train event (admin command or wait for auto-spawn).
2. Run `/pdg events`. Expect `Active global events:` to list `ArmoredTrain`.
3. Run `/pdg context`. Expect `AtPvpEvent` (or your configured `TriggerContext`).
4. When train ends, the entry clears.

## Recipe: separate contexts for convoy vs train

```jsonc
"Contexts": {
  "Default": { "Rules": { "RealPlayer -> RealPlayer": "block" } },
  "AtConvoy": {
    "Inherits": "Default",
    "Rules": { "RealPlayer -> RealPlayer": "allow", "VehicleNpc -> RealPlayer": "scale:{Default:1.0,Bullet:0.5}" }
  },
  "AtArmoredTrain": {
    "Inherits": "Default",
    "Rules": { "RealPlayer -> RealPlayer": "allow", "VehicleNpc -> RealPlayer": "scale:{Default:1.0,Explosion:0.75}" }
  }
}
```

Then configure `GlobalEventTriggers.TriggerContext`. The current implementation uses a single trigger context for all global events; for per-event contexts open a feature request on GitHub. As a workaround, use a shared `AtPvpEvent` context with averaged NPC scaling.
