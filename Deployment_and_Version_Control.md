# Deployment, releases and version control

Date: 12 September 2026  
Status: agreed architecture extended with a proposed release process. The local repository is established at `C:\dev\ElfDaq`, with baseline commit `90e0b51`. Stage 2 includes compiled build identity and recording provenance. Portable deployment packaging and a remote Git host remain planned.

## Recommended deployment

Distribute an immutable, versioned Windows x64 release that includes its own .NET runtime. Operators download or copy the release, unpack/install it, and launch the application. They should not need Git, Visual Studio, an SDK, administrator access under ordinary per-user installation, or Python for acquisition. Institutional execution policies still apply and must be tested on the target laptops.

Start with a ZIP containing a published application folder and a small launcher. Keep ordinary files initially; single-file publishing, trimming and native AOT add no necessary benefit to this prototype. Add a signed installer and shortcuts when the WPF interface exists. Evaluate MSIX against the institution's signing/deployment policies and the separate recorder process lifecycle; retain the ZIP for offline diagnostics and fallback. An installer format is not a prerequisite for the first portable release.

Self-contained .NET publishing includes the runtime but makes runtime servicing our responsibility: rebuild, retest and release a new package when adopting runtime security updates. Windows x64 is the initial supported target; ARM64 or another OS requires a separately built and qualified package. WPF remains Windows-specific. [Microsoft publishing documentation](https://learn.microsoft.com/en-us/dotnet/core/deploying/).

The current Stage 1 build output is a developer build, not yet a qualified portable release. Self-contained publishing needs RID-specific runtime assets. The current NuGet configuration disables all feeds, so the packaging work must provision and pin the required runtime packs on the build machine, through an explicit build-only feed/cache configuration. Existing dependency-free tests should remain usable offline. End-user machines must never restore packages.

## Source control and collaboration

The original OneDrive workspace was not a functioning Git repository. A dedicated local repository now exists at `C:\dev\ElfDaq`, containing the C# solution, Python package, Pico firmware, contracts, small fixtures, release scripts and documentation. Keep development clones outside actively synchronized OneDrive folders. Investigate/preserve any existing `.git` metadata before moving or replacing anything; this plan does not modify it.

Use an institution-supported private Git host. Developers clone the repository, work on short-lived branches, run checks and merge reviewed changes into `main`. Each machine has its own clone. Operators receive release packages rather than editing installed code or pulling a development branch. The local source location is established; the remote host remains to be selected. No remote has been created or contacted for publication.

Track source, build settings, dependency locks, default configuration templates and small test evidence. Exclude SDKs, virtual environments, credentials, local configuration, build output and experimental recordings. Store large recordings separately, with checksums and session identifiers. Keep releases in the Git host's release/artifact storage with retention configured for long-term scientific reproducibility; do not depend solely on expiring CI logs.

## Version identities

| Identity | Purpose and policy |
|---|---|
| Product release | Semantic version, e.g. a proposed `0.1.0` for the first packaged Stage 1 snapshot. Major = incompatible public behavior, minor = added functionality, patch = compatible fixes. Stage numbers are not release versions. |
| Source revision | Full Git commit SHA, plus a dirty-tree marker for development builds. Reject dirty or unidentifiable source for official releases. |
| Release tag | Annotated tag such as `v0.1.0`, pointing to the tested commit. Never move or reuse a published tag; issue another version. |
| Compatibility versions | Sensor protocol, log format, local IPC and configuration schema have independent version numbers. Product upgrades do not automatically change file or wire formats. |
| Analysis and firmware | Python worker/package version and commit; each node's firmware version and commit. A release manifest specifies tested compatible combinations. |
| Measurement provenance | Calibration identity/hash, timing model, acquisition settings and analysis parameters, scoped to the session or result that used them. |

Keep the C# host, GUI, simulator and libraries on one coordinated product release initially. Publish the optional analysis bundle with that release and list its separate package/runtime versions. Runtime handshakes verify compatibility; incompatible analysis is disabled with a clear status while raw recording remains available. An incompatible sensor protocol is rejected explicitly, not interpreted optimistically.

Expose product version, full commit and supported formats through `--version`, the future GUI About view and service handshake. Put these values in the diagnostic bundle and every future recording's session metadata. Each derived Python result should identify its input session/range, software version, settings and relevant calibration/timing identities. A filename alone is insufficient provenance. Implement this through versioned metadata without silently changing the frozen binary envelopes.

## Release contents and build process

Suggested release contents:

```text
ElfDaq-<version>-win-x64/
    app/                         published host; GUI when available
    tools/simulator/             separately runnable simulator
    defaults/                    versioned configuration templates
    docs/                        operator guide and release notes
    release-manifest.json        commit, versions, compatibility and build provenance
    checksums.sha256             hashes of final shipped files
    licenses/                    redistributed component notices
```

The optional analysis bundle and Pico UF2 images can be separate downloads referenced by the same release manifest. The manifest records exact SDK/runtime versions, target architecture, dependency lock hashes, test results, build identity and bundled component versions. Inventory third-party dependencies and licenses, preferably as an SBOM. Hashes detect accidental changes; authenticated distribution or code signing establishes who supplied the package. MSIX requires a valid signing certificate. [Microsoft signing documentation](https://learn.microsoft.com/en-us/windows/msix/package/signing-package-overview).

Build on a clean Windows CI runner from a specific commit using the pinned SDK and locked dependencies. Run contract and integration checks on each change. For a release candidate, also run endurance tests and clean-machine installation tests. Publish into a new staging directory, test the actual published executables, then sign and calculate final hashes. Verify the signed package and retain the exact tested artifact. Promote that artifact to a release; avoid rebuilding separately for every laptop.

Aim first for repeatable builds with complete provenance. Deterministic compiler settings alone do not establish byte-identical ZIPs or signed installers: archive timestamps and signatures may differ. Archive the actual released bytes and their hashes. Official builds must fail if their required Git revision, version, tests or dependency locks are missing.

## Configuration, installation and upgrades

Keep installed binaries separate from mutable state:

- Per-user version folders under `%LOCALAPPDATA%\ElfDaq\releases\<version>` for the initial ZIP/launcher scheme; an installer may manage its own application location later.
- Machine/user settings under `%LOCALAPPDATA%\ElfDaq\config`, with an explicit schema version and an export/import command.
- Diagnostic logs in a separate application-state directory.
- Recordings in an operator-selected data directory, initially a local SSD, independent of the installed application version.

Resolve bundled resources relative to the executable, not the shell's working directory. Keep secrets out of exported templates and routine diagnostic bundles. At session start, save the effective non-secret configuration with the recording, including any command-line overrides. An upgrade must not overwrite local settings or delete recordings.

Install a candidate alongside the last known-good version and run its synthetic self-test. Switch versions only while acquisition is stopped; once recording exists, drain and flush before switching. Never update the recorder, GUI or analysis worker midway through a run. Field laptops can remain pinned to a qualified release; provide an explicit check/download/import workflow rather than mandatory background updating.

Back up configuration before migration. Rollback selects the previous application and its compatible configuration snapshot. Older applications must refuse unsupported newer schemas clearly. Preserve original recording files; migrations or conversions create separate outputs. Test interrupted installation, failed migration, rollback and uninstall without losing data. Offline copying by USB or an internal share must work without a cloud connection.

## Optional Python deployment

Acquisition ships independently of Python. At Stage 3, build a separate, versioned analysis bundle with a pinned Python interpreter and exact NumPy/SciPy/HDF5 dependencies for the target architecture. Do not copy a development virtual environment between computers: virtual environments generally contain machine-specific paths and are not portable. [Python documentation](https://docs.python.org/3/library/venv.html).

For the first managed bundle, package an app-private interpreter plus an offline, hash-locked wheel collection and create its environment at the final installation location. Invoke it by its explicit path, never an arbitrary `python` on PATH. Validate required native libraries and redistribution terms. A later relocatable bundle is acceptable only after clean-machine tests prove it works. If analysis installation fails or the worker crashes, the C# recorder must still operate normally.

## Implementation sequence and acceptance

This release work starts before Stage 6; Stage 6 qualifies the finished distribution.

1. **Next release-foundation increment:** establish the Git repository and baseline commit; implement build identity/`--version`, a publish script, manifest/checksum generation and portable Stage 1 host/simulator launchers. Produce the first candidate from clean source. No new tag or release is claimed by this document.
2. **Stage 2:** record build/firmware/configuration provenance in sessions; add configuration schema management and explicit data locations; exercise upgrade/rollback against existing recordings.
3. **Stage 3:** deliver the independently optional Python bundle and compatibility handshake, with offline dependency installation tests.
4. **Stage 4:** add GUI installation/shortcuts, visible version information and an upgrade workflow that respects the recorder's independent lifetime. Select/sign the installer after testing institutional policy constraints.
5. **Stage 5:** generate version-stamped firmware artifacts and retain the hardware/software compatibility matrix.
6. **Stage 6:** qualify the exact release package on a clean supported Windows laptop without Git, SDKs, a preinstalled .NET runtime, Python or internet access. Test an ordinary user account, paths containing spaces, a different username, readonly application files, optional analysis absent/present, upgrade, rollback, interrupted install, incompatible versions and uninstall with recordings retained. Verify LAN/firewall behavior on the real network without silently changing system policy.

The immediate acceptance target is simple: another person copies one release to a clean laptop, runs the two-node synthetic demo without installing developer tools, and can report its exact version/commit and attach a useful diagnostic bundle. The next target is the same experience with recording and the two Pico nodes.
