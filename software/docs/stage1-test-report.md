# Stage 1 test report

Date: 12 September 2026  
**Status: Stage 1 complete. Regression tests and both prescribed endurance runs passed.**

## Environment and scope

Windows 11 Pro Insider Preview 10.0.26220; Intel Core Ultra 7 265K (20 cores/logical processors); approximately 191.3 GiB visible RAM. Project-local .NET SDK 10.0.401, Release build; Python 3.12.14. This is the development desktop, not a qualified field laptop.

The endurance harness uses real TCP loopback sockets with simulator and receiver in one process; memory includes both. Standalone executables were also tested together for eight seconds with two nodes: all 368,640 produced rows were received and verified. Timed interactive-host shutdown passed with stdin held open. No sensor radio, ADC, recorder, GUI or Python live-analysis worker participates.

## Regression results

- Locked restore and Release build: zero warnings/errors.
- Stage 0: 550 C# assertions and eight independent Python test methods pass; golden fixture bytes are unchanged.
- Stage 1: 61 integration assertions pass. Coverage includes fragmented/coalesced writes, both encodings, command state/idempotency, unsupported durable acknowledgments, finite-buffer overflow, reconnect, reboot identity, delayed-range repair, duplicate suppression, malformed lengths/metadata, CRC isolation, lost-tail accounting and mismatched configuration ACKs.
- The demo launcher and separate-process host/simulator checks pass.

## Endurance results

25,000 XYZ rows/s/unit, 256 rows/frame, packed int24 counter mode, 1 MiB source queue/unit. The two-node run finished before the sixteen-node run began. Every received code was compared with an independently reconstructed expected value; final unique counts matched production.

| Nodes | Duration | Verified XYZ rows | Payload bytes | Missing rows / value errors | Result |
|---|---|---:|---:|---|---|
| 2 | 30 minutes | 89,996,288 | 809,966,592 | 0 / 0 | PASS |
| 16 | 30 minutes | 719,974,400 | 6,479,769,600 | 0 / 0 | PASS |

Each row contains three axis codes. Small differences from rate times duration reflect handshake startup and complete-block emission; no generated rows were discarded or missing. Combined payload rates are approximately 0.45 MB/s and 3.6 MB/s, excluding framing/TCP overhead.

## Memory and queues

Measurements exclude the first 60 seconds. Early/late medians use the first and last thirds of the remaining samples. Predeclared regression ceilings: 32 MiB median managed-heap growth and 64 MiB working-set growth. Both pass.

| Nodes | Managed median, early to late | Managed peak | Working-set median, early to late | Working-set peak |
|---|---:|---:|---:|---:|
| 2 | 8.80 MiB to 8.80 MiB | 16.14 MiB | 59.63 MiB to 59.62 MiB | 60.53 MiB |
| 16 | 5.09 MiB to 11.62 MiB | 20.27 MiB | 70.06 MiB to 70.28 MiB | 72.17 MiB |

Managed measurements include short-lived allocations awaiting collection. Sixteen-node managed median growth was 6.54 MiB; working-set median growth was 0.21 MiB. The results support bounded operation over this test interval, not constant memory for arbitrary duration.

The largest source queue in either run was 12,020 bytes/unit against a 1,048,576-byte limit. Parser capacity was 4,096 bytes for nominal frames. Recent fingerprints stayed capped at 1,024/session; baseline missing-interval counts were zero. No baseline source drops, reconnects or receiver errors occurred.

## Reproduce and inspect

From the software directory:

```powershell
.\scripts\test.ps1 -Python C:\path\to\python.exe
.\scripts\benchmark-stage1.ps1 -Python C:\path\to\python.exe
```

- [Machine-readable qualification](evidence/stage1/qualification.json)
- [Two-node summary](evidence/stage1/baseline-2-1800-summary.json)
- [Sixteen-node summary](evidence/stage1/baseline-16-1800-summary.json)
- [Combined fault scenario](evidence/stage1/faults-summary.json)
- [CRC isolation](evidence/stage1/crc-fault-summary.json) and [terminal loss](evidence/stage1/tail-loss-summary.json)

Full one-second telemetry remains under `.artifacts/stage1/`, which is ignored by source control. Compact summaries are preserved with this report. The workspace is not currently a usable Git repository; delivery is saved as files without a commit.

## Limits and next step

Stage 1 validates the simulator, framing, multi-unit receiver and bounded state on this machine. It does not establish Wi-Fi range/capacity, synchronization precision, Pico timing or storage durability. Recovery is limited to recent fingerprints and known gaps; there is no retained source history or durable acknowledgment. See the [operation guide](stage1-operation.md) for exact bounds.

Stage 2 is next: durable recording, verification/scanning and replay.
