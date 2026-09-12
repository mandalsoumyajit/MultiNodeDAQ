# Stage 2 qualification report

Passed on 12 September 2026. The sixteen-node recording ran for 7,200 seconds, then independently scanned all 144 segments. The benchmark exited successfully with 38 endurance assertions, and the evidence analyzer reported `full_duration_gate: true`.

| Measurement | Result |
|---|---:|
| Nodes / sample rate | 16 / 25,000 XYZ rows per second per node |
| Verified XYZ rows | 2,879,978,240 |
| Individual axis samples | 8,639,934,720 |
| Recording files / bytes | 144 / 35,952,670,013 |
| Missing rows / sample errors | 0 / 0 |
| Recording / host errors | 0 / 0 |
| Peak recorder queue | 2,240,820 bytes (64 MiB limit) |
| Peak managed memory | 30,028,792 bytes |
| Peak working set | 101,908,480 bytes |
| Managed-memory median growth | 2,600,476 bytes (32 MiB limit) |
| Working-set median growth | 6,408,192 bytes (64 MiB limit) |

All produced rows matched independently verified recorded rows. Per-node and global queue bounds passed; all writes were flushed and recording queues drained. Memory growth compares first and last thirds after a 60-second warm-up.

## Evidence and reproduction

- [Compact qualification summary](evidence/stage2/qualification-16-7200.json)
- [Full benchmark summary](evidence/stage2/benchmark-16-7200-summary.json)
- [Recording and replay commands](stage2-operation.md)

Qualified build: `0.2.0-stage2+de6c28b9679fd9a90cdaa8ca10596e145d39b59b`. The endurance run predates the MultiNodeDAQ source rename; its original paths and build identity are preserved in the evidence. The rename is checked separately with the complete short regression suite; it does not claim a second endurance run.

Host: Windows 11 Pro 10.0.26220, Intel Core Ultra 7 265K, approximately 191 GiB RAM; .NET SDK 10.0.401. Simulator and receiver shared one process and used loopback TCP. Data used int32 containers for signed 24-bit XYZ values, 256-row blocks, 256 MiB segment targets and one-second flushes.

From `software`, after building:

```powershell
.\scripts\benchmark-stage2.ps1
python scripts/analyze_stage2.py --seconds 7200 --nodes 16
```

The benchmark performs the independent scan before writing its final summary. The analyzer checks saved evidence; it does not itself re-read all sample files. Raw recordings and one-second telemetry remain local ignored artifacts.

## Regression and failure coverage

The full suite passed both before and after the MultiNodeDAQ rename: 550 Stage 0 C# assertions, 61 Stage 1 socket assertions, 4,159 Stage 2 assertions and eight Python test methods. The renamed solution passed locked restore and Release build with zero warnings or errors. A sixteen-node, 60-second smoke run independently verified 23,976,960 rows. Separate CLI recording and range replay checks also passed.

Stage 2 exercises rotation and metadata chains, exact replay samples, historical duplicates and conflicts, gap preservation, commit acknowledgments gated by successful flush, truncated/corrupt records, invalid commit/footer semantics, injected partial writes and disk failures, slow-writer queue overflow, and recovery after killing a writer process.

These results qualify software acquisition and recording over loopback. Wi-Fi range, Pico timing, GPS-denied synchronization, and physical power-loss durability require separate hardware tests. Replay currently scans recordings sequentially; an accelerated persistent index is deferred.
