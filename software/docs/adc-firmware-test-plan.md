# ADS127L11 ADC firmware test strategy

Date: 2026-10-10
Status: proposed bench qualification; no ADC firmware or hardware qualification completed.

Use the ADS127L11EVM-PDK to establish a TI reference capture, replace the PHI controller with a Pico 2 W for single-channel firmware development, then integrate transport and qualify the final three-channel hardware.

## Sources and existing implementation

- [EVM manual SBAU351](ADS127L11EVM/sbau351.pdf), especially sections 1.3, 2.7, 3, 4, 6 and schematics in section 7.
- [ADS127L11 datasheet](https://www.ti.com/lit/ds/symlink/ads127l11.pdf), especially timing, programming, CRC and filter latency.
- [Project architecture](../../Architecture_and_Design_Options.md).
- [Current counter firmware](../firmware/pico_synthetic/README.md) and [protocol v1](protocol-v1.md).
- [Analog Discovery 3 bench control and J2 decoder](ad3-bench-control.md) (`multinodedaq.ad3` and `scripts/ad3-control.py`).

The target is high-speed operation, wideband filter, OSR 512 and a 25.6 MHz conversion clock for nominal 25 kSPS per axis. The existing firmware produces timer-paced synthetic XYZ counters. ADC conversion events must replace that timing source while preserving bounded queues and explicit gaps.

## Bench configuration

- Establish the reference with the intact EVM and PHI. Save GUI version, register dump, jumpers, supplies, reference voltage, clock source/frequency and signal-source settings.
- PHI uses 1.8 V digital I/O. Pico connection requires suitable level translation or a deliberately reconfigured supply arrangement. J2 exposes signals but does not disconnect PHI drivers.
- Prefer a disconnected PHI and documented standalone power arrangement for Pico tests. Prepare and verify a wiring sheet covering power routing, jumpers, logic levels, grounds and GPIO assignments before connection. External supply routing can require changing zero-ohm resistor connections; consult the actual board revision.
- J2 exposes RESET, START/SYNC, SDI, CS, SCLK, SDO/DRDY and DRDY. SPI is mode 1. The identification EEPROM is unnecessary for ADC operation.
- Default PHI clock is 24 MHz; EVM oscillator is 25 MHz. At the target filter/OSR these give 23,437.5 and 24,414.0625 SPS. Use an external 25.6 MHz clock for the nominal 25 kSPS target and measure actual rate. Do not label a different rate as 25 kSPS.
- Standalone local clock: JP7 2-3, JP6 1-2, JP3 removed to enable Y1. For external clock use J14 and the external selection on JP6; verify amplitude against IOVDD and the schematic.
- Required equipment: Pico 2 W, EVM/PHI, logic analyzer and oscilloscope, suitable supplies/interface wiring, and a signal source. Precision noise/distortion claims require a sufficiently clean source. External 25.6 MHz clock is needed for the target-rate qualification.

## Qualification stages

| Stage | Work | Completion criterion |
|---|---|---|
| TI reference | Capture raw codes, DC histograms and sine waves with PHI/GUI | Repeatable baseline with all settings recorded |
| Pico control | Standalone reset, register reads/writes/readback, start and stop | Repeatable initialization across resets/power cycles; traces meet datasheet timing |
| Acquisition | DRDY-driven SPI/DMA into bounded buffers, initially without Wi-Fi | Every expected conversion captured or explicitly accounted for; no unexplained overruns/framing errors |
| Integrity/recovery | CRC/status, malformed commands, corrupted frames, delayed reads and restart | Errors detected; invalid data excluded or marked; recovery returns to known configuration/sample boundary |
| Transport | Recording, Wi-Fi load, backpressure, disconnect/reconnect | Hardware-paced acquisition preserved; losses and causes visible in recording |
| Three channels | Shared clock/START, ordering, daisy chain and isolation | Simultaneity and sustained acquisition demonstrated on intended topology |

## Analog reference measurements

1. Zero differential input at a valid common-mode voltage: offset, RMS noise, histogram and spectrum.
2. Several positive/negative DC levels: polarity, gain, signed-code interpretation and clipping.
3. Sine waves across 600 Hz to 5 kHz: amplitude response, distortion and noise; selected out-of-band tones for alias rejection.

Use differential SMA inputs for performance measurements and obey input/common-mode limits. Match clock, settings, source, record length and analysis between PHI and Pico. Initial acceptance is agreement within measured repeatability and instrument uncertainty. Set absolute analog limits using the actual circuit and source capability. Compare radio off/on and idle/transmitting conditions to identify interference.

## Firmware checks

Begin with conservative SPI speed and register transactions, then DMA. Observe DRDY, CS, SCLK and data to verify read completion before the next conversion, startup/filter settling, byte order, signed 24-bit decoding and clean stop/reconfiguration boundaries. Test decoding boundary vectors independently of analog stimulation.

The manual's GUI version does not support SPI CRC, status output or all START modes. Verify current GUI support and test these independently in Pico firmware. CRC applies to incoming commands as well as outgoing data; transmitting all-zero commands in CRC mode can set SPI_ERR. Test error clearing and command recovery against the datasheet. Frames are 24 bits for data alone, 32 with CRC, and 40 with both status and CRC; use the actual format in timing budgets.

Software sample counters alone cannot prove continuity. Include an independent DRDY count or hardware timing observation, DMA completion counts, overruns and deliberate fault injection. Repeated analog codes are valid and cannot establish duplicate samples. If exact loss cannot be reconstructed, report timing/continuity uncertainty rather than inventing a precise count.

Preserve raw codes and separate acquisition errors from network queue losses. Log register configuration, build, boot/session, expected/observed rate, captured count, acquisition overruns, CRC errors, queue drops and maximum service latency. Keep register changes outside active capture initially; account for filter settling on restart.

## Integration and duration

Protocol v1 requires XYZ rows and does not provide per-sample quality masks. Start single-channel work with a diagnostic capture format. Any temporary XYZ adapter must explicitly identify unmeasured channels and must not imply real triaxial data. A protocol change needs an explicit compatibility decision and independent fixture checks. Counter-pattern verification cannot validate arbitrary ADC waveforms.

Progress from a short functional capture to a 30-minute acquisition-only run and a two-hour integrated recording. Deliberately stall networking below and beyond available buffer duration; conversion timing must continue and losses must be explicit. Existing transport endurance problems remain separate qualification work. Preserve the current ring/rate baseline for controlled transport comparisons and check RAM/stack headroom when adding DMA buffers.

One EVM does not qualify three-ADC synchronization, daisy chaining, final isolation or cross-node timing. Those require final hardware, shared clock/START, channel-order checks and latency/phase measurements. Account for analog phase and digital-filter delay before making timing claims.

## First implementation milestone

Create a standalone `pico_adc_diagnostic` target with reset/configuration readback, one-channel DRDY/DMA capture, raw-data export and timing/error counters. Retain the synthetic transport target as a regression baseline. Save large captures and traces under ignored `software/.artifacts`; commit compact reports and configuration/evidence summaries.

## Current bench status

On 2026-10-10 the TI GUI splash-screen failure was resolved by installing the missing Visual C++ 2010 x86 dependency. After USB connection, Windows detected `TI,PHI(Bulk Only)` with status `OK`; the user reported D5 steady and D1/D2 blinking. The user then completed a short capture with no input connected and reported an expected noise-like waveform, confirming basic GUI-to-EVM acquisition. This floating-input smoke test is not a quantitative noise baseline; register/clock configuration, controlled-input captures and performance measurements remain outstanding. See the [startup investigation](evm-gui-startup-debug.md).
