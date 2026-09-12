# Laptop base-station software plan

Date: 12 September 2026  
Status: architecture baseline; Stage 0 contracts/codecs and compiling skeleton implemented. A runnable acquisition application is not implemented yet. See the [Stage 0 report](software/docs/stage0-test-report.md).  
Companion: [Detector architecture and design options](Architecture_and_Design_Options.md).

## 1. Purpose and recommendation

Build an offline laptop application supporting multiple triaxial sensor units concurrently. Its two primary functions are:

1. Log original incoming samples and metadata for subsequent scientific analysis.
2. Perform live processing and visualization so users can evaluate sensor function, interference, signal response, and communications.

Use **C#/.NET 10 LTS for an independent acquisition/recording process and a WPF desktop application**, with an optional **out-of-process Python/NumPy/SciPy analysis worker**. Essential health checks and waveforms remain available without Python. Target Windows x64 laptops first; keep protocol and recording libraries independent of WPF. Package a reproducible environment for field users. No cloud, GPS, or internet connection is required. See the concrete [staged implementation plan](Software_Implementation_Plan.md).

The choice favors predictable ownership of buffers, Windows integration, packaging, and fault isolation; it is not a claim that Python cannot sustain the expected 4.8 MB/s sixteen-unit load. C# is not hard real-time, and reliability still requires bounded buffering and testing. Python is not embedded in the recorder and never owns the durable recording path. A separate Python reader/API supports notebooks and replay.

## 2. Architecture

```text
Sensor A ----+
Sensor B ----+-> per-node TCP receive -> validation -> recording queue -> writer -> SSD
Sensor ... --+                              |
                                           +-> bounded live buffers -> processing
                                                                            |
                                                                     GUI snapshots

Recorded session -> replay adapter -> same processing functions -> same GUI
```

The acquisition service owns connections, framing, session state, and recording. Use asynchronous .NET socket readers with incremental framing and a dedicated writer worker for disk I/O. Use bounded Channels, pooled buffers with explicit ownership, cancellation, and byte-budget accounting; a message-count bound alone does not bound memory. Run the GUI separately so minimizing, freezing, or restarting it does not stop recording. Isolate heavier processing in a worker process; exchange blocks or reduced display snapshots rather than individual Python sample objects.

Recording has priority. Live visualization may skip obsolete updates, but recording loss must never be silent. Give every queue a byte limit, high-water warning, and explicit policy. If the recorder cannot keep up, request node buffering/recovery where supported; otherwise mark the affected ranges lost and the session incomplete. TCP backpressure eventually fills finite node buffers and does not guarantee losslessness.

Use per-node queue/recovery limits so one slow or reconnecting unit cannot monopolize resources. Use versioned length-prefixed messages over loopback TCP for local C#/Python communication, with JSON control/metadata and binary little-endian int32 sample arrays. Bind the local API only to loopback and use a per-launch connection token. Sensor listeners are separate and bind to the selected field-network interface. A message broker is unnecessary.

Separate control/status, live-analysis delivery, and replay requests so a slow subscriber cannot block commands or acquisition. Python subscribes to selected units and returns versioned spectra/scalars with sample ranges, parameters, and validity. Analysis queues drop stale work with explicit counters; they never backpressure the recorder. Start with buffered copies, not shared memory. Add shared memory only if profiling justifies its ownership complexity. Python worker failure triggers an unavailable/stale indicator and an explicit restart path, while recording and C# health monitoring continue.

## 3. Multiple-unit discovery, identity, and control

Nodes connect as clients to a configured laptop address and port. Offer optional local discovery plus manual address entry, since discovery may fail across routed subnets or access-point isolation. Show selected network interface, listen address, and connection diagnostics.

The handshake provides persistent unit ID, boot/acquisition-session ID, firmware/protocol version, axis mapping, ADC configuration, sample rate, calibration ID, and capabilities. Identify units independently of IP address. Detect duplicate IDs and incompatible versions instead of merging streams.

Assign operator labels, such as `North fence`. Store optional surveyed position/orientation in a local site coordinate system; GPS coordinates are not required. A table is the primary fleet view; a site map is optional.

Provide per-unit and selected-group commands for status, arm/start/stop acquisition, supported configuration, and recovery requests. Commands have unique IDs and acknowledgments indicating actual resulting state. Group operations are not atomic; show success/failure separately for each unit.

Distinguish **sampling**, **streaming**, and **recording to laptop disk**. Preview without recording may be supported, but must be unmistakably labeled. Log configuration changes at their effective sample counter. Changes requiring ADC restart create a segment boundary and invalid settling interval. Sending a group start command does not itself synchronize sampling.

## 4. Data protocol

Use versioned, length-prefixed binary blocks over TCP initially. TCP is a byte stream: handle partial headers, partial payloads, and multiple blocks in one read. Validate sizes before allocation. Specify endianness, signed 24-bit decoding, axis order, frame checksum, and maximum length in the protocol specification before firmware implementation.

Each block includes:

- Unit and boot/acquisition-session IDs.
- Frame sequence, first sample counter, sample count, axes, and encoding.
- ADC configuration/calibration identifiers and nominal rate.
- Timing-reference/model identifier, mapping or observations, and uncertainty.
- ADC CRC/error, overflow, clipping/overrange where available, and settling flags.
- Original sample payload and an end-to-end checksum.

An illustrative block of 256 triaxial samples contains 2,304 packed bytes and spans 10.24 ms at 25 kSPS. TCP segments this as necessary; a TCP application block need not fit one network packet. Final block size must balance overhead, latency, and recovery granularity.

Use laptop monotonic receive time for latency and stalls, and optional wall-clock time for operator context. Neither replaces ADC acquisition time. Record the origin and quality of the common time reference.

On reconnect, distinguish a reboot from a link interruption. Deduplicate by unit, acquisition session, and sample range. Keep conflicting duplicate payloads as errors. Recovered historical blocks retain original counters and do not rewind live plots. If nodes delete buffered data based on laptop acknowledgments, define a separate **durably recorded acknowledgment**; a TCP ACK only establishes transport receipt. Test the actual disk durability boundary before relying on it.

## 5. Recording format and session management

Recommended session structure:

```text
session_label_UUID/
    manifest.json
    events.jsonl
    calibration/
    nodes/
        unit_001/segment_000001.elflog
        unit_001/segment_000002.elflog
        unit_002/segment_000001.elflog
    derived/
```

The manifest records software version, units, operator/site notes, configuration snapshots, time-reference definitions, and completion state. Events capture recording transitions, joins/leaves, errors, configuration changes, timing changes, and operator annotations.

Use one C# writer owner per file. The initial authoritative format is a documented, versioned, checksummed append-only `.elflog` container with a file header, length-delimited records, original identity/counter/timing metadata, and int32 sample arrays. Rotate per unit about every five minutes. Preserve the original 24-bit codes exactly, without filtering. Keep optional rebuildable indices for seeking; the log must be readable without the index.

Store configuration and timing records before samples that reference them, and retain an acquisition-session ID across file rotations. Distinguish dataset/file order from sample-time order. Recovered blocks retain original counters; readers reconstruct chronology and identify duplicates/conflicts. Do not insert zero samples to hide gaps.

A C# recovery scanner verifies complete records and checksums, reports a truncated tail, and does not silently skip interior corruption. Scan read-only by default; explicit repair creates a new recovered artifact. Closing a segment records final counts and its integrity summary. Track received, written, and durably committed positions separately. Advance durable acknowledgments only after the selected flush-to-disk operation completes; validate its behavior on the target OS/filesystem/storage. No software flush guarantees survival of every hardware power-loss failure.

The Python API reads the raw log directly and exports selected or closed sessions to **HDF5 through h5py** for scientific interchange. Export preserves raw values, sample ranges/gaps, configuration, calibration, timing quality, events, and processing provenance. HDF5 is not a prerequisite for live recording. Use exact cross-language fixtures to verify C# logs and Python readers. Keep raw logs until exported copies have been validated and retention policy permits removal; never delete originals automatically.

Raw data remains immutable. Derived outputs carry algorithm version and parameters. CSV is for short selected exports and summaries, not bulk continuous recording. Export additional disk requirements must be shown to the operator. Native C# HDF5 writing can be revisited if a concrete requirement justifies its dependency and recovery testing; it is not needed for the first release.

Default to a local SSD recording directory. Copy closed sessions to shared/cloud storage afterward; avoid making recording depend on a network or synchronizing filesystem.

### Capacity

At three axes x 25 kSPS x four bytes, each unit writes **300 kB/s, 1.08 GB/hour, or 25.92 GB/day**. Sixteen units produce **4.8 MB/s, 17.28 GB/hour, or 414.72 GB/day**, before metadata. Five minutes per unit is approximately 90 MB. Packed 24-bit storage would reduce these figures by 25%.

A ten-second buffer for sixteen units holds 48 MB of sample payload, excluding copies/object overhead. This absorbs brief stalls, not prolonged failures. Display free disk space and estimated remaining recording duration. Disk-full/write failure must immediately change recording status and trigger retention requests where supported; never silently overwrite prior data.

## 6. Operator interface and workflow

All selected units are logged regardless of which are plotted. Use a fleet table and selected-unit detail panels.

| View | Contents |
|---|---|
| Session bar | Output path, elapsed time, recording state, selected units, free disk, start/stop recording and event marker |
| Fleet table | Label/ID, connected/sampling/recording state, last-data age, received rate, gaps/recovery, timing uncertainty, clipping, RMS/band power; battery/temperature/RSSI when available |
| Waveforms | X/Y/Z, selectable time span, units, fixed/autoscale, clipping and gap markers |
| Spectrum | X/Y/Z PSD or ASD, frequency range, averaging and labeled units |
| Spectrogram | Selected axis/unit, explicit color scale and missing-data regions |
| Trends | Band power, RMS, peak frequency, timing and link health over minutes |
| Event log | Test annotations, faults, disconnects, configuration and recording changes |
| Replay | Session/time/unit selection, play/pause/seek/speed, same analysis views |

Show stale values explicitly. Use text/icons as well as color. Pause/freeze affects visualization only. Closing the GUI while recording should offer a clear choice between keeping the recorder running and stopping it cleanly.

Field workflow: select output directory -> discover/select units -> inspect health -> start recording -> examine chosen units -> annotate physical test actions -> stop and review the session summary. Group command acknowledgments and missing units remain visible throughout.

## 7. Real-time processing for functional evaluation

C# implements continuity, received rate, last-data age, clipping, mean/RMS, disk/queue health, and basic waveforms. Python provides configurable spectral and experimental processing. Start with interpretable diagnostics:

1. Raw/voltage waveforms, mean/DC offset, RMS, peak, clipping fraction and integrity checks.
2. Windowed PSD/ASD and spectrogram; default to 600 Hz-5 kHz with wider inspection available.
3. Integrated band power, baseline/noise trends, prominent peaks, optional harmonic overlays.
4. X/Y/Z comparisons; calibrated vector magnitude or covariance/polarization diagnostics later.
5. Optional baseline-relative threshold events, labeled as diagnostic flags rather than validated drone detections.

Use voltage units until suitable calibration is available. Coil-to-field conversion generally requires a frequency-dependent transfer function, not one volts-to-tesla constant. Save per-axis gain/phase response, validity band, orientation, and uncertainty. Label PSD in units squared/Hz and ASD in units/sqrt(Hz); distinguish these from FFT magnitude and integrated RMS.

A proposed starting spectrum uses 4,096 samples, a Hann window, and 50% overlap. At 25 kSPS this spans **163.84 ms**, has **6.10 Hz bin spacing**, and produces a window every **81.92 ms**. Averaging adds latency; bin spacing is not window equivalent-noise bandwidth. Plot at about 5 updates/s and show the effective window/averaging settings.

Compute lightweight health statistics for every unit. Initially compute detailed spectra for selected units; broaden only after profiling. Use envelope/min-max plotting to preserve peaks in reduced displays, or anti-alias filtering for a truly decimated waveform. Display reduction never changes recorded raw data.

Do not process FFT windows across gaps, settling periods, or configuration changes as valid continuous samples. Reset stateful filters where required. Show processing backlog separately from acquisition loss. Live processing may skip updates to preserve recording.

Cross-unit coherence/phase processing is a later capability gated by timing quality, rate correction/resampling, and analog calibration. A common screen does not imply aligned samples. Suppress or qualify comparisons when timing uncertainty exceeds the analysis threshold.

## 8. Replay and reproducibility

Implement processing functions accepting sample arrays plus timing, validity, and calibration. Live and replay sources call the same functions. Save algorithm versions and parameters with derived results; altering a display filter never changes raw data.

Replay supports deterministic recomputation and clearly labeled accelerated playback. Event markers retain operator/laptop time and its mapping/uncertainty to network time where available. Session summaries list expected/received/recorded/recovered samples per unit, unresolved gaps, timing-quality intervals, changes, and recording failures.

## 9. Implementation stages and acceptance tests

The executable work breakdown, component boundaries, dependencies, deliverables, and pass/fail gates are in [Software_Implementation_Plan.md](Software_Implementation_Plan.md). Stage 0 is complete for contracts and a compiling skeleton; the runtime stages remain planned. The initial hardware milestone is **two stock Pico 2 W boards producing hardware-paced synthetic triaxial data**, each powered by a USB battery pack. Laptop simulators exercise fleet scaling before boards arrive; they do not reproduce RF contention.

## Sources for software choices

- **B1:** [Microsoft .NET support policy](https://dotnet.microsoft.com/en-us/platform/support/policy) - .NET 10 LTS, supported into November 2028; pin the SDK and supported servicing version at implementation.
- **B2:** [Microsoft WPF overview](https://learn.microsoft.com/en-us/dotnet/desktop/wpf/overview/) - Windows desktop UI framework.
- **B3:** [SciPy signal processing](https://docs.scipy.org/doc/scipy/reference/signal.html) - filtering and spectral analysis.
- **B4:** [h5py documentation](https://docs.h5py.org/en/stable/) - Python HDF5 interchange/export.

Library choices, defaults, UI, and acceptance targets are proposals. No laptop throughput, crash-recovery, or numerical implementation has been validated yet.
