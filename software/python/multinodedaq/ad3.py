"""Digilent Analog Discovery 3 (AD3) control and ADS127L11 bench-test utilities.

Provides WaveForms SDK (dwf) wrappers for:
- Device enumeration and configuration management
- Waveform Generator (W1/W2): single-ended/differential DC, sine tones, and FM sweeps
- Logic Analyzer (DIO 0-15): buffered and streaming digital capture
- ADS127L11 J2 signal analysis: DRDY rate/timing and SPI Mode 1 frame/CRC decoding
"""

from __future__ import annotations

import ctypes
from ctypes import byref, c_bool, c_byte, c_double, c_int, c_ubyte, c_uint16, create_string_buffer
from dataclasses import asdict, dataclass
import math
import sys
import time
from typing import Any, Sequence


class AD3Error(RuntimeError):
    """Raised when a Digilent WaveForms SDK operation or validation fails."""


# WaveForms SDK constants
HDWF_NONE = 0
ENUMFILTER_ALL = 0

DECI_ANALOG_IN_CHANNEL_COUNT = 1
DECI_ANALOG_OUT_CHANNEL_COUNT = 2
DECI_DIGITAL_IN_CHANNEL_COUNT = 4
DECI_DIGITAL_OUT_CHANNEL_COUNT = 5
DECI_ANALOG_IN_BUFFER_SIZE = 7
DECI_ANALOG_OUT_BUFFER_SIZE = 8
DECI_DIGITAL_IN_BUFFER_SIZE = 9
DECI_DIGITAL_OUT_BUFFER_SIZE = 10

DWF_PARAM_ON_CLOSE = 4          # 0 = run, 1 = stop, 2 = shutdown
DWF_PARAM_DIGITAL_VOLTAGE = 13  # mV
DWF_PARAM_DIGITAL_THRESHOLD = 15  # mV

ON_CLOSE_MODES = {
    "run": 0,
    "stop": 1,
    "shutdown": 2,
}

ANALOG_OUT_NODE_CARRIER = 0
ANALOG_OUT_NODE_FM = 1
ANALOG_OUT_NODE_AM = 2

FUNC_DC = 0
FUNC_SINE = 1
FUNC_SQUARE = 2
FUNC_TRIANGLE = 3
FUNC_RAMP_UP = 4
FUNC_RAMP_DOWN = 5
FUNC_NOISE = 6

ACQMODE_SINGLE = 0
ACQMODE_RECORD = 3

TRIGSRC_NONE = 0
TRIGSRC_DETECTOR_DIGITAL_IN = 3
TRIGSRC_ANALOG_OUT_1 = 7

DWF_STATE_READY = 0
DWF_STATE_ARMED = 1
DWF_STATE_DONE = 2
DWF_STATE_TRIGGERED = 3
DWF_STATE_CONFIG = 4
DWF_STATE_PREFILL = 5

# Reference conversion rates for ADS127L11 wideband filter at OSR 512 (f_CLK / 1024)
ADS127L11_OSR512_RATES_SPS = {
    "phi_24mhz": 24_000_000.0 / 1024.0,       # 23,437.5 SPS
    "evm_25mhz": 25_000_000.0 / 1024.0,       # 24,414.0625 SPS
    "target_25p6mhz": 25_600_000.0 / 1024.0,  # 25,000.0 SPS
}


@dataclass(frozen=True)
class DeviceConfigInfo:
    config_index: int
    analog_in_buffer: int
    analog_out_buffer: int
    digital_in_buffer: int
    digital_out_buffer: int


@dataclass(frozen=True)
class DeviceInfo:
    device_index: int
    name: str
    serial: str
    in_use: bool
    configs: tuple[DeviceConfigInfo, ...]


@dataclass(frozen=True)
class J2PinMap:
    """ADS127L11EVM J2 header to AD3 DIO pin mapping."""
    drdy: int = 0
    cs: int = 1
    sclk: int = 2
    sdi: int = 3
    sdo: int = 4
    start: int | None = None
    reset: int | None = None

    def validate(self) -> None:
        pins = [
            ("drdy", self.drdy),
            ("cs", self.cs),
            ("sclk", self.sclk),
            ("sdi", self.sdi),
            ("sdo", self.sdo),
        ]
        if self.start is not None:
            pins.append(("start", self.start))
        if self.reset is not None:
            pins.append(("reset", self.reset))
        seen: dict[int, str] = {}
        for name, pin in pins:
            if not (0 <= pin <= 15):
                raise AD3Error(f"DIO pin for {name} must be in 0..15, got {pin}")
            if pin in seen and not (
                {seen[pin], name} == {"drdy", "sdo"}
            ):
                raise AD3Error(f"Duplicate DIO pin {pin} assigned to both {seen[pin]} and {name}")
            seen[pin] = name


@dataclass(frozen=True)
class DRDYAnalysis:
    sample_rate_hz: float
    total_samples: int
    duration_s: float
    falling_edges: int
    rising_edges: int
    observed_rate_sps: float | None
    mean_period_us: float | None
    min_period_us: float | None
    max_period_us: float | None
    std_period_us: float | None
    mean_low_width_us: float | None
    closest_reference_clock: str | None
    reference_rate_error_ppm: float | None


@dataclass(frozen=True)
class SPIFrame:
    frame_index: int
    start_sample: int
    end_sample: int
    duration_us: float
    bit_count: int
    sdi_value: int
    sdo_value: int
    sdi_bytes: bytes
    sdo_bytes: bytes
    sclk_hz: float | None
    drdy_to_cs_us: float | None
    completed_before_next_drdy: bool | None


@dataclass(frozen=True)
class ADS127L11SampleFrame:
    frame_index: int
    bit_count: int
    raw_code_u24: int
    code_i24: int
    normalized_full_scale: float
    status_byte: int | None
    received_crc: int | None
    computed_crc: int | None
    crc_valid: bool | None
    command_word: int
    drdy_to_cs_us: float | None
    completed_before_next_drdy: bool | None


def load_dwf() -> ctypes.CDLL:
    """Load the platform Digilent WaveForms runtime library (dwf)."""
    try:
        if sys.platform.startswith("win"):
            return ctypes.cdll.dwf
        if sys.platform.startswith("darwin"):
            return ctypes.cdll.LoadLibrary("/Library/Frameworks/dwf.framework/dwf")
        return ctypes.cdll.LoadLibrary("libdwf.so")
    except OSError as exc:
        raise AD3Error(
            "Could not load Digilent WaveForms SDK library (dwf). "
            "Verify that Digilent WaveForms Runtime / SDK is installed."
        ) from exc


def _check_dwf(dwf: Any, rc: int, context: str) -> None:
    if rc == 0:
        err_buf = create_string_buffer(512)
        dwf.FDwfGetLastErrorMsg(err_buf)
        msg = err_buf.value.decode("utf-8", errors="replace").strip()
        raise AD3Error(f"{context} failed: {msg or 'unknown DWF error'}")


def get_dwf_version(dwf: Any | None = None) -> str:
    """Return the installed WaveForms SDK version string."""
    lib = dwf or load_dwf()
    ver = create_string_buffer(32)
    _check_dwf(lib, lib.FDwfGetVersion(ver), "FDwfGetVersion")
    return ver.value.decode("ascii", errors="replace")


def enumerate_devices(dwf: Any | None = None) -> list[DeviceInfo]:
    """Enumerate all connected Digilent WaveForms devices and their buffer configurations."""
    lib = dwf or load_dwf()
    count = c_int()
    _check_dwf(lib, lib.FDwfEnum(c_int(ENUMFILTER_ALL), byref(count)), "FDwfEnum")
    devices: list[DeviceInfo] = []
    for idx in range(count.value):
        name_buf = create_string_buffer(64)
        sn_buf = create_string_buffer(32)
        in_use = c_bool()
        lib.FDwfEnumDeviceName(c_int(idx), name_buf)
        lib.FDwfEnumSN(c_int(idx), sn_buf)
        lib.FDwfEnumDeviceIsOpened(c_int(idx), byref(in_use))

        cfg_count = c_int()
        lib.FDwfEnumConfig(c_int(idx), byref(cfg_count))
        configs: list[DeviceConfigInfo] = []
        for c_idx in range(cfg_count.value):
            ai = c_int()
            ao = c_int()
            di = c_int()
            do = c_int()
            lib.FDwfEnumConfigInfo(c_int(c_idx), c_int(DECI_ANALOG_IN_BUFFER_SIZE), byref(ai))
            lib.FDwfEnumConfigInfo(c_int(c_idx), c_int(DECI_ANALOG_OUT_BUFFER_SIZE), byref(ao))
            lib.FDwfEnumConfigInfo(c_int(c_idx), c_int(DECI_DIGITAL_IN_BUFFER_SIZE), byref(di))
            lib.FDwfEnumConfigInfo(c_int(c_idx), c_int(DECI_DIGITAL_OUT_BUFFER_SIZE), byref(do))
            configs.append(
                DeviceConfigInfo(
                    config_index=c_idx,
                    analog_in_buffer=ai.value,
                    analog_out_buffer=ao.value,
                    digital_in_buffer=di.value,
                    digital_out_buffer=do.value,
                )
            )
        serial = sn_buf.value.decode("ascii", errors="replace")
        if serial.startswith("SN:"):
            serial = serial[3:]
        devices.append(
            DeviceInfo(
                device_index=idx,
                name=name_buf.value.decode("ascii", errors="replace"),
                serial=serial,
                in_use=bool(in_use.value),
                configs=tuple(configs),
            )
        )
    return devices


def sign_extend_24(value: int) -> int:
    """Convert an unsigned 24-bit integer to signed 24-bit two's complement."""
    if not (0 <= value <= 0xFFFFFF):
        raise ValueError(f"24-bit value out of range: {value}")
    return value - 0x1000000 if (value & 0x800000) else value


def crc8_ads127l11(data: bytes | Sequence[int], init: int = 0xFF) -> int:
    """Compute ADS127L11 SPI CRC-8 (polynomial x^8 + x^2 + x + 1 = 0x07, init 0xFF)."""
    crc = init & 0xFF
    for byte in data:
        crc ^= (byte & 0xFF)
        for _ in range(8):
            if crc & 0x80:
                crc = ((crc << 1) ^ 0x07) & 0xFF
            else:
                crc = (crc << 1) & 0xFF
    return crc


def analyze_drdy(
    samples: Sequence[int],
    sample_rate_hz: float,
    drdy_pin: int = 0,
) -> DRDYAnalysis:
    """Analyze ADS127L11 DRDY pulse timing and conversion rate from digital samples."""
    if sample_rate_hz <= 0:
        raise AD3Error(f"sample_rate_hz must be positive, got {sample_rate_hz}")
    if not (0 <= drdy_pin <= 15):
        raise AD3Error(f"drdy_pin must be in 0..15, got {drdy_pin}")

    n = len(samples)
    if n == 0:
        return DRDYAnalysis(
            sample_rate_hz=sample_rate_hz,
            total_samples=0,
            duration_s=0.0,
            falling_edges=0,
            rising_edges=0,
            observed_rate_sps=None,
            mean_period_us=None,
            min_period_us=None,
            max_period_us=None,
            std_period_us=None,
            mean_low_width_us=None,
            closest_reference_clock=None,
            reference_rate_error_ppm=None,
        )

    mask = 1 << drdy_pin
    falling_indices: list[int] = []
    rising_indices: list[int] = []
    low_widths: list[int] = []
    last_falling: int | None = None

    prev = 1 if (samples[0] & mask) else 0
    for i in range(1, n):
        curr = 1 if (samples[i] & mask) else 0
        if prev == 1 and curr == 0:
            falling_indices.append(i)
            last_falling = i
        elif prev == 0 and curr == 1:
            rising_indices.append(i)
            if last_falling is not None:
                low_widths.append(i - last_falling)
                last_falling = None
        prev = curr

    dt_us = 1e6 / sample_rate_hz
    mean_low_us = (sum(low_widths) / len(low_widths) * dt_us) if low_widths else None

    if len(falling_indices) < 2:
        return DRDYAnalysis(
            sample_rate_hz=sample_rate_hz,
            total_samples=n,
            duration_s=n / sample_rate_hz,
            falling_edges=len(falling_indices),
            rising_edges=len(rising_indices),
            observed_rate_sps=None,
            mean_period_us=None,
            min_period_us=None,
            max_period_us=None,
            std_period_us=None,
            mean_low_width_us=mean_low_us,
            closest_reference_clock=None,
            reference_rate_error_ppm=None,
        )

    periods_samples = [
        falling_indices[k + 1] - falling_indices[k]
        for k in range(len(falling_indices) - 1)
    ]
    periods_us = [p * dt_us for p in periods_samples]
    mean_period_samples = sum(periods_samples) / len(periods_samples)
    mean_period_us = mean_period_samples * dt_us
    observed_rate_sps = sample_rate_hz / mean_period_samples

    if len(periods_us) > 1:
        variance = sum((p - mean_period_us) ** 2 for p in periods_us) / len(periods_us)
        std_period_us = math.sqrt(variance)
    else:
        std_period_us = 0.0

    closest_name = min(
        ADS127L11_OSR512_RATES_SPS,
        key=lambda k: abs(observed_rate_sps - ADS127L11_OSR512_RATES_SPS[k]),
    )
    ref_rate = ADS127L11_OSR512_RATES_SPS[closest_name]
    ref_err_ppm = ((observed_rate_sps - ref_rate) / ref_rate) * 1e6

    return DRDYAnalysis(
        sample_rate_hz=sample_rate_hz,
        total_samples=n,
        duration_s=n / sample_rate_hz,
        falling_edges=len(falling_indices),
        rising_edges=len(rising_indices),
        observed_rate_sps=observed_rate_sps,
        mean_period_us=mean_period_us,
        min_period_us=min(periods_us),
        max_period_us=max(periods_us),
        std_period_us=std_period_us,
        mean_low_width_us=mean_low_us,
        closest_reference_clock=closest_name,
        reference_rate_error_ppm=ref_err_ppm,
    )


def decode_spi_frames(
    samples: Sequence[int],
    sample_rate_hz: float,
    cs_pin: int = 1,
    sclk_pin: int = 2,
    sdi_pin: int = 3,
    sdo_pin: int = 4,
    drdy_pin: int | None = 0,
    cpol: int = 0,
    cpha: int = 1,
) -> list[SPIFrame]:
    """Decode active-low CS SPI frames from 16-bit logic analyzer samples.

    ADS127L11 uses SPI Mode 1 (CPOL=0, CPHA=1): SCLK idles low, data is launched on
    SCLK rising edges and sampled on SCLK falling edges, MSB first.
    """
    if sample_rate_hz <= 0:
        raise AD3Error(f"sample_rate_hz must be positive, got {sample_rate_hz}")
    if cpol not in (0, 1) or cpha not in (0, 1):
        raise AD3Error(f"Invalid SPI mode: cpol={cpol}, cpha={cpha}")

    # Determine which SCLK transition samples data:
    # Mode 0 (0,0) or Mode 3 (1,1): rising edge (0 -> 1) when cpol==cpha
    # Mode 1 (0,1) or Mode 2 (1,0): falling edge (1 -> 0) when cpol!=cpha
    sample_on_rising = (cpol == cpha)

    cs_mask = 1 << cs_pin
    sclk_mask = 1 << sclk_pin
    sdi_mask = 1 << sdi_pin
    sdo_mask = 1 << sdo_pin
    drdy_mask = (1 << drdy_pin) if drdy_pin is not None else 0

    n = len(samples)
    if n == 0:
        return []

    # Pre-index DRDY falling edges if drdy_pin is provided
    drdy_falling: list[int] = []
    if drdy_pin is not None:
        prev_drdy = 1 if (samples[0] & drdy_mask) else 0
        for i in range(1, n):
            curr_drdy = 1 if (samples[i] & drdy_mask) else 0
            if prev_drdy == 1 and curr_drdy == 0:
                drdy_falling.append(i)
            prev_drdy = curr_drdy

    frames: list[SPIFrame] = []
    in_frame = (samples[0] & cs_mask) == 0
    frame_start = 0 if in_frame else -1
    bit_count = 0
    sdi_val = 0
    sdo_val = 0
    sclk_edges: list[int] = []

    prev_sclk = 1 if (samples[0] & sclk_mask) else 0
    prev_cs = 1 if (samples[0] & cs_mask) else 0
    drdy_cursor = 0

    for i in range(1, n):
        word = samples[i]
        curr_cs = 1 if (word & cs_mask) else 0
        curr_sclk = 1 if (word & sclk_mask) else 0

        if prev_cs == 1 and curr_cs == 0:
            in_frame = True
            frame_start = i
            bit_count = 0
            sdi_val = 0
            sdo_val = 0
            sclk_edges = []
        elif in_frame:
            if curr_cs == 0:
                is_sample_edge = (
                    (prev_sclk == 0 and curr_sclk == 1)
                    if sample_on_rising
                    else (prev_sclk == 1 and curr_sclk == 0)
                )
                if is_sample_edge:
                    bit_count += 1
                    sdi_val = (sdi_val << 1) | (1 if (word & sdi_mask) else 0)
                    sdo_val = (sdo_val << 1) | (1 if (word & sdo_mask) else 0)
                    sclk_edges.append(i)
            else:
                # CS rising edge ends the frame
                if bit_count > 0:
                    byte_len = (bit_count + 7) // 8
                    sdi_bytes = sdi_val.to_bytes(byte_len, byteorder="big")
                    sdo_bytes = sdo_val.to_bytes(byte_len, byteorder="big")
                    if len(sclk_edges) >= 2:
                        mean_clk_samples = (sclk_edges[-1] - sclk_edges[0]) / (len(sclk_edges) - 1)
                        sclk_hz = sample_rate_hz / mean_clk_samples if mean_clk_samples > 0 else None
                    else:
                        sclk_hz = None

                    drdy_to_cs_us: float | None = None
                    completed_before_next: bool | None = None
                    if drdy_falling:
                        while drdy_cursor + 1 < len(drdy_falling) and drdy_falling[drdy_cursor + 1] <= frame_start:
                            drdy_cursor += 1
                        if drdy_falling[drdy_cursor] <= frame_start:
                            drdy_to_cs_us = (frame_start - drdy_falling[drdy_cursor]) * 1e6 / sample_rate_hz
                            if drdy_cursor + 1 < len(drdy_falling):
                                completed_before_next = i < drdy_falling[drdy_cursor + 1]

                    frames.append(
                        SPIFrame(
                            frame_index=len(frames),
                            start_sample=frame_start,
                            end_sample=i,
                            duration_us=(i - frame_start) * 1e6 / sample_rate_hz,
                            bit_count=bit_count,
                            sdi_value=sdi_val,
                            sdo_value=sdo_val,
                            sdi_bytes=sdi_bytes,
                            sdo_bytes=sdo_bytes,
                            sclk_hz=sclk_hz,
                            drdy_to_cs_us=drdy_to_cs_us,
                            completed_before_next_drdy=completed_before_next,
                        )
                    )
                in_frame = False

        prev_cs = curr_cs
        prev_sclk = curr_sclk

    return frames


def parse_ads127l11_frame(
    frame: SPIFrame,
    has_status: bool = False,
    has_crc: bool = False,
) -> ADS127L11SampleFrame:
    """Interpret a decoded SPIFrame as an ADS127L11 conversion frame (24, 32, or 40 bits)."""
    expected_bits = 24 + (8 if has_status else 0) + (8 if has_crc else 0)
    if frame.bit_count != expected_bits:
        raise AD3Error(
            f"Frame {frame.frame_index} has {frame.bit_count} bits, expected {expected_bits} "
            f"(has_status={has_status}, has_crc={has_crc})"
        )

    raw_bytes = frame.sdo_bytes
    idx = 0
    status_byte: int | None = None
    if has_status:
        status_byte = raw_bytes[0]
        idx += 1

    raw_u24 = (raw_bytes[idx] << 16) | (raw_bytes[idx + 1] << 8) | raw_bytes[idx + 2]
    code_i24 = sign_extend_24(raw_u24)
    idx += 3

    received_crc: int | None = None
    computed_crc: int | None = None
    crc_valid: bool | None = None
    if has_crc:
        received_crc = raw_bytes[idx]
        computed_crc = crc8_ads127l11(raw_bytes[:idx])
        crc_valid = (received_crc == computed_crc)

    return ADS127L11SampleFrame(
        frame_index=frame.frame_index,
        bit_count=frame.bit_count,
        raw_code_u24=raw_u24,
        code_i24=code_i24,
        normalized_full_scale=code_i24 / 8388608.0,
        status_byte=status_byte,
        received_crc=received_crc,
        computed_crc=computed_crc,
        crc_valid=crc_valid,
        command_word=frame.sdi_value,
        drdy_to_cs_us=frame.drdy_to_cs_us,
        completed_before_next_drdy=frame.completed_before_next_drdy,
    )


class AD3Device:
    """Controller for a Digilent Analog Discovery 3 over the WaveForms SDK."""

    def __init__(
        self,
        device_index: int = -1,
        serial: str | None = None,
        config_index: int = 3,
        on_close: str = "run",
        dwf: Any | None = None,
    ) -> None:
        if on_close not in ON_CLOSE_MODES:
            raise AD3Error(f"on_close must be one of {tuple(ON_CLOSE_MODES)}, got {on_close!r}")
        self.dwf = dwf or load_dwf()
        self.device_index = device_index
        self.serial = serial
        self.config_index = config_index
        self.on_close = on_close
        self.hdwf = c_int(HDWF_NONE)
        self.device_info: DeviceInfo | None = None

    def open(self) -> "AD3Device":
        if self.hdwf.value != HDWF_NONE:
            return self

        devices = enumerate_devices(self.dwf)
        if not devices:
            raise AD3Error("No Digilent WaveForms devices found on USB.")

        target_idx = self.device_index
        if self.serial is not None:
            norm_sn = self.serial[3:] if self.serial.upper().startswith("SN:") else self.serial
            matched = [d for d in devices if d.serial.upper() == norm_sn.upper()]
            if not matched:
                available = ", ".join(f"{d.name} ({d.serial})" for d in devices)
                raise AD3Error(f"Device with serial {self.serial!r} not found. Connected: {available}")
            target_idx = matched[0].device_index
            self.device_info = matched[0]
        elif target_idx == -1:
            self.device_info = devices[0]
            target_idx = 0
        else:
            if not (0 <= target_idx < len(devices)):
                raise AD3Error(f"device_index {target_idx} out of range (found {len(devices)} device(s))")
            self.device_info = devices[target_idx]

        if self.device_info.in_use:
            raise AD3Error(
                f"{self.device_info.name} (SN:{self.device_info.serial}) is already in use by another "
                "application (for example, the Digilent WaveForms GUI). Close or disconnect it in "
                "WaveForms before controlling it from Python."
            )

        _check_dwf(
            self.dwf,
            self.dwf.FDwfParamSet(c_int(DWF_PARAM_ON_CLOSE), c_int(ON_CLOSE_MODES[self.on_close])),
            "FDwfParamSet(DwfParamOnClose)",
        )
        _check_dwf(
            self.dwf,
            self.dwf.FDwfDeviceConfigOpen(c_int(target_idx), c_int(self.config_index), byref(self.hdwf)),
            "FDwfDeviceConfigOpen",
        )
        if self.hdwf.value == HDWF_NONE:
            raise AD3Error("FDwfDeviceConfigOpen returned handle 0")

        # Require explicit FDwf*Configure calls so multi-parameter setups apply atomically
        _check_dwf(
            self.dwf,
            self.dwf.FDwfDeviceAutoConfigureSet(self.hdwf, c_int(0)),
            "FDwfDeviceAutoConfigureSet",
        )
        return self

    def close(self) -> None:
        if self.hdwf.value != HDWF_NONE:
            try:
                self.dwf.FDwfDeviceClose(self.hdwf)
            finally:
                self.hdwf = c_int(HDWF_NONE)

    def __enter__(self) -> "AD3Device":
        return self.open()

    def __exit__(self, exc_type: Any, exc: Any, tb: Any) -> None:
        self.close()

    def _require_open(self) -> c_int:
        if self.hdwf.value == HDWF_NONE:
            raise AD3Error("AD3Device is not open.")
        return self.hdwf

    @staticmethod
    def _validate_voltage(value: float, name: str, limit: float = 5.0) -> None:
        if not math.isfinite(value) or abs(value) > limit:
            raise AD3Error(f"{name}={value} V exceeds AD3 +/-{limit} V range")

    @staticmethod
    def _validate_channel(channel: int) -> None:
        if channel not in (0, 1):
            raise AD3Error(f"Wavegen channel must be 0 (W1) or 1 (W2), got {channel}")

    def stop_wavegen(self, channel: int = -1) -> dict[str, Any]:
        """Disable and reset AnalogOut channel(s): 0=W1, 1=W2, -1=both."""
        hdwf = self._require_open()
        if channel not in (-1, 0, 1):
            raise AD3Error(f"channel must be -1, 0, or 1, got {channel}")
        _check_dwf(self.dwf, self.dwf.FDwfAnalogOutReset(hdwf, c_int(channel)), "FDwfAnalogOutReset")
        _check_dwf(self.dwf, self.dwf.FDwfAnalogOutConfigure(hdwf, c_int(channel), c_int(0)), "FDwfAnalogOutConfigure(stop)")
        return {"action": "stop_wavegen", "channel": channel}

    def configure_dc(self, channel: int, voltage_v: float) -> dict[str, Any]:
        """Configure single-ended DC level on W1 (channel=0) or W2 (channel=1)."""
        hdwf = self._require_open()
        self._validate_channel(channel)
        self._validate_voltage(voltage_v, "voltage_v")

        ch = c_int(channel)
        node = c_int(ANALOG_OUT_NODE_CARRIER)
        _check_dwf(self.dwf, self.dwf.FDwfAnalogOutReset(hdwf, ch), "FDwfAnalogOutReset")
        _check_dwf(self.dwf, self.dwf.FDwfAnalogOutNodeEnableSet(hdwf, ch, node, c_int(1)), "FDwfAnalogOutNodeEnableSet")
        _check_dwf(self.dwf, self.dwf.FDwfAnalogOutNodeFunctionSet(hdwf, ch, node, c_ubyte(FUNC_DC)), "FDwfAnalogOutNodeFunctionSet")
        _check_dwf(self.dwf, self.dwf.FDwfAnalogOutNodeOffsetSet(hdwf, ch, node, c_double(voltage_v)), "FDwfAnalogOutNodeOffsetSet")
        _check_dwf(self.dwf, self.dwf.FDwfAnalogOutConfigure(hdwf, ch, c_int(1)), "FDwfAnalogOutConfigure")
        return {
            "mode": "dc",
            "channel": channel,
            "voltage_v": voltage_v,
        }

    def configure_differential_dc(self, diff_v: float, common_mode_v: float = 0.0) -> dict[str, Any]:
        """Configure differential DC voltage V(W1) - V(W2) = diff_v around common_mode_v."""
        hdwf = self._require_open()
        v_w1 = common_mode_v + 0.5 * diff_v
        v_w2 = common_mode_v - 0.5 * diff_v
        self._validate_voltage(v_w1, "v_w1")
        self._validate_voltage(v_w2, "v_w2")

        node = c_int(ANALOG_OUT_NODE_CARRIER)
        _check_dwf(self.dwf, self.dwf.FDwfAnalogOutReset(hdwf, c_int(-1)), "FDwfAnalogOutReset")
        for ch_idx, v_ch in ((0, v_w1), (1, v_w2)):
            ch = c_int(ch_idx)
            _check_dwf(self.dwf, self.dwf.FDwfAnalogOutNodeEnableSet(hdwf, ch, node, c_int(1)), "FDwfAnalogOutNodeEnableSet")
            _check_dwf(self.dwf, self.dwf.FDwfAnalogOutNodeFunctionSet(hdwf, ch, node, c_ubyte(FUNC_DC)), "FDwfAnalogOutNodeFunctionSet")
            _check_dwf(self.dwf, self.dwf.FDwfAnalogOutNodeOffsetSet(hdwf, ch, node, c_double(v_ch)), "FDwfAnalogOutNodeOffsetSet")
        _check_dwf(self.dwf, self.dwf.FDwfAnalogOutMasterSet(hdwf, c_int(1), c_int(0)), "FDwfAnalogOutMasterSet")
        _check_dwf(self.dwf, self.dwf.FDwfAnalogOutConfigure(hdwf, c_int(0), c_int(1)), "FDwfAnalogOutConfigure")
        return {
            "mode": "differential_dc",
            "diff_v": diff_v,
            "common_mode_v": common_mode_v,
            "w1_v": v_w1,
            "w2_v": v_w2,
        }

    def configure_sine(
        self,
        channel: int,
        frequency_hz: float,
        amplitude_v: float,
        offset_v: float = 0.0,
        phase_deg: float = 0.0,
    ) -> dict[str, Any]:
        """Configure a single-ended sine wave on W1 (0) or W2 (1)."""
        hdwf = self._require_open()
        self._validate_channel(channel)
        if not math.isfinite(frequency_hz) or frequency_hz <= 0:
            raise AD3Error(f"frequency_hz must be positive, got {frequency_hz}")
        if not math.isfinite(amplitude_v) or amplitude_v < 0:
            raise AD3Error(f"amplitude_v must be non-negative, got {amplitude_v}")
        self._validate_voltage( abs(offset_v) + amplitude_v, "peak_output_v")

        ch = c_int(channel)
        node = c_int(ANALOG_OUT_NODE_CARRIER)
        _check_dwf(self.dwf, self.dwf.FDwfAnalogOutReset(hdwf, ch), "FDwfAnalogOutReset")
        _check_dwf(self.dwf, self.dwf.FDwfAnalogOutNodeEnableSet(hdwf, ch, node, c_int(1)), "FDwfAnalogOutNodeEnableSet")
        _check_dwf(self.dwf, self.dwf.FDwfAnalogOutNodeFunctionSet(hdwf, ch, node, c_ubyte(FUNC_SINE)), "FDwfAnalogOutNodeFunctionSet")
        _check_dwf(self.dwf, self.dwf.FDwfAnalogOutNodeFrequencySet(hdwf, ch, node, c_double(frequency_hz)), "FDwfAnalogOutNodeFrequencySet")
        _check_dwf(self.dwf, self.dwf.FDwfAnalogOutNodeAmplitudeSet(hdwf, ch, node, c_double(amplitude_v)), "FDwfAnalogOutNodeAmplitudeSet")
        _check_dwf(self.dwf, self.dwf.FDwfAnalogOutNodeOffsetSet(hdwf, ch, node, c_double(offset_v)), "FDwfAnalogOutNodeOffsetSet")
        _check_dwf(self.dwf, self.dwf.FDwfAnalogOutNodePhaseSet(hdwf, ch, node, c_double(phase_deg)), "FDwfAnalogOutNodePhaseSet")
        _check_dwf(self.dwf, self.dwf.FDwfAnalogOutConfigure(hdwf, ch, c_int(1)), "FDwfAnalogOutConfigure")
        return {
            "mode": "sine",
            "channel": channel,
            "frequency_hz": frequency_hz,
            "amplitude_v": amplitude_v,
            "offset_v": offset_v,
            "phase_deg": phase_deg,
        }

    def configure_differential_sine(
        self,
        frequency_hz: float,
        diff_amplitude_v: float,
        common_mode_v: float = 0.0,
    ) -> dict[str, Any]:
        """Configure phase-locked differential sine wave across W1 (0 deg) and W2 (180 deg).

        Each channel outputs amplitude = diff_amplitude_v / 2 around common_mode_v so that
        V(W1) - V(W2) has peak amplitude diff_amplitude_v (peak-to-peak 2 * diff_amplitude_v).
        """
        hdwf = self._require_open()
        if not math.isfinite(frequency_hz) or frequency_hz <= 0:
            raise AD3Error(f"frequency_hz must be positive, got {frequency_hz}")
        if not math.isfinite(diff_amplitude_v) or diff_amplitude_v < 0:
            raise AD3Error(f"diff_amplitude_v must be non-negative, got {diff_amplitude_v}")
        per_ch_amp = 0.5 * diff_amplitude_v
        self._validate_voltage(abs(common_mode_v) + per_ch_amp, "per_channel_peak_v")

        node = c_int(ANALOG_OUT_NODE_CARRIER)
        _check_dwf(self.dwf, self.dwf.FDwfAnalogOutReset(hdwf, c_int(-1)), "FDwfAnalogOutReset")
        for ch_idx, phase in ((0, 0.0), (1, 180.0)):
            ch = c_int(ch_idx)
            _check_dwf(self.dwf, self.dwf.FDwfAnalogOutNodeEnableSet(hdwf, ch, node, c_int(1)), "FDwfAnalogOutNodeEnableSet")
            _check_dwf(self.dwf, self.dwf.FDwfAnalogOutNodeFunctionSet(hdwf, ch, node, c_ubyte(FUNC_SINE)), "FDwfAnalogOutNodeFunctionSet")
            _check_dwf(self.dwf, self.dwf.FDwfAnalogOutNodeFrequencySet(hdwf, ch, node, c_double(frequency_hz)), "FDwfAnalogOutNodeFrequencySet")
            _check_dwf(self.dwf, self.dwf.FDwfAnalogOutNodeAmplitudeSet(hdwf, ch, node, c_double(per_ch_amp)), "FDwfAnalogOutNodeAmplitudeSet")
            _check_dwf(self.dwf, self.dwf.FDwfAnalogOutNodeOffsetSet(hdwf, ch, node, c_double(common_mode_v)), "FDwfAnalogOutNodeOffsetSet")
            _check_dwf(self.dwf, self.dwf.FDwfAnalogOutNodePhaseSet(hdwf, ch, node, c_double(phase)), "FDwfAnalogOutNodePhaseSet")

        _check_dwf(self.dwf, self.dwf.FDwfAnalogOutMasterSet(hdwf, c_int(1), c_int(0)), "FDwfAnalogOutMasterSet")
        _check_dwf(self.dwf, self.dwf.FDwfAnalogOutConfigure(hdwf, c_int(0), c_int(1)), "FDwfAnalogOutConfigure")
        return {
            "mode": "differential_sine",
            "frequency_hz": frequency_hz,
            "diff_amplitude_v": diff_amplitude_v,
            "per_channel_amplitude_v": per_ch_amp,
            "common_mode_v": common_mode_v,
        }

    def configure_sweep(
        self,
        start_hz: float,
        stop_hz: float,
        duration_s: float,
        amplitude_v: float,
        offset_v: float = 0.0,
        channel: int = 0,
        differential: bool = False,
        repeat: int = 0,
    ) -> dict[str, Any]:
        """Configure a linear FM sine sweep from start_hz to stop_hz over duration_s."""
        hdwf = self._require_open()
        if not math.isfinite(start_hz) or start_hz <= 0 or not math.isfinite(stop_hz) or stop_hz <= 0:
            raise AD3Error(f"Sweep frequencies must be positive: start_hz={start_hz}, stop_hz={stop_hz}")
        if not math.isfinite(duration_s) or duration_s <= 0:
            raise AD3Error(f"duration_s must be positive, got {duration_s}")
        if not math.isfinite(amplitude_v) or amplitude_v < 0:
            raise AD3Error(f"amplitude_v must be non-negative, got {amplitude_v}")

        mid_hz = 0.5 * (start_hz + stop_hz)
        fm_pct = 100.0 * (stop_hz - mid_hz) / mid_hz
        per_ch_amp = 0.5 * amplitude_v if differential else amplitude_v
        self._validate_voltage(abs(offset_v) + per_ch_amp, "sweep_peak_v")

        carrier = c_int(ANALOG_OUT_NODE_CARRIER)
        fm_node = c_int(ANALOG_OUT_NODE_FM)

        if differential:
            _check_dwf(self.dwf, self.dwf.FDwfAnalogOutReset(hdwf, c_int(-1)), "FDwfAnalogOutReset")
            channels = [(0, 0.0), (1, 180.0)]
        else:
            self._validate_channel(channel)
            _check_dwf(self.dwf, self.dwf.FDwfAnalogOutReset(hdwf, c_int(channel)), "FDwfAnalogOutReset")
            channels = [(channel, 0.0)]

        for ch_idx, phase in channels:
            ch = c_int(ch_idx)
            _check_dwf(self.dwf, self.dwf.FDwfAnalogOutNodeEnableSet(hdwf, ch, carrier, c_int(1)), "FDwfAnalogOutNodeEnableSet(carrier)")
            _check_dwf(self.dwf, self.dwf.FDwfAnalogOutNodeFunctionSet(hdwf, ch, carrier, c_ubyte(FUNC_SINE)), "FDwfAnalogOutNodeFunctionSet(carrier)")
            _check_dwf(self.dwf, self.dwf.FDwfAnalogOutNodeFrequencySet(hdwf, ch, carrier, c_double(mid_hz)), "FDwfAnalogOutNodeFrequencySet(carrier)")
            _check_dwf(self.dwf, self.dwf.FDwfAnalogOutNodeAmplitudeSet(hdwf, ch, carrier, c_double(per_ch_amp)), "FDwfAnalogOutNodeAmplitudeSet(carrier)")
            _check_dwf(self.dwf, self.dwf.FDwfAnalogOutNodeOffsetSet(hdwf, ch, carrier, c_double(offset_v)), "FDwfAnalogOutNodeOffsetSet(carrier)")
            _check_dwf(self.dwf, self.dwf.FDwfAnalogOutNodePhaseSet(hdwf, ch, carrier, c_double(phase)), "FDwfAnalogOutNodePhaseSet(carrier)")

            _check_dwf(self.dwf, self.dwf.FDwfAnalogOutNodeEnableSet(hdwf, ch, fm_node, c_int(1)), "FDwfAnalogOutNodeEnableSet(FM)")
            _check_dwf(self.dwf, self.dwf.FDwfAnalogOutNodeFunctionSet(hdwf, ch, fm_node, c_ubyte(FUNC_RAMP_UP)), "FDwfAnalogOutNodeFunctionSet(FM)")
            _check_dwf(self.dwf, self.dwf.FDwfAnalogOutNodeFrequencySet(hdwf, ch, fm_node, c_double(1.0 / duration_s)), "FDwfAnalogOutNodeFrequencySet(FM)")
            _check_dwf(self.dwf, self.dwf.FDwfAnalogOutNodeAmplitudeSet(hdwf, ch, fm_node, c_double(fm_pct)), "FDwfAnalogOutNodeAmplitudeSet(FM)")
            _check_dwf(self.dwf, self.dwf.FDwfAnalogOutNodeSymmetrySet(hdwf, ch, fm_node, c_double(100.0)), "FDwfAnalogOutNodeSymmetrySet(FM)")

        master_ch = c_int(0 if differential else channel)
        if repeat > 0:
            _check_dwf(self.dwf, self.dwf.FDwfAnalogOutRunSet(hdwf, master_ch, c_double(duration_s)), "FDwfAnalogOutRunSet")
            _check_dwf(self.dwf, self.dwf.FDwfAnalogOutRepeatSet(hdwf, master_ch, c_int(repeat)), "FDwfAnalogOutRepeatSet")

        if differential:
            _check_dwf(self.dwf, self.dwf.FDwfAnalogOutMasterSet(hdwf, c_int(1), c_int(0)), "FDwfAnalogOutMasterSet")

        _check_dwf(self.dwf, self.dwf.FDwfAnalogOutConfigure(hdwf, master_ch, c_int(1)), "FDwfAnalogOutConfigure")
        return {
            "mode": "differential_sweep" if differential else "sweep",
            "channel": "W1/W2" if differential else channel,
            "start_hz": start_hz,
            "stop_hz": stop_hz,
            "duration_s": duration_s,
            "amplitude_v": amplitude_v,
            "offset_v": offset_v,
            "repeat": repeat,
        }

    def capture_logic(
        self,
        sample_rate_hz: float = 25_000_000.0,
        num_samples: int = 32768,
        trigger_pin: int | None = None,
        trigger_edge: str = "falling",
        prefill_ratio: float = 0.1,
        timeout_s: float = 5.0,
    ) -> tuple[list[int], float]:
        """Acquire a single high-speed buffer from DIO 0-15.

        Returns (samples_uint16, actual_sample_rate_hz).
        """
        hdwf = self._require_open()
        if sample_rate_hz <= 0:
            raise AD3Error(f"sample_rate_hz must be positive, got {sample_rate_hz}")
        if num_samples <= 0:
            raise AD3Error(f"num_samples must be positive, got {num_samples}")
        if not (0.0 <= prefill_ratio < 1.0):
            raise AD3Error(f"prefill_ratio must be in [0.0, 1.0), got {prefill_ratio}")

        _check_dwf(self.dwf, self.dwf.FDwfDigitalInReset(hdwf), "FDwfDigitalInReset")

        hz_internal = c_double()
        _check_dwf(self.dwf, self.dwf.FDwfDigitalInInternalClockInfo(hdwf, byref(hz_internal)), "FDwfDigitalInInternalClockInfo")
        divider = max(1, int(round(hz_internal.value / sample_rate_hz)))
        actual_rate_hz = hz_internal.value / divider

        max_buf = c_int()
        _check_dwf(self.dwf, self.dwf.FDwfDigitalInBufferSizeInfo(hdwf, byref(max_buf)), "FDwfDigitalInBufferSizeInfo")
        if num_samples > max_buf.value:
            raise AD3Error(
                f"Requested num_samples={num_samples} exceeds single-capture DigitalIn buffer size "
                f"{max_buf.value} (use record_logic for longer streaming captures)."
            )

        _check_dwf(self.dwf, self.dwf.FDwfDigitalInAcquisitionModeSet(hdwf, c_int(ACQMODE_SINGLE)), "FDwfDigitalInAcquisitionModeSet")
        _check_dwf(self.dwf, self.dwf.FDwfDigitalInDividerSet(hdwf, c_int(divider)), "FDwfDigitalInDividerSet")
        _check_dwf(self.dwf, self.dwf.FDwfDigitalInSampleFormatSet(hdwf, c_int(16)), "FDwfDigitalInSampleFormatSet")
        _check_dwf(self.dwf, self.dwf.FDwfDigitalInBufferSizeSet(hdwf, c_int(num_samples)), "FDwfDigitalInBufferSizeSet")
        _check_dwf(self.dwf, self.dwf.FDwfDigitalInInputOrderSet(hdwf, c_int(0)), "FDwfDigitalInInputOrderSet")

        if trigger_pin is not None:
            if not (0 <= trigger_pin <= 15):
                raise AD3Error(f"trigger_pin must be in 0..15, got {trigger_pin}")
            mask = 1 << trigger_pin
            rise_mask = mask if trigger_edge in ("rising", "either") else 0
            fall_mask = mask if trigger_edge in ("falling", "either") else 0
            if rise_mask == 0 and fall_mask == 0:
                raise AD3Error(f"trigger_edge must be 'rising', 'falling', or 'either', got {trigger_edge!r}")
            post_samples = int(round(num_samples * (1.0 - prefill_ratio)))
            _check_dwf(self.dwf, self.dwf.FDwfDigitalInTriggerSourceSet(hdwf, c_ubyte(TRIGSRC_DETECTOR_DIGITAL_IN)), "FDwfDigitalInTriggerSourceSet")
            _check_dwf(self.dwf, self.dwf.FDwfDigitalInTriggerPositionSet(hdwf, c_int(post_samples)), "FDwfDigitalInTriggerPositionSet")
            _check_dwf(self.dwf, self.dwf.FDwfDigitalInTriggerPrefillSet(hdwf, c_int(num_samples - post_samples)), "FDwfDigitalInTriggerPrefillSet")
            _check_dwf(
                self.dwf,
                self.dwf.FDwfDigitalInTriggerSet(hdwf, c_int(0), c_int(0), c_int(rise_mask), c_int(fall_mask)),
                "FDwfDigitalInTriggerSet",
            )
            _check_dwf(self.dwf, self.dwf.FDwfDigitalInTriggerAutoTimeoutSet(hdwf, c_double(0.0)), "FDwfDigitalInTriggerAutoTimeoutSet")
        else:
            _check_dwf(self.dwf, self.dwf.FDwfDigitalInTriggerSourceSet(hdwf, c_ubyte(TRIGSRC_NONE)), "FDwfDigitalInTriggerSourceSet")

        _check_dwf(self.dwf, self.dwf.FDwfDigitalInConfigure(hdwf, c_int(1), c_int(1)), "FDwfDigitalInConfigure")

        sts = c_ubyte()
        deadline = time.monotonic() + timeout_s
        while True:
            _check_dwf(self.dwf, self.dwf.FDwfDigitalInStatus(hdwf, c_int(1), byref(sts)), "FDwfDigitalInStatus")
            if sts.value == DWF_STATE_DONE:
                break
            if time.monotonic() > deadline:
                self.dwf.FDwfDigitalInConfigure(hdwf, c_int(0), c_int(0))
                raise TimeoutError(
                    f"Logic analyzer capture timed out after {timeout_s:.2f}s (status={sts.value}, "
                    f"trigger_pin={trigger_pin}, trigger_edge={trigger_edge!r})."
                )
            time.sleep(0.002)

        buf = (c_uint16 * num_samples)()
        _check_dwf(
            self.dwf,
            self.dwf.FDwfDigitalInStatusData(hdwf, buf, c_int(2 * num_samples)),
            "FDwfDigitalInStatusData",
        )
        return list(buf), actual_rate_hz

    def record_logic(
        self,
        sample_rate_hz: float = 10_000_000.0,
        num_samples: int = 100_000,
        trigger_pin: int | None = None,
        trigger_edge: str = "falling",
        timeout_s: float = 10.0,
    ) -> tuple[list[int], float, int, int]:
        """Stream DigitalIn samples over USB in record mode.

        Returns (samples_uint16, actual_sample_rate_hz, lost_samples, corrupted_samples).
        """
        hdwf = self._require_open()
        if sample_rate_hz <= 0:
            raise AD3Error(f"sample_rate_hz must be positive, got {sample_rate_hz}")
        if num_samples <= 0:
            raise AD3Error(f"num_samples must be positive, got {num_samples}")

        _check_dwf(self.dwf, self.dwf.FDwfDigitalInReset(hdwf), "FDwfDigitalInReset")

        hz_internal = c_double()
        _check_dwf(self.dwf, self.dwf.FDwfDigitalInInternalClockInfo(hdwf, byref(hz_internal)), "FDwfDigitalInInternalClockInfo")
        divider = max(1, int(round(hz_internal.value / sample_rate_hz)))
        actual_rate_hz = hz_internal.value / divider

        _check_dwf(self.dwf, self.dwf.FDwfDigitalInAcquisitionModeSet(hdwf, c_int(ACQMODE_RECORD)), "FDwfDigitalInAcquisitionModeSet")
        _check_dwf(self.dwf, self.dwf.FDwfDigitalInDividerSet(hdwf, c_int(divider)), "FDwfDigitalInDividerSet")
        _check_dwf(self.dwf, self.dwf.FDwfDigitalInSampleFormatSet(hdwf, c_int(16)), "FDwfDigitalInSampleFormatSet")
        _check_dwf(self.dwf, self.dwf.FDwfDigitalInTriggerPositionSet(hdwf, c_int(num_samples)), "FDwfDigitalInTriggerPositionSet")
        _check_dwf(self.dwf, self.dwf.FDwfDigitalInTriggerPrefillSet(hdwf, c_int(0)), "FDwfDigitalInTriggerPrefillSet")
        _check_dwf(self.dwf, self.dwf.FDwfDigitalInInputOrderSet(hdwf, c_int(0)), "FDwfDigitalInInputOrderSet")

        if trigger_pin is not None:
            if not (0 <= trigger_pin <= 15):
                raise AD3Error(f"trigger_pin must be in 0..15, got {trigger_pin}")
            mask = 1 << trigger_pin
            rise_mask = mask if trigger_edge in ("rising", "either") else 0
            fall_mask = mask if trigger_edge in ("falling", "either") else 0
            if rise_mask == 0 and fall_mask == 0:
                raise AD3Error(f"trigger_edge must be 'rising', 'falling', or 'either', got {trigger_edge!r}")
            _check_dwf(self.dwf, self.dwf.FDwfDigitalInTriggerSourceSet(hdwf, c_ubyte(TRIGSRC_DETECTOR_DIGITAL_IN)), "FDwfDigitalInTriggerSourceSet")
            _check_dwf(
                self.dwf,
                self.dwf.FDwfDigitalInTriggerSet(hdwf, c_int(0), c_int(0), c_int(rise_mask), c_int(fall_mask)),
                "FDwfDigitalInTriggerSet",
            )
        else:
            _check_dwf(self.dwf, self.dwf.FDwfDigitalInTriggerSourceSet(hdwf, c_ubyte(TRIGSRC_NONE)), "FDwfDigitalInTriggerSourceSet")

        _check_dwf(self.dwf, self.dwf.FDwfDigitalInConfigure(hdwf, c_int(1), c_int(1)), "FDwfDigitalInConfigure")

        buf = (c_uint16 * num_samples)()
        sts = c_ubyte()
        c_avail = c_int()
        c_lost = c_int()
        c_corrupted = c_int()
        collected = 0
        total_lost = 0
        total_corrupted = 0
        deadline = time.monotonic() + timeout_s

        while collected < num_samples:
            if time.monotonic() > deadline:
                self.dwf.FDwfDigitalInConfigure(hdwf, c_int(0), c_int(0))
                raise TimeoutError(
                    f"Logic record timed out after {timeout_s:.2f}s ({collected}/{num_samples} samples collected)."
                )
            _check_dwf(self.dwf, self.dwf.FDwfDigitalInStatus(hdwf, c_int(1), byref(sts)), "FDwfDigitalInStatus")
            if sts.value in (DWF_STATE_CONFIG, DWF_STATE_PREFILL, DWF_STATE_ARMED) and collected == 0:
                time.sleep(0.001)
                continue

            _check_dwf(
                self.dwf,
                self.dwf.FDwfDigitalInStatusRecord(hdwf, byref(c_avail), byref(c_lost), byref(c_corrupted)),
                "FDwfDigitalInStatusRecord",
            )
            total_lost += c_lost.value
            total_corrupted += c_corrupted.value

            if c_avail.value > 0:
                to_read = min(c_avail.value, num_samples - collected)
                _check_dwf(
                    self.dwf,
                    self.dwf.FDwfDigitalInStatusData(hdwf, byref(buf, 2 * collected), c_int(2 * to_read)),
                    "FDwfDigitalInStatusData",
                )
                collected += to_read

            if sts.value == DWF_STATE_DONE:
                break

        return list(buf[:collected]), actual_rate_hz, total_lost, total_corrupted

    def capture_analog(
        self,
        channel: int = 0,
        sample_rate_hz: float = 1_000_000.0,
        num_samples: int = 8192,
        range_v: float = 5.0,
        settle_s: float = 0.05,
        timeout_s: float = 5.0,
    ) -> tuple[list[float], float]:
        """Acquire a single-buffer oscilloscope capture on AnalogIn channel 0 (CH1) or 1 (CH2).

        Returns (samples_volts, actual_sample_rate_hz).
        """
        hdwf = self._require_open()
        if channel not in (0, 1):
            raise AD3Error(f"AnalogIn channel must be 0 (CH1) or 1 (CH2), got {channel}")
        if sample_rate_hz <= 0:
            raise AD3Error(f"sample_rate_hz must be positive, got {sample_rate_hz}")
        if num_samples <= 0:
            raise AD3Error(f"num_samples must be positive, got {num_samples}")

        max_buf = c_int()
        _check_dwf(self.dwf, self.dwf.FDwfAnalogInBufferSizeInfo(hdwf, c_int(0), byref(max_buf)), "FDwfAnalogInBufferSizeInfo")
        if max_buf.value > 0 and num_samples > max_buf.value:
            raise AD3Error(f"Requested num_samples={num_samples} exceeds AnalogIn buffer size {max_buf.value}")

        ch = c_int(channel)
        _check_dwf(self.dwf, self.dwf.FDwfAnalogInReset(hdwf), "FDwfAnalogInReset")
        _check_dwf(self.dwf, self.dwf.FDwfAnalogInChannelEnableSet(hdwf, ch, c_int(1)), "FDwfAnalogInChannelEnableSet")
        _check_dwf(self.dwf, self.dwf.FDwfAnalogInChannelRangeSet(hdwf, ch, c_double(range_v)), "FDwfAnalogInChannelRangeSet")
        _check_dwf(self.dwf, self.dwf.FDwfAnalogInChannelOffsetSet(hdwf, ch, c_double(0.0)), "FDwfAnalogInChannelOffsetSet")
        _check_dwf(self.dwf, self.dwf.FDwfAnalogInAcquisitionModeSet(hdwf, c_int(ACQMODE_SINGLE)), "FDwfAnalogInAcquisitionModeSet")
        _check_dwf(self.dwf, self.dwf.FDwfAnalogInFrequencySet(hdwf, c_double(sample_rate_hz)), "FDwfAnalogInFrequencySet")
        _check_dwf(self.dwf, self.dwf.FDwfAnalogInBufferSizeSet(hdwf, c_int(num_samples)), "FDwfAnalogInBufferSizeSet")
        _check_dwf(self.dwf, self.dwf.FDwfAnalogInTriggerSourceSet(hdwf, c_ubyte(TRIGSRC_NONE)), "FDwfAnalogInTriggerSourceSet")

        # Configure channel offset/range and allow input frontend to settle before starting capture
        _check_dwf(self.dwf, self.dwf.FDwfAnalogInConfigure(hdwf, c_int(1), c_int(0)), "FDwfAnalogInConfigure(reconfigure)")
        actual_rate = c_double()
        _check_dwf(self.dwf, self.dwf.FDwfAnalogInFrequencyGet(hdwf, byref(actual_rate)), "FDwfAnalogInFrequencyGet")
        if settle_s > 0:
            time.sleep(settle_s)
        _check_dwf(self.dwf, self.dwf.FDwfAnalogInConfigure(hdwf, c_int(0), c_int(1)), "FDwfAnalogInConfigure(start)")

        sts = c_ubyte()
        deadline = time.monotonic() + timeout_s
        while True:
            _check_dwf(self.dwf, self.dwf.FDwfAnalogInStatus(hdwf, c_int(1), byref(sts)), "FDwfAnalogInStatus")
            if sts.value == DWF_STATE_DONE:
                break
            if time.monotonic() > deadline:
                self.dwf.FDwfAnalogInConfigure(hdwf, c_int(0), c_int(0))
                raise TimeoutError(f"AnalogIn capture timed out after {timeout_s:.2f}s (status={sts.value})")
            time.sleep(0.002)

        buf = (c_double * num_samples)()
        _check_dwf(self.dwf, self.dwf.FDwfAnalogInStatusData(hdwf, ch, buf, c_int(num_samples)), "FDwfAnalogInStatusData")
        return list(buf), actual_rate.value


def analyze_analog_waveform(samples: Sequence[float], sample_rate_hz: float) -> dict[str, Any]:
    """Compute DC offset, RMS, peak-to-peak, and zero-crossing fundamental frequency for an analog capture."""
    n = len(samples)
    if n == 0 or sample_rate_hz <= 0:
        raise AD3Error("analyze_analog_waveform requires non-empty samples and positive sample_rate_hz")

    mean_v = sum(samples) / n
    min_v = min(samples)
    max_v = max(samples)
    vpp = max_v - min_v
    ac_rms_v = math.sqrt(sum((x - mean_v) ** 2 for x in samples) / n)
    est_peak_v = ac_rms_v * math.sqrt(2.0)

    # Interpolated rising zero-crossings around mean_v
    crossings: list[float] = []
    for i in range(n - 1):
        y0 = samples[i] - mean_v
        y1 = samples[i + 1] - mean_v
        if y0 <= 0.0 < y1:
            frac = -y0 / (y1 - y0) if (y1 - y0) != 0.0 else 0.0
            crossings.append(i + frac)

    if len(crossings) >= 2:
        mean_period_samples = (crossings[-1] - crossings[0]) / (len(crossings) - 1)
        freq_hz: float | None = sample_rate_hz / mean_period_samples if mean_period_samples > 0 else None
    else:
        freq_hz = None

    return {
        "num_samples": n,
        "sample_rate_hz": sample_rate_hz,
        "mean_dc_v": mean_v,
        "min_v": min_v,
        "max_v": max_v,
        "vpp_v": vpp,
        "ac_rms_v": ac_rms_v,
        "estimated_sine_amplitude_v": est_peak_v,
        "estimated_frequency_hz": freq_hz,
        "rising_zero_crossings": len(crossings),
    }


