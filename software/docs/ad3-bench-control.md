# Digilent Analog Discovery 3 (AD3) bench control and ADS127L11 analysis

Date: 2026-10-15
Status: implemented and verified against connected AD3 (`SN:210415C1188A`, WaveForms SDK `3.25.1`).

This document describes the Python module [`multinodedaq.ad3`](../python/multinodedaq/ad3.py) and command-line script [`scripts/ad3-control.py`](../scripts/ad3-control.py) for controlling a Digilent Analog Discovery 3 (AD3) during [ADS127L11 ADC firmware and bench qualification](adc-firmware-test-plan.md).

---

## 1. Overview and bench role

The AD3 supports two primary bench workflows for the ADS127L11EVM-PDK and Raspberry Pi Pico 2 W:

1. **Analog input stimulus (`W1` / `W2` Waveform Generator)**:
   - Zero-differential DC baseline at a specified common-mode voltage ($V_{\text{W1}} = V_{\text{W2}} = V_{\text{cm}}$).
   - Single-ended and differential DC levels ($V_{\text{diff}} = V_{\text{W1}} - V_{\text{W2}}$) for polarity, gain, signed-code interpretation, and clipping checks.
   - Single-ended and phase-locked differential sine waves ($0^\circ / 180^\circ$ via `FDwfAnalogOutMasterSet`) across the $600\text{ Hz}$ to $5\text{ kHz}$ passband and out-of-band frequencies for alias-rejection testing.
   - Linear FM frequency sweeps across configurable frequency spans.
2. **Digital `J2` header capture and SPI Mode 1 verification (`DIO 0–15` Logic Analyzer)**:
   - High-speed single-buffer capture (up to `32,768` samples in AD3 Configuration `3` at up to `100+ MHz`) and USB streaming record mode (`record_logic`) with lost/corrupted sample detection.
   - `DRDY` pulse period, jitter, low-pulse width, and conversion-rate measurement compared against the three OSR-512 wideband reference rates (`23,437.5 SPS` @ 24 MHz PHI clock, `24,414.0625 SPS` @ 25 MHz EVM oscillator, `25,000.0 SPS` @ 25.6 MHz target clock).
   - SPI Mode 1 (`CPOL = 0, CPHA = 1`, MSB-first, sampled on `SCLK` falling edge) frame extraction, verifying that each conversion read finishes before the next `DRDY` falling edge, decoding signed 24-bit conversion codes, and checking the ADS127L11 CRC-8 (`x^8 + x^2 + x + 1`, poly `0x07`, init `0xFF`) for 24-, 32-, and 40-bit frames.

---

## 2. Prerequisites and device access

- **WaveForms Runtime / SDK**: Requires the Digilent WaveForms runtime (`C:\Windows\System32\dwf.dll` on Windows, `libdwf.so` on Linux, or `dwf.framework` on macOS).
- **Exclusive device handle**: Only one process can open an AD3 at a time. If the Digilent WaveForms GUI application has the device open (`in_use: true`), `AD3Device.open()` raises an `AD3Error` instructing you to close or disconnect the device in the WaveForms GUI first.
- **Default buffer configuration**: `AD3Device` opens configuration index `3` by default, allocating `32,768` samples to `DigitalIn`, `16,384` to `AnalogIn`, `4,096` to `AnalogOut`, and `2,048` to `DigitalOut`.
- **Keep-alive behavior (`on_close`)**: By default, `on_close="run"` sets `DwfParamOnClose = 0` so that configured `W1`/`W2` waveforms continue running after `ad3-control.py` exits. Use `ad3-control.py stop` (or `on_close="stop"`) to disable outputs.

---

## 3. Hardware connection notes

### Default `J2` to AD3 `DIO` mapping

| ADS127L11EVM `J2` signal | Default AD3 pin | CLI override flag | Direction during capture |
|---|---|---|---|
| `DRDY` | `DIO 0` | `--drdy-pin` | High-Z input |
| `CS` | `DIO 1` | `--cs-pin` | High-Z input |
| `SCLK` | `DIO 2` | `--sclk-pin` | High-Z input |
| `SDI` (MOSI) | `DIO 3` | `--sdi-pin` | High-Z input |
| `SDO` / `SDO/DRDY` (MISO) | `DIO 4` | `--sdo-pin` | High-Z input |

- **1.8 V PHI logic caution**: When the TI PHI controller is attached to the EVM, `J2` operates at `1.8 V` `IOVDD`. AD3 `DIO` pins in Logic Analyzer (`DigitalIn`) mode are high-impedance inputs and can safely observe `J2`, but **do not** enable AD3 `DigitalOut` drivers onto `J2` while the PHI is connected.
- **Analog inputs**: Respect the ADS127L11EVM input range and common-mode voltage requirements before enabling `W1`/`W2`. `AD3Device` validates that single-ended channel peaks (`|offset| + amplitude`) stay within the AD3 $\pm 5.0\text{ V}$ hardware limit.

---

## 4. Python API reference (`multinodedaq.ad3`)

### 4.1 Discovery and device management

- `get_dwf_version(dwf=None) -> str`
  Returns the installed Digilent WaveForms SDK version string (e.g. `"3.25.1"`).
- `enumerate_devices(dwf=None) -> list[DeviceInfo]`
  Enumerates all connected Digilent devices, returning [`DeviceInfo`](../python/multinodedaq/ad3.py#L84-L90) records with `device_index`, `name`, `serial`, `in_use`, and a tuple of [`DeviceConfigInfo`](../python/multinodedaq/ad3.py#L75-L81) buffer sizes.
- `AD3Device(device_index=-1, serial=None, config_index=3, on_close="run", dwf=None)`
  Context manager and controller class for an Analog Discovery 3.
  - `open() -> AD3Device`: Opens the target device (by `serial` or `device_index`) with explicit configuration (`FDwfDeviceAutoConfigureSet(hdwf, 0)`).
  - `close() -> None`: Closes the device handle while respecting `on_close` (`"run"`, `"stop"`, or `"shutdown"`).

### 4.2 Waveform Generator (`W1` / `W2`) methods on `AD3Device`

- `configure_dc(channel: int, voltage_v: float) -> dict`
  Sets a single-ended DC voltage on `W1` (`channel=0`) or `W2` (`channel=1`).
- `configure_differential_dc(diff_v: float, common_mode_v: float = 0.0) -> dict`
  Configures synchronized DC outputs $V_{\text{W1}} = V_{\text{cm}} + V_{\text{diff}}/2$ and $V_{\text{W2}} = V_{\text{cm}} - V_{\text{diff}}/2$. Setting `diff_v=0.0` produces a zero-differential baseline at $V_{\text{cm}}$.
- `configure_sine(channel: int, frequency_hz: float, amplitude_v: float, offset_v: float = 0.0, phase_deg: float = 0.0) -> dict`
  Configures a single-ended sine tone on `W1` (`0`) or `W2` (`1`).
- `configure_differential_sine(frequency_hz: float, diff_amplitude_v: float, common_mode_v: float = 0.0) -> dict`
  Configures a phase-locked differential sine wave across `W1` ($0^\circ$) and `W2` ($180^\circ$, slaved to `W1` via `FDwfAnalogOutMasterSet`). Each channel outputs peak amplitude `diff_amplitude_v / 2` centered at `common_mode_v`, yielding a differential waveform $V_{\text{W1}}(t) - V_{\text{W2}}(t) = A_{\text{diff}} \sin(2\pi f t)$ with peak amplitude `diff_amplitude_v` (peak-to-peak `2 * diff_amplitude_v`).
- `configure_sweep(start_hz: float, stop_hz: float, duration_s: float, amplitude_v: float, offset_v: float = 0.0, channel: int = 0, differential: bool = False, repeat: int = 0) -> dict`
  Configures a linear FM sine sweep from `start_hz` to `stop_hz` over `duration_s` seconds, either single-ended or phase-locked differential across `W1`/`W2`.
- `stop_wavegen(channel: int = -1) -> dict`
  Resets and disables `W1` (`0`), `W2` (`1`), or both (`-1`).

### 4.3 Logic Analyzer capture methods on `AD3Device`

- `capture_logic(sample_rate_hz=25_000_000.0, num_samples=32768, trigger_pin=None, trigger_edge="falling", prefill_ratio=0.1, timeout_s=5.0) -> tuple[list[int], float]`
  Acquires a single high-speed buffer of 16-bit `DIO 0–15` samples (`acqmodeSingle`). Returns `(samples_uint16, actual_sample_rate_hz)`. Supports edge triggering (`"falling"`, `"rising"`, `"either"`) on any `DIO` pin with configurable pre-trigger fraction `prefill_ratio`.
- `record_logic(sample_rate_hz=10_000_000.0, num_samples=100_000, trigger_pin=None, trigger_edge="falling", timeout_s=10.0) -> tuple[list[int], float, int, int]`
  Streams 16-bit `DIO 0–15` samples over USB (`acqmodeRecord`) for captures longer than the hardware buffer. Returns `(samples_uint16, actual_sample_rate_hz, lost_samples, corrupted_samples)`.

### 4.4 Pure-Python ADS127L11 `J2` analysis and decoding functions

These functions operate on any sequence of 16-bit digital sample words and are unit-tested independently of hardware in [`python/tests/test_ad3.py`](../python/tests/test_ad3.py):

- `analyze_drdy(samples: Sequence[int], sample_rate_hz: float, drdy_pin: int = 0) -> DRDYAnalysis`
  Measures falling/rising edge counts, mean/min/max/std falling-edge period ($\mu\text{s}$), mean active-low pulse width ($\mu\text{s}$), and observed conversion rate ($\text{SPS}$). Identifies the closest OSR-512 wideband reference clock (`phi_24mhz`, `evm_25mhz`, or `target_25p6mhz`) and reports relative frequency error in $\text{ppm}$.
- `decode_spi_frames(samples: Sequence[int], sample_rate_hz: float, cs_pin: int = 1, sclk_pin: int = 2, sdi_pin: int = 3, sdo_pin: int = 4, drdy_pin: int | None = 0, cpol: int = 0, cpha: int = 1) -> list[SPIFrame]`
  Extracts active-low `CS` SPI frames (defaulting to SPI Mode 1: `cpol=0, cpha=1`, MSB-first sampled on `SCLK` falling edges). For each frame, records start/end sample index, duration ($\mu\text{s}$), bit count, raw `SDI`/`SDO` integers and byte strings, estimated `SCLK` frequency ($\text{Hz}$), delay from the preceding `DRDY` falling edge (`drdy_to_cs_us`), and whether `CS` deasserted before the next `DRDY` falling edge (`completed_before_next_drdy`).
- `parse_ads127l11_frame(frame: SPIFrame, has_status: bool = False, has_crc: bool = False) -> ADS127L11SampleFrame`
  Parses a 24-bit (`[data24]`), 32-bit (`[status8, data24]` or `[data24, crc8]`), or 40-bit (`[status8, data24, crc8]`) `SPIFrame` into an [`ADS127L11SampleFrame`](../python/multinodedaq/ad3.py#L140-L153) with `raw_code_u24`, signed `code_i24` ($\in [-8388608, +8388607]$), `normalized_full_scale`, optional `status_byte`, and CRC-8 validation (`received_crc`, `computed_crc`, `crc_valid`).
- `sign_extend_24(value: int) -> int`
  Converts an unsigned 24-bit integer (`0x000000..0xFFFFFF`) to signed two's complement (`-8388608..+8388607`).
- `crc8_ads127l11(data: bytes | Sequence[int], init: int = 0xFF) -> int`
  Computes the ADS127L11 CRC-8 checksum (`x^8 + x^2 + x + 1`, polynomial `0x07`, initial value `0xFF`).

---

## 5. CLI usage (`software/scripts/ad3-control.py`)

Run from `C:\dev\MultiNodeDAQ\software`:

### 5.1 Device discovery

```powershell
.\.venv\Scripts\python.exe scripts\ad3-control.py info
```

### 5.2 Waveform Generator (`wavegen` and `stop`)

```powershell
# Zero-differential DC baseline at 0.0 V common-mode (W1 = 0 V, W2 = 0 V)
.\.venv\Scripts\python.exe scripts\ad3-control.py wavegen --mode diff-dc --voltage 0.0 --common-mode 0.0

# Differential DC level of +0.5 V around 0.0 V common-mode (W1 = +0.25 V, W2 = -0.25 V)
.\.venv\Scripts\python.exe scripts\ad3-control.py wavegen --mode diff-dc --voltage 0.5 --common-mode 0.0

# Phase-locked differential 1 kHz sine wave, 2.0 V peak differential (W1/W2 = +/-1.0 V, 180 deg out of phase)
.\.venv\Scripts\python.exe scripts\ad3-control.py wavegen --mode diff-sine --freq 1000 --amplitude 2.0 --common-mode 0.0

# Single-ended 2.5 kHz sine wave on W1 (channel 0), 1.0 V amplitude, 0.0 V offset
.\.venv\Scripts\python.exe scripts\ad3-control.py wavegen --mode sine --channel 0 --freq 2500 --amplitude 1.0 --offset 0.0

# Differential linear frequency sweep from 600 Hz to 5 kHz over 1.0 s
.\.venv\Scripts\python.exe scripts\ad3-control.py wavegen --mode diff-sweep --start-freq 600 --stop-freq 5000 --duration 1.0 --amplitude 2.0

# Stop and reset W1/W2 outputs
.\.venv\Scripts\python.exe scripts\ad3-control.py stop
```

### 5.3 Logic Analyzer and SPI Mode 1 decoding (`logic`)

```powershell
# Capture 32,768 samples at 25 MHz triggered on DRDY falling edge (DIO 0) and measure DRDY rate
.\.venv\Scripts\python.exe scripts\ad3-control.py logic --rate 25000000 --samples 32768 --trigger drdy

# Capture and decode 24-bit SPI Mode 1 conversion frames
.\.venv\Scripts\python.exe scripts\ad3-control.py logic --rate 25000000 --samples 32768 --trigger drdy --decode-spi

# Capture and decode 32-bit frames (24-bit data + 8-bit CRC) and save summary JSON
.\.venv\Scripts\python.exe scripts\ad3-control.py logic --rate 25000000 --samples 32768 --trigger drdy --decode-spi --adc-crc --output .artifacts\ad3\j2-capture.json
```
