# Pico 2 W firmware — Stage 5 bring-up

The first target, multinodedaq_diagnostic, initializes the CYW43 interface, toggles the onboard LED once per second, and emits newline-delimited JSON over USB CDC. It reports the RP2350 unique ID, source build, SDK version, system clock, uptime and initialization status. It does not yet acquire or stream samples over Wi-Fi.

Build from the repository root using software/scripts/build-pico-diagnostic.ps1. The script uses the official VS Code extension's installed SDK 2.2.0, ARM GCC 14.2.Rel1, CMake 3.31.5, Ninja 1.12.1, pioasm 2.2.0 and picotool 2.2.0-a4. Override SdkRoot if installed elsewhere. Output is software/.artifacts/stage5/build-diagnostic/multinodedaq_diagnostic.uf2.

Connect one board in BOOTSEL mode. Inspect it with picotool info -a; target an explicit USB serial number when flashing. picotool load -v -x FILE --ser SERIAL writes, verifies and executes the diagnostic. USB CDC appears as a COM port afterward; open it at 115200 baud with DTR enabled. JSON is emitted once per second while connected. SDK USB reset support permits later picotool commands with -f.

Keep firmware, build scripts and test reports in Git; firmware binaries, flash backups, serial captures and future private Wi-Fi configuration belong in ignored local artifacts. Use explicit little-endian encoders for protocol v1; never send native C structures directly.

The diagnostic target above is the initial bring-up program. The stream target below now implements hardware-paced counters, finite queues, Wi-Fi/TCP commands and streaming on two boards. See [current status](../../docs/implementation-status.md) for evidence and remaining Stage 5 work.

## Counter transport prototype (Stage 5, in progress)

stream.c uses Pico SDK 2.2.0 and lwIP raw TCP polling. The initial fixed configuration is ID 1, 25,000 XYZ rows/s, packed signed 24-bit counter data. No ADC, calibrated timing or clock synchronization is implemented.

A hardware timer produces 256-row blocks every 10.24 ms. The 160-block RAM ring holds 40,960 rows (1.6384 seconds). Production advances counters even when the ring overflows. STATUS reports dropped rows; DATA indices expose missing intervals. Frames are pipelined through a bounded 64-entry acknowledgement ledger. Each block remains buffered until its full frame is TCP-acknowledged. This is not a durable recording commit. Power loss discards buffered data.

Unit identity derives from the board ID; a random acquisition session is created on boot. Reconnect preserves both session and counters. A fresh receiver cannot yet recover configuration for an already-sampling Pico; reboot the Pico in that case. Additional modes, configurable rates and fault-injection qualification remain outstanding. A two-hour two-board recording finalized with losses and incomplete independent verification; an instrumented repeat was interrupted by a PC freeze. Neither satisfies endurance qualification.

Commands use vendored cJSON v1.7.19 (MIT; vendor/cjson/LICENSE). Limits: 4096-byte control frames, 2048-byte JSON payloads, nesting depth 16, duplicate-key rejection, eight exact-payload cached replies. Expired request IDs never execute again.

### Build and test

Create an ignored .artifacts/stage5/private/network.json with ssid, password, host (IPv4), port (45230), seed (17). Run scripts/build-pico-stream.ps1. It generates a private header with UTF-8 octal string literals. Credentials do not enter compiler arguments. The resulting UF2 contains credentials and must remain local.

Flash with picotool load -v -x PATH_TO_UF2 --ser BOARD_SERIAL -f. Close COM3 first. USB diagnostics use 115200 baud with DTR.

scripts/test-stage5-counter.py starts a C# receiver, waits for the Pico, measures 60 seconds, stops/drains recording and independently verifies counter data. For the tested computer-initiated mode, use --connect with the Pico DHCP address; see below.

scripts/enable-stage5-firewall.ps1 is a legacy machine-specific administrator helper; it is not needed for computer-initiated TCP. It opens only TCP 45230 for the local .NET runtime on Wi-Fi from 192.168.1.0/24. Remove after bench work with Remove-NetFirewallRule -Name MultiNodeDAQ-Stage5-WiFi.

### Computer-initiated TCP (preferred on this computer)

Set `"transport": "listen"` in the ignored network.json and rebuild/flash.
The Pico listens on the configured port (45230) and accepts one connection.
The host initiates TCP and then uses the same HELLO/arm/start/stop protocol.
No inbound receiver firewall rule or change from Public networking is needed.
The default when transport is omitted remains `connect` for compatibility.
The `host` field is unused in listen mode but retained in the private config.

From `C:\dev\MultiNodeDAQ\software`, after rebooting the Pico:

```powershell
.\.venv\Scripts\python.exe scripts/test-stage5-counter.py --connect 192.168.1.175 --port 45230 --ipc-port 45231
.\.venv\Scripts\python.exe scripts/verify-stage5-counter.py .artifacts/stage5/counter-TIMESTAMP
```

Use the board's current DHCP address. For the host directly, `--connect IP[,IP]`
creates bounded outbound connections instead of an acquisition listener, with
one-second retry and five-second connection attempts. IPC remains loopback-only.
This option is currently exposed through the host CLI and smoke harness.

USB reports build/destination, TCP state, total loss, timer loss and queue loss.
The larger sample ring uses a 16-packet receive pool; the linker map must be
reviewed after memory changes. Bursts longer than available buffer capacity can
still drop data; this is not a lossless endurance guarantee.

## Current diagnostic baseline — 2026-09-14

Both boards were flashed with the [instrumented build](../../docs/stage5-diagnostic-repeat.md). Recorded STATUS includes separate timer/queue drops, actual peak ring occupancy, maximum ACK-progress wait, maximum sampling-loop interval, TCP write ERR_MEM attempts and Wi-Fi link state.

The repeat showed zero timer drops, explicit queue losses and roughly 2.5-second ACK-progress stalls. Keep the 40,960-row (1.6384-second) ring and nominal rate for controlled diagnosis. Overflow identifies the immediate loss mechanism; it does not establish an inherently insufficient buffer. Investigate firmware/lwIP send/ACK handling alongside receiver/TCP and PC/AP/radio behavior before choosing a capacity change. See [analysis](../../docs/stage5-diagnostic-repeat-analysis.md).

## TCP pump update — 2026-09-14

The send pump in tcp_pump.h retries pending tcp_output independently of new
tcp_write acceptance, with a 1 ms retry interval after output errors. It also
services lwIP unsent data at bounded intervals. This does not force packets
past TCP congestion/receiver windows or replace lwIP retransmission policy.
Accepted bytes are never submitted twice. A pump call handles at most 8 KiB,
16 write iterations and a 500 us elapsed-time budget checked between calls;
a single SDK callback can exceed that budget. Radio polling resumes each loop.

The polling context exclusively owns TCP, frame preparation and ACK callbacks.
The next main-loop pump runs immediately after radio/ACK processing. USB stdout
has zero wait timeout and diagnostics are skipped with four or more source
blocks buffered. USB lines are best-effort and may be incomplete; recorded
STATUS remains the authoritative diagnostic path. The 25 krow/s rate, 256-row
blocks, 160-slot source ring and lwIP buffer configuration are unchanged.

Additional STATUS diagnostics:

- prepared_frames counts frames constructed; queued_bytes counts bytes accepted
  by tcp_write; acked_bytes counts bytes reported by tcp_sent. These include
  control traffic and reset on TCP reconnection.
- max_ack_wait_us now measures time without ACK progress only while accepted
  bytes remain outstanding. It no longer starts at frame construction, so its
  semantics differ from the earlier diagnostic build. Acceptance is not proof
  of physical transmission, and ACK is not durable disk commit.
- max_queue_wait_us measures a pending DATA backlog without new accepted bytes.
  Maximum queue/ACK waits, source drops, loop maximum and stall history survive
  reconnects within a boot. output_calls/output_errors reset per connection.
- sndbuf, snd_wnd, cwnd, snd_queuelen, nrtx, unsent and unacked snapshot lwIP
  state. nrtx is lwIP's current retransmission state, not a lifetime total.
- stall_count counts observed state transitions after 100 ms without ACK or
  DATA queueing progress, including recovery transitions. stall_events retains
  the latest four entries, oldest first. Each compact entry is:
  [boot_us_string, flags, sndbuf, peer_window, congestion_window,
  send_queue_pbufs, current_retransmissions, last_output_error].
- Flag bits: 1 send buffer empty; 2 peer window zero; 4 unsent segments;
  8 unacknowledged segments; 16 frame ledger full; 32 output retry pending;
  64 ACK stall; 128 DATA queueing stall. Flags zero marks recovery.
  These are observations, not causal classifications. A rapidly changing stall
  can overwrite older entries before the next one-second STATUS frame.

Control queue JSON capacity is 1536 bytes; STATUS construction checks bounds.
Its scratch buffers are static to avoid growing the small MCU stack. Detailed
history is bounded; heap headroom must still be checked after future changes.

See [native fault tests](tests/README.md). A short hardware smoke test does not
establish that the previous two-board endurance stalls have been eliminated.