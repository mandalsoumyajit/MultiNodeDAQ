# Stage 5 initial board bring-up

Date: 2026-09-12. One Pico 2 W connected over USB; RP2350 A2, 4 MiB flash.

- Firmware source: 7969098a0830, clean build, version 0.5.0-stage5-bringup.
- SDK 2.2.0; ARM GNU 14.2.Rel1; board target pico2_w / rp2350-arm-s.
- UF2 SHA-256: C55E437FC9DD3F1FF7C47AF3A581EE3DB003E8284E45725AC2F1FA7BEA317639.
- Original 4 MiB flash image saved locally before programming. Backup and USB captures are excluded from Git under software/.artifacts/stage5.
- picotool load -v -x completed: flash verification OK and application booted.
- USB CDC enumerated as COM3. Five consecutive JSON heartbeats reported the expected unique board ID, source build, SDK, 150 MHz clock, CYW43 initialization result 0, increasing uptime/sequence and alternating LED state.
- picotool info -a -f successfully requested BOOTSEL over USB, read firmware metadata, and returned to application mode. A subsequent USB heartbeat confirmed the same board/build after reboot (uptime about 2 seconds).

This verifies compilation, flash programming/readback, USB serial, chip identity, CYW43 driver initialization and software-controlled reboot. LED state was reported by firmware; optical operation was not independently measured. Wi-Fi association, radio range, synthetic data pacing, protocol streaming and two-node endurance are not yet tested.

## First Wi-Fi firmware, 2026-09-12

The counter-stream firmware compiles without warnings and was flashed with
picotool readback verification. Current private UF2 SHA-256:
E42C94A1E25BDAEB29B49791F1A807425196220326D6D398E93C7CFF2EDC273C.

USB diagnostics confirm Wi-Fi association and DHCP assignment. The board
remains idle while waiting for the receiver handshake. No received sample or
recording-integrity result is claimed yet.

The first network test exposed a laptop routing conflict: Cisco AnyConnect
installs a route for the local sensor subnet with priority over the direct
Wi-Fi route. A temporary board-only route was attempted, but disappeared
despite a successful Windows command, consistent with VPN enforcement.
Do not repeatedly override that route. The user must disconnect the VPN for
the bench test or use an organization-permitted local-LAN setting.

A narrowly scoped inbound Windows firewall rule was installed with elevation.
It permits only the local .NET receiver, Wi-Fi interface, configured laptop
IP, local subnet and TCP port 45230. No firewall profile was disabled.
Network credentials, generated header, firmware binaries and test logs remain
ignored local artifacts. The generated firmware embeds credentials and must
not be included in a public release.

Reproducible build, firmware behavior/limits, and the 60-second counter smoke
test are documented in firmware/pico_synthetic/README.md. Remaining gates:
first successful stream and verified recording; command/reconnect/overflow
tests; additional synthetic modes; second-board and endurance qualification.
