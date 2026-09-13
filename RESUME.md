## Diagnostic recording analyzed - 2026-09-13

Read-only full byte/CRC/counter scan found 297,057,024 saved XYZ rows (~99m06s), zero counter/CRC errors; all 12 finalized segment hashes match. Two final segments have valid record boundaries but no footer. Queue drops total 140,800/94,208 with zero timer drops and ~2.5s ACK stalls. Losses began before the final screen-off transition, so it is not the sole established trigger. See [analysis](software/docs/stage5-diagnostic-repeat-analysis.md). Originals remain untouched; no full two-hour or semantic verification pass is claimed.

## Diagnostic run interrupted by PC freeze - 2026-09-13

The PC rebooted uncleanly at 11:44; last intact diagnostic metrics are 11:02:10 EDT, about 99 minutes into the run. It is no longer running. status.json is zero-filled; preserve originals. See [PC freeze investigation](software/docs/pc-freeze-investigation-20260913.md). Both recent Event 41 records have Modern Standby in progress. Specific root cause remains unproven; administrator SleepStudy/dump analysis is needed. Prior launch notes below are historical.

## Historical launch: diagnostic two-board repeat - 2026-09-13

Instrumented firmware is flashed on both boards. A two-hour Wi-Fi repeat started at 09:23:15 EDT, due 11:23:15 EDT plus drain/verification. Ethernet is unavailable; boards remain on USB power. At 30 seconds both boards had zero missing rows, timer drops, queue drops and counter errors. Use `software/.artifacts/stage5/active-diagnostic-test.json`, not the older battery pointer, to inspect this run. See [diagnostic repeat](software/docs/stage5-diagnostic-repeat.md) for build hash, telemetry semantics, validation and limitations. Do not reset boards or start another receiver during measurement. The process holds a temporary system-awake request.

## Two-board measurement finished; drop investigation

The two-hour recording finalized, but is not lossless: source/receiver losses are 30,464 and 29,184 rows. At investigation time, full verification was still running; inspect the active run status/report before claiming a final verification result. Recorded STATUS frames show both source buffers reaching capacity during the main loss. See [drop investigation](software/docs/stage5-drop-investigation.md) for exact gaps, evidence, attribution limits and the next experiment. The running-test notes below are historical launch context.

## GUI aliases and IP display (2026-09-13)

Implemented editable local aliases keyed by permanent unit ID, with separate live/last IP and TCP port display. Use **Edit alias**; blank restores the firmware label. Preferences live in `%LOCALAPPDATA%\MultiNodeDAQ\aliases.json`. Operator instructions are in `software/docs/stage4-operation.md`.

Validated an isolated Release build (zero warnings/errors), 71 integration assertions, 34 desktop assertions, and the rendered fleet layout. Build outputs are under `software/.artifacts/gui-alias-build/bin`; the original Release binaries and active two-board battery receiver were deliberately left running unchanged. After the endurance test and verification finish, rebuild normally and restart the receiver/desktop to activate endpoint reporting.

# Active two-board battery test — 2026-09-13

Both boards are running the two-hour concurrent counter test, started at about
07:02 EDT; measurement is due to finish about 09:02 EDT, followed by automatic
verification. **No two-board endurance result is claimed yet.**
See [run details and artifact locations](software/docs/stage5-two-board-battery-run.md).
Read the active run's status.json/report.json before starting another receiver.

---
# Latest checkpoint: first one-Pico Wi-Fi smoke test PASSED

Saved 2026-09-12 in C:\dev\MultiNodeDAQ. The Pico is programmed and verified.
The cleaner connection direction works: the PC connects outward to the Pico,
which listens at 192.168.1.175:45230. No inbound receiver firewall rule is needed.
Windows is back on Public networking; all temporary firewall changes were removed.

The 60-second run recorded 1,514,496 XYZ rows at 25,006.93 rows/s with zero
missing rows, counter errors or reported drops. C# and independent Python
verification passed; shutdown drained and closed the recording cleanly.

Read [the tested checkpoint and repeat commands](software/docs/stage5-replacement-computer.md).
Tools and the ignored private configuration are installed on this computer;
transport is listen, board ID 895DFE4DF2C37EC4, USB COM5. The firmware pipelines
TCP sends and buffers 40,960 rows (1.6384 seconds), retaining explicit overflow
reporting. Use the Pico's current DHCP address with --connect.

## Second board update — 2026-09-13

Second Pico **1A0D3F9F4FDF64D9** now has the same verified firmware as the first.
USB is COM6; its observed DHCP address is 192.168.1.178, listening on TCP 45230.
Boot and Wi-Fi are verified; second-board streaming/two-board qualification
remain pending. See [the programming record](software/docs/stage5-second-board.md).
Next: command/re-arm/reconnect/overflow qualification, then additional waveforms,
a second board and endurance. The initial smoke pass is not Stage 5 completion.

The original handoff below is historical; the linked checkpoint supersedes its
addresses, installed-tool state, transport instructions and NOT-passed status.

---

# Resume point: Stage 5 on another computer

Saved 2026-09-12. Implementation checkpoint: **5749dc9**.
Repository: https://github.com/mandalsoumyajit/MultiNodeDAQ (main, MIT).

## Objective and next action

Continue the first **one-Pico, 60-second Wi-Fi counter-stream test**, then
independently verify the recording. Do not restart the project or repeat
Stages 0–4. The user is moving computers because ongoing work requires Cisco
AnyConnect to remain connected on the original laptop. Leave that VPN alone.

**Wi-Fi streaming has NOT passed yet.** Firmware compilation, verified flash,
USB diagnostics, Wi-Fi association and DHCP have passed. No data reached the
receiver in the attempted tests. The original laptop's AnyConnect route
preferred its VPN interface over Wi-Fi for the sensor subnet; a temporary
board-only route disappeared after creation. This is the known blocker,
not proof that the firmware transport has no further defects.

## What Git contains, and what it does not

The source, vendored cJSON license, build scripts, test harness and findings
are committed. A pull provides the work needed to continue. It does not
install tools or transfer any of these ignored, machine-local items:

- Wi-Fi credentials and generated private header.
- UF2/ELF binaries (the UF2 embeds credentials), original flash backup.
- .NET SDK, Pico tools, Python virtual environment, compiled C# binaries.
- Test recordings, logs and IPC tokens.
- Windows firewall rules or routes.

Recreate the private configuration on the destination. Do not put passwords
in chat, shell command history, commits or release assets. There is no need
to copy the old computer's virtual environment, recordings or SDK folders.

## Known hardware and firmware state

One **Raspberry Pi Pico 2 W**, RP2350 A2, 4 MiB flash, was programmed.
USB serial/board ID: **895DFE4DF2C37EC4**. It was COM3 on the old computer;
discover its COM port again on the destination.

The last flashed counter UF2 SHA-256 was
E42C94A1E25BDAEB29B49791F1A807425196220326D6D398E93C7CFF2EDC273C.
Its embedded build label was f5624b7d84bc-dirty; its source was subsequently
saved in 5749dc9 (with final documentation/script and whitespace changes).
A rebuild will have a new hash/build label.

The installed firmware still targets the **old receiver at 192.168.1.180,
TCP 45230**. The Pico received 192.168.1.175 by DHCP there. These are historical
addresses, not settings to assume on the new computer. **Rebuild and reflash
with the new receiver address even if using the same Wi-Fi router.**

The board is idle until a receiver performs HELLO, arm and start. The wire
unit ID is 4D4E442D5049434F895DFE4DF2C37EC4. Each boot creates a fresh
acquisition-session ID. Reset the board when starting a fresh receiver:
recovery of configuration into a restarted receiver is not implemented yet.

Prototype behavior:

- Configuration 1, 25,000 XYZ rows/s, encoding 1, signed packed 24-bit,
  exact counter values with seed 17.
- Hardware timer produces 256-row blocks every 10.24 ms. The 32-block ring
  holds 8192 rows (327.68 ms).
- Sampling advances independently of network sends; overflows increment
  dropped counters. STATUS and sample-index gaps expose loss.
- TCP acknowledgement releases buffered blocks; it is not a disk commit.
- US regulatory domain; Wi-Fi power saving disabled for the bench test.
- No ADC, synchronized clock, calibrated timebase or other waveform yet.

## Destination setup: Windows x64

Use a checkout outside OneDrive, preferably C:\dev\MultiNodeDAQ.
If the checkout already exists, inspect local changes before pulling;
do not overwrite them.

~~~powershell
cd C:\dev\MultiNodeDAQ
git status --short
git pull --ff-only
cd software
.\scripts\bootstrap-dotnet.ps1
.\scripts\setup-stage3.ps1 -Python 'C:\path\to\Python312\python.exe'
.\scripts\test.ps1
~~~

For a new checkout, first clone the repository into C:\dev\MultiNodeDAQ.
Use PowerShell 7, Windows x64 Python 3.12 and the pinned .NET SDK 10.0.401.
The setup script installs the locked Python dependencies. The test script
restores, builds Release and runs the existing software regression suite.
The Stage 5 harness specifically uses the ignored local .tools/dotnet SDK
and .venv; run the bootstrap even if another system SDK is installed.

Install the official Raspberry Pi Pico VS Code extension. The prior
installation was raspberry-pi.raspberry-pi-pico 0.21.0. The build script
expects these versions beneath $env:USERPROFILE\.pico-sdk:

| Component | Relative location |
| --- | --- |
| Pico SDK 2.2.0 | sdk/2.2.0 |
| ARM GCC 14.2.Rel1 | toolchain/14_2_Rel1 |
| CMake 3.31.5 | cmake/v3.31.5/bin/cmake.exe |
| Ninja 1.12.1 | ninja/v1.12.1/ninja.exe |
| picotool 2.2.0-a4 | picotool/2.2.0-a4/picotool/picotool.exe |
| pioasm 2.2.0 | tools/2.2.0/pioasm |

Use the extension's SDK/tool setup to obtain these versions. Check that all
paths exist before building. Pass -SdkRoot for a different root; if tool
versions/layout differ, update the build script explicitly rather than
silently assuming compatibility. The explicit pioasm path avoids a native
host-C++ build dependency encountered during bring-up.

## Network configuration and flash

Choose a 2.4 GHz network shared with the receiver computer. The computer may
use 5 GHz on the same bridged LAN. Check its IPv4 address and route to the
Pico; guest/client isolation or an enforced VPN route can prevent traffic.
Do not override organization-managed VPN settings.

From the software directory, create the ignored private folder:

~~~powershell
New-Item -ItemType Directory -Force .artifacts/stage5/private
~~~

Using a local editor, create .artifacts/stage5/private/network.json:

~~~json
{
  "ssid": "YOUR_2_4_GHZ_SSID",
  "password": "ENTER_LOCALLY",
  "host": "NEW_RECEIVER_IPV4",
  "port": 45230,
  "mode": "counter",
  "seed": 17,
  "country": "US"
}
~~~

Replace the placeholders locally. Use a stable receiver address during the
test. Existing Wi-Fi credentials are not available through Git.
Then build:

~~~powershell
.\scripts\build-pico-stream.ps1
~~~

Close any USB serial monitor. Verify the attached board identity and flash
the selected board; the serial below identifies the already-tested board:

~~~powershell
$taskPicotool = Join-Path $env:USERPROFILE '.pico-sdk/picotool/2.2.0-a4/picotool/picotool.exe'
& $taskPicotool load -v -x .artifacts/stage5/build-stream/multinodedaq_stream.uf2 --ser 895DFE4DF2C37EC4 -f
~~~

If software reboot is unavailable, hold BOOTSEL while connecting USB and
use picotool without -f. Require flash verification OK. USB CDC diagnostics
use 115200 baud with DTR enabled. Link 2 means associated but no IP; link 3
means an IP was acquired. USB also reports IP, TCP state and sample counters.

## Receiver firewall and smoke test

The existing enable-stage5-firewall.ps1 is **specific to the old laptop**.
Do not run it unchanged on the destination. Configure a scoped inbound rule
for the new receiver address, interface and sensor subnet. Example for an
Administrator PowerShell, started in the software directory:

~~~powershell
$taskReceiverIp = 'REPLACE_WITH_NEW_RECEIVER_IPV4'
$taskSensorSubnet = 'REPLACE_WITH_SENSOR_LAN_CIDR'
$taskInterface = 'Wi-Fi' # replace if the receiver uses another LAN interface
$taskProgram = (Resolve-Path .tools/dotnet/dotnet.exe).Path
New-NetFirewallRule -Name 'MultiNodeDAQ-Stage5-WiFi' -DisplayName 'MultiNodeDAQ Stage 5 Wi-Fi receiver' -Direction Inbound -Action Allow -Protocol TCP -LocalPort 45230 -LocalAddress $taskReceiverIp -RemoteAddress $taskSensorSubnet -InterfaceAlias $taskInterface -Profile Any -Program $taskProgram
~~~

Inspect an existing rule of this name instead of blindly creating a duplicate.
Leave IPC port 45231 local; the Pico needs only TCP 45230.
Do not disable firewall profiles.

Run from a normal PowerShell in the software directory:

~~~powershell
.\.venv\Scripts\python.exe scripts/test-stage5-counter.py --address NEW_RECEIVER_IPV4 --port 45230 --ipc-port 45231
~~~

The harness starts its own receiver and recording, waits up to 150 seconds
for samples, measures 60 seconds, stops/drains, then scans the recording.
Do not start another host on the same ports. If necessary, reboot the Pico
after the harness starts using picotool reboot --ser BOARD_SERIAL -f.
A serial monitor must be closed before picotool.

Artifacts appear in .artifacts/stage5/counter-TIMESTAMP:
host.log, host.json, recording/, verify.json and (after verification)
report.json. Initial acceptance requires:

- Complete recording and successful independent verification.
- Zero counter errors, missing rows and reported dropped rows.
- Observed rate between 24,500 and 25,500 XYZ rows/s (smoke-test tolerance,
  not timebase calibration).
- Review host diagnostics and shutdown, not just process exit.

If it fails, use USB IP/link/TCP state and host.log to separate network,
handshake, parser, throughput and shutdown failures. Treat the hardware
stream implementation as unqualified until the test actually passes.

## Work after the first pass

Record the hardware result and commit its concise report. Then qualify
command behavior, stop/re-arm/start, reconnect, queue overflow and exact
counter continuity. Add the other synthetic waveforms and user-selectable
settings, connect the second board, and perform the multi-board endurance
gate in the implementation plan. A one-board smoke pass is not Stage 5
completion and provides no hundreds-of-meters range result.

Stages 0–4, C# acquisition/recording, Python FAM spectral processing and the
reviewed WPF GUI already exist. Preserve the user's architecture choices:
Pico 2 W nodes, C# reliable acquisition, optional Python processing, multiple
triaxial units, eventual GUI-selectable settings, and GPS-denied operation.

The original laptop was left with a scoped firewall rule
MultiNodeDAQ-Stage5-WiFi; no persistent Pico route was established. Leave its
VPN and unrelated ongoing work untouched. No monitoring automation was
created for this handoff.

## Context for the next coding session

Suggested prompt:

> Read RESUME.md and software/docs/stage5-bringup.md. Continue Stage 5 from
> the committed Pico counter-stream prototype. This is the replacement
> computer; discover its tools and LAN address, recreate ignored local
> credentials, rebuild/reflash the Pico, and complete the first 60-second
> counter-stream/recording verification. Do not assume the old IP or COM port
> and do not repeat Stages 0–4.

Supporting references: software/firmware/pico_synthetic/README.md,
software/docs/protocol-v1.md, software/docs/stage4-operation.md,
Software_Implementation_Plan.md and Deployment_and_Version_Control.md.
