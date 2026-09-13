# Stage 4 operator guide

MultiNodeDAQ Desktop is the Windows WPF operator application. The receiver/recorder is a separate process; UI pause, minimization, exit or failure does not stop it. Python is independently optional.

## Run

From the software directory, build with .tools/dotnet/dotnet.exe build MultiNodeDAQ.slnx -c Release, then run scripts/demo-stage4.ps1 in PowerShell 7. The demo launches two synthetic units for five minutes and introduces one recoverable disconnection. Recordings go under the current user's LocalAppData/MultiNodeDAQ/demos. The simulator and host end automatically.

For normal operation, launch MultiNodeDAQ.Desktop.exe. **Start local service** starts a loopback-only receiver on sensor port 45100 with IPC on 45101. **Connection** sets the local IPC port/token. They are saved in LocalAppData/MultiNodeDAQ/connection.json; exclude this private file from Git and reports.

For physical nodes, start the packaged host with --address LAPTOP_LAN_IP --port 45100 --ipc-port 45101 and MULTINODEDAQ_IPC_TOKEN set to the GUI token. Sensor traffic is LAN TCP; IPC is always loopback. Network binding and firewall changes are explicit operator tasks.

## Board names and addresses

The sensor fleet shows a display name, the current IP address and TCP port, and the unit/session identity separately. Disconnected sessions show **Last IP** so a stale address is not presented as a live connection. An older receiver that does not report endpoints shows an explicit service-update message.

Select a board and choose **Edit alias** to give it a local name such as `Bench A` or `North sensor`. Names are trimmed and limited to 64 characters. Clear the field to restore the firmware-provided label. The synthetic-source marker remains visible.

Aliases are saved in `%LOCALAPPDATA%\MultiNodeDAQ\aliases.json`, keyed by the full permanent unit ID. They survive GUI restarts, board reboots and DHCP address changes. They apply only to this Windows user on this computer; they do not rename firmware or change recorded metadata. **Unit details** retains the full identity and original receiver metadata.

Update both the desktop and receiver to enable IP display. Finalize any active recording before restarting the receiver; updating the GUI alone cannot add endpoint information to an older running service.

## Acquire

- Select a unit/acquisition-session pair. Synthetic sources are labeled.
- **New recording** creates a uniquely named subfolder in the chosen parent. Existing directories are never overwritten.
- Starting during an acquisition includes known configuration/timing definitions. Earlier samples remain absent and appear as an explicit missing prefix; counters are never reset to hide that absence.
- **Stop recording** stops sampling on all units, waits for terminal watermarks, drains the writer and finalizes the manifest. Restart sampling for preview afterward. Failed drain/write is marked incomplete/faulted.
- **Start sampling** re-arms an idle unit with its last acknowledged (or initial declared) configuration, then starts it. Status reflects acknowledgment, not optimism.
- **Pause plots** freezes display only; status/disk health remain monitored.
- Closing offers background continuation, orderly service shutdown, or cancel. Shutdown acknowledgment means the request was accepted; the independent service then finishes its drain.

Mean/RMS, clipping, continuity, receive rate and envelopes use C# without Python. Waveforms have at most 400 min/max bins per axis and 16,384 buffered rows per session. Missing bins and rows remain explicit. Age means time since host reception, not physical sensing latency. Calibration, battery and RSSI stay unavailable. Unsynchronized sources never imply cross-unit phase accuracy.

Disk duration is estimated from received payload rate and free space, not reserved capacity. Storage exhaustion is a recorder fault.

## Spectral analysis

Install the optional Stage 3 Python environment first. **Start / restart Python** selects its python.exe and starts a worker for the selected unit. Separate workers can cover additional units. Restart terminates only the worker launched by this window.

Views include SCF, PSD, ASD, rolling spectrogram, RMS and band-power trends. SCF uses the reduced worker grid and validity mask. Band power integrates the full one-sided PSD from DC through the configured maximum frequency before preview downsampling. These are diagnostics, not a validated drone classifier.

**Analysis settings** exposes all FAM parameters. Paper defaults: 25,000 Hz input, 10 Hz frequency resolution, 5 Hz cyclic resolution, 5,000 Hz maximum frequency, 1,000 Hz maximum cyclic frequency, hop fraction 0.25 (75% overlap), pair batch 256. Settings apply globally to workers. Requested/applied revisions and durable rejection messages remain distinct. Worker validation enforces memory/grid bounds. Changing settings resets partial windows and clears incompatible display history; raw recordings are unaffected.

Spectrogram/trend history is limited to 120 results and clears on invalid windows or revision changes. Full scientific arrays and offline analysis remain available through Python.

## Replay and reports

**Open recording** verifies a complete recording and builds a bounded sparse index off the UI thread. Choose a stream, seek, play/pause at 0.25x/1x/2x/4x, and export the displayed range to CSV plus JSON metadata/gaps. Originals are never modified. Incomplete/corrupt sessions require the existing recovery workflow.

Replay currently displays XYZ waveforms and stored events; offline spectra/HDF5 use Stage 3 Python. Nonmonotonic recovery data is handled by scanning affected segments without an unsafe early exit. Limits: 250,000 sparse entries, 1,024 displayed events. Changed source files must be reopened and verified.

**Save summary** exports service status, bounded operator events and software build identity. It excludes the connection token.

## Deploy and update

scripts/package-stage4.ps1 publishes self-contained Windows x64 desktop, host and simulator, documentation, demo launcher and per-user installer. The package needs no .NET installation. Python remains a separate optional environment. The ZIP includes file hashes, commit identity and a dirty-source flag.

Extract the ZIP and run install-stage4.ps1 in PowerShell 7. It checks file hashes, installs into a new version directory under LocalAppData/Programs/MultiNodeDAQ and creates a Start menu shortcut. Previous versions remain available for rollback. Finalize a session before switching recorder service versions.

Packages are unsigned. Clean-machine/no-internet qualification and extended field/load testing remain Stage 6 work.
