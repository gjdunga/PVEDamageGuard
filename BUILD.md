# Building / Compiling PVEDamageGuard

PVEDamageGuard ships as a single source file, `oxide/plugins/PVEDamageGuard.cs`,
which the Oxide runtime compiles **on the Rust server** when the plugin loads.
You do not need to build anything to deploy it — copy the `.cs` file and reload.

This document describes the optional **compile-validation chain**: a way to
type-check the plugin against the *real* Oxide, Rust and Unity assemblies on your
own machine (or in CI) so API breaks are caught before they reach a live server,
instead of showing up in `oxide.log` after deploy.

> The DLL produced by this build is a throwaway. The shipped artifact is always
> the raw `.cs` file. This project exists only to make the compiler tell you
> whether that `.cs` file still binds against the current game API.

---

## What's in the chain

| Path | Purpose |
| --- | --- |
| `build/PVEDamageGuard.csproj` | SDK-style project that compiles the plugin source against the reference assemblies. Targets `net48` (Rust's Mono profile). |
| `tools/fetch-references.sh` | Linux/macOS: downloads a Rust dedicated server + Oxide and stages the reference DLLs under `references/`. |
| `tools/fetch-references.ps1` | Windows (PowerShell 5.1+): same as above. |
| `Makefile` | Convenience targets (`make references-managed`, `make build`). |
| `.github/workflows/compile.yml` | CI: fetches references (weekly-cached) and compiles on every push / PR. |

`references/` and `.steamcmd/` are git-ignored — the game DLLs are proprietary
and are fetched per-machine, never committed.

---

## Prerequisites

- **.NET SDK 8.0+** (`dotnet --version`). Get it from <https://dotnet.microsoft.com/download>.
  The `net48` reference assemblies are pulled automatically from NuGet
  (`Microsoft.NETFramework.ReferenceAssemblies`), so **no Mono / .NET Framework
  install is required**, even on Linux.
- **curl, tar, unzip** (Linux/macOS) for the fetch script.
- On Linux, SteamCMD needs 32-bit runtime libs:
  ```bash
  sudo dpkg --add-architecture i386
  sudo apt-get update
  sudo apt-get install -y lib32gcc-s1 ca-certificates
  ```
- Disk space: the Rust dedicated server download is several GB. Use
  `--managed-only` (below) to keep only the small `Managed/` folder afterward.

---

## One-time: fetch the reference assemblies

The reference DLLs come from a Rust dedicated server (Steam app **258550** — free,
anonymous, no login) with the latest **Oxide.Rust** release overlaid. The fetch
script installs both.

**Linux / macOS**

```bash
# Keep only RustDedicated_Data/Managed (~tens of MB) — recommended:
make references-managed
# ...or the full server install:
make references
```

**Windows (PowerShell)**

```powershell
tools\fetch-references.ps1 -ManagedOnly
```

When it finishes you'll have:

```
references/RustDedicated_Data/Managed/        # Oxide.Core.dll, Oxide.Rust.dll,
                                              # Assembly-CSharp.dll, UnityEngine.*.dll,
                                              # Facepunch.*.dll, Newtonsoft.Json.dll, ...
```

### Already have a server?

Skip the download — point the build at an existing install's `Managed` folder:

```bash
# env var:
export RUST_MANAGED="/path/to/server/RustDedicated_Data/Managed"
dotnet build build/PVEDamageGuard.csproj -c Release

# or per-build property (highest precedence):
dotnet build build/PVEDamageGuard.csproj -c Release -p:ManagedDir="/path/to/.../Managed"
```

---

## Compile

```bash
make build
# equivalently:
dotnet build build/PVEDamageGuard.csproj -c Release
```

A clean run ends with `Build succeeded` and `0 Error(s)`. **Errors mean the
plugin will not load on a server** with the matching Oxide/Rust build — fix them
before opening a PR or releasing. (Some warnings are expected: Unity/Oxide emit
binding-redirect and obsolete-API noise; the project already suppresses the
purely cosmetic ones.)

If references aren't present, the build stops with a single clear message telling
you to run the fetch script — not a wall of `CS0246` type-not-found errors.

---

## How CI uses it

`.github/workflows/compile.yml` runs on every push to `main` and every PR that
touches the plugin or the build chain. It:

1. Restores the `Managed/` folder from a cache keyed by ISO **year-week**.
2. On a cache miss (i.e. once a week, or first run), installs SteamCMD's
   prerequisites, runs `tools/fetch-references.sh --managed-only`, and caches the
   result.
3. Runs `dotnet build build/PVEDamageGuard.csproj -c Release`.

The weekly cache key means the plugin is automatically re-validated against the
latest Rust/Oxide build roughly in step with Rust's monthly force-wipe / weekly
patch cadence — without re-downloading the server on every single run.

---

## Notes & limitations

- This validates **compilation only**. It does not run the plugin or exercise
  Facepunch runtime behaviour — see the manual checklist in
  [`CONTRIBUTING.md`](CONTRIBUTING.md#testing) for that.
- The target framework is `net48` because Rust's server scripting backend is
  Mono / .NET Framework 4.x flavoured. `LangVersion` is pinned to `9.0` so a
  local build can't pass using a C# feature the uMod build server can't compile.
- Keep the plugin dependency-free (per `CONTRIBUTING.md`): it must compile with
  only the stock assemblies present in a Rust + Oxide `Managed/` folder. The
  build chain deliberately adds no third-party references.
