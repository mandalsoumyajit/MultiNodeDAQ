# Staged software implementation plan

Date: 12 September 2026  
Status: **Stages 0 and 1 completed on 12 September 2026**. Stage 1 includes the simulator/receiver and passing 30-minute two-node and sixteen-node qualification runs. Stages 2-7 remain planned. See [source and commands](software/README.md), [Stage 0 report](software/docs/stage0-test-report.md) and [Stage 1 report](software/docs/stage1-test-report.md).  
Design basis: [Base-station software plan](Base_Station_Software_Plan.md) and [detector architecture](Architecture_and_Design_Options.md).

## 1. Scope and decisions

Deliver a Windows laptop application that records multiple triaxial detector streams reliably, provides basic functionality without Python, and supports optional live/offline Python analysis. First demonstrate with **two Pico 2 W boards generating synthetic samples**, powered independently by USB battery packs. Actual ADC drivers, custom antennas, and precise field synchronization are later integrations.

Baseline decisions:

- C# on .NET 10 LTS; WPF for the Windows x64 GUI.
- Independently running recorder process, initially a user-mode console/host executable rather than an installed Windows Service requiring administration.
- C# owns sensor connections, framing, integrity, session state, raw logging, recovery, health statistics, and waveform reduction.
- Separate Python process for NumPy/SciPy spectra and experimental processing. No embedded interpreter or Python dependency for recording.
- Sensor TCP protocol; loopback TCP IPC with versioned controls and binary arrays. Separate traffic paths for controls and bulk subscriptions.
- Checksummed append-only `.elflog` as the initial authoritative recording format; Python direct reader and validated HDF5 export.
- Manual connection configuration first; discovery later. Offline operation and no GPS dependency throughout.

These update the earlier all-Python/PySide recommendation. WPF is Windows-specific; core libraries and IPC remain UI-independent. A plotting component is selected through a small WPF compatibility/performance/license review during Stage 4; that decision does not block recording development.

## 2. Proposed implementation layout

Stage 0 created these project boundaries. Stage 1 implements the simulator, receiver and command state; recording, desktop and Python live components remain later-stage boundaries:

```text
Digital_Backend/software/
    docs/                         protocol, recording, IPC and operator specifications
    src/Elf.Protocol/             frames, validation, sample decoding; no UI dependency
    src/Elf.Core/                 sessions, node registry, counters and quality models
    src/Elf.Recording/            writer, scanner, indices and recovery
    src/Elf.Acquisition/          network listeners, bounded queues and subscriptions
    src/Elf.Host/                 headless executable and control API
    src/Elf.Desktop/              WPF application, controls and live views
    src/Elf.Simulator/            multi-unit protocol generator and fault injection
    tests/                        unit, integration, durability and load tests
    fixtures/                     small shared golden binary files and expected results
    python/elfdaq/                reader, live client, analysis worker and HDF5 export
    firmware/pico_synthetic/      Pico SDK C/C++ synthetic-data source
    scripts/                      repeatable build, run, package and benchmark commands
```

Pin SDK/runtime and dependency versions with reproducible restore files. Provide one documented build/test command per language and a local demo launcher. Do not download dependencies at runtime in field operation.

## 2a. Deployment and release work across stages

Follow the [deployment and version-control plan](Deployment_and_Version_Control.md). Start with a release-foundation increment: establish a private Git repository, stamp build identity, publish self-contained Windows x64 host/simulator packages and verify a clean-machine demo. Stage 2 records version/configuration provenance and manages configuration schemas; Stage 3 packages optional Python; Stage 4 adds GUI installation and controlled upgrades; Stage 5 stamps firmware; Stage 6 qualifies the exact offline release and rollback process. Operators install immutable releases rather than maintaining development checkouts. Packaging work is planned, not part of the completed Stage 1 acceptance claim.

## 3. Stage overview

| Stage | Deliverable | Depends on | Hardware needed |
|---|---|---|---|
| 0 (complete) | Contracts, test vectors, solution skeleton | None | Laptop |
| 1 (complete) | Simulator and multi-unit receiver | 0 | Laptop |
| 2 | Durable recording, verification and replay reader | 1 | Laptop + local SSD |
| 3 | Python client/worker and export | 2 | Laptop |
| 4 | Operator GUI and live diagnostics | 2; spectra require 3 | Laptop |
| 5 | Two Pico synthetic-streaming prototype | Stable 0; end-to-end test uses 2-4 | Two Pico 2 W boards, AP, USB packs |
| 6 | Load/failure qualification and field package | 2-5 | Target laptop and boards |
| 7 | ADC/timing/calibration integration | 6 plus hardware availability | Detector hardware |

The dependency table permits firmware work once the protocol is stable; it does not authorize or require parallel agents. No fixed schedule is promised before the first stages establish integration effort.

## 4. Stage 0: freeze contracts and establish fixtures

**Deliverables**

- `protocol-v1.md`: exact offsets/types, magic/version, maximum length, byte order, checksum algorithm/coverage, commands, acknowledgment and reconnect semantics.
- `elflog-v1.md`: file/record structure, metadata ordering, checksums, footer, rebuildable index, committed position and recovery rules.
- `ipc-v1.md`: discovery of local service endpoint, per-launch token, controls, subscriptions, binary array shape/type and analysis-result schema.
- Shared golden fixtures consumed by C#, Python and firmware, including signed 24-bit extrema, axis ordering and metadata.
- Solution/package skeleton, locked dependencies, and test entry points.

**Initial parameters to specify precisely:** 25,000 triaxial samples/s, 256 samples/block, packed signed 24-bit and signed int32 wire encodings, 64-bit sample counters, persistent unit ID and fresh acquisition-session ID after reboot/restart. Define a finite frame-length limit and reject oversize/unsupported frames before allocating payload storage. Distinguish command IDs, frame sequences, and sample counters.

**Gate:** C# and Python encode/decode identical fixtures; boundary values, CRC corruption, wrong versions, truncation and invalid lengths yield specified results. Protocol document resolves all layout/CRC ambiguities before firmware integration. Timing can initially be `unsynchronized` with an explicit local counter; do not invent precision.

## 5. Stage 1: simulator and receiver

**Deliverables**

- `Elf.Simulator` with configurable unit count, stable IDs, rate, encoding, deterministic seed, tones/noise, counter patterns and scripted faults.
- `Elf.Host` CLI accepting multiple sensor clients and showing per-unit counters, rate, state, age, gaps and queue occupancy.
- Incremental TCP parser, cancellation/reconnect handling, bounded pooled buffers and fairness across units.
- A repeatable benchmark scenario file and machine-readable summary.

Pace simulation from an independent elapsed-time/sample schedule. Do not redefine produced sample time when sending stalls. Model finite device buffers and explicit drops. Counter mode must allow exact expected-data reconstruction; repeating low bits of a counter are interpreted with its full sample index.

**Gate:** two clients for 30 minutes at nominal rate, then sixteen for 30 minutes; expected sequences and generated values match. Fragment/coalesce TCP writes deliberately. Disconnect/reboot one client, inject invalid frames, and verify other streams continue. Buffer capacity is bounded in bytes and no continuing memory growth remains after warm-up. This validates laptop software, not RF scaling.

## 6. Stage 2: recorder, scanner and replay reader

**Deliverables**

- Per-unit rotating `.elflog` writer, manifests, event log, optional rebuildable index and clean-stop summary.
- Independent scanner validating every record and reconstructing counts/ranges.
- C# range reader, CLI replay source, and offline verify command.
- Explicit recording states: idle, recording, draining, complete, incomplete/faulted.
- Received/written/committed counters and a documented flush policy. Start with a configurable one-second flush interval; measure disk costs before choosing the field default.

Advance durable acknowledgments only after the chosen disk flush completes. Persist configuration/timing dependencies first. On shutdown stop accepting new recording work, drain bounded queues, flush/close files and finalize status; timeout or failure leaves an incomplete session. TCP acknowledgment is never permission to erase the node's only copy.

**Gate:** sixteen simulated units recorded for two hours at 25 kSPS, exactly verified against the generator. Inject disk stalls, write failure/full conditions, host process termination, incomplete final records and interior corruption. Scanner reports a truncated tail and detects interior corruption without silently repairing originals. Previously closed segments remain readable; recovery never duplicates or conceals conflicting sample ranges. Killing the process is not evidence of power-loss durability: separately document and test the storage/OS behavior before claiming it.

Proposed sizing: sixteen int32 streams = 4.8 MB/s; ten seconds of pending payload = 48 MB. Set a 64 MiB recorder payload budget with explicit per-node quotas and separate limits for parser/subscriber buffers. Track actual total process memory as well as payload accounting.

## 7. Stage 3: Python interface and reproducible analysis

**Deliverables**

- `elfdaq.open_session(...)` and `read_samples(...)` returning arrays plus counters, gaps, time models and calibration.
- `elfdaq.subscribe(...)` for selected-unit live blocks; `AnalysisWorker` executable returns PSD/ASD, spectrogram slices and trends.
- `elfdaq.export_hdf5(...)` and a notebook demonstrating offline analysis.
- Isolated Python environment, startup handshake, worker health and graceful restart.

IPC sends binary little-endian int32 arrays with versioned metadata, never JSON per sample or Python pickle. Result messages identify unit/session, covered sample ranges, analysis version, settings, time quality and validity. Start with copied blocks; shared memory is deferred. Limit each live-analysis subscription to a bounded recent history (initially two seconds of data) and report skipped work.

**Gate:** Python reconstructs C# fixture/log samples exactly. HDF5 round trip preserves codes and all gap/configuration/timing records. A bin-centered calibrated test tone has integrated power within 1% of its known value under the specified estimator; seeded broadband tests use documented statistical tolerance. Live and replay windows match to stated floating-point tolerance. Kill or stall Python repeatedly: recording continuity is unchanged, C# health continues, and analysis is visibly unavailable/stale. Exports must not be required to close a recording session.

## 8. Stage 4: Windows operator interface

**Deliverables**

- WPF session controls, fleet table, selected-unit X/Y/Z waveforms and status/event panels.
- Python-result views for spectra, spectrogram and band-power trends.
- Replay open/seek/play/speed controls, event markers, disk-duration estimate and summary report.
- A plotting component selection recorded with supported framework, redistribution terms and a representative drawing benchmark.

C# computes essential continuity, clipping, mean/RMS, receive-rate and disk health. Basic waveform drawing uses bounded envelope data at about 5 updates/s. Show separate sampling, streaming, recording and analysis states; staleness and faults cannot be represented by a frozen green indicator. Optional metadata stays unavailable rather than showing fabricated battery/RSSI/calibration values.

**Gate:** GUI pause/minimize/restart does not stop the recorder. Essential controls and waveform/status remain useful with Python absent. Target selected-unit display age below 500 ms on nominal local-network load, excluding intentional averaging; show actual age when exceeded. Verify clipping/gap/unsynchronized indicators and command failures. Changing filters affects display/derived outputs, never original recording. Closing the GUI explicitly offers recorder continuation or orderly shutdown.

## 9. Stage 5: two-board synthetic field prototype

**Deliverables**

- Pico SDK C/C++ firmware with persistent IDs, protocol v1, hardware-paced sample production, ring buffers and network service.
- Counter, seeded-noise, tone, burst, sweep and clipping modes with documented expected results.
- Configurable interruption/reboot simulation and queue/overflow telemetry.
- Flashing, Wi-Fi setup and two-node run instructions.

Use a timer/PIO/DMA-assisted schedule with measured pacing independent of socket progress. Nominal payload is 225 kB/s packed or 300 kB/s int32 per board. Maintain sample counters even when data is lost; never slow synthetic time to disguise saturation. Synthetic data is labeled in handshake, files and GUI. The initial firmware's buffering is RAM-only unless storage is actually added; outages longer than retention must yield explicit gaps.

Power each board from a USB pack through micro-USB. Check low-current shutoff during idle/reconnect and measure actual current/runtime. Use an ordinary AP with the laptop preferably wired to it for the baseline, then repeat the intended laptop Wi-Fi arrangement.

**Gate:** both boards stream concurrently for two hours with exact counter verification and no unexplained losses under baseline conditions. Interrupt one link beyond its buffer, restore it, and confirm explicit gaps/recovery while the other continues. Compare seeded signals with laptop-generated references. Report per-node payload, pacing error, buffer peaks, reconnect behavior, and power-bank stability. This gate does not establish ADC noise, precision synchronization or final antenna range.

## 10. Stage 6: qualification and field packaging

**Deliverables**

- Qualify the self-contained Windows x64 release built through the earlier deployment increments, plus the independently optional Python analysis bundle; preserve release manifests, source tags and exact tested artifacts.
- Offline installer/demo, operator guide, configuration templates, diagnostic bundle and replay sample.
- Automated acceptance scenarios and a report identifying laptop CPU/RAM/SSD, OS, runtime versions, AP/network and test settings.

**Gate:** eight-hour sixteen-unit simulated recording, GUI and Python active; no unexplained data loss, stable bounded queues and memory after warm-up. Test recovery traffic alongside live streams, disk pressure, process restarts, malformed clients, laptop network changes, and interrupted sessions. Do not infer sixteen-radio capacity from simulated clients. Qualify the actual RF fleet separately when hardware exists.

Test on a clean laptop without developer tools and without internet. Recording starts and operates without Python installed/enabled. Confirm log verification, replay, configuration persistence and clear firewall/network diagnostics. Do not alter laptop sleep/power policies silently; provide a preflight indication and record sleep-related interruptions.

## 11. Stage 7: real ADC, timing and calibration integration

Replace only the synthetic producer with the three-ADC acquisition path while preserving protocol and recording interfaces. Add ADC status/CRC, synchronized clock/START, local storage recovery, timing observations and calibrated transfer functions as hardware becomes available.

Gate calibrated units/phase features on valid calibration and GPS-independent timing evidence. Record ADC filter delay, settling, configuration boundaries and timing uncertainty. Exercise cold start without GPS or internet, holdover and reacquisition. Cross-node coherence is enabled only when its timing/error criteria are satisfied. Advanced drone detection is a separate validated algorithm milestone, not a prerequisite for basic DAQ.

## 12. First usable release and deferred work

The first usable two-node release completes Stages 0-5: independent C# recording, fleet health, raw waveforms, optional Python spectra, verified replay/export, and two hardware-paced synthetic Pico streams. Stage 6 is required before unattended field use is claimed.

Deferred: custom antenna boards, precise synchronization implementation, ADC integration, cloud features, automatic classifier claims, shared-memory optimization, and native C# HDF5 writing. The plan preserves their extension points without making the cheap two-board prototype depend on them.

Each stage ends with saved source/documentation, reproducible commands, small fixtures and a test report; commit when the workspace has a usable source-control repository. A failed gate yields an explicit issue and revised budget/design; marking a stage complete requires evidence, not just a running demonstration.
