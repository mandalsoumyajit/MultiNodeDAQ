# ADS127L11 EVM GUI startup investigation

Date: 2026-10-10

## Symptom and diagnosis

ADS127L11 EVM GUI 1.3.0 remained at the splash screen. LabVIEW Runtime 2019 (32-bit) was installed and loaded. The Microsoft Visual C++ 2010 x86 runtime was absent.

The dependency chain is `tiPHIChar.dll -> pthreadVSE2.dll -> MSVCR100.dll`. The PHI and pthread DLLs are shipped in the GUI's `Library` directory. Before repair, MSVCR100.dll was absent and a 32-bit load probe returned Windows error 126 (module not found).

The pre-repair LabVIEW log `%TEMP%/ADS127L11 EVM_BrokenVILog_2026-10-10T163048.txt` reports broken VIs at linkage to `tiPHIChar.dll`, including PHI_Close and PHI_GetErrorInfo. This supports a software dependency failure before hardware communication.

TI describes a similar splash-screen failure in another PHI-based GUI caused by a missing Visual C++ 2010 runtime: [TI support discussion](https://e2e.ti.com/support/data-converters-group/data-converters/f/data-converters-forum/1205828/ads8860evm-pdk-unable-to-run-software). The local dependency and log evidence establish that this runtime was also missing here.

## Repair and verification

Installed the x86 Microsoft Visual C++ 2010 SP1 redistributable from the [official Microsoft package page](https://www.microsoft.com/en-us/download/details.aspx?id=26999). Verified the downloaded executable's Authenticode status was Valid and the signer was Microsoft Corporation. Installer completed with exit code 0 using `/q /norestart`.

The installed runtime reports version 10.0.40219. A subsequent 32-bit load probe, with an explicit DLL directory and full paths, successfully loaded MSVCR100.dll, pthreadVSE2.dll and tiPHIChar.dll. Restarted only the stalled TI GUI. Its loaded modules now include all three dependencies. No new BrokenVILog was observed after restart.

The first PHI probe used relative library names after PowerShell Set-Location; that is not a reliable DLL search-path setup. Its PHI failures alone were not treated as proof. The missing runtime, original LabVIEW linkage log, and corrected post-repair probe provide the diagnosis and verification.

## Confirmed startup and USB detection

The user confirmed that the GUI now reaches the main interface or connection prompt, resolving the splash-screen startup failure. The computer-use tool could not expose the TI window, so this visual result is user-confirmed.

The user then connected the PHI/EVM and reported D5 steady with D1 and D2 blinking. A subsequent Windows PnP check detected `TI,PHI(Bulk Only)` with status `OK`, USB VID_1CBE/PID_AF81. The earlier absence of the USB device was before connection.

## First capture

On 2026-10-10 the user completed a short GUI capture with no input connected and reported the expected noise-like waveform. This confirms basic GUI-to-EVM acquisition operation following the runtime repair. No raw capture or settings were reviewed independently.

Floating-input data does not establish ADC noise performance. Next, record the register/clock configuration and collect a zero-differential baseline at a valid common-mode voltage, followed by a known signal, as specified in the [ADC test plan](adc-firmware-test-plan.md). Explicit register readback and quantitative performance remain unverified.
