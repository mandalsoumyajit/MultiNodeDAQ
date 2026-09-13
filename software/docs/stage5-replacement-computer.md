# Stage 5: first lossless one-Pico Wi-Fi recording

Date: 2026-09-12. Checkout: C:\dev\MultiNodeDAQ.

## Result

The one-Pico, 60-second counter-stream smoke test **passed** using a
computer-initiated TCP connection to the Pico. Windows remained on the Public
network profile, with no inbound acquisition firewall rule. Both the C# scanner
and independent Python reader verified the complete recording.

| Measurement | Result |
| --- | --- |
| Recorded XYZ rows | 1,514,496 |
| Observed measurement-window rate | 25,006.93 rows/s |
| Missing rows | 0 |
| Counter-value errors | 0 |
| Reported source drops | 0 |
| Acquisition/recording errors | 0 / 0 |
| Final recording / source state | complete / idle |
| Final recording queue / source buffer | 0 / 0 |

Run: software/.artifacts/stage5/counter-20260912-201312. report.json,
verify.json, python-verify.json, host.json and the recording are ignored local
artifacts. The host made two connection attempts while the Pico was starting,
then established one acquisition connection; no parser/acquisition errors
occurred. Shutdown left no active source or pending command.

This passes the initial one-board gate only. Re-arm/reconnect/overflow fault
qualification, other waveforms, two-board operation and endurance remain open.
No calibrated timebase, ADC, clock synchronization or range result is claimed.

## Hardware and firmware

Pico 2 W: RP2350 A2, 4 MiB flash, ID 895DFE4DF2C37EC4; USB CDC COM5.
Pico DHCP address: 192.168.1.175. The PC initiates TCP to port 45230.
Build label on tested firmware: 9b4ebbb7e1f6-dirty (changes saved with this report).
Verified-flash UF2 SHA-256:
AE990C3564EEAE071BBE65E55B0C89F03F570C04CA77B2CCEAF61C105615EF03.
Credentials, binaries and the complete pre-reflash backup remain local/ignored.

Firmware now supports transport=listen. It accepts one client and sends the
same HELLO/command/data protocol. The original connect mode remains available.
TCP sends are pipelined through a bounded 64-entry acknowledgement ledger;
sample blocks remain in RAM until their complete frame is acknowledged.
Partial/cumulative ACKs retire frames in order. Disconnect resets submission
state to the first unacknowledged block. These are TCP ACKs, not disk commits.

The ring now holds 160 x 256 = 40,960 XYZ rows (1.6384 seconds), and the lwIP
receive packet pool has 16 entries. The linker leaves roughly 35 KB for dynamic
allocation, plus the separate stack area. Slot cursors handle production-counter
wrap independently of the non-power-of-two ring size. USB reports build,
connection state and separate timer/queue loss counters. Longer network stalls
can still overflow the finite ring and must remain visible as reported loss.

## Why the connection direction changed

Original PC-listener attempts timed out without a connection, despite successful
Wi-Fi/DHCP, ping, correct destination, and a direct Wi-Fi route. A scoped local
firewall rule was inactive (CategoryDisabled). A user-approved temporary Private
classification did not resolve it. All temporary changes were undone: the rule
was removed and Public restored. Managed endpoint/VPN protection was untouched.
That investigation did not prove the exact filtering cause.

Computer-initiated TCP worked immediately. Early recordings then exposed source
queue overflow with the old 328 ms ring; all recorded values were correct and
loss was reported. Pipelining plus the larger bounded ring produced the pass
above. Neither missing samples nor overflow counters were hidden or reset.

## Software validation and installed tools

Release build: zero warnings/errors. Regression suite: 550 contract assertions,
70 integration assertions (including outbound retry, handshake, reconnect and
shutdown), 4159 recording assertions, 25 desktop assertions and 12 Python tests.
The independent hardware verifier checks manifest/segment integrity, record and
frame checksums, sample extents and every XYZ counter value via the Python reader.

Project-local .NET SDK 10.0.401 and the locked Python environment are installed.
Python is Windows x64 3.12. Pico tools under C:\Users\smandal\.pico-sdk:
SDK 2.2.0, ARM GCC 14.2.Rel1, CMake 3.31.5, Ninja 1.12.1, picotool 2.2.0-a4,
pioasm 2.2.0. Downloads came from official extension release URLs; SDK libraries
use the pinned SDK gitlink revisions. Two TinyUSB documentation symlinks were
excluded during Windows extraction. The ARM archive matched its server checksum.
CMake now obtains Git source identity relative to the source checkout.

## Repeat this test

The ignored software/.artifacts/stage5/private/network.json is already populated
and has transport=listen. Discover the current Pico DHCP address over USB if it
changes. Reset the Pico before starting a fresh receiver (configuration recovery
into a restarted receiver is not yet implemented).

From C:\dev\MultiNodeDAQ\software:

```powershell
& "$env:USERPROFILE\.pico-sdk\picotool\2.2.0-a4\picotool\picotool.exe" reboot --ser 895DFE4DF2C37EC4 -f
.\.venv\Scripts\python.exe scripts/test-stage5-counter.py --connect 192.168.1.175 --port 45230 --ipc-port 45231
.\.venv\Scripts\python.exe scripts/verify-stage5-counter.py .artifacts/stage5/counter-TIMESTAMP
```

The host also accepts --connect IP[,IP] for bounded outbound connections at one
port. Omitting --connect preserves its original listener mode. Local IPC remains
loopback-only. The new option is available in the host CLI and smoke harness;
GUI configuration for it remains future work.
