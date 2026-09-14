# Implementation status — 2026-09-14

Latest firmware update, September 14: output retries, bounded send servicing and stall history are implemented and native-tested. Board 895DFE4DF2C37EC4 was flashed and boot-checked; the new streaming test is blocked by this PC's VPN route. The second board 1A0D3F9F4FDF64D9 is also flashed and boot-checked, using clean implementation commit 23142600d523; its observed address is 192.168.1.178:45230. See [implementation and validation](stage5-tcp-pump-update.md).

Assessment of committed results through September 13, 2026. This documentation update adds no new acquisition or verification result. The [implementation plan](../../Software_Implementation_Plan.md) defines the acceptance gates.

## Position in the plan

| Stage | Status | Evidence and remaining scope |
| --- | --- | --- |
| 0: contracts and fixtures | Complete | Independent C#/Python binary fixtures and error checks passed. |
| 1: simulator and receiver | Complete | Two- and sixteen-node 30-minute loopback runs passed without missing rows or value errors. |
| 2: recording and replay | Complete | Sixteen simulated nodes, two hours, 2,879,978,240 verified XYZ rows across 144 segments; zero missing rows/sample errors. |
| 3: Python analysis/export | Complete | Independent reading, HDF5 round trip, spectral validation and worker-failure isolation passed. |
| 4: operator GUI | Implemented and tested | GUI/Python restart isolation, replay and packaged launch passed; clean-machine and sustained field qualification remain Stage 6. |
| 5: two-Pico synthetic prototype | In progress; acceptance gate unmet | Both boards stream concurrently. Smoke tests passed; endurance exposed delivery stalls, explicit source losses and a separate PC freeze. |
| 6: field qualification/package | Pending | Eight-hour sixteen-simulator full-system test, broader recovery/failure scenarios and clean-machine offline qualification remain. Earlier packaging work already exists. |
| 7: ADC, timing and calibration | Pending | Current Pico data is synthetic; real ADC integration, precise GPS-independent timing and calibration remain. |

Stages 0–5 define the first usable two-node release. Stage 6 is required before unattended field use is claimed. The completed simulator tests do not establish RF fleet capacity or qualify the current laptop against physical power loss.

Reports: [Stage 0](stage0-test-report.md), [Stage 1](stage1-test-report.md), [Stage 2](stage2-test-report.md), [Stage 3](stage3-test-report.md), [Stage 4](stage4-test-report.md).

## Hardware results

| Run | Result | Qualification limit |
| --- | --- | --- |
| One Pico, 60 seconds, September 12 | 1,514,496 XYZ rows; zero missing rows, counter errors or source drops; C# and independent Python verification passed. | One-board smoke only. |
| Two Picos on batteries, two hours, September 13 | Orderly finalization; 359,965,696 saved XYZ rows. Source/receiver losses: 30,464 and 29,184 rows. C# verification reported zero sample errors. | Not lossless. Independent Python verification timed out after 1,800 seconds; harness failed. |
| Instrumented two-Pico smoke, 10 seconds | 523,520 XYZ rows; zero drops/errors; C# and Python verification passed. | Short check of the diagnostic build only. |
| Instrumented two-Pico repeat, September 13 | PC freeze interrupted acquisition after about 99 minutes. Read-only scan checked 297,057,024 saved XYZ rows with zero CRC/counter/order errors. Queue losses: 140,800 and 94,208 rows; timer losses: zero. | Not a completed two-hour run. Twelve finalized segment hashes match; two final segments lack footers. Full configuration/timing/commit/segment-chain semantic verification is not established. |

Sources: [one-board result](stage5-counter-smoke-result.json), [battery drop investigation](stage5-drop-investigation.md), [diagnostic build and smoke](stage5-diagnostic-repeat.md), [saved-data analysis](stage5-diagnostic-repeat-analysis.md), [verification timeout and PC freeze](pc-freeze-investigation-20260913.md).

A finalized recording is not necessarily lossless, and correct saved samples do not establish that every produced sample was saved.

## Buffer interpretation and unresolved cause

The current RAM ring holds 40,960 XYZ rows: **1.6384 seconds at 25,000 rows/s**. The working engineering expectation is that this should provide substantial margin for ordinary local Wi-Fi delivery variation at the nominal payload of 225 kB/s per board. That expectation is a diagnostic baseline, not a measured guarantee for this network.

The diagnostic repeat measured approximately 2.5 seconds without TCP acknowledgment progress while data was outstanding. Both rings reached capacity; missing-row totals exactly match queue-drop totals, with zero timer drops. This establishes the immediate loss mechanism: production continued while acknowledged delivery fell behind and exhausted the ring.

It does **not** establish that a 1.6-second ring is inherently undersized, that ordinary Wi-Fi behavior explains the stalls, or that more RAM is the appropriate fix. A larger ring would tolerate longer stalls but would not identify their cause. Retain the current sample rate and ring capacity for the next diagnostic comparison.

The stalled component remains unidentified. Candidates include PC Wi-Fi/driver or power-state behavior, AP/radio delivery, receiver read/processing pauses, TCP receive-window/backpressure behavior, and firmware/lwIP send/ACK handling. These are hypotheses, not findings. Nearly coincident losses support examining the shared path. Low recorder queue occupancy in the battery run does not support recorder queue exhaustion, but cannot exclude a brief receiver pause.

The diagnostic firmware's millisecond main-loop maxima and zero timer drops narrow the problem. Zero tcp_write ERR_MEM attempts does not clear the send path: firmware skips tcp_write when tcp_sndbuf is zero. The ACK-progress maximum is not a packet RTT and does not identify a retransmission timeout.

The final PC freeze is an additional unresolved failure. Screen-off/Modern Standby is associated with the hang; losses began before the final screen-off transition and occurred long before the freeze. Keeping the display on is a controlled comparison for that association, not an established cure for all losses.

## Next work to close Stage 5

1. Preserve original recordings and make remaining semantic verification practical. The battery-run Python timeout is incomplete verification, not evidence of sample corruption. Analyze/recover the interrupted run into separate outputs; do not fabricate finalization or repair originals in place.
2. Instrument receiver read/processing pauses and disk-flush duration alongside source ring occupancy and ACK progress. Capture TCP windows/retransmissions and PC-side timing where available; inspect firmware/lwIP send eligibility, acknowledgment-ledger progress and reconnect handling. Correlate observations without claiming synchronized board clocks.
3. Compare otherwise matched runs with the system and display held awake, then a wired-PC baseline when Ethernet is available. Keep the Pico rate, ring capacity, firmware, power source and background workload matched wherever possible; record unavoidable differences. Avoid concurrent full-recording verification during the controlled baseline. Existing firmware already disables Wi-Fi power saving.
4. After diagnosing or correcting the stalls, repeat the two-hour two-board baseline with complete independent verification, reported pacing error, buffer peaks and loss accounting. A short smoke pass is insufficient.
5. Qualify commands, stop/re-arm/start, reconnect and a deliberate interruption longer than retention while the other board continues. Fresh-receiver configuration recovery for an already-sampling Pico is a known implementation gap.
6. Add and compare seeded noise, tone, burst, sweep and clipping modes with reference signals. Complete power-bank idle/reconnect stability and current/runtime measurements.

Do not mark Stage 5 complete until the plan's complete two-board gate and remaining deliverables are evidenced. No buffer-size increase, sampling-rate reduction, driver change or power-policy change is prescribed by this status update.
