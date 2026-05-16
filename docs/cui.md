# CUI Admin Panel

PVEDamageGuard ships an in-game CUI (Concentric UI) admin panel that's being built incrementally across v1.6 through v1.9. v1.6 ships the framework and the first tab. Per the [roadmap](../ROADMAP.md), additional tabs are added one wipe-cycle at a time so each gets battle-tested before v2.0 launches.

## Opening the panel

In game as an admin:

```
/pdg ui
```

The panel opens centered on screen with five tabs. You must hold the `pvedamageguard.admin` permission or have the server-admin flag.

To close: click the **X** in the title bar, or run `/pdg close` in chat, or run `pdgui.close` in the F1 console.

## What works in v1.6

| Tab | Status |
|---|---|
| **Status** | Functional. Read-only mirror of the `/pdg` status text output, styled as a CUI panel with color-coded section headings. |
| **Logging** | Placeholder. Functional in v1.7. Use `/pdg log <level>` and `/pdg logfile <on/off>` for now. |
| **History** | Placeholder. Functional in v1.7. Use `/pdg history [N]` for now. |
| **Rules** | Read-only browser of the rule matrix (v1.8). Editable in v1.9. Shows contexts, inheritance chains, color-coded action types. |
| **Scaling** | Placeholder. Functional in v1.9. Use `/pdg scale <type> <mult>` and edit the JSON config for now. |

Clicking a placeholder tab shows you which version brings that functionality and which CLI command to use in the meantime.

## Theme and design

The panel uses a dark theme tuned for legibility during gameplay:

- Background: very dark gray with 95% alpha (lets you still see combat through it)
- Active tab: coral accent (`#ee7d5a`) at reduced alpha
- Inactive tabs: medium gray
- Text: near-white for primary, medium gray for hints/muted info
- Section headings in Status tab: coral accent

You cannot retheme the panel via config in v1.6. Theme customization is on the v2.x feature wishlist.

## Permission model

Same as the chat commands:

- `pvedamageguard.admin` lets you open the panel.
- `pvedamageguard.bypass` is unrelated to the panel - that's the damage-immunity perm.

A player without the admin perm who somehow gets the `pdgui.tab` console command to fire will be silently rejected; the panel won't open.

## Lifecycle

| Event | Behavior |
|---|---|
| Player disconnects with panel open | Panel state cleared from server; next reconnect starts fresh. |
| Plugin reloads (`oxide.reload`) | All open panels are destroyed cleanly. Admins reopen with `/pdg ui` after reload. |
| Server restarts | Same as plugin reload. |

## Why a CUI panel at all

Most server admins are comfortable in chat and config files. The CUI exists for the case where a non-technical co-admin needs to make a quick tune - "the heli is too lethal tonight, scale down NPC explosion damage" - without learning the JSON schema or the full `/pdg` command set.

Slash commands remain canonical; the CUI is a layered convenience. Anything the CUI does eventually (in v1.9) can also be done via `/pdg` commands or by editing the config file directly.

## Known limitations in v1.6

- Status tab is a snapshot at open-time; close and reopen to refresh. Live updates come in v1.7 (it's the natural time to add the streaming logger).
- Only 5 tabs are wired (Status, Logging, History, Rules, Scaling). New tabs would require source edits in v1.7+.
- No theming. Coral accent is hardcoded.
- No size adjustment. Panel is fixed at 64% screen width by 67% screen height. Should be readable at all common Rust resolutions but extreme ultra-wide monitors may want adjustment.

## Troubleshooting

| Symptom | Cause / Fix |
|---|---|
| `/pdg ui` says "must be run by an in-game player" | You ran it from server console / RCON. The CUI only renders for in-game players. |
| Panel opens but cursor isn't visible | The CUI sets `CursorEnabled = true` on the main panel. If your cursor still doesn't appear, try `/pdg close` then `/pdg ui` again. |
| Panel stays on screen after I unload PVEDamageGuard | Plugin's `Unload()` hook should have destroyed it. If you see a stuck panel, type `bind f1 cui.destroy PvedgPanel` in console and hit F1, or rejoin the server. |
| Other admins see the panel I opened | The CUI is rendered per-player; only you see your own panel. If you do see another admin's panel, that's a bug - please file an issue. |
