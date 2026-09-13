# Diagnostic repeat analysis - September 13, 2026

## Result

The saved recording contains **297,057,024 XYZ rows**, spanning approximately **99 minutes 5.85 seconds** of source counters. It is useful diagnostic evidence, but is not a complete two-hour recording.

A read-only scan of all 14 segments checked record-envelope CRCs, frame CRCs, every saved counter value, identity and DATA ordering. It found zero CRC/counter/order errors and no malformed/truncated record tails. All 12 finalized segments match their manifest SHA-256 hashes. The two active final segments end on complete record boundaries, but lack closing footers and manifest hashes. Their hashes were calculated for evidence retention. No truncation or byte repair is needed based on this scan; no closing records were fabricated. Data produced after the final persisted records remains unknown.

This bounded independent scan does not replace full recording semantic verification (configuration/timing/commit/segment-chain validation). The scanner was checked against the previously C#/Python-verified smoke recording: all 523,520 rows, zero counter errors and matching hashes. Original files were not modified.

| Metric | First board (895DFE4DF2C37EC4) | Second board (1A0D3F9F4FDF64D9) |
| --- | ---: | ---: |
| Saved XYZ rows | 148,505,088 | 148,551,936 |
| Missing rows within saved counter extent | 140,800 | 94,208 |
| Missing fraction | 0.09472% | 0.06338% |
| Disjoint missing-counter intervals | 15 | 16 |
| Timer-dropped rows | 0 | 0 |
| Queue-dropped rows | 140,800 | 94,208 |
| Actual peak source buffer | 40,960 rows | 40,960 rows |
| Longest observed outstanding-data ACK-progress wait | 2.500670 s | 2.504892 s |
| Longest sampling main-loop interval | 5.031 ms | 4.344 ms |
| TCP write ERR_MEM attempts | 0 | 0 |

Counts come from persisted DATA frames; diagnostic maxima/totals come from the last persisted source STATUS. The later DATA totals exceed the final ten-second metrics snapshot by about eleven seconds. Neither stale manifest row counts nor the zero-filled status.json were used as authoritative sample totals.

## Mechanism and timeline

The missing intervals exactly equal each board's queue-drop total. Timer losses are zero, main-loop delays are milliseconds rather than seconds, and source buffers reached capacity. This confirms **source ring overflow while TCP acknowledgment progress fell behind sampling**, rather than sampling-timer overruns. Zero ERR_MEM attempts does not rule out TCP send-buffer exhaustion: firmware skips tcp_write when tcp_sndbuf is zero.

The ring holds only 1.6384 seconds at 25,000 rows/s, shorter than the recorded 2.5-second acknowledgment stalls. Max ACK wait is a cumulative longest interval without acknowledgment progress while frames are outstanding; it is not a per-packet RTT or proof of a particular retransmission timeout.

Ten-second receiver snapshots first reported new losses at these local EDT times:

- 09:34:06: first-board loss already present, **before** the 09:34:19 screen-off/Modern Standby entry.
- 09:34:26-09:34:36: further losses on both boards around that transition.
- 09:38:46: both boards.
- 10:01:17: first board.
- 10:23:28: second board, a small 256-row loss.
- 10:36:09, 10:36:49 and 10:37:39: both boards.
- 10:42:29: first board; no further drops appear in saved telemetry before the interruption.

These are observation times with up to roughly ten seconds of polling uncertainty, not exact packet-loss timestamps. A snapshot increment may contain multiple disjoint gaps. The last intact receiver snapshot is 11:02:10; saved DATA continues approximately eleven seconds further. The final freeze is therefore separate from multiple earlier overflow episodes.

## Attribution

Simultaneous loss bursts on both nodes support investigation of the shared PC/access-point/network path. However, this recording contains no packet capture, advertised-window/retransmission trace, or receiver scheduling trace. It cannot distinguish PC Wi-Fi/driver stalls, access-point or radio interference, receiver pauses, or network-stack effects.

The screen-off state may contribute but cannot be the sole trigger: drops began before the final screen-off transition. Nor does the final PC freeze explain losses tens of minutes earlier. This run used USB power rather than the previous run's batteries, and a previous-recording verifier was active during part of it; the comparison is not controlled for those differences.

Keep the screen-on experiment as a test of the freeze association, not as an established cure for all drops. Next acquisition diagnostics should capture receiver read/processing pauses alongside source ACK stalls, ideally with PC TCP tracing when administrative access is available. Increasing buffering alone would increase tolerance without identifying the stalled component.

## Artifacts

- Original run: `software/.artifacts/stage5/two-board-diagnostic-20260913-092301`.
- Read-only scanner: `software/scripts/analyze-stage5-diagnostic.py`.
- Full extracted STATUS history: `software/.artifacts/stage5/diagnostic-analysis-20260913-v2/source-status.jsonl`.
- Compact evidence: `software/docs/stage5-diagnostic-repeat-result.json`.

No board reset, new acquisition, system setting change, or mutation of original recordings was performed during this analysis.
