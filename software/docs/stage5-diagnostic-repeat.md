# Instrumented two-board Wi-Fi repeat - 2026-09-13

**Final outcome:** interrupted by a PC hang after about 99 minutes. [Saved-data analysis](stage5-diagnostic-repeat-analysis.md) documents 297,057,024 checked rows and confirmed queue-overflow losses. [PC investigation](pc-freeze-investigation-20260913.md) documents the unresolved freeze. The following sections preserve the launch conditions.

Ethernet was unavailable. Both boards were connected over USB for flashing and left on USB power for this repeat. The PC and both Picos still communicate over Wi-Fi. This is a diagnostic repeat, not the proposed Ethernet comparison or a matched battery-power test.

Both serial-selected flashes completed with readback verification. UF2 SHA-256: `C6F82356712DF581DB279DA3ACF2A30708EA65586E84A09397D71E1F12195BB5`. Firmware reports build `d5acb4c016df-dirty`; this identifies the source state used for the flashed build.

Firmware STATUS adds a backward-compatible `diagnostics` object containing:

- Separate lifetime `timer_dropped` and `queue_dropped` counters.
- Actual peak `peak_buffer_rows`, updated by the producer.
- `max_ack_wait_us`: longest observed interval with outstanding TCP data and no acknowledgment progress, including current wait at STATUS time. It is not an individual packet RTT.
- `max_loop_gap_us`: longest main-loop interval while sampling, including radio polling.
- `tcp_write_mem_errors`: number of TCP write attempts returning ERR_MEM; repeated attempts during one stall are counted separately.
- `wifi_link`: source radio link state.

These diagnostics are preserved in raw STATUS recordings and surfaced as `source_diagnostics` in receiver snapshots and the final test report. Counters/maxima are per boot; both boards were rebooted after the smoke test. No sampling rate, ring capacity or TCP buffer sizes were changed.

The test harness now accepts `--host-dll` for an isolated receiver build and `--require-diagnostics` to reject missing instrumentation. On Windows it holds a thread-scoped system-awake request and releases it on exit, without changing saved power settings. This does not override explicit user sleep or lid closure.

Windows denied PktMon driver access, so this run has no packet capture. It cannot directly identify retransmitted packets or TCP receive-window stalls. There is also no RSSI sampling or detailed disk-flush/scheduler trace in this instrumentation. Separate source timer/queue counters and ACK/main-loop delays narrow the cause without claiming those missing measurements.

Validation: firmware and isolated .NET build succeeded without warnings; 72 integration assertions passed. A 10-second two-board smoke recorded 523,520 rows with zero drops/errors, and C# plus Python verification passed. Smoke artifacts are `software/.artifacts/stage5/diagnostic-smoke-20260913`.

The two-hour repeat was launched in `software/.artifacts/stage5/two-board-diagnostic-20260913-092301`. The pointer `software/.artifacts/stage5/active-diagnostic-test.json` contains its log paths. The prior battery test retains its own pointer and artifacts. Inspect status.json for the actual measurement start/end and final report; launch alone is not a pass. Original battery-run Python verification was still active at launch, which is an additional PC-load difference to retain when interpreting this run.
