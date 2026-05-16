# Installation

## Requirements

- A Rust dedicated server running [Oxide / uMod](https://umod.org/) (Carbon should also work but is untested under load).
- Server admin access (file system + RCON or in-game admin).

## Standard install

1. Download `oxide/plugins/PVEDamageGuard.cs` from the [latest release](https://github.com/gjdunga/PVEDamageGuard/releases) or from the `main` branch of this repo.
2. Copy it to your server's `oxide/plugins/` directory.
3. Oxide auto-loads new plugins. You can also force-load with:
   ```
   oxide.reload PVEDamageGuard
   ```
4. On first load, a default config is written to `oxide/config/PVEDamageGuard.json` and lang files are written to `oxide/lang/<lang>/PVEDamageGuard.json`.
5. Grant admin chat-command access to your moderators:
   ```
   oxide.grant user <steamid> pvedamageguard.admin
   oxide.grant group admin pvedamageguard.admin
   ```
6. (Optional, for damage-immune admin testing only) grant the bypass perm:
   ```
   oxide.grant user <steamid> pvedamageguard.bypass
   ```

## Verifying it loaded

In server console:
```
oxide.plugins
```
You should see `PVE Damage Guard (1.0.0) by Gabriel Dungan (DunganSoft Technologies)`.

Then in-game as an admin:
```
/pdg
```
This dumps the current configuration. If you get "You do not have permission" check that you have the `pvedamageguard.admin` perm or the `IsAdmin` server flag.

## Companion mode with TruePVE

If [TruePVE](https://umod.org/plugins/true-pve) is also installed, PVEDamageGuard auto-detects it and switches to companion mode on load. You will see:
```
TruePVE detected. Yielding allow/block to TruePVE; PVEDamageGuard will only classify, scale, and reflect-on-request.
```
This means TruePVE decides whether a PvP hit is allowed (per its own rule sets and zones), and PVEDamageGuard layers NPC scaling on top. PvP reflect is disabled in companion mode by default, since most TruePVE servers either block PvP outright or have a separate reflect plugin (like [PunishAttacker](https://codefling.com/plugins/punishattacker) or [ReflectDamage](https://codefling.com/plugins/reflectdamage)).

To override companion mode and run both reflects in parallel, set `"Yield allow/block decisions to TruePVE..."` to `false` in the config. **Test carefully** - both plugins hooking the damage pipeline can produce surprising results.

## Updating

PVEDamageGuard is updated frequently around Rust's monthly forced wipes (first Thursday of each month). To update:

1. Replace `oxide/plugins/PVEDamageGuard.cs` with the new version.
2. `oxide.reload PVEDamageGuard`.
3. Read the [CHANGELOG](../CHANGELOG.md) for any breaking config changes.

## Uninstalling

```
oxide.unload PVEDamageGuard
```
Then delete `oxide/plugins/PVEDamageGuard.cs`. Config and lang files are left in place in case you reinstall.
