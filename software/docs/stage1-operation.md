# Stage 1: simulator and multi-unit receiver

Date: 12 September 2026

## Runtime boundaries

`MultiNodeDAQ.Simulator` generates independent unit streams; `MultiNodeDAQ.Host` receives them using `MultiNodeDAQ.Acquisition`. `MultiNodeDAQ.Protocol.FrameStream` reads a 12-byte prefix, validates the advertised length before renting payload capacity, reads exactly one complete frame and checks its CRC and binary contract. TCP write/read boundaries are irrelevant. Control frames and DATA share each connection, with a serialized write lock and sequence assignment inside that lock.

`MultiNodeDAQ.Core.CommandState` implements status, arm, start and stop with applied-state acknowledgments. An ACK must match a pending request, and an arm ACK must echo the requested configuration before DATA is accepted. Configuration IDs remain immutable within a session. The simulator returns `unavailable` for recovery requests because it has no retained history; injected delayed frames exercise receiver recovery independently. `commit_through` returns `unsupported`: Stage 1 never claims durable storage.

The receiver tracks each unit/session separately. A reconnect retains counters, sequence, metadata and deduplication state; a reboot creates a new session. Simultaneously active duplicate unit IDs are rejected. The receiver auto-arms a newly idle unit and auto-starts after a successful arm, unless `--no-auto-start` is selected. Stop followed by arm/start resumes the same simulated session; the reboot fault represents a fresh acquisition restart.

No WPF, Python runtime or disk recorder participates in reception. The integration benchmark hosts source and receiver in the same process on actual TCP loopback sockets, so its process memory includes both. The standalone executables also work in separate processes.

## Sample generation and transport faults

Default settings are 25,000 XYZ rows/s, 256 rows/block and packed int24: 225,000 payload bytes/s per unit. Int32 uses 300,000 bytes/s. Sequence numbers identify messages; full 64-bit sample counters identify data. Simulation uses elapsed monotonic time to determine how many complete blocks should exist, independent of the socket sender. It may catch up after scheduling delay; if more than 128 blocks become due in one iteration, older complete blocks are counted as producer drops. Sub-block tails are not emitted. This is a software pacing model, not a measured hardware clock.

Scenario JSON properties (case insensitive):

| Property | Default | Meaning |
|---|---|---|
| Nodes | 2 | 1–128 simulated units; receiver default permits 32 connections |
| Seconds | 30 | Production duration, including connection/setup time |
| Rate | 25000 | Nominal XYZ rows/s |
| Encoding | 1 | 1 packed int24; 2 signed int32 containing 24-bit codes |
| BlockRows | 256 | 1–4096 rows/frame |
| Mode | counter | counter, noise, tone, burst, sweep, clipping |
| Seed | 17 | Deterministic counter/noise seed |
| BufferBytes | 1048576 | Per-node queued-frame byte budget, including frame overhead |
| FragmentBytes | 0 | 0 normal writes; otherwise split writes at this many bytes |
| CoalesceFrames | 1 | Up to 16 available frames combined in one write; no wait to fill batch |
| ClockPpm | 0 | Relative source sample-schedule drift; no synchronization claim |
| Faults | [] | Events with Node (zero-based), AtSeconds, Kind, DurationSeconds |

Fault kinds: `pause` stalls the sender while production continues; `disconnect` closes its socket and delays reconnect; `reboot` clears buffered data and establishes a new session; `drop` discards newly produced rows during an interval; `delay` retains one frame and releases it later; `duplicate` repeats the next block; `corrupt` flips the final CRC byte of the next transmitted batch. Shutdown drains for a bounded extra period (at most approximately ten seconds), then accounts for discarded queued rows. A failed socket write is conservatively counted as discarded by the simulator; TCP acceptance cannot establish whether the receiver processed the bytes.

Counter formula for row `n`, axis `a` in 0,1,2: `((3*n + a + seed) mod 2^24) - 2^23`. The receiver reconstructs this independently from each full sample index. Noise is an index/axis/seed hash (implementation in `Synthetic.cs`), so a dropped frame does not change later values. Tone uses X/Y/Z frequencies 1000/1250/1500 Hz and amplitude 2,000,000 codes. Burst enables that tone for the first 0.25 seconds of each two-second period. Sweep phase is `2*pi*(600*t + 220*t*t)` on all axes. Clipping uses 12,000,000-code tone amplitude saturated at signed 24-bit limits and sets the clipping flag. Floating-point tone output is a diagnostic signal; only integer counter/noise modes promise bit-exact reconstruction. All units advertise synthetic data, calibration ID 0 and timing ID 0.

## Continuity, health and bounds

The receiver counts unique rows by range. Forward jumps create missing intervals; late frames contained in those intervals repair them. Exact recent duplicates increment a separate counter without counting rows twice. Conflicting ranges close that client's connection and leave a diagnostic. A drained idle STATUS watermark also exposes lost tail rows when no later DATA arrives. For a disconnected source without a final watermark, its unknown tail remains unknown; displayed missing counts cover only established ranges.

SHA-256 fingerprints retain the most recent 1,024 accepted blocks per session. A repeated older range outside both that window and a known gap is rejected as unverifiable. Stage 2's durable range index must support longer-history recovery. GAP messages are validated and reported as diagnostics; missing-row counts are derived from DATA ranges and terminal STATUS, avoiding double counting declarations.

| Resource | Stage 1 limit |
|---|---|
| Concurrent connections | 32 default; pre-HELLO clients count against it |
| Session registry | 128 default, retained until host restart; reject further sessions at capacity |
| Per-connection parser | One pooled buffer, maximum 1 MiB; decoded current frame adds a bounded copy |
| Pending host commands | 8/connection; reject ACKs older than 5 seconds (silent peers hit frame timeout) |
| Frame read / host write | 15-second frame deadline / 5-second write deadline |
| Configuration / timing models | 64 each per session |
| Deduplication / missing intervals | 1,024 each per session |
| Diagnostic history | 64 messages |
| Simulator frame queue | Configurable 1 MiB default, maximum 16 MiB/unit |
| Simulator command replies | 256 cached requests; expired earlier IDs rejected |

The producer queue budget excludes one deliberately delayed frame, the active coalesced send batch (at most 16 frames), frame serialization copies and socket/runtime buffers. These have separate fixed maxima; the queue number is not total process memory. Application receive queue occupancy is zero in Stage 1: each connection processes its current frame before reading the next. It yields after 64 messages to avoid monopolizing a worker during a buffered burst. Stage 2 introduces disk queues and explicit quotas.

Health snapshots include received unique rows, payload bytes (including duplicate traffic), average payload rate since the first DATA frame, last-data age, established gaps/recoveries, duplicate/conflict/error counts, reconnections, source-reported queue rows, parser capacity, pending commands and process memory. Source queue reports are periodic, not an instantaneous measurement. The average rate includes idle time; age/state must be read alongside it. `sent_rows` in simulator telemetry means a successful socket write, never durable receipt. Cumulative simulator production/drop totals span its injected reboots; host totals are per acquisition session.

New request IDs are monotonically increasing within each acquisition session, and retries carry identical payload bytes. The bounded reply cache returns the original ACK for a cached retry; older expired IDs cannot execute again. These explicit prototype limits avoid unbounded history while preserving side-effect protection.

## Qualification and next stage

`scripts/test.ps1` runs the unchanged cross-language Stage 0 fixtures and real-socket Stage 1 regression suite. `scripts/benchmark-stage1.ps1` runs two clients for 1,800 seconds, then sixteen for 1,800 seconds. Each run checks every counter value and compares generated unique rows with received rows. One-second JSONL samples capture queue and process memory. `scripts/analyze_stage1.py` compares first/last thirds after a 60-second warm-up: allowed median growth is 32 MiB managed and 64 MiB working set. These are regression ceilings, not a claim that runtime memory is mathematically constant; the saved medians/peaks support review.

Interactive console input does not hold up a timed or Ctrl+C shutdown if Windows leaves a read pending.

The default host listens on loopback. A trusted isolated LAN is required when explicitly binding a LAN interface; authentication and RF qualification are later work. Stage 1 has no raw-data recording, subscriptions, spectrum calculation, precision timing or ADC support. Stage 2 adds the writer, scanner and replay path using the established protocol boundaries.
