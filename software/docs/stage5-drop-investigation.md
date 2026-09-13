# Two-board drop investigation - 2026-09-13

## Finding

The immediate loss mechanism is source-side buffering exhaustion during brief delivery stalls. The main event is directly evidenced by both boards reporting their full 40,960-row buffers while sampling continued. The smaller first-board event shows the same buffer buildup and is strongly consistent with overflow. The underlying cause of the delivery stalls is not established by this recording.

The run is `software/.artifacts/stage5/two-board-20260913-070215`. The recorder finalized its manifest as complete, with 179,982,080 rows from 895DFE4DF2C37EC4 and 179,983,616 rows from 1A0D3F9F4FDF64D9. Complete means orderly recording finalization, not lossless acquisition. Full independent verification was still running when this investigation was written.

## Evidence

Direct inspection of the frame headers and STATUS payloads in each board's `00001.elflog` segment found these missing sample-counter intervals (end exclusive):

| Board | First missing counter | End | Rows | Missing sample time |
| --- | ---: | ---: | ---: | ---: |
| 895DFE4DF2C37EC4 | 28,581,632 | 28,609,792 | 28,160 | 1.12640 s |
| 1A0D3F9F4FDF64D9 | 28,580,608 | 28,609,792 | 29,184 | 1.16736 s |
| 895DFE4DF2C37EC4 | 38,409,216 | 38,411,520 | 2,304 | 0.09216 s |

The main losses began approximately 07:21:19 EDT; the smaller loss began approximately 07:27:53 EDT. Wall-clock estimates use nearby receiver timestamps and sample counters; they are not synchronized hardware timestamps.

Around the main event, consecutive one-second source STATUS buffer counts were:

- First board: 256 -> 12,032 -> 36,608 -> 40,960 -> 256 rows. At the full-buffer snapshot, cumulative drops were already 20,736; they settled at 28,160.
- Second board: 0 -> 12,800 -> 37,632 -> 40,960 -> 256 rows. At the full-buffer snapshot, cumulative drops were already 21,504; they settled at 29,184.
- Around the smaller first-board event: 256 -> 11,264 -> 35,072 -> 256 rows; cumulative drops increased from 28,160 to 30,464. The one-second STATUS sampling did not capture the exact full-buffer instant here.

`stream.c` has 160 slots of 256 rows, providing 1.6384 seconds at 25,000 rows/s. `produce()` advances sample counters and counts discarded rows when the ring is full. Slots are released by `sent()` only as TCP acknowledgments complete DATA frames. The observed buildup therefore indicates that acknowledged transmission was not keeping pace with production. Timer loss has a separate firmware counter, but only the combined drop count is sent over Wi-Fi; battery operation did not capture the separate USB counters. A timer contribution cannot be excluded categorically.

If delivery stops completely from an empty buffer, the main event's buffer capacity plus lost samples corresponds to roughly 2.76-2.81 seconds without sufficient draining. This is a model estimate, not a measured outage duration. It explains why a 1.64-second buffer cannot absorb this event.

## What the available logs do and do not support

- Source drop totals exactly match receiver missing-row totals: 30,464 and 29,184. Receiver sample errors, sequence gaps, conflicts and reconnects were zero. There is no evidence that successfully received DATA frames were lost by the recorder.
- The finalized recorder peak queue was 452,608 bytes against a 67,108,864-byte limit; per-board peaks were 219,648 and 232,960 bytes against 4,194,304-byte limits. Recorder errors were empty. Disk queue exhaustion is not supported.
- Both boards' first gaps nearly coincide, supporting investigation of their shared access point, PC Wi-Fi interface, and receiver scheduling. This does not identify which component stalled, nor rule out radio/firmware effects.
- Ten-second polling remained regular (largest interval 10.016 seconds), but cannot rule out a shorter stall between polls. It also missed the source buffer peaks: sampled maxima were only 21,248 rows, whereas recorded one-second STATUS frames reveal 40,960.
- No WLAN AutoConfig events were found in the 07:19-07:30 window. This does not rule out packet loss, retries, interference or scans that do not disconnect.
- A Windows Modern Standby event at 07:22:44 occurred after the first loss and does not align with either gap. It is not evidence that standby caused these losses. Application logs in the inspected window contained no matching event.
- Firmware requests `CYW43_NO_POWERSAVE_MODE`; simply suggesting that existing setting as a fix would not change this build.

## Next discriminating experiment

Before another endurance run, include separate timer/queue drop counters, maximum ring occupancy, longest TCP-ACK delay and Wi-Fi link/RSSI diagnostics in recorded STATUS frames. Capture PC-side TCP timing/window/retransmission evidence and receiver scheduling/disk-flush timing during the run. These distinguish source timer stalls, radio delivery stalls, TCP backpressure and receiver pauses.

Repeat with the PC connected to the router by Ethernet while the Picos remain on Wi-Fi, using a process-scoped keep-awake request for acquisition. Compare with the current all-Wi-Fi topology under otherwise matched conditions. Do not change firewall or managed endpoint policies. Additional RAM/storage or lower production rate could increase stall tolerance, but neither demonstrates that the underlying delivery problem is fixed.

No firmware, network settings or board state were changed during this investigation.
