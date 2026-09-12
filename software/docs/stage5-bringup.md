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
