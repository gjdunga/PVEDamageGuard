# Feature Tour GIF Script

60-90 second recording script for the marketing GIF. Goes on the Codefling listing as the headline video. Designed for buyers to grasp the plugin's value in under a minute.

## Target spec

- **Length**: 60-90 seconds
- **Resolution**: 1280×720 (smaller is fine; bigger is wasteful for Codefling's display)
- **Format**: GIF (Codefling autoplays) or MP4 with autoplay
- **File size**: under 5 MB for the GIF, under 10 MB for the MP4
- **Frame rate**: 12-15 fps for GIF (keeps file size manageable while staying readable)

## Tools

- **Recording**: OBS Studio (free, professional) or LICEcap (lighter, GIF-native, free)
- **Editing**: ShotCut or DaVinci Resolve if you need to trim. Free alternatives.
- **GIF conversion**: ffmpeg one-liner or ezgif.com for small jobs

## Setup before recording

Same as screenshot capture (see screenshots.md):
1. PVEDamageGuard v2.0.0 loaded
2. Rule matrix enabled in config
3. A few damage events already in history so tabs aren't empty
4. Mouse cursor visible (some recorders need this enabled)
5. Close other game UI (inventory, map) so the recording is clean

## The script (timestamps are approximate)

### Scene 1 — Hook (0:00–0:05)

**Text overlay** (add in post if your recorder doesn't have live text):
> "The PVE damage plugin that doesn't break every wipe."

**On-screen action**: pan over a Rust server scene (player + a scientist NPC nearby). 5 seconds.

**Voiceover (optional)**: none. Text overlay is enough.

### Scene 2 — The classifier moat (0:05–0:15)

**Text overlay**:
> "Every other plugin matches NPC prefab names. They break every Facepunch update."

**On-screen action**: aim at the scientist, run `/pdg test` in chat. The output appears showing:
```
Target: scientistnpc_full_any (type=ScientistNPC) classified as HumanNpc, subtype=Scientist
...
```

Linger 5 seconds so viewers read the classification.

**Text overlay (replaces previous)**:
> "Type-based detection survives. ScientistNPC, HumanNPCNew, future variants — caught automatically."

### Scene 3 — Per-damage-type scaling (0:15–0:25)

**On-screen action**: in chat, run `/pdg test fire Bullet 100` aimed at the same target. Output shows:
```
Dry-run: Bullet 100.0 damage to scientistnpc_full_any (HumanNpc / subtype=Scientist).
Legacy scaling path. Final damage if applied: 25.0.
```

**Text overlay**:
> "Per-damage-type scaling. 100 bullet → 25 actual. No allow/block binary."

### Scene 4 — The CUI panel (0:25–0:45)

**On-screen action**:
1. Type `/pdg ui`, panel opens with Status tab.
2. Pause 2 seconds.
3. Click Logging tab. Damage events streaming.
4. Pause 2 seconds.
5. Click History tab. Paginated rows.
6. Pause 2 seconds.
7. Click Rules tab. Read-only browser visible.
8. Click "Edit mode: OFF" button. It flips to ON, cycle/del buttons appear.
9. Pause 3 seconds.
10. Click Scaling tab. Slider rows visible.
11. Click "ON" for one of the toggles. It flips visually.

**Text overlay** (changes every 4 seconds with the tab):
- "Status — config at a glance"
- "Logging — live stream with filters"
- "History — paginated audit trail"
- "Rules — inheritance, color-coded"
- "Scaling — live tuning, no reload"

### Scene 5 — Foreign structure reflect (0:45–0:55)

**On-screen action**: aim at someone else's wall (use an admin setup ahead of time). Hit it once. Show that YOU take the damage instead.

**Text overlay**:
> "Damaging another player's base reflects back at you. Authorized owners damage their own freely."

### Scene 6 — Ecosystem integrations (0:55–1:15)

**On-screen action**: switch to chat, fast-type:
```
/pdg events
```

Show output listing tracked Bradley / Heli / Cargo + RaidableBases domes + Convoy / ArmoredTrain flags (if any active).

Then:
```
/pdg validate
```

Shows clean validation pass.

**Text overlay** (changes every 5 sec):
- "Auto-detects TruePVE, RaidableBases, Convoy, Armored Train, Backpacks"
- "ZoneManager integration for per-zone contexts"
- "Discord webhooks with rate limiting"

### Scene 7 — Close (1:15–1:30)

**Text overlay (final)**:
> "PVE Damage Guard
> $15 one-time, unlimited servers.
> Same-week patches every wipe.
> github.com/gjdunga/PVEDamageGuard"

**On-screen action**: hold the panel or a brand splash for 5 seconds.

### Total: 90 seconds

If you need to trim to 60, cut Scene 6 short — keep Scenes 1-5 and the close.

## ffmpeg conversion (MP4 → GIF)

If you record as MP4 and need GIF:

```bash
ffmpeg -i tour.mp4 -vf "fps=12,scale=960:-1:flags=lanczos,split[s0][s1];[s0]palettegen[p];[s1][p]paletteuse" -loop 0 tour.gif
```

Resulting GIF should be 2-4 MB at 90 seconds. If it's too big, drop fps to 10 or scale to 800:-1.

## Pre-upload checklist

- [ ] File size under Codefling's limit (5 MB GIF / 10 MB MP4)
- [ ] No personal info or server names visible (sleep mode for that test character if needed)
- [ ] All text overlays readable on a small thumbnail (Codefling's listing thumbnail is small)
- [ ] First frame is interesting (not a black screen or empty world)
- [ ] No audio (GIFs are silent; MP4 audio is OK but most listings mute by default)

## On capture day

Plan to record 2-3 takes; the first usually has timing issues. Cut the best one. Don't over-edit; the goal is "shows the plugin in action," not "polished marketing reel."

If the recording is hard to time well, alternative format: 3-4 separate short GIFs (one per major feature) embedded throughout the listing description rather than one long tour. Codefling supports multiple images / GIFs per listing.
