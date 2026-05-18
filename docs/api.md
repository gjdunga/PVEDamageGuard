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

### API_GetActiveContext(Vector3 pos) -> string (v1.2.0)

Returns the active rule-matrix context name at the given position, or `null` if the rule matrix is disabled. Walks the configured ContextProviders in order (ZoneManager first, then EventTracker proximity).

```csharp
var ctx = (string)PVEDamageGuard?.Call("API_GetActiveContext", player.transform.position);
if (ctx == "AtPvpEvent")
{
    // a Bradley/Heli/Cargo event is active near this player
}
```

### API_IsPvpAt(Vector3 pos) -> bool (v1.2.0)

Returns `true` if PvP is allowed at the given position under the currently active context's rules. Performs a `RealPlayer -> RealPlayer` rule lookup at `pos`.

When the rule matrix is disabled, returns `true` only if PvP is configured to neither reflect nor block (the rare "vanilla PvP" config).

```csharp
var pvpHere = (bool)(PVEDamageGuard?.Call("API_IsPvpAt", player.transform.position) ?? false);
if (pvpHere) ShowPvpIndicator(player);
```

This is the integration point for RaidableBases, Convoy, Backpacks-on-death, and any plugin that needs to know whether PvP applies at a location.

### API_RegisterCategory(string name, Func<BaseEntity, bool> matcher) -> bool (v1.8.0)

Registers a custom NPC category from another plugin. When the matcher returns `true` for an entity, PVEDamageGuard's classifier returns the given name from `ClassifySubtype()`. Registered names appear in `/pdg test` output and are valid keys in rule matrix `"AttackerCat -> VictimCat"` strings.

Matchers run BEFORE the built-in `ClassifySubtype` checks. First match wins. This lets plugin authors override built-in classifications intentionally (e.g. classify a `BaseNpc` with `scarecrow` in its prefab as `"FrontierBandit"` instead of the built-in `"Zombie"`).

```csharp
[PluginReference] private Plugin PVEDamageGuard;

void OnServerInitialized()
{
    PVEDamageGuard?.Call("API_RegisterCategory", "FrontierBandit",
        (Func<BaseEntity, bool>)(entity =>
        {
            if (!(entity is BaseNpc)) return false;
            var prefab = entity.ShortPrefabName ?? "";
            return prefab.Contains("frontier_bandit");
        }));
}
```

The classification cache is automatically cleared on register so the new matcher takes effect on the next hit. Matchers are called in try/catch; throwing matchers are reported and skipped.

Returns `true` if the matcher was registered, `false` if the name is empty or the matcher is null.

### API_UnregisterCategory(string name) -> bool (v1.8.0)

Removes a previously registered category matcher. Returns `true` if a category was removed, `false` if no category with that name was registered.

```csharp
void Unload()
{
    PVEDamageGuard?.Call("API_UnregisterCategory", "FrontierBandit");
}
```

The classification cache is automatically cleared on unregister.

### API_ListCustomCategories() -> string[] (v1.8.0)

Returns the names of all currently-registered custom categories. Useful for verifying that your registration worked.

```csharp
var names = PVEDamageGuard?.Call("API_ListCustomCategories") as string[];
if (names != null && !names.Contains("FrontierBandit"))
{
    PrintWarning("FrontierBandit category did not register correctly");
}
```

### API_IsAllowed(BaseEntity attacker, BaseEntity victim) -> bool (v1.2.0)

Returns `true` unless the rule matrix would `block` this attacker-victim pairing in the victim's current context. Useful for plugins that want to short-circuit their own behavior based on the centralized PVE rules.

```csharp
var allowed = (bool)(PVEDamageGuard?.Call("API_IsAllowed", attackerEntity, victimEntity) ?? true);
if (!allowed) return; // PVE rules forbid this hit; skip our own scoring logic
```

When the rule matrix is disabled, returns `true` (defer to the legacy scaling path's own logic).

## Public types

### NpcCategory enum

The full taxonomy is exposed as a public enum:

```csharp
public enum NpcCategory
{
    None,           // not an NPC
    RealPlayer,     // human player
    HumanNpc,       // BasePlayer.IsNpc, ScientistNPC, HumanNPCNew, vendor guards, Tutorial/Frontier, future
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

### Humanoid NPC subtype names (v2.0.1)

`API_ClassifySubtype` returns one of the following strings for humanoid NPCs, in priority order. Use them in rule-matrix entries (`"TutorialNPC -> RealPlayer": "scale:0.25"`) and `PerAttackerStructureScaling` keys.

| Subtype | Matches when prefab contains |
|---|---|
| `TutorialNPC` | `tutorial` |
| `FrontierNPC` | `frontier` |
| `TravellingVendorGuard` | `vendor` |
| `HumanNPCNew` | `humannpc` |
| `HeavyScientist` | `heavyscientist` |
| `Scientist` | (fallback for any other humanoid AI) |

Classification is **strict-type-checks-first, AI-brain-component-fallback-second** (v2.0.1). If an NPC inherits from `BasePlayer`/`NPCPlayer`/`BaseNpc` or has a Unity component whose type name matches Rust's AI brain naming convention (`BaseAIBrain`, `*AIBrain`, `*AIAgent`, `HTN*`, `*Brain`), it classifies as `HumanNpc` and a subtype is assigned per the table above.

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
