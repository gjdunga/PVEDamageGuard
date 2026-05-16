# Integration: Discord Webhooks

PVEDamageGuard can forward log events to a Discord webhook URL with token-bucket rate limiting. Useful for moderation channels watching for PvP attempts on PVE servers or for an audit feed of reflects/blocks.

## Setup

1. In Discord, open the channel you want messages in.
2. Server Settings -> Integrations -> Webhooks -> New Webhook.
3. Name the webhook (e.g. `PVE Damage Guard`), pick an avatar, copy the **Webhook URL**.
4. Paste the URL into `oxide/config/PVEDamageGuard.json`:

```jsonc
"DiscordWebhook": {
  "Enabled": true,
  "Url": "https://discord.com/api/webhooks/...",
  "MinLevel": "Reflects",
  "RateLimitPerMinute": 20,
  "MessagePrefix": "[FACEWAN] ",
  "Username": "PVE Damage Guard",
  "AvatarUrl": ""
}
```

5. `/pdg reload` or `oxide.reload PVEDamageGuard`.
6. `/pdg webhook test` to verify.

## What gets sent

Every line that the in-game logger emits at or above `DiscordWebhook.MinLevel`. Suggested levels:

| MinLevel | Discord traffic | Use case |
|---|---|---|
| `None` | nothing | webhook configured but turned off via `Enabled=false` |
| `Reflects` | only PvP reflect events | typical moderation channel - alerts on every PvP attempt |
| `Scaled` | reflects + every NPC-scaled hit + structure blocks | audit-grade feed; will be noisy on busy servers |
| `All` | also passthroughs | rarely useful for Discord; use file logging instead |
| `Trace` | full HitInfo dump | never appropriate for Discord - use file logging |

The recommended starting point is `Reflects`. Bump to `Scaled` only if you need audit-grade visibility AND your rate limit can handle it.

## Rate limiting

Discord caps webhook posts at 30/minute per URL. PVEDamageGuard defaults to 20/min to leave headroom. The rate limiter is a sliding 1-minute window: when 20 messages have been sent in the last 60 seconds, additional messages are silently dropped until the window opens up.

If your server is busy enough to hit the cap regularly:
- Raise `MinLevel` to a less chatty setting.
- Lower `RateLimitPerMinute` to ensure you never get throttled by Discord itself (Discord's 429 response can briefly disable the webhook).
- Use file logging (`/pdg logfile on`) for full audit; use Discord only for highlights.

## Message format

```
[PrefixIfConfigured] tag attackerCat(attackerName) -> victimCat(victimName) | DamageType totalDamage
```

Example reflects-level line:
```
[FACEWAN] [reflect] PlayerA -> PlayerB | Bullet 45.0
```

Lines are truncated at ~1900 characters (Discord's 2000-char limit minus headroom for the prefix).

## Username and avatar

- `Username` overrides what name the webhook posts as. Default `PVE Damage Guard`. Set to empty string to use the webhook's configured default.
- `AvatarUrl` overrides the avatar shown next to messages. Empty string uses the webhook's default avatar.

## Toggling at runtime

- `/pdg webhook on` / `/pdg webhook off` - flip Enabled without editing config.
- `/pdg webhook test` - send a one-shot test message. Useful after setup to verify the URL is correct and the channel can receive.
- `/pdg webhook` (no args) - show current status, recent send count, configured min level.

## Security

- **Treat the webhook URL as a secret.** Anyone with the URL can post arbitrary messages to that channel.
- Do not commit `PVEDamageGuard.json` to a public repo with the URL filled in.
- Discord allows you to regenerate a webhook URL at any time (Edit Webhook -> Copy New URL).

## Troubleshooting

| Symptom | Cause | Fix |
|---|---|---|
| `/pdg webhook test` says "queued" but nothing shows in Discord | Wrong URL, or webhook deleted in Discord | Regenerate URL in Discord, paste into config, `/pdg reload`. |
| Console shows `Discord webhook returned HTTP 429` | Discord rate limiting (you exceeded 30/min) | Lower `RateLimitPerMinute` and/or raise `MinLevel`. |
| Console shows `HTTP 401` | Webhook URL is invalid or revoked | Regenerate in Discord. |
| Messages arrive but with wrong username | `Username` field set differently than expected | Edit and `/pdg reload`. |
| First message arrives but rest don't | You hit the rate limit; sliding window is full | Wait 60 seconds; the queue clears as old entries age out. |
