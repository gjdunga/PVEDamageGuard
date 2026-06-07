# Contributing

Thanks for helping improve PVE Damage Guard. This repo follows the
[DunganSoft Plugin Standard](https://github.com/gjdunga/rust-plugin-standard).
It is a **commercial-marketplace** plugin (`umod: false`), so unlike the
uMod-targeted plugins it is GPL-3.0 licensed and may use Reflection and direct
file I/O; the rest of the standard still applies.

## Before you open a PR

- **Branch** off `main` (`feat/…`, `fix/…`, `perf/…`, `docs/…`, `i18n/…`).
- **Compile** against the real assemblies: `make references-managed` once, then
  `make build` (see [BUILD.md](BUILD.md)). It must report `0 Error(s)`. The plugin
  targets both Oxide and Carbon.
- **Conformance:** `python3 tools/check-standard.py .` must report `0 errors`.
- **Version + changelog:** any user-visible change bumps the version in lockstep
  across `[Info]`, `manifest.json`, `.umod.yaml`, and the top `CHANGELOG.md`
  heading, with a `CHANGELOG.md` entry (Keep a Changelog).
- **Translations:** if you add or change a message, update **every** locale in
  `oxide/lang/` (8 locales) — same keys, same placeholders.

## Code style

- C# 8+, early returns over nested `if`s, XML-doc the non-obvious (explain *why*).
- Keep the classification cache, hook-timing telemetry, and rule-matrix paths
  allocation-light — they run on every combat event.

## Reviews & testing

There is no unit-test harness (the plugin exercises Facepunch combat APIs
in-process). Verify on a private Rust server (PVE config) and note what you
checked in the PR.

## Security

See [SECURITY.md](SECURITY.md) — report vulnerabilities privately.
