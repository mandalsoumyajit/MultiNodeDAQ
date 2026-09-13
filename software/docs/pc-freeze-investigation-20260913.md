# PC freeze investigation - September 13, 2026

## Conclusion and limits

An unclean system interruption terminated the diagnostic recording after about 99 minutes. The strongest recurring context is a Modern Standby session, with historical graphics-kernel live dumps warranting investigation of the display driver/firmware power-management path. No specific defective driver or component has been established. Event 41 records an unclean shutdown, not its cause.

## Timeline (EDT)

- 09:23:15: diagnostic two-board measurement starts.
- 09:24:30: Kernel-Power 506, entering Modern Standby, Idle Timeout.
- 09:26:23: Kernel-Power 507, exit due to touchpad input.
- 09:34:19: Kernel-Power 506, entering Modern Standby, Idle Timeout; scenario 28.
- 11:02:10.311: last intact metrics record, elapsed 5,934.407 seconds (594 valid records). Acquisition continued for roughly 88 minutes after the last standby-entry event. Entry alone therefore does not show immediate CPU suspension.
- 11:44:14: latest Windows boot time.
- 11:44:24: Kernel-Power 41; BugcheckCode=0, ConnectedStandbyInProgress=true, scenario 28, WHEABootErrorCount=0.
- Event 6008 records an unexpected shutdown time of 11:01:40. That estimate precedes the last intact metrics by about 30 seconds; do not treat it as an exact measured freeze time.

An earlier September 12 07:04:05 Event 41 also has BugcheckCode=0 and ConnectedStandbyInProgress=true. This supports a recurring power-state context, not a proven root cause.

## Power configuration and hardware

- Lenovo model 21RYS0F000; BIOS R2XET39W (1.19), March 2026.
- AC sleep and hibernate timeouts are both zero (Never). AC display timeout is 300 seconds.
- Supported standby: S0 Low Power Idle, Network Disconnected. S3 is unavailable. Network-connected standby is disabled by policy.
- AMD Radeon 890M driver 32.0.31041.1004, August 2026; MediaTek MT7925 driver 26.30.3.64, May 2026. These are inventory facts, not determinations of currency or defects.
- The acquisition harness requested ES_CONTINUOUS | ES_SYSTEM_REQUIRED. It did not request ES_DISPLAY_REQUIRED, so its protection did not keep the display on. It was not a guarantee against all Modern Standby phases or a hard hang.

Microsoft distinguishes screen-off and sleep phases within Modern Standby. Thus a standby-entry event is compatible with continued application work and does not alone prove the machine entered deepest idle:
https://learn.microsoft.com/en-us/windows-hardware/design/device-experiences/modern-standby-states
https://learn.microsoft.com/en-us/windows-hardware/design/device-experiences/prepare-software-for-modern-standby

## Error evidence

No current bugcheck code was recorded. No MEMORY.DMP was found. Access to LiveKernelReports was denied, so absence of a current live dump has NOT been established. Readable event queries did not reveal a contemporaneous WHEA/storage-reset event identifying a failing component.

Post-reboot Windows Error Reporting replayed LiveKernelEvent 193 reports referencing watchdog dumps dated July 15, July 31, August 27, August 31 and September 11. Their current event-log timestamps are reporting times, not new crash times. Microsoft identifies 0x193 as VIDEO_DXGKRNL_LIVEDUMP (graphics kernel). These support investigating graphics/display power management but do not tie a graphics fault to today's recording. An older 0x1cc resource-timeout report was also replayed.
https://learn.microsoft.com/en-us/windows-hardware/drivers/debugger/bug-check-0x193--video-dxgkrnl-livedump
https://learn.microsoft.com/en-us/troubleshoot/windows-client/performance/event-id-41-restart

## Recording impact

The diagnostic run did not finish. Its status.json is 3,162 zero bytes, and no final report/host summary exists. Preserve the original artifacts; do not label this a successful two-hour run or rewrite the damaged status file. Recording recovery and validation must work on a separate output.

At the last intact metrics, board 895DFE4DF2C37EC4 had 140,800 queue drops and board 1A0D3F9F4FDF64D9 had 94,208. Both had zero timer drops, full-buffer peaks of 40,960 and maximum ACK-progress stalls around 2.50 seconds. Their main-loop maxima were only 5.031 and 4.344 ms. This now confirms queue overflow rather than timer overrun for the recorded losses; it does not establish which PC/radio component caused the ACK stalls.

The original battery recording finalized, and its C# verification reported 359,965,696 rows with zero sample errors. Its independent Python verifier timed out after 1,800 seconds; its harness state is failed. Do not claim a complete independent verification pass.

## Next diagnostic steps

1. Have a Windows administrator collect SleepStudy and system sleep diagnostics and inspect the existing WATCHDOG dumps with WinDbg. A collection helper is provided at software/scripts/collect-pc-power-diagnostics.ps1. It only writes diagnostic outputs and inventories dumps; it does not change power/security settings or upload data.
2. Compare an otherwise matched run that requests both system and display awake (screen stays on, lid open, AC connected). This tests the screen-off/standby association; a pass would be evidence, not proof of a repaired driver.
3. Use Lenovo/BNL-supported BIOS, AMD graphics/chipset and MediaTek driver review once dumps identify the failing path. No drivers, firmware, managed policies or registry settings were changed by this investigation.

SleepStudy generation and live-dump inventory were blocked by Windows administrator permissions, not by an automatic approval-review rejection.

## Administrator report follow-up

The user ran the collector as administrator. SleepStudy, dump inventory and current power requests were saved in `software/.artifacts/stage5/pc-power-diagnostics`. Windows deprecated `/systemsleepdiagnostics`; that separate report was not created. The collector now uses `/systempowerreport` for future runs.

SleepStudy session 77 begins at 09:34:19 local, entered by **Video Idle Timeout**, on AC, with the lid opened and no external monitor connected. Its exit is Unknown and its duration is zero because the session was not finalized; those fields do not mean the machine stopped at 09:34. Persisted power snapshots classify 09:34:19-09:49:21 and 09:49:21-10:04:22 as **Screen Off**, with monitor off, DC=false, and Energy Saver off. CPU/application activity is recorded. No normal exit is recorded before session 78, **Abnormal Shutdown**, at 11:44:24. This supports a screen-off hang context and does not establish a transition to deepest low-power sleep or identify its cause.

The administrator dump inventory lists no live-kernel dump dated September 13. Its newest entry is WATCHDOG-20260911-0856.dmp; the graphics watchdog history predates this failure. The current post-reboot power-requests snapshot has no SYSTEM or DISPLAY request, which does not show whether the previous recording's request was active before the crash.

The new evidence does not identify a culpable driver. A controlled screen-on comparison remains the next useful experiment; dump analysis of older incidents can investigate the historical graphics/firmware lead, with that date limitation preserved.
