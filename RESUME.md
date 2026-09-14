# Resume development — 2026-09-14

Latest firmware update, September 14: output retries, bounded send servicing and stall history are implemented and native-tested. Board 895DFE4DF2C37EC4 was flashed and boot-checked; the new streaming test is blocked by this PC's VPN route. The second board retains the previous build. See [implementation and validation](software/docs/stage5-tcp-pump-update.md).

## Current checkpoint

**Stage 5 is in progress; its acceptance gate has not passed.** Stages 0–3 are complete; Stage 4 is implemented and tested. Continue from the working two-Pico counter-stream prototype rather than repeating the earlier stages.

Read [current implementation status](software/docs/implementation-status.md) for results, interpretation and prioritized remaining work, and [the implementation plan](Software_Implementation_Plan.md) for the acceptance gates.

- One-Pico 60-second smoke: passed with C# and independent Python verification.
- Two-board battery run: two-hour recording finalized, but lost 30,464 / 29,184 rows. C# verification reported zero sample errors; Python verification timed out and the harness failed.
- Instrumented repeat: PC freeze ended the run after about 99 minutes. The saved 297,057,024 XYZ rows passed a byte/CRC/counter/order scan; this is not full semantic verification. Source queue losses were 140,800 / 94,208 rows, with zero timer drops.
- Both September 13 runs have ended. Historical active-run pointers and launch PIDs are not evidence of a running acquisition. The diagnostic status.json is zero-filled; use the preserved analysis and original recording evidence.

## Immediate diagnostic priority

The 40,960-row ring retains 1.6384 seconds at 25,000 XYZ rows/s. Keep it as the baseline: current evidence identifies overflow during roughly 2.5-second ACK-progress stalls, not an inherently inadequate buffer. Investigate the unexpected stalls in the PC/AP/radio, receiver/TCP and firmware/lwIP paths before choosing a buffering change.

Next, correlate receiver pauses and TCP evidence with source diagnostics and perform a controlled screen-on comparison. Use a wired PC baseline when available. Screen-off is associated with the separate freeze, but losses began before that transition. Avoid concurrent verification load during the controlled acquisition.

See [diagnostic analysis](software/docs/stage5-diagnostic-repeat-analysis.md) and [PC freeze investigation, including administrator follow-up](software/docs/pc-freeze-investigation-20260913.md). Preserve original recordings; perform recovery into separate outputs.

## Hardware and connection

| Board ID | Last reported USB port | Last observed address |
| --- | --- | --- |
| 895DFE4DF2C37EC4 | COM5 | 192.168.1.175:45230 |
| 1A0D3F9F4FDF64D9 | COM6 | 192.168.1.178:45230 |

These ports/addresses belong to the tested replacement computer and must be rediscovered after moving computers or DHCP changes. The September 13 build on both boards was the instrumented firmware described in [the diagnostic run record](software/docs/stage5-diagnostic-repeat.md); its historical verified UF2 SHA-256 is C6F82356712DF581DB279DA3ACF2A30708EA65586E84A09397D71E1F12195BB5.

Preferred transport is listen: the PC connects outward to each Pico on TCP 45230 using host --connect IP1,IP2. No inbound acquisition firewall rule was needed on the tested computer; IPC stays loopback-only. Do not reuse the legacy machine-specific firewall helper or override managed VPN policies.

The producer remains fixed counter data, packed signed 24-bit XYZ at 25,000 rows/s, configuration 1. RAM is released on TCP acknowledgment, not durable disk commit. Fresh-receiver configuration recovery for an already-sampling Pico is not yet implemented. Before a new acquisition, first establish that no measurement is active, then reset the boards for the fresh receiver.

## Setup and reproducibility

Canonical checkout: C:\dev\MultiNodeDAQ, outside OneDrive. Git transfers source and compact reports, not installed tools, recordings, private Wi-Fi configuration, UF2/ELF binaries or flash backups. Firmware binaries embed credentials and remain ignored/local.

On a new computer, use PowerShell 7, project-local .NET SDK 10.0.401 and Windows x64 Python 3.12. From software, run scripts/bootstrap-dotnet.ps1, scripts/setup-stage3.ps1 with the local Python path, then scripts/test.ps1. This recreates the local tools needed by the Stage 5 harness; do not assume they arrived with git pull.

The pinned Pico toolchain uses SDK 2.2.0, ARM GCC 14.2.Rel1, CMake 3.31.5, Ninja 1.12.1, picotool 2.2.0-a4 and pioasm 2.2.0 under the user's .pico-sdk tree. Recreate ignored software/.artifacts/stage5/private/network.json locally with Wi-Fi credentials and transport=listen. Build/flash instructions are in [the firmware guide](software/firmware/pico_synthetic/README.md); target each explicit board serial and require readback verification.

Operational references:

- [One-board verified checkpoint and repeat commands](software/docs/stage5-replacement-computer.md) — historical machine paths and initial firmware hash.
- [Two-board harness and repeat command](software/docs/stage5-two-board-battery-run.md).
- [Instrumented firmware, diagnostics and build identity](software/docs/stage5-diagnostic-repeat.md).
- [GUI operation, aliases and endpoint display](software/docs/stage4-operation.md).
- [Protocol](software/docs/protocol-v1.md), [deployment](Deployment_and_Version_Control.md).

Remaining Stage 5 work includes stall diagnosis and endurance verification, command/reconnect/overflow qualification, additional synthetic modes, measured pacing and power-bank stability. Stage 6 is required before unattended field use; ADC integration, calibration and precise GPS-independent synchronization remain Stage 7.
