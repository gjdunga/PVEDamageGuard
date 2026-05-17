# Screenshot Capture Guide

Eight screenshots to capture for the Codefling listing and the GitHub README. These need to be taken from a live Rust server with PVEDamageGuard loaded; I can't do that from here, so this is the playbook for you.

## Setup

1. **Test server with PVE Damage Guard v2.0.0 loaded.** Use your FACEWAN test instance or a dedicated capture server.
2. **Some active state to make screenshots meaningful**:
   - At least 2 contexts configured in `RuleMatrix` (Default + AtPvpEvent ships out of the box)
   - Recent damage events (run around aggroing scientists for 60 seconds before capturing the History tab)
   - A Bradley or patrol heli spawned (so `/pdg events` shows tracked entities)
3. **Resolution**: capture at 1920×1080. Codefling auto-resizes for thumbnails; the full-res image is what buyers see when they click.
4. **Tool**: anything that takes a clean screenshot. F2 in Rust works; Greenshot, ShareX, or Windows Snipping Tool all fine.
5. **Output folder**: save to `D:/claude code/PVEDamageGuard/docs/marketing/screenshots/` so they live alongside this guide.

## File naming convention

Use the order Codefling will display them in:

```
01-status-tab.png
02-logging-tab.png
03-history-tab.png
04-rules-tab.png
05-rules-edit-mode.png
06-scaling-tab.png
07-test-command.png
08-comparison.png
```

## The eight shots

### 01-status-tab.png

**What**: CUI Status tab open showing live config state.

**How**:
1. In game as admin, run `/pdg ui`.
2. Make sure Status tab is selected (default).
3. F2 to screenshot.

**Why this shot matters**: First impression. Shows the panel exists, shows the brand (coral accent), shows we display a lot of state at a glance.

**Crop**: full panel visible. Some game world around the edges is fine — admins know this is in-game.

---

### 02-logging-tab.png

**What**: Logging tab with several recent damage events streaming, color-coded by level.

**How**:
1. Before capturing: `/pdg log Scaled` (so Scaled and Reflects events both flow).
2. Aggro a few scientists, take some hits, deal some damage.
3. Open `/pdg ui`, click the Logging tab.
4. Should see at least 4-6 log lines with different colors (coral for Reflects, cyan for Scaled, gray for older Trace entries if any).
5. F2 to screenshot.

**Why**: Live diagnostic value. Buyers see we don't just print to console — we have a real in-game log view.

**Crop**: full panel; make sure at least one coral Reflect line is visible. Filter buttons across the top should be visible.

---

### 03-history-tab.png

**What**: History tab paginated with several rows of recent hits.

**How**:
1. After accumulating ~20+ hits from step 02, open `/pdg ui`, click History.
2. Should see a populated rows list with columns: time, tag, attacker, victim, damage, action, context.
3. F2 to screenshot.

**Why**: Showcase the audit-trail capability. Useful for admins demonstrating PVE rule enforcement.

**Crop**: full panel; page indicator visible at top. Prev/Next buttons visible.

---

### 04-rules-tab.png

**What**: Rules tab read-only browser, default mode, showing the matrix.

**How**:
1. Enable rule matrix: edit `oxide/config/PVEDamageGuard.json`, set `RuleMatrix.Enabled` to `true`. `/pdg reload`.
2. `/pdg ui` → Rules tab.
3. Should see the active context highlighted in the left column with a ★, and the right column showing Direct rules (with color-coded actions) above Inherited rules (in muted gray).
4. F2 to screenshot.

**Why**: The rule matrix is one of our biggest differentiators. This shot proves it works visually.

**Crop**: full panel. Make sure the inheritance chain in the header ("Viewing: AtPvpEvent → Default") is visible.

---

### 05-rules-edit-mode.png

**What**: Same Rules tab but with edit mode toggled ON, so the per-rule cycle/del buttons are visible.

**How**:
1. From the screenshot 04 state, click the "Edit mode: OFF" button at the top right.
2. It should flip to coral "Edit mode: ON" and each direct rule should grow a `cycle` button and a `del` button.
3. F2 to screenshot.

**Why**: Buyers want to see they can edit without leaving the game.

**Crop**: same as 04, but the cycle/del buttons are now visible on at least 2-3 rules.

---

### 06-scaling-tab.png

**What**: Scaling tab with multiplier rows, toggle buttons, dropdowns.

**How**:
1. `/pdg ui` → Scaling tab.
2. The three multiplier rows (NPC->Player default, NPC->Structure default, Reflect multiplier) should each show their current value with the +/- button strip.
3. Some toggle buttons should be visibly ON (coral) and others OFF (gray) depending on your config.
4. The Logging dropdown row should have one option highlighted in coral.
5. F2 to screenshot.

**Why**: Showcase live tuning. Most damage plugins make you edit JSON and reload.

**Crop**: full panel. Footer hint at the bottom ("Per-damage-type tuning: /pdg scale...") visible for context.

---

### 07-test-command.png

**What**: `/pdg test` output in chat aimed at an entity, showing classification and rule preview.

**How**:
1. In game, aim at a scientist (or any NPC).
2. Type `/pdg test` in chat.
3. The chat output appears with classification, subtype, distance, hour, rule that would apply, per-victim subtype scaling.
4. F2 to screenshot capturing the chat + the entity in the crosshair.

**Why**: The `/pdg test` command is genuinely unique to PVEDamageGuard. Buyers immediately see the diagnostic value.

**Crop**: chat text visible at full readability, target entity visible behind the chat. If you can frame both clearly, this is the strongest single-image pitch.

---

### 08-comparison.png

**What**: Side-by-side or before/after showing the actual scaling working. Example: a damage number popup or stat readout demonstrating NPC bullet at 47 dmg (vanilla) vs 11.75 dmg (with PVEDamageGuard scaling Bullet to 0.25x).

**How**:
1. Hardest shot to capture cleanly. Three options, pick whichever you can stage:
   - **Option A (preferred)**: take damage from a scientist with PVEDamageGuard loaded, capture the damage indicator and the Logging tab in the same shot. The log line shows the scaling applied.
   - **Option B**: a screenshot from `/pdg test fire Bullet 100` aimed at a player target, showing the dry-run output "Final damage if applied: 25.0" (because Bullet defaults to 0.25x).
   - **Option C**: graphics composition. Make a 2-panel image: left says "Vanilla Rust: 47 dmg per scientist headshot", right says "PVE Damage Guard: 11.75 dmg per scientist headshot". Use Photoshop, Photopea, or GIMP.

**Why**: Numbers sell. Buyers ask "does it actually reduce damage" and a screenshot with concrete numbers answers it instantly.

**Crop**: large readable numbers. Branded title at top if Option C ("PVE Damage Guard - The damage actually changes").

---

## Optional 9th shot for the README hero image

A composite that goes at the top of GitHub README.md. Same dimensions as the others. Picks one panel (likely Status or Rules) and adds a small overlay:

```
PVE Damage Guard
The PVE damage plugin that doesn't break every wipe.
v2.0.0  •  GPL-3.0  •  Oxide + Carbon
```

Fine to skip if you want to keep it minimal. The README looks fine without a hero image.

## Post-capture checklist

- [ ] All 8 files exist in `docs/marketing/screenshots/` with the naming above
- [ ] Each is under 2 MB (Codefling has size limits; PNG compression should handle it)
- [ ] No PII in any shot (player names, IP addresses, server names you don't want public)
- [ ] No corrupted UI elements (the CUI sometimes has rendering hiccups at panel-reload; recapture if anything looks broken)
- [ ] Drop them into the Codefling listing in numeric order

## On capture day

If you want to record the GIF (per docs/marketing/feature-tour.md) on the same session, capture screenshots first since you'll have a clean setup. Then start the GIF recording without quitting / reloading.
