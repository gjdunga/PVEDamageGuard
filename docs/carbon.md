# Carbon Framework Support

PVEDamageGuard is declared compatible with the [Carbon](https://carbonmod.gg/) framework as of v1.5.0. Carbon is a faster, more modern alternative to Oxide/uMod that runs Oxide plugins with minimal changes.

## Compatibility status

| Concern | Status |
|---|---|
| Standard Oxide hooks (`OnEntityTakeDamage`, `OnEntitySpawned`, `OnServerInitialized`, etc.) | Works in both |
| `[Command]` chat commands (Covalence) | Works in both |
| `[PluginReference]` cross-plugin references | Works in both |
| `[HookMethod]` public API | Works in both |
| `Oxide.webrequest` for HTTP | Works in both (Carbon provides Oxide compatibility) |
| Newtonsoft.Json | Bundled with both |
| Config / lang file conventions | Same paths in both |
| `oxide.reload`, `oxide.unload` | Carbon has equivalents (`carbon.reload`, etc.) but supports the `oxide.` aliases |

PVEDamageGuard uses only the hooks and APIs above. There are no Oxide-specific features in v1.5.0 that don't have direct Carbon equivalents.

## Verification on a Carbon server

1. Drop `PVEDamageGuard.cs` into `carbon/plugins/`.
2. Server console:
   ```
   carbon.reload PVEDamageGuard
   ```
3. Expect output:
   ```
   PVE Damage Guard v1.5.0 loaded. ...
   Self-test: 10/11 checks passed.
   ```
   (TOD_Sky may not be ready at load time and is the soft-fail.)
4. In-game as admin:
   ```
   /pdg
   ```
   Status block should print normally.
5. `/pdg selftest` - all hard checks should pass.
6. `/pdg test` aimed at a scientist - should classify as `HumanNpc`, subtype `Scientist`.

If any of these fail, please open a [GitHub issue](https://github.com/gjdunga/PVEDamageGuard/issues) with the Carbon version and the error output.

## Known differences

None at v1.5.0. This section will be updated as Carbon-specific issues are reported.

## Why ship on both

Carbon has a vocal portion of the Rust modding community, particularly servers prioritizing performance. uMod remains the dominant ecosystem and the platform most plugins (TruePVE, RaidableBases, Convoy) are tested on. Supporting both costs essentially nothing because our hook surface is small and uses only well-defined Oxide APIs that Carbon faithfully reimplements.

## Compatibility commitment

PVEDamageGuard will continue to compile and run on Carbon as long as Carbon maintains compatibility with the standard Oxide hook signatures. If Carbon diverges in a way that breaks PVEDamageGuard, we'll add a conditional compilation branch rather than abandoning either framework.

## See also

- [docs/installation.md](installation.md) - applies to both Oxide and Carbon, the install commands have `carbon.` equivalents for `oxide.` commands.
- [docs/performance.md](performance.md) - performance characteristics apply equivalently to both frameworks.
