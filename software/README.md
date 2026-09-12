# ELF DAQ software: Stages 0–1

C#/.NET acquisition foundation with an independent Python interface. This directory contains **binary contracts, codecs, golden fixtures, a multi-unit TCP simulator and a working headless receiver**. Durable recording, the desktop GUI, Python live analysis and Pico firmware remain later stages.

## Quick verification (Windows x64)

Install/pin .NET SDK 10.0.401 or run `scripts/bootstrap-dotnet.ps1` to obtain the official checksum-verified SDK under ignored `.tools/`. This does not replace the system SDK. Python 3.11-3.14 is supported; Stage 0 has no third-party Python dependencies.

From this directory:

```powershell
.\scripts\test.ps1 -Python 'C:\path\to\python.exe'
```

The script uses `.tools/dotnet/dotnet.exe` if present, otherwise `dotnet` on PATH (or pass `-Dotnet`). It performs locked restore, Release build, independent C# fixture checks, real-socket Stage 1 integration tests (about 25 seconds), and Python unittest discovery. All NuGet sources are disabled because Stage 0 requires no third-party packages; project lock files are included. WPF requires the Windows desktop targeting pack included in the Windows SDK distribution. The desktop project is currently a class-library boundary, not a runnable UI.

Standalone Python check:

```powershell
python -m unittest discover -s python/tests -v
```

Standalone C# check after build:

```powershell
dotnet run --project tests/Elf.Contracts.Tests -c Release --no-build -- fixtures
```

The C# test project is a dependency-free executable harness, not an xUnit/MSTest discovery project. Use the supplied commands rather than expecting `dotnet test` to discover it. Failures exit nonzero. No expected fixture is regenerated during tests.

## Contracts and scope

- [Sensor protocol v1](docs/protocol-v1.md): exact framing, signed samples, identity/counters, JSON message grammar, commands and reconnect semantics.
- [Acquisition log v1](docs/elflog-v1.md): header/record envelopes, normalized int32 data, commit/close/recovery rules.
- [Local IPC v1](docs/ipc-v1.md): C#/Python control, binary blocks and result metadata.
- `src/Elf.Protocol`: working C# wire/IPC codecs and bounded prefix validation.
- `src/Elf.Recording`: working log header/record envelope codec; no file writer yet.
- `python/elfdaq/contracts.py`: independent standard-library implementation of these same envelopes.
- `fixtures/manifest.json`: SHA-256 hashes, expected values and valid/invalid expectations.
- `src/Elf.Core`: deterministic synthetic signals and the command state machine.
- `src/Elf.Acquisition`: multiple TCP clients, bounded stream parsing, session state, continuity checks and command acknowledgments.
- `src/Elf.Host` and `src/Elf.Simulator`: runnable console applications.
- Desktop, durable recording and Python live IPC remain later stages.

Encoders/decoders enforce binary layout, sample bounds and strict JSON syntax; Stage 1 handlers additionally enforce mandatory metadata schemas, state transitions and command acknowledgments. Cross-record commit semantics require the Stage 2 recorder. That separation is explicit: a syntactically valid object is not authorization to execute a command.

Fixtures include complete XYZ edge-value blocks in both encodings, nominal block size, JSON frames, log/IPC envelopes, malformed lengths, CRC corruption, invalid identity/encoding/range, UTF-8/JSON errors, and truncation. Both implementations re-encode valid fixtures byte-for-byte and check decoded values against the manifest. CRC also has the external standard `123456789` check vector.

To intentionally revise fixtures, run `python scripts/generate_fixtures.py`, review changed bytes/hashes/expectations, then run tests in both languages. The generator is Python; the C# implementation uses an independent CRC/serialization implementation. Golden byte changes are protocol changes requiring review, not automatic fixes for failed tests.

## Run two simulated units

Open two PowerShell terminals in this directory after building. In the first:

```powershell
.\.tools\dotnet\dotnet.exe run --project src/Elf.Host -c Release --no-build -- --seconds 40 --summary host-summary.json
```

In the second, promptly:

```powershell
.\.tools\dotnet\dotnet.exe run --project src/Elf.Simulator -c Release --no-build -- --nodes 2 --seconds 30 --summary sim-summary.json
```

The receiver automatically acknowledges HELLO, arms each unit and starts it. It prints a JSON health snapshot every second. `host-summary.json` contains diagnostics, **not recorded sample data**. The simulator summary reports generated, sent and discarded rows and buffer peaks. Both applications stop on Ctrl+C. Default binding is loopback; for another computer, explicitly bind the host to its LAN address and pass that address with simulator `--host`. No firewall changes are made automatically.

For an integrated one-command demonstration:

```powershell
.\scripts\demo.ps1
```

Use a scenario file for all settings, for example `--scenario scenarios/faults.json`. The baseline files specify two or sixteen units for 1,800 seconds. Stable simulator unit IDs derive from node index; independent fleets using the same indices intentionally conflict. Each invocation/reboot creates fresh acquisition-session IDs.

Add host `--interactive` for `status UNIT`, `stop UNIT`, `arm UNIT [CONFIG RATE ENCODING]`, `start UNIT`, and `quit`. UNIT is the 32-digit hex identifier in health output. Automatic mode starts a unit after a successful arm; use `--no-auto-start` for explicit arm/start. Reusing a config ID requires identical settings.

## Qualification

```powershell
.\scripts\benchmark-stage1.ps1 -Python 'C:\path\to\python.exe'
```

This runs two nodes for 30 minutes, then sixteen for 30 minutes, using actual loopback TCP sockets. Exact counter values and row counts are checked; one-second memory/queue telemetry and JSON summaries are saved under `.artifacts/stage1/`. The Python script checks bounded queues and compares memory medians after warm-up. `-Seconds 60` is a smoke run and does not satisfy the full-duration gate.

See [Stage 1 design and operation](docs/stage1-operation.md), [Stage 1 test report](docs/stage1-test-report.md), [Stage 0 report](docs/stage0-test-report.md), and [staged plan](../Software_Implementation_Plan.md). Stage 2 adds durable recording and replay. These loopback tests do not qualify Wi-Fi range, real Pico timing or GPS-denied synchronization.

## Deployment and releases

See [deployment and version control](../Deployment_and_Version_Control.md) for the proposed self-contained Windows package, Git/release workflow, optional Python bundle, recording provenance and upgrade/rollback policy. Packaging begins in the next release-foundation increment; Stage 1 source tests do not yet establish portable deployment.
