import math
import sys
import unittest
from pathlib import Path

ROOT = Path(__file__).resolve().parents[1]
if str(ROOT) not in sys.path:
    sys.path.insert(0, str(ROOT))

from multinodedaq.ad3 import (
    AD3Device,
    AD3Error,
    J2PinMap,
    analyze_analog_waveform,
    analyze_drdy,
    crc8_ads127l11,
    decode_spi_frames,
    parse_ads127l11_frame,
    sign_extend_24,
)


def _synthesize_spi_mode1_stream(
    frames_bits: list[tuple[int, int, int]],
    pins: J2PinMap,
    drdy_period_samples: int = 400,
    idle_before_cs: int = 10,
    half_clk_samples: int = 2,
) -> list[int]:
    """Synthesize 16-bit logic analyzer samples containing DRDY pulses and SPI Mode 1 frames.

    Each entry in frames_bits is (bit_count, sdi_word, sdo_word).
    In SPI Mode 1 (CPOL=0, CPHA=1):
    - SCLK idles low (0)
    - CS is active low (0), idle high (1)
    - Data is set on SCLK rising edge (0 -> 1) and sampled on SCLK falling edge (1 -> 0).
    """
    drdy_mask = 1 << pins.drdy
    cs_mask = 1 << pins.cs
    sclk_mask = 1 << pins.sclk
    sdi_mask = 1 << pins.sdi
    sdo_mask = 1 << pins.sdo

    idle_word = drdy_mask | cs_mask
    total_samples = max(1, len(frames_bits) + 1) * drdy_period_samples
    stream = [idle_word] * total_samples

    for idx, (nbits, sdi_val, sdo_val) in enumerate(frames_bits):
        t_drdy = (idx + 1) * drdy_period_samples - (drdy_period_samples - 20)
        # Active-low DRDY pulse of width 4 samples
        for k in range(t_drdy, min(total_samples, t_drdy + 4)):
            stream[k] &= ~drdy_mask

        t_cs = t_drdy + idle_before_cs
        cur = t_cs
        # Assert CS low (SCLK still 0)
        for k in range(cur, min(total_samples, cur + half_clk_samples)):
            stream[k] = (stream[k] & ~cs_mask)
        cur += half_clk_samples

        for bit_idx in range(nbits - 1, -1, -1):
            sdi_bit = (sdi_val >> bit_idx) & 1
            sdo_bit = (sdo_val >> bit_idx) & 1
            # Rising edge of SCLK: launch data
            for k in range(cur, min(total_samples, cur + half_clk_samples)):
                w = (stream[k] & ~(cs_mask | sdi_mask | sdo_mask)) | sclk_mask
                if sdi_bit:
                    w |= sdi_mask
                if sdo_bit:
                    w |= sdo_mask
                stream[k] = w
            cur += half_clk_samples
            # Falling edge of SCLK: sample data
            for k in range(cur, min(total_samples, cur + half_clk_samples)):
                w = stream[k] & ~(cs_mask | sclk_mask | sdi_mask | sdo_mask)
                if sdi_bit:
                    w |= sdi_mask
                if sdo_bit:
                    w |= sdo_mask
                stream[k] = w
            cur += half_clk_samples

        # Hold CS low briefly before deasserting
        for k in range(cur, min(total_samples, cur + half_clk_samples)):
            stream[k] = stream[k] & ~(cs_mask | sclk_mask)

    return stream


class TestAD3Analysis(unittest.TestCase):
    def test_sign_extend_24_boundary_vectors(self) -> None:
        self.assertEqual(sign_extend_24(0x000000), 0)
        self.assertEqual(sign_extend_24(0x7FFFFF), 8388607)
        self.assertEqual(sign_extend_24(0x800000), -8388608)
        self.assertEqual(sign_extend_24(0xFFFFFF), -1)
        with self.assertRaises(ValueError):
            sign_extend_24(-1)
        with self.assertRaises(ValueError):
            sign_extend_24(0x1000000)

    def test_crc8_ads127l11(self) -> None:
        # Verify deterministic CRC-8 (poly 0x07, init 0xFF) properties
        payload = bytes([0x12, 0x34, 0x56])
        c = crc8_ads127l11(payload)
        self.assertTrue(0 <= c <= 0xFF)
        # Appending the CRC byte itself and running CRC over payload+crc yields 0x00
        self.assertEqual(crc8_ads127l11(payload + bytes([c])), 0x00)

    def test_pin_map_validation(self) -> None:
        J2PinMap(drdy=0, cs=1, sclk=2, sdi=3, sdo=4).validate()
        # Shared SDO/DRDY pin is allowed by ADS127L11 J2
        J2PinMap(drdy=4, cs=1, sclk=2, sdi=3, sdo=4).validate()
        with self.assertRaises(AD3Error):
            J2PinMap(drdy=0, cs=0, sclk=2, sdi=3, sdo=4).validate()
        with self.assertRaises(AD3Error):
            J2PinMap(drdy=16, cs=1, sclk=2, sdi=3, sdo=4).validate()

    def test_drdy_rate_and_spi_mode1_decoding_24_32_40_bit(self) -> None:
        pins = J2PinMap(drdy=0, cs=1, sclk=2, sdi=3, sdo=4)
        sample_rate_hz = 10_000_000.0  # 10 MHz -> 400 samples = 40 us = 25,000 SPS

        # Test 24-bit frame (data only)
        code_neg = 0x800000  # -8388608
        code_pos = 0x7FFFFF  # +8388607
        stream_24 = _synthesize_spi_mode1_stream(
            [(24, 0x000000, code_neg), (24, 0x000000, code_pos)],
            pins=pins,
            drdy_period_samples=400,
        )

        drdy_stats = analyze_drdy(stream_24, sample_rate_hz, drdy_pin=pins.drdy)
        self.assertEqual(drdy_stats.falling_edges, 2)
        self.assertAlmostEqual(drdy_stats.observed_rate_sps or 0.0, 25_000.0, places=3)
        self.assertAlmostEqual(drdy_stats.mean_period_us or 0.0, 40.0, places=3)
        self.assertEqual(drdy_stats.closest_reference_clock, "target_25p6mhz")
        self.assertAlmostEqual(drdy_stats.reference_rate_error_ppm or 0.0, 0.0, places=3)

        frames_24 = decode_spi_frames(
            stream_24,
            sample_rate_hz,
            cs_pin=pins.cs,
            sclk_pin=pins.sclk,
            sdi_pin=pins.sdi,
            sdo_pin=pins.sdo,
            drdy_pin=pins.drdy,
        )
        self.assertEqual(len(frames_24), 2)
        p0 = parse_ads127l11_frame(frames_24[0], has_status=False, has_crc=False)
        p1 = parse_ads127l11_frame(frames_24[1], has_status=False, has_crc=False)
        self.assertEqual(p0.code_i24, -8388608)
        self.assertEqual(p1.code_i24, 8388607)
        self.assertTrue(p0.completed_before_next_drdy)

        # Test 32-bit frame (24-bit data + 8-bit CRC) and 40-bit frame (8-bit status + 24-bit data + 8-bit CRC)
        data_bytes = bytes([0xFF, 0xFF, 0xFE])  # -2 in signed 24-bit
        crc_32 = crc8_ads127l11(data_bytes)
        word_32 = int.from_bytes(data_bytes + bytes([crc_32]), "big")

        stream_32 = _synthesize_spi_mode1_stream(
            [(32, 0x00000000, word_32), (32, 0x00000000, word_32 ^ 0x01)],
            pins=pins,
            drdy_period_samples=400,
        )
        frames_32 = decode_spi_frames(stream_32, sample_rate_hz)
        parsed_valid = parse_ads127l11_frame(frames_32[0], has_status=False, has_crc=True)
        parsed_corrupt = parse_ads127l11_frame(frames_32[1], has_status=False, has_crc=True)
        self.assertEqual(parsed_valid.code_i24, -2)
        self.assertTrue(parsed_valid.crc_valid)
        self.assertFalse(parsed_corrupt.crc_valid)

        status_And_data = bytes([0xA5, 0x00, 0x01, 0x00])  # status=0xA5, code=+256
        crc_40 = crc8_ads127l11(status_And_data)
        word_40 = int.from_bytes(status_And_data + bytes([crc_40]), "big")
        stream_40 = _synthesize_spi_mode1_stream([(40, 0, word_40)], pins=pins, drdy_period_samples=400)
        frames_40 = decode_spi_frames(stream_40, sample_rate_hz)
        parsed_40 = parse_ads127l11_frame(frames_40[0], has_status=True, has_crc=True)
        self.assertEqual(parsed_40.status_byte, 0xA5)
        self.assertEqual(parsed_40.code_i24, 256)
        self.assertTrue(parsed_40.crc_valid)

    def test_voltage_and_channel_validation(self) -> None:
        with self.assertRaises(AD3Error):
            AD3Device._validate_voltage(5.5, "test")
        with self.assertRaises(AD3Error):
            AD3Device._validate_channel(2)

    def test_analyze_analog_waveform(self) -> None:
        rate = 200_000.0
        freq = 1000.0
        amp = 1.25
        offset = 0.15
        samples = [
            offset + amp * math.sin(2.0 * math.pi * freq * (i / rate))
            for i in range(4000)
        ]
        metrics = analyze_analog_waveform(samples, rate)
        self.assertAlmostEqual(metrics["mean_dc_v"], offset, places=4)
        self.assertAlmostEqual(metrics["estimated_sine_amplitude_v"], amp, places=3)
        self.assertAlmostEqual(metrics["estimated_frequency_hz"], freq, places=2)


if __name__ == "__main__":
    unittest.main(verbosity=2)

