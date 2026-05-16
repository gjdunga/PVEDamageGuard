# Permissions

PVEDamageGuard ships two permissions, deliberately split so you can grant chat-command access without granting damage immunity, and vice versa.

| Permission | Effect |
|---|---|
| `pvedamageguard.bypass` | Holder takes full vanilla damage (PvP, NPC, environment) - all PVEDamageGuard rules are skipped for them as victim. **Damage immunity bypass only; does NOT grant chat command access.** |
| `pvedamageguard.admin`  | Allows use of the `/pdg` chat commands. Does NOT grant damage immunity. |

## Granting

Standard Oxide commands:

```
# To a specific player
oxide.grant user 76561198000000000 pvedamageguard.admin

# To an Oxide group (e.g. the built-in 'admin' group)
oxide.grant group admin pvedamageguard.admin
oxide.grant group moderator pvedamageguard.admin

# Bypass for damage-testing on a staging server
oxide.grant user 76561198000000000 pvedamageguard.bypass
```

## Revoking

```
oxide.revoke user 76561198000000000 pvedamageguard.admin
oxide.revoke group admin pvedamageguard.bypass
```

## Server admin (`IsAdmin`)

Players with the server admin flag (granted via `ownerid` / `moderatorid` in `users.cfg` or by being on the auth list) implicitly have `pvedamageguard.admin` without needing the explicit grant. They do NOT implicitly have `pvedamageguard.bypass` - that must be granted explicitly even for server owners (so admin testing doesn't quietly differ from player experience by accident).

## Console / RCON

The server console (`IsServer == true`) is always allowed to run `/pdg` commands. RCON-connected admin tools therefore work without any permission grant.

## Why split bypass from admin

Most damage plugins conflate "I can change settings" with "I can't be hurt". This is a security smell - it means giving a moderator the ability to change scaling forces you to also give them invulnerability, which they may not want or be expected to have.

PVEDamageGuard's split lets you:
- Give moderators command access (`admin`) so they can tune scaling and logging without being unkillable.
- Give a dedicated test account immunity (`bypass`) so you can verify damage behavior from a controlled state without granting admin powers.

## Inspecting current grants

```
oxide.show user 76561198000000000
oxide.show perm pvedamageguard.admin
```
