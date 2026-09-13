# Two-board battery endurance run — 2026-09-13

Status at launch: **running; no endurance pass claimed yet**.
Both boards were reported by the user to be battery-powered.
The receiver computer was on AC power, with AC idle sleep disabled by its
existing power plan. No power, firewall or network-profile settings were changed.

- Board 895DFE4DF2C37EC4: 192.168.1.175:45230.
- Board 1A0D3F9F4FDF64D9: 192.168.1.178:45230.
- Both initially entered sampling with distinct acquisition sessions, zero
  missing rows, zero counter errors and zero reported drops.
- Measurement started about 07:02:16 EDT; target is 7200 seconds, ending about
  09:02:16 EDT. Recording drain and verification follow the measurement.
- Run directory: software/.artifacts/stage5/two-board-20260913-070215.
- Background test PID at launch: 30016 (do not assume PID remains valid later).

The new scripts/test-stage5-multi.py waits for both expected boards before
starting the measurement window, samples per-board status every ten seconds,
then stops/drains the recording and shuts down the receiver. It runs the C#
recording verifier and independent Python verification for both identities.
Source-buffer peaks are sampled maxima, not guaranteed instantaneous peaks.
Unexpected identities/acquisition-session changes fail the run. Gaps, source
drops, bad counter values, receiver errors or rates outside 24,500–25,500 rows/s
prevent a lossless pass. Failures remain in the artifacts and are not reset.

Inspect status.json for running/draining/verifying/complete/failed state.
Final report.json and python-verify.json are written after verification; metrics,
host logs and failed-state evidence remain available even if the test fails.
The active-run pointer is software/.artifacts/stage5/active-two-board-test.json.
No IPC credential is written into that pointer or source control.

Repeat command (freshly reset boards and current DHCP addresses required):

```powershell
.\.venv\Scripts\python.exe scripts/test-stage5-multi.py --connect 192.168.1.175,192.168.1.178 --seconds 7200 --output .artifacts/stage5/NEW-UNIQUE-RUN
```

This run covers the concurrent counter baseline. Deliberate link interruption,
recovery, additional waveforms and measured battery runtime/current remain
separate qualification work even if this baseline passes.
