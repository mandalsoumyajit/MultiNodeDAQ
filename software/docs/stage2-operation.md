# Stage 2 recording, verification and replay

Development repository: `C:\dev\ElfDaq`; commands below run from its `software` directory. Implementation and short tests are available; the full two-hour gate is reported separately.

## Record and inspect

Build with `scripts/test.ps1` using the pinned local SDK. In one terminal:

```powershell
.\.tools\dotnet\dotnet.exe run --project src/Elf.Host -c Release --no-build -- --record C:\data\elf-run-001 --seconds 60
```

In another terminal, start the simulator promptly:

```powershell
.\.tools\dotnet\dotnet.exe run --project src/Elf.Simulator -c Release --no-build -- --nodes 2 --seconds 50
```

The recording directory must not already exist. Existing files are never appended to or overwritten. The example data directory is an operator choice; the application does not change system settings. The host records only when `--record` is supplied. It prints acquisition and recording health independently. `--version` reports the compiled product version, full Git revision and dirty-tree marker. The manifest and segment-start events preserve that build identity; node HELLO records preserve source firmware/build declarations and synthetic-data labeling.

On timed shutdown or Ctrl+C, the host requests stop from connected nodes in parallel, waits for a new drained idle STATUS within a ten-second budget, stops reception, then drains recorder queues. Unconfirmed acquisition drain, queue overflow, write/flush failure or recorder timeout produces a faulted/incomplete result and host exit code 2. Unknown tails from an uncleanly disconnected source are not silently declared received. An unexpectedly terminated process leaves a manifest in recording state and/or files without a footer.

Verify a complete directory or inspect a single interrupted file:

```powershell
.\.tools\dotnet\dotnet.exe run --project src/Elf.Host -c Release --no-build -- --verify C:\data\elf-run-001 --summary verify.json
.\.tools\dotnet\dotnet.exe run --project src/Elf.Host -c Release --no-build -- --verify C:\data\elf-run-001\UNIT-SESSION-00000.elflog
```

Directory verification checks manifest hashes, recording identity, canonical segment names/order, prior-range continuity, all record CRCs, metadata dependencies, sample bounds, commit semantics and footer counts. Counter-mode recordings also get exact code verification. Single-file inspection reports its validated byte prefix and distinguishes incomplete, truncated and corrupt. It can read a rotated segment independently, but its declared prior coverage is not proof of earlier segments; directory verification checks that context against the preceding files. A COMMIT marker found after a crash is structural evidence, not proof of storage power-loss protection.

## Range reader and replay

`RecordingReader.ReadRange` returns normalized int32 DATA blocks, configuration/timing/HELLO frames, missing ranges and a source-completeness flag. Reads are bounded to one million requested XYZ rows. It currently scans the recording, so large recordings have sequential-read latency; accelerated persistent indices are deferred. The disposable fingerprint index is rebuilt from authoritative log records, not trusted after restart.

CLI replay emits CSV to a new output file, with a JSON sidecar carrying unit/session/range, completeness, gaps and the exact metadata frames encoded in Base64:

```powershell
.\.tools\dotnet\dotnet.exe run --project src/Elf.Host -c Release --no-build -- --replay C:\data\elf-run-001 --unit UNIT_HEX --session SESSION_HEX --first 0 --count 4096 --output replay.csv
```

Use actual unit and acquisition-session identifiers from the manifest. CSV rows include sample index, XYZ codes, flags, configuration, calibration and timing IDs. `--speed 1` paces block output approximately at nominal rate; default 0 exports immediately. This CLI is an offline replay source, not a node TCP emulator or the future GUI player. No interpolation fills gaps.

Ordinary replay requires a complete verified recording. `--allow-incomplete` explicitly permits a verified prefix of an interrupted recording, including a truncated final record. It does not permit known CRC/semantic corruption, missing declared segments or hash mismatch. The sidecar marks `SourceComplete=false`; unavailable requested ranges can include an unknown tail. Recovery never edits original recordings.

## Storage and resource limits

A laptop recording-session UUID groups all per-unit/per-acquisition files. Each unit has one writer owner and a FIFO. DATA is stored losslessly as int32 axis codes, retaining IDs, flags, counters and original sender sequence. Exact duplicate blocks are suppressed with an event; conflicting historical blocks fault the recording. SHA-256 fingerprints reside on temporary disk, while merged coverage intervals are bounded in RAM. Temporary index files are deleted on close, including process termination, and never authoritative.

| Resource | Default/limit |
|---|---|
| Queued recording work | 64 MiB global, 4 MiB per acquisition stream; at most 2,048 entries/stream |
| Accounting | Worst-case normalized DATA bytes plus per-item allowance, retained through processing; excludes current serialization copies, file buffers and runtime overhead |
| Acquisition streams | 128 per recording |
| Coverage intervals | 4,096 per stream; fail explicitly on capacity exhaustion |
| Configurations/timing definitions | At most 1 MiB cached definition frames/stream; configurations limited to 64 |
| Rotated segment size | Approximately 256 MiB; metadata/footer and one block may extend it |
| Segment registry | At most 4,096 per recording |
| Flush interval | One second; CLI `--flush-seconds` |
| Recorder shutdown drain | Fifteen seconds; timeout leaves a faulted result |

Segment starts repeat HELLO and configuration/timing definitions and declare prior coverage/extent in a `recording_started` EVENT. This extends JSON metadata while preserving log-v1 binary envelopes. Directory scanning verifies coverage continuity. FOOTER row counts are unique rows newly written in that segment; complete means an orderly file close, not gap-free data. Manifest state describes the whole recording.

A COMMIT names the immediately preceding record end and the highest contiguous sample starting at zero across the acquisition session. The writer appends it, calls `FileStream.Flush(true)`, and only after successful return advances the in-memory committed counter. A node receives `commit_through` only if it advertises that capability and the counter advances. The current simulator intentionally does not advertise it because it has no retained retransmission history; a separate socket test exercises the acknowledgment path. Written, flushed and contiguous-committed values remain distinct when gaps exist.

Failures are visible in the manifest/host summary and per-stream event records. The scanner is read-only and does not search past corrupted bytes. Cleanly closed segment hashes are retained in an atomically replaced manifest. Atomic replacement and successful OS flush do not establish physical power-failure durability; the target OS/storage stack still needs qualification.

## Qualification

`scripts/test.ps1` runs the unchanged contract suite, Stage 1 socket tests and Stage 2 recording tests. Tests include rotation, exact range replay, old duplicates, late gap repair, terminal gaps, invalid commit/footer semantics, corruption/truncation, blocked flush acknowledgment ordering, failed flushes, partial writes/disk-full simulation, bounded queue overflow and abrupt termination of a test-owned helper process. Physical disk exhaustion or power interruption is not simulated by killing that process.

`scripts/benchmark-stage2.ps1` runs sixteen int32 sources for 7,200 seconds, records them with one-second flushes, and independently scans all saved data. Expected payload alone is 34.56 GB; allow additional framing, temporary indices and test evidence. A shorter `-Seconds 60` is a smoke check. Results and one-second telemetry remain in `.artifacts/stage2`; no recording data enters Git. Qualification applies to the tested development machine and does not replace field-laptop, radio or power-loss validation.
