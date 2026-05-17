# Console Reference

For admins running PVE Damage Guard via **Pterodactyl Panel**, **Pufferpanel**, **Wisp**, **plain RCON**, or any other non-chat console interface. These commands work without you being an in-game player.

## Chat vs console: the distinction

PVE Damage Guard registers each command twice via Oxide's Covalence framework:

- **Chat command** — typed in the in-game chat box (the F1 chat or the on-screen chat UI). Always prefixed with `/`.
  ```
  /pdg
  /pdg log Scaled
  /pdg test
  ```

- **Console command** — typed in the server console, RCON window, or web panel command field. **Never** prefixed with `/`.
  ```
  pdg
  pdg log Scaled
  pdg validate
  ```

Same plugin, same code, same handler. The difference: from the console, the plugin sees `IPlayer.IsServer == true` and skips permission checks. You're effectively a super-admin when talking to the plugin from the console.

If you type `/pdg` in the Pterodactyl console, it won't work — the Rust server treats the `/` prefix as a chat-only prefix that the console doesn't recognize.

## All commands that work from console

### Status and configuration

| Command | What it does |
|---|---|
| `pdg` | Print the full status block (config flags, feature states, current hour, command list) |
| `pdg reload` | Reload `oxide/config/PVEDamageGuard.json` and lang files; re-run validation |
| `pdg validate` | Run the config validator and report issues |
| `pdg selftest` | Re-run the Rust-type-resolution self-test (verifies plugin can compile against current Rust types) |

### Tuning

| Command | What it does |
|---|---|
| `pdg scale <DamageType> <multiplier>` | Set NPC->Player scaling for one damage type. Persists. Example: `pdg scale Bullet 0.1` |
| `pdg preset <name>` | Apply a known-good complete config snapshot. Names: `pvepure`, `pvereflect`, `pvevehicleraids`, `pvphoursevents`. **Overwrites your existing config.** |
| `pdg import damagecontrol` | Read `oxide/config/DamageControl.json` and migrate compatible fields. Backs up your current PDG config first |

### Logging and observability

| Command | What it does |
|---|---|
| `pdg log <None\|Reflects\|Scaled\|All\|Trace>` | Set console log verbosity |
| `pdg logfile <on\|off>` | Toggle writing log lines to `oxide/logs/PVEDamageGuard/damage-YYYY-MM-DD.txt` |
| `pdg history [N]` | Print last N classified hits (default 10, max 100) |
| `pdg hour` | Show current in-game hour and all four TOD multipliers |
| `pdg timing` | Show hook timing stats (mean/p95/max in microseconds over last 1000 hits) |
| `pdg timing <on\|off\|clear>` | Control hook timing recording |
| `pdg cache` | Show classification cache size |
| `pdg cache clear` | Flush the per-entity classification cache |
| `pdg events` | List currently tracked events: entity events, RaidableBases domes, global events |

### Rule matrix

| Command | What it does |
|---|---|
| `pdg rules` | List all rule-matrix contexts and the default name |
| `pdg rules <context>` | Print direct + inherited rules of one context |
| `pdg categories` | List custom NPC categories registered by other plugins via API |

### Discord webhook

| Command | What it does |
|---|---|
| `pdg webhook` | Show webhook status (enabled, URL set?, min level, rate limit, recent send count) |
| `pdg webhook test` | Send a one-shot test message |
| `pdg webhook on` / `pdg webhook off` | Toggle the webhook enabled flag without editing config |

### Stats

| Command | What it does |
|---|---|
| `pdg stats <steamid>` | Show damage stats for a specific player by Steam ID |
| `pdg stats <partial-name>` | Look up by partial name (works if the player has connected at least once) |

(Without args, `pdg stats` requires the caller to be an in-game player. From console, always pass an explicit Steam ID or name.)

### Help

| Command | What it does |
|---|---|
| `pdg help` | Print the subcommand list with one-line descriptions |
| `pdg help <subcommand>` | Print usage and example for one subcommand |

## Commands that DON'T work from console

These need an in-game player for position, aim, or UI rendering. From console, they'll reply with `"must be run by an in-game player"` and exit.

| Command | Why it can't run from console |
|---|---|
| `pdg test` | Raycasts from your in-game crosshair. No crosshair from console. |
| `pdg test fire <type> <amount>` | Same — needs an in-game target |
| `pdg context` | Resolves the rule-matrix context at your in-game position. No position from console. |
| `pdg ui` | Opens the CUI admin panel, which only renders for in-game players |
| `pdg close` | Closes the CUI panel, no-op when no player |
| `pdg stats` (no args) | Defaults to "your" stats; from console there's no "you" |

For everything in this group, you need to log in to the game and use `/pdg ...` instead.

## Oxide-level commands you'll use alongside

PVEDamageGuard inherits Oxide's standard plugin and permission management. These aren't part of the plugin itself but you need them to manage it:

| Command | What it does |
|---|---|
| `oxide.plugins` | List all loaded plugins (also written as `o.plugins`) |
| `oxide.load PVEDamageGuard` | Load the plugin if it's not auto-loaded |
| `oxide.unload PVEDamageGuard` | Unload it |
| `oxide.reload PVEDamageGuard` | Reload (equivalent to `pdg reload` plus a full plugin restart) |
| `oxide.grant user <steamid> pvedamageguard.admin` | Grant chat-command access to one player |
| `oxide.grant group admin pvedamageguard.admin` | Grant to the admin group |
| `oxide.grant group moderator pvedamageguard.admin` | Grant to moderators |
| `oxide.grant user <steamid> pvedamageguard.bypass` | Grant damage-immunity (testing only) |
| `oxide.revoke user <steamid> pvedamageguard.admin` | Remove perm from one player |
| `oxide.revoke group admin pvedamageguard.admin` | Remove from a group |
| `oxide.show user <steamid>` | Show what perms and groups one player has |
| `oxide.show perm pvedamageguard.admin` | Show who currently has this perm |
| `oxide.show perm pvedamageguard.bypass` | Show who has damage immunity |

If you're on **Carbon** instead of Oxide, the commands are the same but prefixed with `carbon.` instead of `oxide.` (Carbon also accepts the `oxide.` aliases).

## CUI button commands

The `pdgui.*` commands are wired to the CUI panel's buttons. You probably never need to call them from console, but they're valid if you want to script the UI somehow:

| Command | What it does |
|---|---|
| `pdgui.tab <status\|logging\|history\|rules\|scaling>` | Switch CUI tab for the calling player |
| `pdgui.close` | Close the calling player's CUI panel |
| `pdgui.logfilter <level>` | Set the Logging tab's level filter |
| `pdgui.histpage <prev\|next\|first>` | Navigate History tab pagination |
| `pdgui.rulesctx <contextname>` | Set the Rules tab's viewing context |
| `pdgui.rulesedit` | Toggle Rules tab edit mode |
| `pdgui.ruleaction <context> <rule-key>` | Cycle a rule's action through allow/block/reflect:1.0/scale:0.5 |
| `pdgui.ruledel <context> <rule-key>` | Delete a rule from a context |
| `pdgui.scalemod <field> <delta>` | Adjust a Scaling tab multiplier by delta (e.g. `+0.1`, `-0.01`) |
| `pdgui.toggle <field>` | Flip a Scaling tab boolean toggle |
| `pdgui.dropdown <field> <value>` | Set a Scaling tab dropdown (Logging, TimeOfDaySource) |

These all require the calling context to have `IsServer` or hold `pvedamageguard.admin`. From the Pterodactyl console you have `IsServer`, so any of these work.

## Common Pterodactyl workflows

### First-time setup after installing the plugin

```
oxide.reload PVEDamageGuard
oxide.grant user 76561198000000000 pvedamageguard.admin
oxide.grant group admin pvedamageguard.admin
pdg preset pvereflect
pdg validate
pdg
```

The status block at the end confirms what loaded. Look for `Config issues: 0`.

### Tune NPC damage on the fly

```
pdg scale Bullet 0.1
pdg scale Explosion 0.5
pdg
```

Changes save to config and take effect immediately. No reload needed.

### Diagnose unexpected damage behavior

```
pdg log Scaled
pdg logfile on
# play through some combat in-game
pdg history 30
pdg log None
pdg logfile off
```

Or use the in-game `/pdg test` aimed at the entity in question.

### Performance check

```
pdg timing on
# play through 60-90 seconds of combat in-game
pdg timing
pdg timing off
```

Target on a 100-player server: mean < 50us, p95 < 120us, max < 200us.

### Migrate from Damage Control

```
oxide.unload DamageControl
pdg import damagecontrol
pdg validate
pdg
```

Then move `DamageControl.cs` out of `oxide/plugins/` so it doesn't auto-load on restart.

### Set up Discord webhooks

```
# 1. Edit oxide/config/PVEDamageGuard.json:
#    - DiscordWebhook.Enabled = true
#    - DiscordWebhook.Url = "https://discord.com/api/webhooks/..."
#    - DiscordWebhook.MinLevel = "Reflects"
# 2. Then in the console:
pdg reload
pdg webhook test
pdg webhook
```

### Apply a different preset and confirm

```
pdg preset pvevehicleraids
pdg validate
pdg
```

Each preset overwrites your config completely. Backup first if you want a fall-back.

### Verify the plugin is healthy after a Rust forced wipe

```
pdg selftest
pdg validate
pdg
```

If `selftest` fails, paste the output into a GitHub issue — that means Facepunch changed a type PVEDamageGuard depends on.

## Tips for Pterodactyl specifically

- **Console output is logged to file** in addition to the panel. You can grep `wings/servers/<uuid>/logs/` for `PVE Damage Guard` to find startup banners and warnings.
- **Multi-line output** (like `pdg help` or `pdg history 30`) renders fine in the panel — scroll the console window to see it all.
- **Command history**: Pterodactyl remembers the last N commands you've typed. Hit the up arrow to re-run a recent one.
- **Schedule / cron**: Pterodactyl's schedule feature can run console commands on a cron. Useful for daily `pdg cache clear` if you ever decide you need it (you generally don't; auto-invalidation on entity-kill handles it).

## RCON

If you're using a raw RCON tool (rcon.io, RustEdit, custom client), the command syntax is identical. RCON connections have `IsServer == true` implicitly, so all the console commands above work.

## What to do if a command doesn't work

1. **Typo'd `/pdg` instead of `pdg` from console?** Drop the leading slash.
2. **The plugin isn't loaded?** Run `oxide.plugins` to check; load with `oxide.load PVEDamageGuard` if missing.
3. **Got "no permission" from chat in-game?** Grant the perm: `oxide.grant user <your-steamid> pvedamageguard.admin`. Server-admin flag (ownerid/moderatorid) implicitly grants it.
4. **Got "must be run by an in-game player"?** That command is in the chat-only list above. Use the in-game `/pdg ...` instead.
5. **Got "Unknown command pdg" from console?** The plugin didn't load successfully. Check the Oxide log for compile errors: `wings/servers/<uuid>/logs/oxide_*.log`.

If none of those fix it, open a GitHub issue per [SUPPORT.md](../SUPPORT.md) with the exact command you typed, the output you got, and the relevant log lines.
