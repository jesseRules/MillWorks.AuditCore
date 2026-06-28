# Local NuGet Package Publishing Guide

How to publish MillWorks.AuditCore as local NuGet packages so consuming projects (MillWorks, IExcel, etc.) can reference them via their IDE's NuGet package manager — with version tracking, updates, and dependency resolution, just like packages from nuget.org.

---

## What This Project Is

MillWorks.AuditCore is a **tamper-evident audit logging and compliance platform** published as 5 independent NuGet packages, each owning a responsibility layer:

```
MillWorks.AuditCore.Abstractions   — Pure interfaces, DTOs, models (no EF/ASP.NET dependencies)
MillWorks.AuditCore.EntityFramework — DbContext, entities, repositories, migrations
MillWorks.AuditCore.Providers       — Entity-specific audit providers (User, etc.)
MillWorks.AuditCore.Services        — Business logic, tamper detection, DLQ, archival, compliance
MillWorks.AuditCore                  — Top-level umbrella package: DI registration, middleware, configuration builder
                                       (built from the MillWorks.AuditCore.AspNetCore project; PackageId is MillWorks.AuditCore)
```

### Dependency Graph

```
Tier 0 ─ Abstractions (pure base types, no framework dependencies)

Tier 1 ─ EntityFramework → Abstractions
         Providers        → Abstractions

Tier 2 ─ Services → Abstractions, EntityFramework, Providers

Tier 3 ─ AspNetCore → Abstractions, EntityFramework, Providers, Services
```

Consuming apps install `MillWorks.AuditCore` (the top-level umbrella package — produced by the `MillWorks.AuditCore.AspNetCore` project but published under PackageId `MillWorks.AuditCore`) and get the entire stack via transitive dependencies. Apps that only need the interfaces (e.g., shared domain libraries) reference `MillWorks.AuditCore.Abstractions` alone.

### Versioning

All 5 packages share a single version number managed in `Directory.Build.props`:

```xml
<Version>1.9.2</Version>
<AssemblyVersion>1.9.2.0</AssemblyVersion>
<FileVersion>1.9.2.0</FileVersion>
```

Bump this ONE place to update all projects.

---

## How It Works

```
AuditCore repo                         Consuming repo (MillWorks, IExcel, etc.)
┌─────────────────────────┐            ┌──────────────────────────────┐
│ src/...AspNetCore/       │            │ MyApp.csproj                 │
│ src/...Services/         │            │   <PackageReference          │
│ src/...EntityFramework/  │  pack      │     Include="MillWorks       │
│ src/...Providers/        │ ────────►  │     .AuditCore.AspNetCore"   │
│ src/...Abstractions/     │  writes to │     Version="1.9.2" />      │
│                          │  feed      │                              │
│ ~/LocalNuGetPackages/    │◄──────────│ nuget.config                 │
│   *.nupkg                │  restore   │   points to feed             │
└─────────────────────────┘  reads from └──────────────────────────────┘
```

1. Run `./build-and-publish.sh` — builds all 5 projects and packs them
2. Packages land in `~/LocalNuGetPackages` (configurable)
3. Consuming projects reference that folder as a NuGet source
4. IDE NuGet manager shows available versions, updates, etc.

---

## Quick Start

### 1. Build and Publish Locally

```bash
cd /Users/jesse/RiderProjects/MillWorks.AuditCore

# Build and pack all 5 packages to the local feed
./build-and-publish.sh
```

The script:
- Reads the version from `Directory.Build.props` automatically
- Builds each project in dependency order (Abstractions → EntityFramework → Providers → Services → AspNetCore)
- Packs to `~/LocalNuGetPackages` by default
- Prints next-steps instructions

**Options:**
```bash
./build-and-publish.sh -v 1.2.0-dev.1    # Override version
./build-and-publish.sh -p ./my-packages   # Override output path
./build-and-publish.sh -h                 # Show help
```

### 2. Configure the Consuming Project

Add a `nuget.config` to the consuming project's repo root:

```xml
<?xml version="1.0" encoding="utf-8"?>
<configuration>
  <packageSources>
    <add key="MillWorks Local" value="/Users/jesse/LocalNuGetPackages" />
    <add key="nuget.org" value="https://api.nuget.org/v3/index.json" />
  </packageSources>
</configuration>
```

### 3. Add Package References

```xml
<!-- Most apps just need the top-level umbrella package -->
<PackageReference Include="MillWorks.AuditCore" Version="1.9.2" />

<!-- Shared domain libraries that only need interfaces -->
<PackageReference Include="MillWorks.AuditCore.Abstractions" Version="1.9.2" />
```

### 4. Restore and Run

```bash
dotnet restore
dotnet run
```

---

## Version Management

### When to Bump

| Change type | Version bump | Example |
|------------|-------------|---------|
| Breaking API change (removed/renamed public types) | Major | `1.1.0` → `2.0.0` |
| New feature, new public API surface (additive) | Minor | `1.1.0` → `1.2.0` |
| Bug fix, internal refactor, no public API change | Patch | `1.1.0` → `1.1.1` |

### How to Bump

Edit `Directory.Build.props` — all 5 packages inherit the version:

```xml
<Version>1.2.0</Version>
<AssemblyVersion>1.2.0.0</AssemblyVersion>
<FileVersion>1.2.0.0</FileVersion>
```

Then pack:

```bash
./build-and-publish.sh
```

### Pre-release Versions

Use pre-release suffixes during active development:

```bash
./build-and-publish.sh -v 1.2.0-dev.1
```

NuGet treats these as lower precedence than release versions. Rider/VS shows them when "Include prerelease" is checked. Bump the suffix (`-dev.2`, `-dev.3`) for each iteration, then drop it for the release.

### NuGet Cache Gotcha

NuGet aggressively caches packages. If you pack `1.1.0`, change code, and pack `1.1.0` again **without bumping**, consumers use the cached old version.

**Solutions:**
1. **Always bump the version** when you change code (even just the patch)
2. **Clear the NuGet cache** if you must repack the same version: `dotnet nuget locals all --clear`
3. **Use pre-release suffixes** during active development: `1.2.0-dev.1`, `1.2.0-dev.2`, etc.

---

## Workflow: Day-to-Day Usage

### Making Changes in AuditCore

```bash
# 1. Make your changes
# 2. Run tests
dotnet test

# 3. Bump version in Directory.Build.props
# 4. Update CHANGELOG.md
# 5. Pack to local feed
./build-and-publish.sh

# 6. In consuming project, update the package version
#    (via Rider NuGet manager or manually in .csproj)
```

### Updating in Rider

1. Open the consuming project
2. Right-click solution → **Manage NuGet Packages**
3. Select "MillWorks Local" as the package source
4. Go to the **Updates** tab
5. Select the AuditCore packages to update → **Update**

### Updating via CLI

```bash
cd /Users/jesse/RiderProjects/MillWorks
dotnet add src/MillWorks.Api/MillWorks.Api.csproj package MillWorks.AuditCore --version 1.9.2
```

---

## Do's and Don'ts

### Do

- **Do bump the version every time you pack** — even for tiny changes. NuGet caches aggressively.
- **Do use pre-release versions** (`-dev.1`, `-beta.1`) while iterating. Bump to release when stable.
- **Do pack in Release configuration** — consumers get optimized binaries (the script does this by default).
- **Do run tests before packing** — consumers trust the version they install passed its test suite.
- **Do update `CHANGELOG.md`** before packing — consumers need to know what changed.
- **Do clear NuGet cache** when things look stale: `dotnet nuget locals all --clear`

### Don't

- **Don't use `<ProjectReference>` across repo boundaries** — use `<PackageReference>` to versioned packages.
- **Don't repack the same version with different code** — always bump. NuGet assumes same version = same bits.
- **Don't commit `.nupkg` files to git** — packages are build artifacts.
- **Don't use floating version ranges** (`1.*`) for local packages — pin exact versions for reproducibility.

---

## Local Feed Limitations

The local folder approach is a **single-developer solution**. It breaks with multiple developers, CI/CD, or new machines. Plan to graduate to a hosted feed when ready.

| Trigger | Solution |
|---------|----------|
| Multiple developers | **GitHub Packages** (free for private repos), **Azure Artifacts**, or **BaGet** (self-hosted) |
| CI/CD needs packages | Any hosted feed — local folders don't work in CI |
| Public distribution | **nuget.org** (AuditCore is already published there) |

The package format doesn't change — only the `nuget.config` URL.

---

## CI/CD Packaging

AuditCore also has a CI/CD publish path. See `.github/workflows/` for the automated pipeline that publishes to nuget.org on tagged releases. The local feed is for development iteration between releases.

---

## Quick Reference: Common Commands

```bash
# Pack all packages to local feed
./build-and-publish.sh

# Pack with a specific version
./build-and-publish.sh -v 1.2.0-dev.1

# Pack to a custom output path
./build-and-publish.sh -p ./my-packages

# List packages in the local feed
ls ~/LocalNuGetPackages/MillWorks.AuditCore.*.nupkg

# Clear NuGet cache (when packages seem stale)
dotnet nuget locals all --clear

# Check what version a consuming project is using
dotnet list MyApp/MyApp.csproj package

# Add/update a package in a consuming project
dotnet add MyApp/MyApp.csproj package MillWorks.AuditCore --version 1.9.2

# Add the local NuGet source (one-time setup)
dotnet nuget add source ~/LocalNuGetPackages --name MillWorksLocal
```
