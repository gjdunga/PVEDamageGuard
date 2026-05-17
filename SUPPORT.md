# Support

How to get help with PVE Damage Guard, what's in scope, and the response-time commitments.

## Where to file

### Free tier (uMod / GitHub users)

[github.com/gjdunga/PVEDamageGuard/issues](https://github.com/gjdunga/PVEDamageGuard/issues)

Use the bug-report or feature-request template. Anyone with a GitHub account can open an issue. Response on a best-effort basis, typically within a week.

### Paid tier (Codefling buyers)

Codefling's support thread on the listing + the linked Discord channel (see the Discord invite at the top of the Codefling listing page).

Same maintainer, same code, **prioritized response**:
- Critical issues: < 48 hours
- Bug reports: < 7 days
- Feature requests: triaged monthly with each minor release

Both tiers get the same fixes; paying is for the response-time SLO and direct support channel.

## SLOs

### Critical (< 48 hours)

These are bumped to the top of the queue and patched within 48 hours of confirmation:

- Plugin won't load on a current Rust + Oxide/Carbon build (after a forced wipe, this is also a same-week patch — usually faster)
- Data loss (config file wiped, stats file corrupted by the plugin)
- Server crash attributable to the plugin (stack trace pointing at PVEDamageGuard)
- Security vulnerability (privilege escalation, RCE, credential exposure)
- A reflect / scale rule that's applying with the wrong sign or magnitude causing severe gameplay impact

### Standard bugs (< 7 days)

- Feature works but produces wrong output in a specific edge case
- Performance regressions (`/pdg timing` shows mean > 200μs)
- CUI rendering glitches
- Config validation false positives
- Documentation errors that mislead admins

### Feature requests (triaged monthly)

Open the issue; it gets reviewed during the planning window for the next minor release (aligned with Rust's first-Thursday forced wipe). Feature requests with attached patches or test cases are reviewed faster.

### Out of scope

- Server administration help unrelated to the plugin
- Rust gameplay design advice
- Custom one-off rule configurations (point at INSTALL.md and docs/configuration.md instead)
- Issues with other plugins (we'll help where they touch us, but unaffiliated plugin bugs go to those maintainers)
- Free private consulting on building your own plugin (open-source the relevant parts and the community helps)

## Before opening an issue

Run these and include the output:

1. **`/pdg`** — full status block.
2. **`/pdg selftest`** — type-resolution check.
3. **`/pdg validate`** — config validation.
4. **`oxide.plugins`** — list of other loaded plugins.
5. **The exact reproduction steps** — what command, what entity, what configuration field, what the unexpected outcome is.

If the issue is a damage calculation question:

6. **`/pdg test`** aimed at the entity in question.
7. **`/pdg test fire <DamageType> <amount>`** dry-run output.

If the issue is a CUI rendering problem:

8. **Screenshot of the broken panel state.**
9. **Your screen resolution** (1920×1080, 2560×1440, etc.).

This information cuts triage time from days to hours.

## Issue templates

### Bug report

```markdown
**PVEDamageGuard version**: v2.0.0
**Oxide / Carbon version**: 
**Rust server build**:
**Other relevant plugins loaded**:

### What I expected to happen

(One paragraph)

### What actually happened

(One paragraph)

### Reproduction steps

1.
2.
3.

### Diagnostic output

#### /pdg
```
(paste here)
```

#### /pdg selftest
```
(paste here)
```

#### /pdg validate
```
(paste here)
```

#### Any error lines from oxide/logs/oxide_*.log
```
(paste here)
```
```

### Feature request

```markdown
**PVEDamageGuard version**: v2.0.0

### The use case

(One paragraph: what server type, what scenario, why current behavior is insufficient)

### Proposed behavior

(One paragraph: what would solve it)

### Alternatives considered

(Any existing config / workaround you've tried)

### Willing to contribute

[ ] I'll open a PR
[ ] I'll test a beta build
[ ] Reporting only
```

## What happens after you file

1. Maintainer ack within the SLO window (usually faster on Codefling tier)
2. If reproducible: a fix or workaround in the next minor release, or sooner for critical
3. If not reproducible: a request for more info; issue stays open
4. Each closed issue links to the commit / release that resolved it

## Communication channels

| Channel | Use case |
|---|---|
| **GitHub issues** | Public bug reports, feature requests, anything that benefits future searchers |
| **Codefling support thread** (listing-attached) | Paid-tier prioritized response; less searchable than GitHub |
| **Discord channel** (Codefling tier) | Real-time chat for urgent issues; the maintainer is online during US daytime hours |
| **GitHub Pull Request** | Code contributions, doc fixes, translations |
| Direct email | Reserved for credential / security disclosures only (gjdunga@dstaftn.net) |

Don't use the email for general support. PRs are welcome from anyone; small PRs with tests get merged fastest.

## Translation contributions

We ship 8 language files (en, ru, es, la, fr, de, zh, pt). The non-en files are machine-quality. Native-speaker contributions are welcome via PR:

1. Fork the repo
2. Edit `oxide/lang/<your-lang>/PVEDamageGuard.json`
3. Open a PR; the GitHub Action validates JSON syntax + key parity
4. Merged usually within a week

## Translation gaps

If a language we don't ship needs support, open an issue with the language code and offer to maintain. We'll add `oxide/lang/<code>/PVEDamageGuard.json` from your initial translation and you become the maintainer for ongoing updates.

## What the paid tier doesn't get

To be explicit:

- **Not** custom feature work for your specific server. Feature requests go through the public triage process regardless of tier.
- **Not** private documentation. Everything in the GitHub repo is available to everyone.
- **Not** removed features in the free version. There's no "free has less" pattern; both tiers ship identical binaries.
- **Not** unlimited support. SLOs are commitments to response time, not to "all my problems solved." Some issues require investigation that takes days even when prioritized.

The paid tier exists for response speed and a direct line, nothing more.

## Maintenance commitment

PVE Damage Guard targets:

- **Monthly forced wipe** (first Thursday of each month at 18:00 UTC): same-week patch for any Facepunch breaking change. Goal: < 48 hours from broken wipe to fixed release.
- **Critical bugs**: hotfix within 48 hours regardless of wipe schedule.
- **Feature requests**: triaged on a monthly cadence aligned with each minor release.
- **Pull requests**: reviewed within one week. Changes that include tests are merged faster.
- **API stability**: the public API surface (everything prefixed `API_`) is considered stable from v1.0 forward. Breaking changes require a major version bump (v3.0+) and one major-version cycle of deprecation warnings.

These are commitments, not aspirations. If the SLO slips, the next release notes will explicitly note it and the reason.

## Maintainer

Gabriel Dungan (gjdunga). Cañon City, Colorado, USA. Mountain Time zone. Online for support during US daytime hours; emergencies handled outside hours when possible.

Founder of DunganSoft Technologies. Also maintains:
- ModernNoCupboardDecay (uMod)
- NitroBoostLinker (uMod)
- ModernItemBlocker (uMod)
- bottomlesswater (uMod)
- TireShopPOS (commercial product, unrelated to Rust modding)

This is a real product with a real maintainer. Not abandonware.
