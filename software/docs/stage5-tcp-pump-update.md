# TCP pump implementation and checks — 2026-09-14

Items 1–3 of the firmware improvement proposal are implemented: output retries,
separate progress/stall diagnostics, and bounded send servicing. The sample
ring remains 40,960 rows (1.6384 seconds), at 25,000 XYZ rows/s. TCP send/receive
buffer settings remain unchanged.

## Implementation

- tcp_pump.h owns bounded servicing and prepared/queued/ACK progress accounting.
  Pending output is retried even with zero send space and no new accepted bytes.
  Output errors use a 1 ms backoff; normal TCP windows/retransmission rules remain
  owned by lwIP.
- stream.c retains the DATA retirement ledger and ring ownership until ACK.
  STATUS includes TCP resource/window/retransmission state and the last four
  observed stall/recovery transitions. The ACK timer starts at accepted bytes,
  rather than construction of a frame.
- Each pump call is bounded by bytes, iterations and elapsed-time checks between
  SDK calls. USB logging is best-effort with zero stdout wait and is skipped
  when source backlog reaches four blocks. No background/multicore migration was
  introduced.

See [firmware guide](../firmware/pico_synthetic/README.md) for field definitions,
counter reset semantics and limits, and [native test instructions](../firmware/pico_synthetic/tests/README.md).

## Verification completed

- Native C11 fake-TCP tests passed with MSVC /W4 /WX and assertions enabled:
  transient output failure with no send space, error backoff while accepting
  additional frames, partial writes, transient allocation failure, exact byte
  continuity, partial/cumulative/invalid ACK accounting, no premature ACK timer,
  existing unsent servicing, fatal write handling, and time/byte/iteration limits.
- Pico SDK 2.2.0 Release firmware build passed without warnings after correcting
  a format-type warning. Full current .NET solution build: zero warnings/errors.
- Linker map: __end__ 0x2007b0b0, __HeapLimit 0x20080000, leaving 20,304 bytes
  between static allocations and the heap limit. Diagnostic formatting buffers
  are static rather than added to the small MCU stack. This is a static memory
  check, not a runtime heap/stack endurance measurement.
- Attached USB board positively identified as 895DFE4DF2C37EC4, COM3.
  A verified 4 MiB pre-update flash backup was saved under ignored private
  artifacts. Flash programming/readback verification passed.
- Final listen-mode UF2 SHA-256:
  854189118B3E4F9CB84F23F766630B08BAABBC257FBB0A0D627CA1AA87855430.
  Firmware build label: a80212aef673-dirty. Source file hashes are retained in
  [the compact result JSON](stage5-tcp-pump-result.json) because the build label alone cannot
  distinguish uncommitted source variants.
- USB confirmed the new build, listen mode, Wi-Fi link up, DHCP 192.168.1.175
  and no connected TCP client. Credentials and binaries remain local/ignored.

## Hardware streaming check: blocked, no pass

The 60-second smoke harness received no samples and timed out during its
150-second connection phase. It cleaned up its receiver. Run directory:
software/.artifacts/stage5/counter-20260914-071745.

Initially, this computer's saved private configuration selected legacy connect
mode; it was corrected to listen and the board was rebuilt/reflashed. Independently,
Find-NetRoute selected Ethernet 2 (10.4.1.81, the VPN path) for 192.168.1.175,
rather than local Wi-Fi (192.168.1.180). The board was listening, but the receiver
never established a TCP connection. No VPN, routing, firewall or system power
settings were changed.

There is no new end-to-end counter/CRC/recording pass, hardware backpressure
result, or endurance claim for this firmware. The native fake backend tests
application behavior, not physical radio delivery or the real lwIP stack.
The connected board now has the updated firmware; the second board has not
been updated.

## Next hardware check

On a computer with permitted local-LAN reachability, verify the board's current
address and idle state, reset it for a fresh receiver, then run the existing
test-stage5-counter.py --connect ADDRESS and verify-stage5-counter.py RUN.
Confirm the new STATUS diagnostics arrive and inspect losses and shutdown.
Then exercise controlled receiver backpressure shorter than retention, followed
by longer interruptions with explicit loss accounting and the two-board gate.
The previous multi-second stalls remain unexplained until measured with this
build; implementation and flash success do not establish their elimination.
## Second-board deployment — 2026-09-14

After committing the implementation as 23142600d523e09137c4bf4bcf7332544a9c619b,
firmware was rebuilt from that clean source tree and flashed to the second
Pico, serial 1A0D3F9F4FDF64D9 (COM4 on this computer).

- Verified 4 MiB pre-update backup saved to ignored private artifacts.
- Firmware build succeeded; flash readback verification passed.
- USB confirms build 23142600d523, listen mode on TCP 45230, idle state and
  DHCP address 192.168.1.178 after association.
- UF2 SHA-256:
  60EDCE26C6DEF8DE87439D33EBE875BE42967199BD3BEBD5E72B9AF16426DF19.
- Local logs: software/.artifacts/stage5/second-pico-pump-backup.log,
  second-pico-pump-flash.log and second-pico-pump-usb.log in the same directory.

Both boards now have the TCP pump changes. The first board retains the earlier
a80212aef673-dirty build documented above; the second has the clean-commit build.
They share the implemented source behavior but have different embedded build
identities and UF2 hashes. No new streaming or endurance result is claimed.
The PC's previously identified VPN routing limitation was not changed.