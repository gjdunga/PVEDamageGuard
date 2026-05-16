# Performance

PVEDamageGuard hooks `OnEntityTakeDamage`, which fires for every damage event in the game. On a 200-player server during a heli event, that can be thousands of hits per second. This page documents the optimizations that keep the hot path fast and how to verify performance on your own server.

## Hot path optimizations (v1.5)

### Per-entity classification cache

Both `ClassifyEntity` and `ClassifySubtype` consult a dictionary keyed by `BaseEntity.net.ID.Value` before running the type-check ladder. On a steady-state server the cache hit rate is typically 95%+ within minutes of a wipe.

- **Capacity:** 10000 entries (`CacheMaxEntries`). When the cache reaches capacity, it is fully cleared (cheaper than LRU eviction for our access pattern).
- **Invalidation:** entries are removed automatically when `OnEntityKill` fires for the entity, so memory does not leak across entity destruction.
- **Manual flush:** `/pdg cache clear` empties the cache. Useful after manually changing classification logic in a development build.

### Cached enum values

`Enum.GetValues(typeof(DamageType))` allocates an array on every call. PVEDamageGuard caches it once in `_allDamageTypes` and iterates from the cached array on every per-damage-type scaling pass.

### Feature flags computed at config load

`RebuildCaches` sets four bool flags (`_todEnabled`, `_victimScalingEnabled`, `_buildingGradeEnabled`, `_perAttackerStructureEnabled`) based on whether the relevant config block has non-default values. The hot path checks these flags first; if a feature is at defaults, the per-hit cost is one bool read.

### Rule matrix opt-in

When `RuleMatrix.Enabled = false`, the rule matrix code path is never entered. The case-based scaling path has the same complexity it had in v1.0.

## Measuring on your server

### `/pdg timing`

Enable the hook timing wrapper:

```
/pdg timing on
```

Play through some combat (Bradley fight, scientist encounter, PvP exchange). Then:

```
/pdg timing

Hook timing: Enabled=True, samples=847, mean=42us, p95=87us, max=312us. Usage: /pdg timing [on|off|clear]
```

Interpretation:
- **mean** is the average time spent in the hook per damage event in microseconds.
- **p95** is the 95th percentile - 95% of hits complete in at most this time.
- **max** is the worst-case observation in the rolling 1000-hit window.

When you're done measuring, turn it back off (the wrapper has minor overhead even when it has nothing to record):

```
/pdg timing off
```

### Benchmark targets

These are rough targets for a healthy production server. Your numbers will vary based on Rust version, plugin load, and hardware.

| Server scale | Target mean | Target p95 | Notes |
|---|---|---|---|
| Casual (32 players) | < 30us | < 80us | Most hits are short-circuited at the cache or env check. |
| Standard (100 players) | < 50us | < 120us | Cache hit rate matters; warm up before measuring. |
| High-pop (200+) | < 80us | < 200us | Bradley fights spike to the upper end of p95. |

If you see significantly worse numbers, things to check:

1. **Trace logging enabled?** `Trace` log level allocates strings on every hit. `/pdg log Reflects` to drop back to normal.
2. **Discord webhook flooding?** Each webhook send queues an async HTTP request. The send itself doesn't block the hook, but the queueing has overhead. Bump `MinLevel` to reduce the volume.
3. **Validation issues?** `/pdg validate` and fix any reported issues. Some issues (e.g. malformed rule actions) cause exceptions on every matching hit that are silently retried.
4. **Cache turnover?** `/pdg cache` - if the cache is repeatedly filling and clearing, your server has very high entity churn and you may want to bump `CacheMaxEntries` (requires source edit, not yet exposed in config).

## Memory

The classification cache is the main memory consumer added by v1.5. At 10000 entries it uses approximately 1-2 MB depending on subtype string sharing. The hook-timing rolling buffer is fixed at 8 KB (1000 longs).

PVEDamageGuard's overall memory footprint is dominated by the rule matrix config dictionaries; for a typical server config the plugin uses well under 5 MB total.

## Hook ordering

PVEDamageGuard does not call `Unsubscribe` or set hook priorities; it relies on Oxide's default ordering. If another plugin also hooks `OnEntityTakeDamage` and returns a non-null value, PVEDamageGuard never sees the hit. This is intentional: it preserves the "yield to TruePVE" companion mode pattern. If you need PVEDamageGuard to win the hook, unload the conflicting plugin.

## Profiling

For deeper profiling beyond `/pdg timing`, use Oxide's built-in profiler:

```
oxide.timer.threshold 5
```

This logs any hook taking more than 5ms. PVEDamageGuard hits should never appear in the threshold log unless something is very wrong (a classification cache populating a huge map, an OnEntitySpawned storm during heli combat, etc.).
