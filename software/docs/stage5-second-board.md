# Second Pico programmed — 2026-09-13

The second Pico 2 W, board ID **1A0D3F9F4FDF64D9**, was programmed with the
exact same UF2 used in the first board's successful lossless smoke test.

- RP2350 A2; initially attached in BOOTSEL mode with no program metadata.
- UF2 SHA-256: AE990C3564EEAE071BBE65E55B0C89F03F570C04CA77B2CCEAF61C105615EF03.
- Explicit serial-selected flash completed with readback verification OK.
- USB serial: **COM6**; Wi-Fi DHCP address: **192.168.1.178**.
- USB confirms build 9b4ebbb7e1f6-dirty, TCP listen mode on port 45230,
  wifi_link=3, idle acquisition, zero buffered samples and zero drops.
- Unit identity is generated from this board's unique ID, so identical firmware
  does not duplicate the first board's unit identity.

The initial automatic full-flash backup could not determine the blank board's
flash size. A backup of the Pico 2 W's explicit 4 MiB flash address range was
saved before programming. Backup, flash log and USB diagnostics remain ignored
under software/.artifacts/stage5/second-board-20260913.

No receiver firewall rule or network-profile change was needed. This operation
verifies programming, boot and Wi-Fi association; no second-board acquisition
or simultaneous two-board qualification was run.

For later two-board acquisition, discover both current DHCP addresses and use
host --connect IP1,IP2 --port 45230. Reset boards before a fresh receiver.
The independent Python verifier now accepts `--boards SERIAL1,SERIAL2` for
both boards; omitting the option preserves the first-board default. The later
battery-run launch is documented in [the two-board run record](stage5-two-board-battery-run.md).
