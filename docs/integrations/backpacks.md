# Integration: Backpacks

PVEDamageGuard exposes a queryable hook so backpacks-on-death plugins can decide whether to drop a player's backpack based on whether the kill was a PVE-enforced one (reflect, block, or otherwise mediated by PVEDamageGuard).

## Why this matters

On a PVE server, when a player tries to grief another player's base, PVEDamageGuard reflects the damage back at the griefer. If that reflect kills the griefer, **their backpack would normally drop** under default Rust rules — which means the griefer's victim now gets free loot. That's not griefing prevention; it's griefing reward.

The integration: Backpacks plugins query `API_IsPveDeath(BasePlayer victim)` when handling a death event. If the answer is `true`, the backpack stays with the corpse (or is returned to the player on respawn), instead of dropping for whoever happens to be nearby.

## Supported Backpacks plugins

| Plugin | Author | Where | Status |
|---|---|---|---|
| Backpacks | WhiteThunder | [uMod](https://umod.org/plugins/backpacks) | Auto-detected via `[PluginReference] Backpacks` |
| Backpacks 4 | Whispers88 | Codefling | Auto-detected via `[PluginReference] Backpacks4` |

Other Backpacks-like plugins can integrate by calling `API_IsPveDeath` on their own death-handling hook.

## How detection works

PVEDamageGuard marks a player as "recent PVE death" in two cases:

1. **Self-reflect kill** — the player attempted to damage another player or another player's structure, and the reflected damage killed them. The flag is set in `DoReflect` if the reflect amount equals or exceeds the attacker's current health.
2. **Block-induced state** — currently only reflect-kills mark the flag; block doesn't kill anyone (it just stops the damage).

The flag is sticky for **5 seconds** after being set. This gives Backpacks (which fires its own hooks slightly after the death event) plenty of time to query before the flag clears.

## Integration recipe (for Backpacks plugin authors)

```csharp
[PluginReference] private Plugin PVEDamageGuard;

private bool ShouldDropBackpack(BasePlayer victim, HitInfo info)
{
    // Default: drop on death.
    bool dropOk = true;

    // If PVEDamageGuard says this was a PVE-enforced death, don't drop.
    if (PVEDamageGuard != null)
    {
        var pveDeath = PVEDamageGuard.Call("API_IsPveDeath", victim);
        if (pveDeath is bool b && b) dropOk = false;
    }

    return dropOk;
}
```

## Admin configuration

There's no PVEDamageGuard config flag controlling this; the `API_IsPveDeath` hook is always available when PVEDamageGuard is loaded. Whether the Backpacks plugin actually respects the answer depends on that plugin's config.

For Backpacks by WhiteThunder, check its `DropOnDeath` setting. For Backpacks 4, check the equivalent. The Backpacks plugin author owns the decision to call PVEDamageGuard; if they haven't integrated yet, file a feature request on their issue tracker pointing at this doc.

## Verifying the integration works

1. On a test server with both PVEDamageGuard and a Backpacks plugin loaded, place a sleeping bag or wooden wall belonging to another player (you can use admin commands to spoof ownership).
2. As a non-owner non-team player, attack that wall enough times to die from the reflect. (Default v1.7+ behavior reflects damage when you attack a foreign structure.)
3. Check your corpse — backpack should remain with the corpse, not drop as separate loot.
4. If the backpack still drops, the Backpacks plugin isn't calling `API_IsPveDeath`. File an issue on their tracker.

## What if I want PvP reflect kills to drop the backpack anyway?

Different opinion, totally valid: some servers want griefers to lose their backpack as additional punishment. In that case, the Backpacks plugin author can ignore `API_IsPveDeath` or expose its own config flag for the admin to choose.

The PVEDamageGuard side just provides the **signal** ("this was a PVE-enforced death"). The interpretation is the Backpacks plugin author's design choice.

## Edge cases

- **Player dies from NPC** while PVEDamageGuard scaled the damage down — this is NOT a PVE-enforced death from our perspective. We modified the damage but didn't cause the death; the NPC's hit (post-scaling) was what killed them. `API_IsPveDeath` returns false.
- **Player dies from PvP without reflect** (e.g. teammate damage with `AllowTeammateDamage=true`) — also not flagged. We allowed the damage; we didn't cause the death.
- **Player dies from fall/bleed/cold** — we don't touch environmental damage, so the flag is never set for these.

The flag is set if and only if PVEDamageGuard actively bounced damage back to the attacker that then killed them.

## Future work

If demand exists, we can extend `API_IsPveDeath` to include the *cause* (`"reflect-pvp"`, `"reflect-structure"`, etc.) so Backpacks plugins can apply per-cause policies. Open a GitHub issue if you need this.
