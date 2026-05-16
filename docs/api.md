# Plugin API

PVEDamageGuard exposes a public API that other Oxide plugins can call to query entity classification, request damage scaling values, or invoke a safe reflect. This is the "TruePVE companion" promise in practice - other plugins do not need to maintain their own NPC classification logic, they ask PVEDamageGuard.

All API methods are decorated with `[HookMethod]` and can be invoked through Oxide's `plugins.Call()` or `[PluginReference]` patterns.

## Methods

### API_Classify(BaseEntity entity) -> string

Returns the `NpcCategory` of the entity as a string. One of:
- `None`
- `RealPlayer`
- `HumanNpc`
- `AnimalNpc`
- `VehicleNpc`
- `OwnedTrap`
- `Building`
- `Deployable`
- `Environment`
- `Other`

```csharp
[PluginReference] private Plugin PVEDamageGuard;

void OnEntityTakeDamage(BaseCombatEntity entity, HitInfo info)
{
    var category = (string)PVEDamageGuard?.Call("API_Classify", entity);
    if (category == "HumanNpc")
    {
        // entity is any humanoid NPC, current or future Facepunch subclass
    }
}
```

### API_IsNpcAttacker(HitInfo info) -> bool

Returns `true` if the attacker (after walking projectile/explosive/trap wrappers) classifies as `HumanNpc`, `AnimalNpc`, or `VehicleNpc`.

```csharp
bool attackerIsNpc = (bool)(PVEDamageGuard?.Call("API_IsNpcAttacker", info) ?? false);
```

This is the single check that replaces every brittle `entity.ShortPrefabName.Contains("scientist")` pattern in the PVE plugin ecosystem.

### API_ReflectDamage(BasePlayer attacker, BasePlayer victim, HitInfo info, float multiplier) -> bool

Safely reflects damage from `victim` back to `attacker`, scaled by `multiplier`. Uses PVEDamageGuard's re-entrancy guard so calling this from inside `OnEntityTakeDamage` does not infinite-loop.

Returns `true` if the reflect was queued, `false` if any argument was null.

```csharp
PVEDamageGuard?.Call("API_ReflectDamage", attacker, victim, info, 1.0f);
```

Use this in your own plugin when you have your own allow/block logic but want to delegate the actual reflect mechanics. This is how TruePVE-style rule plugins should integrate PvP reflect rather than implementing their own.

### API_GetNpcScaling(string damageType) -> float

Returns the configured NPC -> Player scaling multiplier for a given damage type, falling back to `Default`. Useful if your plugin needs to apply the admin's configured NPC scaling to a synthetic hit.

```csharp
float bulletMult = (float)(PVEDamageGuard?.Call("API_GetNpcScaling", "Bullet") ?? 1.0f);
```

### API_ClassifySubtype(BaseEntity entity) -> string (v1.1.0)

Returns a stable subtype string for entities admins want to tune individually. One of: `Bear`, `Wolf`, `Boar`, `Chicken`, `Stag`, `Horse`, `RidableHorse`, `Minicopter`, `ScrapHelicopter`, `HotAirBalloon`, `BradleyAPC`, `PatrolHelicopter`, `SamSite`, `Barrel`, `Zombie`, `Scientist`, or `null` if the entity does not match any known subtype.

Use this when you want finer-grained classification than `API_Classify` (which returns the broad NpcCategory). For example, you may want to treat a patrol heli differently than a Bradley even though both are `VehicleNpc`.

```csharp
var subtype = (string)PVEDamageGuard?.Call("API_ClassifySubtype", entity);
if (subtype == "PatrolHelicopter")
{
    // do something heli-specific
}
```

### API_GetCurrentHour() -> int (v1.1.0)

Returns the current hour (0-23) PVEDamageGuard uses for time-of-day lookups, respecting the admin's `TimeOfDaySource` setting (`Game` or `Real`).

```csharp
int hour = (int)(PVEDamageGuard?.Call("API_GetCurrentHour") ?? 0);
// announce a transition if hour just changed
```

Use this for plugins that want to synchronize with PVEDamageGuard's TOD schedule (e.g. a Discord plugin announcing "PvP hours start in 30 minutes" using the same source of truth).

## Public types

### NpcCategory enum

The full taxonomy is exposed as a public enum:

```csharp
public enum NpcCategory
{
    None,           // not an NPC
    RealPlayer,     // human player
    HumanNpc,       // BasePlayer.IsNpc, ScientistNPC, HumanNPCNew, vendor guards, future
    AnimalNpc,      // BaseNpc (bears, wolves, boars, zombies, scarecrows)
    VehicleNpc,     // BaseHelicopter, BradleyAPC, their projectiles
    OwnedTrap,      // player-owned trap (auto-turret, shotgun trap, flame turret)
    Building,       // BuildingBlock, Door
    Deployable,     // player-owned DecayEntity
    Environment,    // fall, bleed, cold, etc.
    Other           // unclassified
}
```

If you can reference `PVEDamageGuard` as a direct type (same assembly or shared compilation unit), you can use the enum directly. Otherwise compare strings as shown in the examples above.

## Integration recipe: replacing prefab-name matching

If your plugin currently has code like:

```csharp
// OLD - fragile
if (entity is NPCPlayer || (entity is BasePlayer p && p.IsNpc) ||
    entity.PrefabName.Contains("scientist") || entity.PrefabName.Contains("human"))
{
    // it's an NPC, apply rules
}
```

Replace with:

```csharp
// NEW - future-proof
var cat = (string)PVEDamageGuard?.Call("API_Classify", entity) ?? "None";
if (cat == "HumanNpc" || cat == "AnimalNpc" || cat == "VehicleNpc")
{
    // it's an NPC, apply rules
}
```

The new version catches every current Facepunch NPC subclass and every future one (HumanNPCNew variants, vendor guards, frontier NPCs, anything with `IsNpc=true` or inheriting `BaseNpc`), without needing your plugin to be patched on every monthly forced wipe.

## Hooks PVEDamageGuard fires

None in v1.0.0. The plugin only consumes hooks (`OnEntityTakeDamage`, `OnServerInitialized`, `OnPluginLoaded`, `OnPluginUnloaded`).

Planned for v1.1: an `OnPveClassified(BaseCombatEntity, HitInfo, string category)` hook other plugins can subscribe to for telemetry or audit purposes.

## Backwards compatibility

The API method names use the `API_` prefix to follow uMod convention and to make API methods unambiguously distinguishable from internal helpers. Method signatures will not change within a major version. If a breaking change is required, the new method will be added with a `_v2` suffix and the old method will be preserved for one major version cycle.
