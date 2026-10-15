"""Command-line controller for Digilent Analog Discovery 3 (AD3) bench testing with ADS127L11EVM."""

from __future__ import annotations

import argparse
from dataclasses import asdict
import json
import pathlib
import sys
import time

ROOT = pathlib.Path(__file__).resolve().parents[1]
if str(ROOT / "python") not in sys.path:
    sys.path.insert(0, str(ROOT / "python"))

from multinodedaq.ad3 import (
    AD3Device,
    AD3Error,
    J2PinMap,
    analyze_drdy,
    decode_spi_frames,
    enumerate_devices,
    get_dwf_version,
    parse_ads127l11_frame,
)


def cmd_info(args: argparse.Namespace) -> int:
    version = get_dwf_version()
    devices = enumerate_devices()
    payload = {
        "dwf_version": version,
        "device_count": len(devices),
        "devices": [asdict(d) for d in devices],
    }
    print(json.dumps(payload, indent=2))
    return 0


def cmd_stop(args: argparse.Namespace) -> int:
    with AD3Device(
        device_index=args.device_index,
        serial=args.serial,
        config_index=args.config_index,
        on_close="stop",
    ) as ad3:
        result = ad3.stop_wavegen(channel=args.channel)
        result["device"] = {
            "name": ad3.device_info.name if ad3.device_info else None,
            "serial": ad3.device_info.serial if ad3.device_info else None,
        }
        print(json.dumps(result, indent=2))
    return 0


def cmd_wavegen(args: argparse.Namespace) -> int:
    with AD3Device(
        device_index=args.device_index,
        serial=args.serial,
        config_index=args.config_index,
        on_close=args.on_close,
    ) as ad3:
        if args.mode == "dc":
            res = ad3.configure_dc(channel=args.channel, voltage_v=args.voltage)
        elif args.mode == "diff-dc":
            res = ad3.configure_differential_dc(
                diff_v=args.voltage,
                common_mode_v=args.common_mode,
            )
        elif args.mode == "sine":
            res = ad3.configure_sine(
                channel=args.channel,
                frequency_hz=args.freq,
                amplitude_v=args.amplitude,
                offset_v=args.offset,
                phase_deg=args.phase,
            )
        elif args.mode == "diff-sine":
            res = ad3.configure_differential_sine(
                frequency_hz=args.freq,
                diff_amplitude_v=args.amplitude,
                common_mode_v=args.common_mode,
            )
        elif args.mode in ("sweep", "diff-sweep"):
            res = ad3.configure_sweep(
                start_hz=args.start_freq,
                stop_hz=args.stop_freq,
                duration_s=args.duration,
                amplitude_v=args.amplitude,
                offset_v=args.common_mode if args.mode == "diff-sweep" else args.offset,
                channel=args.channel,
                differential=(args.mode == "diff-sweep"),
                repeat=args.repeat,
            )
        else:
            raise AD3Error(f"Unsupported wavegen mode: {args.mode}")

        res["on_close"] = args.on_close
        res["device"] = {
            "name": ad3.device_info.name if ad3.device_info else None,
            "serial": ad3.device_info.serial if ad3.device_info else None,
        }
        print(json.dumps(res, indent=2))
        if args.hold_seconds > 0:
            time.sleep(args.hold_seconds)
    return 0


def cmd_logic(args: argparse.Namespace) -> int:
    pins = J2PinMap(
        drdy=args.drdy_pin,
        cs=args.cs_pin,
        sclk=args.sclk_pin,
        sdi=args.sdi_pin,
        sdo=args.sdo_pin,
    )
    pins.validate()

    trigger_pin: int | None = None
    if args.trigger == "drdy":
        trigger_pin = pins.drdy
    elif args.trigger == "cs":
        trigger_pin = pins.cs
    elif args.trigger == "pin":
        trigger_pin = args.trigger_pin

    with AD3Device(
        device_index=args.device_index,
        serial=args.serial,
        config_index=args.config_index,
        on_close=args.on_close,
    ) as ad3:
        if args.record:
            samples, actual_rate, lost, corrupted = ad3.record_logic(
                sample_rate_hz=args.rate,
                num_samples=args.samples,
                trigger_pin=trigger_pin,
                trigger_edge=args.trigger_edge,
                timeout_s=args.timeout,
            )
        else:
            samples, actual_rate = ad3.capture_logic(
                sample_rate_hz=args.rate,
                num_samples=args.samples,
                trigger_pin=trigger_pin,
                trigger_edge=args.trigger_edge,
                prefill_ratio=args.prefill,
                timeout_s=args.timeout,
            )
            lost, corrupted = 0, 0

        dev_meta = {
            "name": ad3.device_info.name if ad3.device_info else None,
            "serial": ad3.device_info.serial if ad3.device_info else None,
            "config_index": args.config_index,
        }

    drdy_report = analyze_drdy(samples, actual_rate, drdy_pin=pins.drdy)

    spi_summary: dict[str, object] | None = None
    if args.decode_spi:
        frames = decode_spi_frames(
            samples,
            actual_rate,
            cs_pin=pins.cs,
            sclk_pin=pins.sclk,
            sdi_pin=pins.sdi,
            sdo_pin=pins.sdo,
            drdy_pin=pins.drdy,
            cpol=args.cpol,
            cpha=args.cpha,
        )
        expected_bits = 24 + (8 if args.adc_status else 0) + (8 if args.adc_crc else 0)
        parsed_samples = []
        crc_errors = 0
        wrong_bit_count = 0
        missed_drdy_deadline = 0

        for f in frames:
            if f.completed_before_next_drdy is False:
                missed_drdy_deadline += 1
            if f.bit_count == expected_bits:
                parsed = parse_ads127l11_frame(
                    f,
                    has_status=args.adc_status,
                    has_crc=args.adc_crc,
                )
                if parsed.crc_valid is False:
                    crc_errors += 1
                parsed_samples.append(asdict(parsed))
            else:
                wrong_bit_count += 1

        spi_summary = {
            "total_frames": len(frames),
            "expected_frame_bits": expected_bits,
            "valid_length_frames": len(parsed_samples),
            "wrong_bit_count_frames": wrong_bit_count,
            "crc_errors": crc_errors,
            "missed_drdy_deadline_frames": missed_drdy_deadline,
            "preview_frames": parsed_samples[: min(10, len(parsed_samples))],
        }

    summary = {
        "device": dev_meta,
        "capture": {
            "mode": "record" if args.record else "single",
            "requested_rate_hz": args.rate,
            "actual_rate_hz": actual_rate,
            "num_samples": len(samples),
            "lost_samples": lost,
            "corrupted_samples": corrupted,
            "pins": asdict(pins),
        },
        "drdy": asdict(drdy_report),
        "spi": spi_summary,
    }

    if args.output:
        out_path = pathlib.Path(args.output)
        out_path.parent.mkdir(parents=True, exist_ok=True)
        out_path.write_text(json.dumps(summary, indent=2))

    print(json.dumps(summary, indent=2))
    return 0


def build_parser() -> argparse.ArgumentParser:
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--device-index", type=int, default=-1, help="DWF device index (-1 for first available)")
    parser.add_argument("--serial", help="Explicit AD3 serial number (e.g. 210415C1188A)")
    parser.add_argument(
        "--config-index",
        type=int,
        default=3,
        help="AD3 device configuration index (default 3: 32kS DigitalIn, 16kS AnalogIn, 4kS AnalogOut)",
    )

    sub = parser.add_subparsers(dest="subcommand", required=True)

    sub.add_parser("info", help="Enumerate WaveForms SDK version and connected AD3 devices")

    p_stop = sub.add_parser("stop", help="Stop and reset AD3 waveform generator outputs")
    p_stop.add_argument("--channel", type=int, default=-1, choices=[-1, 0, 1], help="0=W1, 1=W2, -1=both")

    p_wg = sub.add_parser("wavegen", help="Configure AD3 W1/W2 stimulus for ADS127L11EVM")
    p_wg.add_argument(
        "--mode",
        required=True,
        choices=["dc", "diff-dc", "sine", "diff-sine", "sweep", "diff-sweep"],
        help="Signal generation mode",
    )
    p_wg.add_argument("--channel", type=int, default=0, choices=[0, 1], help="Single-ended channel: 0=W1, 1=W2")
    p_wg.add_argument("--voltage", type=float, default=0.0, help="DC voltage or differential DC voltage (V)")
    p_wg.add_argument("--common-mode", type=float, default=0.0, help="Common-mode offset for differential modes (V)")
    p_wg.add_argument("--freq", type=float, default=1000.0, help="Sine frequency in Hz (e.g. 600 to 5000)")
    p_wg.add_argument("--amplitude", type=float, default=1.0, help="Peak amplitude in V (or differential peak V)")
    p_wg.add_argument("--offset", type=float, default=0.0, help="Single-ended DC offset in V")
    p_wg.add_argument("--phase", type=float, default=0.0, help="Single-ended phase in degrees")
    p_wg.add_argument("--start-freq", type=float, default=600.0, help="Sweep start frequency in Hz")
    p_wg.add_argument("--stop-freq", type=float, default=5000.0, help="Sweep stop frequency in Hz")
    p_wg.add_argument("--duration", type=float, default=1.0, help="Sweep duration in seconds")
    p_wg.add_argument("--repeat", type=int, default=0, help="Sweep repeat count (0 = continuous)")
    p_wg.add_argument(
        "--on-close",
        default="run",
        choices=["run", "stop", "shutdown"],
        help="Behavior when script exits (default 'run' keeps W1/W2 active during captures)",
    )
    p_wg.add_argument("--hold-seconds", type=float, default=0.0, help="Optional duration to hold handle open")

    p_la = sub.add_parser("logic", help="Capture and analyze ADS127L11EVM J2 digital signals on DIO 0-15")
    p_la.add_argument("--rate", type=float, default=25_000_000.0, help="Sampling rate in Hz (default 25 MHz)")
    p_la.add_argument("--samples", type=int, default=32768, help="Number of samples to acquire (default 32768)")
    p_la.add_argument("--record", action="store_true", help="Use USB streaming record mode for >32768 samples")
    p_la.add_argument("--trigger", default="none", choices=["none", "drdy", "cs", "pin"], help="Trigger source")
    p_la.add_argument("--trigger-pin", type=int, default=0, help="Explicit trigger DIO pin when --trigger=pin")
    p_la.add_argument("--trigger-edge", default="falling", choices=["falling", "rising", "either"])
    p_la.add_argument("--prefill", type=float, default=0.1, help="Pre-trigger buffer fraction in single mode")
    p_la.add_argument("--timeout", type=float, default=5.0, help="Capture timeout in seconds")
    p_la.add_argument("--drdy-pin", type=int, default=0, help="DIO pin for DRDY (default 0)")
    p_la.add_argument("--cs-pin", type=int, default=1, help="DIO pin for CS (default 1)")
    p_la.add_argument("--sclk-pin", type=int, default=2, help="DIO pin for SCLK (default 2)")
    p_la.add_argument("--sdi-pin", type=int, default=3, help="DIO pin for SDI / MOSI (default 3)")
    p_la.add_argument("--sdo-pin", type=int, default=4, help="DIO pin for SDO / MISO (default 4)")
    p_la.add_argument("--decode-spi", action="store_true", help="Decode SPI frames and ADS127L11 samples")
    p_la.add_argument("--cpol", type=int, default=0, choices=[0, 1], help="SPI CPOL (default 0 for ADS127L11 Mode 1)")
    p_la.add_argument("--cpha", type=int, default=1, choices=[0, 1], help="SPI CPHA (default 1 for ADS127L11 Mode 1)")
    p_la.add_argument("--adc-status", action="store_true", help="Expect 8-bit status prefix in ADS127L11 SPI frame")
    p_la.add_argument("--adc-crc", action="store_true", help="Expect and verify 8-bit CRC suffix in ADS127L11 SPI frame")
    p_la.add_argument("--on-close", default="run", choices=["run", "stop", "shutdown"])
    p_la.add_argument("--output", help="Optional JSON file path to save capture summary")

    return parser


def main(argv: list[str] | None = None) -> int:
    parser = build_parser()
    args = parser.parse_args(argv)
    try:
        if args.subcommand == "info":
            return cmd_info(args)
        if args.subcommand == "stop":
            return cmd_stop(args)
        if args.subcommand == "wavegen":
            return cmd_wavegen(args)
        if args.subcommand == "logic":
            return cmd_logic(args)
        raise AD3Error(f"Unknown subcommand: {args.subcommand}")
    except (AD3Error, TimeoutError) as exc:
        print(json.dumps({"error": str(exc)}), file=sys.stderr)
        return 1


if __name__ == "__main__":
    raise SystemExit(main())
