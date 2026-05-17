# Comparison vs Existing PVE Plugins

Honest assessment of how PVE Damage Guard stacks up against the popular alternatives as of May 2026. Based on the marketplace research that informed the v2.0 launch decision.

## TL;DR

| You currently run | Verdict |
|---|---|
| **Damage Control** (Wulf / MSpeedie, free, uMod) | Migrate. PVEDamageGuard has the same scaling features plus future-proof NPC detection. Run `/pdg import damagecontrol` to bring your config over. |
| **TruePVE alone** (nivex, free, uMod) | Add PVEDamageGuard as a companion. TruePVE keeps allow/block; we layer scaling, reflect, classification, and the CUI on top. |
| **TruePVE + DynamicPVP + ReflectDamage** (the canonical free stack) | PVEDamageGuard replaces DynamicPVP (event-aware context switching is built in) and ReflectDamage (we reflect with re-entrancy safety and structure-damage coverage). Keep TruePVE for zone-based rule sets. |
| **NextGenPVE / SimplePVE / War Mode / Real PvE** (paid) | Each has different strengths. PVEDamageGuard is differentiated on: future-proof classifier, per-attacker scaling, the full CUI tab set, public API for ecosystem integration. Mix as needed. |
| Nothing right now | Start with PVEDamageGuard standalone. Rule matrix gives you most of what TruePVE provides, plus per-damage-type scaling that no allow/block plugin has. |

---

## vs Damage Control (Wulf / MSpeedie)

| Capability | Damage Control 2.5.x | PVEDamageGuard 2.0 |
|---|---|---|
| NPC detection | Prefab-name string matching (`ShortPrefabName.Contains("scientist")`) | Type-based (`BasePlayer.IsNpc`, `BaseNpc`, vehicle NPC types) |
| Breaks on new Facepunch NPC types | Yes, every time | No, by design |
| Per-victim scaling | Yes (per-prefab) | Yes (per-subtype, via classifier) |
| Per-attacker scaling | No (victim-side only) | Yes |
| Time-of-day modifiers | 8 categories × 24 hours | 4 categories × 24 hours (Global / PvP / NpcToPlayer / NpcToStructure) |
| Building grade multipliers | Yes | Yes |
| Heli bypass | Yes (bool flag) | More flexible: `PerAttackerStructureScaling["PatrolHelicopter"]` per-attacker override |
| Rule matrix | No | Yes (opt-in) |
| Discord webhooks | No | Yes |
| CUI admin panel | No | Yes |
| Public API | No | Yes (10+ hooks) |
| Custom NPC category registration | No | Yes (`API_RegisterCategory`) |
| Maintenance | Active until MSpeedie steps away; no public commitment | Same-week patches every forced wipe; < 48h critical-bug SLO |
| Last update | n/a (forked v2.5.14) | 2026-05 (active) |
| Price | Free | $15 Codefling, free on GitHub |

**Migration path**: `/pdg import damagecontrol` on a v1.3+ install reads `oxide/config/DamageControl.json` and maps every supported field. Backs up your current PVEDamageGuard config first. See [INSTALL.md](../INSTALL.md#migrating-from-damage-control).

---

## vs TruePVE (nivex)

PVEDamageGuard and TruePVE are **complementary**, not competitive. Different problem domains.

| Capability | TruePVE | PVEDamageGuard |
|---|---|---|
| Primary value | Allow/block rule sets with entity-group abstractions | Type-based classifier + per-damage-type scaling + reflect |
| Rule expression | RuleSets + Entity Groups | RuleMatrix with contexts + inheritance |
| Scaling per damage type | No (binary allow/block) | Yes |
| Reflect | No (companion plugins handle it) | Yes (self-contained) |
| Per-attacker NPC scaling | No | Yes |
| Per-victim scaling | No (allow/block) | Yes |
| Time of day | Via separate plugins | Built in |
| In-game CUI panel | No (console only) | Yes (5-tab panel) |
| Diagnostic /test command | No | Yes (`/pdg test` + `test fire`) |
| Public API | Yes (allow/block hooks) | Yes (10+ hooks including reflect-as-service) |
| Ecosystem integrations | Tightly integrated with DynamicPVP, RaidableBases, etc. | Auto-detects same set + Backpacks |
| Maintenance | nivex; active | Gabriel Dungan; active, same wipe cadence |
| Price | Free | $15 Codefling, free on GitHub |

**Companion mode**: when both are loaded, PVEDamageGuard auto-yields PvP allow/block decisions to TruePVE and only applies scaling on top. You see one log line at startup confirming companion mode. Config: leave `YieldToTruePVE=true` (default).

**When to run TruePVE alone**: you only need allow/block rule sets and you don't care about per-damage-type scaling, reflect, the CUI panel, or diagnostics.

**When to run PVEDamageGuard alone**: you want the rule matrix without TruePVE's larger surface area, or you want PvP reflect as a first-class feature.

**When to run both**: you want TruePVE's mature zone integrations AND our scaling/reflect/CUI/classifier. This is the most common production combo and what we test against.

---

## vs the canonical free stack (TruePVE + DynamicPVP + ReflectDamage)

This is the most common PVE setup on free Rust servers. Three plugins, three maintainers, three update cadences to track.

| Capability | Free stack | PVEDamageGuard |
|---|---|---|
| PvP allow/block | TruePVE | RuleMatrix (or yield to TruePVE) |
| Per-zone rules | TruePVE + ZoneManager | Same (we integrate ZoneManager too) |
| Event-aware context switching | DynamicPVP | Built-in EventTracker for Bradley/Heli/Cargo; GlobalEventTriggers for Convoy/ArmoredTrain |
| PvP reflect | ReflectDamage | DoReflect with re-entrancy guard |
| Foreign-structure reflect | No (ReflectDamage covers PvP only) | Yes (v1.7.1+) |
| NPC damage scaling | None (TruePVE is allow/block) | Per-attacker per-damage-type |
| Time-of-day | None | Built in |
| Building grade multipliers | None | Built in |
| Backpacks-on-death integration | Plugin-by-plugin | `API_IsPveDeath` central hook |
| Discord webhooks | None | Built in |
| CUI panel | None | Yes |
| Number of plugins to install | 3 (or 4 with ZoneManager) | 1 (+ ZoneManager if needed) |
| Number of update tracks to follow | 3+ | 1 |
| Cost | Free | $15 Codefling, free on GitHub |

**Tradeoff**: PVEDamageGuard replaces DynamicPVP and ReflectDamage outright. You can still run TruePVE alongside in companion mode. Net result: one fewer plugin to update, one config schema, one support channel.

---

## vs Codefling paid alternatives

The marketplace research found these as actively maintained paid PVE plugins on Codefling. Quick comparisons.

### vs SimplePVE (Iftebinjan, $15)
SimplePVE has a CUI editor (`/sprules`) and Discord, both shipped. PVEDamageGuard's CUI is more comprehensive (5 tabs, not just rules editor) and has different focus (classifier moat). If SimplePVE works for your server and you don't need the per-damage-type scaling or reflect-as-service, stay there. If you've outgrown allow/block and need finer control, switch.

### vs War Mode PVP/PVE (Mr01sam, $24.99)
War Mode is per-player flagging — players self-flag PvP or PVE. Different problem entirely. Compatible with PVEDamageGuard; we don't conflict because War Mode operates on player state not damage routing. If you want both per-player flagging AND damage classification, run both.

### vs Real PvE (IIIaKa, $24.99-$39.99)
Real PvE is a full PVE framework: loot queues, paid events, owned-structure damage, NPC aggression rules, API. Substantially more scope than us. If you want a complete PVE server framework with built-in event paywall mechanics, Real PvE is the right choice. PVEDamageGuard is a focused damage layer that you can use independently or alongside Real PvE.

### vs Customizable Protection (0xF, $20)
Per-armor protection profiles. Operates at a different layer (player-protection scaling) from us (damage-source classification). Compatible; consider running both if you want both axes of control.

### vs PunishAttacker (MON@H, $15)
Reflect + escalating punishment for PVE rule violators. PVEDamageGuard's reflect is more general (covers structure damage, has re-entrancy guard, exposes API). PunishAttacker adds escalation (ban after N violations) that we deliberately don't do. Reasonable to run PunishAttacker AND PVEDamageGuard if you want both reflect AND escalation; the reflect-double will need configuration on your part.

---

## What PVE Damage Guard is NOT

Be honest about scope so buyers don't bounce on misaligned expectations.

- **Not a full PVE server framework.** No loot queue, no paid events, no shop integration. Damage layer only.
- **Not a TruePVE replacement.** We coexist as a companion; admins who want zone-based rule sets with entity groups should keep TruePVE and add us.
- **Not a raid base spawner.** Auto-detects RaidableBases domes but doesn't spawn them.
- **Not a Discord bot.** Webhook output yes; standalone bot no.
- **Not a player administration tool.** No ban / mute / kick / inventory commands. Pair with AdminMenu or similar.

---

## The pitch in one paragraph

PVE Damage Guard is the damage handler that doesn't break every wipe. Type-based NPC classifier survives Facepunch's content updates without code changes. Per-attacker per-damage-type scaling, foreign-structure reflect, rule matrix with context switching, full CUI admin panel, Discord webhooks, per-player stats, ten-plus public API hooks for ecosystem integration. Standalone or as a TruePVE companion. One plugin replaces DamageControl + ReflectDamage + DynamicPVP. Same-week patches every forced wipe. $15 one-time, unlimited servers, GPL-3.0 source on GitHub.
