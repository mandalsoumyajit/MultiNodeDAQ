# ELF detector digital backend: architecture and design options

Date: 12 September 2026  
Status: design analysis and working direction; hardware and firmware performance remain to be demonstrated.

## 1. Working decision

Retain the **RP2350 and Pico SDK** as the acquisition platform. Prototype with a stock **Raspberry Pi Pico 2 W**, then consider a compatible custom board with the same RP2350/CYW43439 processor/radio combination and an **external antenna connector**.

This preserves the programming environment, documentation, PIO capability, and firmware investment while addressing the stock board's principal deployment limitation: its onboard antenna. The user's preference is to avoid moving to ESP32 merely to obtain an antenna connector.

Use three synchronously clocked external ADCs for a triaxial detector. Keep conversion, reference, and analog circuitry in the quiet domain and carry data across digital isolation. Treat wireless data transport and precise synchronization as separate functions. The system must start and operate in **GPS-denied environments**, without requiring GPS or internet time.

The architecture is feasible on interface/data-volume grounds. No sustained-throughput, field-range, timing-accuracy, battery-life, or radio-induced-noise result has yet been measured for this system.

## 2. Requirements and assumptions

| Item | Current basis |
|---|---|
| Sensor | Eventually three magnetic-field axes per node |
| Analysis band | 600 Hz to 5 kHz from earlier coil/AFE work |
| Converter | ADS127L11, one per axis, preserving the existing design direction |
| Initial output rate | 25 kSPS per axis |
| Sample representation | Preserve 24-bit conversion codes; 32-bit words are convenient internally |
| Deployment | Multiple stationary detectors across a site hundreds of meters on a side |
| Timing | No dependence on GPS; required relative accuracy depends on processing |
| Software preference | RP2350, Pico C/C++ SDK, PIO and DMA |
| Network | One base computer may serve several access points or coverage sectors |

Node count, terrain, vegetation, antenna heights, required recording duration, battery endurance, and the precise cross-node processing algorithm remain unspecified. Examples below use 16 nodes and 300 m/500 m square sites; these are sizing examples, not accepted deployment requirements.

The earlier [ADC/noise/interface study](../Coil_Design/ldo_integration/README.md) proposes high-speed modulator operation with a 25.6 MHz conversion clock, wideband filter, and OSR 512. This yields 25 kSPS and approximately 10.312 kHz -0.1 dB bandwidth. Preserve this distinction: low-speed operation at the same output rate has different, higher converter noise. The existing analog filtering and ADC digital response still require an explicit alias-rejection check; sample rate alone does not establish rejection. See [S2].

The early interface study is provisional; consult the later PCB/presentation work for subsequent circuit changes. This note does not reopen or validate the analog design.

## 3. Proposed signal and data path

```text
Three coils -> three analog front ends -> three ADS127L11 converters
                                            |
                              shared conversion clock and START
                                            |
                                   digital isolation
                                            |
                          RP2350 acquisition -> RAM ring buffer
                                            |
                             +--------------+---------------+
                             |                              |
                      local recording                 packet transport
                                                            |
                                                      CYW43439 Wi-Fi
                                                            |
                                                 outdoor access point(s)
                                                            |
                                                   Ethernet / backhaul
                                                            |
                                             base storage and processing

Independent timing reference -> hardware timing observation -> sample/time map
```

The ADC clock determines sampling. Wi-Fi service, packet arrival, and storage writes must never determine sample timing. Use data-ready-driven SPI acquisition with DMA and, where beneficial, PIO assistance. Keep the time-critical acquisition path independent of blocking network/storage operations.

Use preallocated buffers and explicit ownership between acquisition and networking. Allocate PIO state machines, DMA channels, and interrupt handlers through SDK mechanisms; account for the resources used by the wireless driver. A second CPU core is useful but does not, by itself, guarantee lossless acquisition.

## 4. Data rates, buffers, and storage

Payload rate is channels x samples/second x bytes/sample. Decimal kB/GB and binary KiB are distinguished below.

| Configuration | Packed 24-bit payload | 32-bit-word payload |
|---|---:|---:|
| One axis, 25 kSPS | 75 kB/s = 0.60 Mbit/s | 100 kB/s = 0.80 Mbit/s |
| One triaxial node | 225 kB/s = 1.80 Mbit/s | 300 kB/s = 2.40 Mbit/s |
| 16 single-axis nodes | 9.60 Mbit/s | 12.80 Mbit/s |
| 16 triaxial nodes | 28.80 Mbit/s | 38.40 Mbit/s |

These exclude packet headers, acknowledgments, retransmissions, and recovery uploads. An ADC CRC byte need not be transmitted as the fourth sample byte: firmware can validate it and pack the 24-bit code while reporting errors separately.

The Pico 2 W has 520 kB SRAM [S1]. An illustrative 256 KiB acquisition buffer holds 3.50 s of packed single-axis data or 1.17 s of packed triaxial data; triaxial 32-bit storage reduces that to 0.87 s. Allocating this much RAM must be checked against actual networking, stack, and processing usage.

Long outages require external storage. Continuous packed recording uses 6.48 GB/day per axis, or 19.44 GB/day per triaxial node. Sixteen triaxial nodes generate approximately 311 GB/day before metadata. Onboard program flash is not a substitute for sustained outage recording.

For initial networking, use TCP with an acquisition buffer and explicit sample counters. TCP cannot prevent ADC data loss when the application queue overflows. For bounded live latency, UDP with sequence numbers and separate recovery of recorded blocks is an alternative.

Every block should identify the node, axes, boot/session, first sample counter, sample count, configuration/gain, timing model/version, timing uncertainty, and acquisition/overflow status. Mark gaps explicitly. Batch samples efficiently; avoid one network transaction per sample. Choose packet sizes to avoid unnecessary IP fragmentation.

Retain raw data during development. Later, spectra/features plus triggered waveform upload could reduce fleet traffic, but that choice depends on validated detection requirements. Do not assume noise-like raw data will compress substantially.

## 5. SPI capacity and triaxial synchronization

The RP2350 provides two hardware SPI controllers. The standard Pico wireless driver communicates with CYW43439 through **PIO-based SPI**, using a state machine and DMA resources rather than occupying SPI0 or SPI1 [S3]. Consequently, three ADCs do not require three independent hardware SPI ports.

### Preferred arrangement: ADC daisy chain

ADS127L11 supports daisy chaining [S2, S4]. Three converters sharing an analog ground/power domain can expose one serial data path and common chip select across the isolation barrier. Each converter samples simultaneously; the results are subsequently shifted out serially.

With 24 data bits and an 8-bit CRC per converter:

```text
3 converters x 32 bits x 25,000 frames/s = 2.4 Mbit/s on the ADC link
At 8 MHz SPI: 96 bits / 8 MHz = 12 us per triaxial frame
Available sample period: 1 / 25 kHz = 40 us
```

The 8 MHz value is an illustrative engineering starting point, not a verified interface setting. Check clock polarity/phase, ADC output timing, isolator round-trip delay, board skew, and chip-select margins. Use the actual frame format and worst-case timing in the final budget.

All three converters need a shared conversion clock and a START signal aligned to that clock. Three independent oscillators plus a common software command are insufficient. TI provides synchronization and multi-converter design guidance [S4]. Match filter configurations and calibrate analog gain/phase differences as well.

### Alternative: shared SPI with separate chip selects

This gives independent register access and fault handling but requires more isolation/control paths. Verify that inactive data-return paths are genuinely high impedance. Do not tie together outputs from three ordinary digital isolators merely because ADC chip selects are separate: the isolator outputs can remain actively driven.

If the three axis boards retain separate isolated domains, revisit the topology rather than assuming they can be chained directly. A common triaxial analog domain with one boundary to the MCU is the simplest daisy-chain implementation.

### Tentative resource allocation

| Resource | Intended use |
|---|---|
| SPI0 | Isolated three-ADC chain |
| SPI1 | Timing radio and/or storage with controlled arbitration |
| PIO | Wireless driver, acquisition/timing assistance as needed |
| GPIO | Data-ready, synchronized START/control, timing inputs and status |

Storage transactions must not delay collection of timing records. PIO-based storage or a different interface can be considered if sharing SPI1 becomes limiting. The existing ISO7762 six-channel concept is a starting point; channel direction, START/reset needs, and data-ready routing require a final pin-level audit.

## 6. GPS-independent timing

### Common time need not be UTC

The base oscillator can define an arbitrary network epoch. Relative synchronization can remain precise even when the entire network drifts from UTC [S5]. No GPS initialization, internet connection, or GPS-equipped master should be required for basic operation.

The processing algorithm determines the timing target:

| Processing | Timing implication |
|---|---|
| Independent detections and slowly varying spectra | Millisecond-scale association may suffice; validate against algorithm |
| Cross-node waveform/phase comparison | Microsecond-scale alignment may matter |
| Three axes within a node | Shared conversion clock and synchronized start |

At 5 kHz, 1 us corresponds to 1.8 degrees and 10 us to 18 degrees of phase. Provision for approximately 1 us relative timing if coherent processing is a likely future requirement, but do not claim that performance before testing. This is a phase-alignment example, not evidence that ELF propagation-delay localization is practical.

### Options

| Method | Usefulness | Limitations |
|---|---|---|
| Two-way messages over existing Wi-Fi | Initial event association; estimate offset and drift from repeated exchanges | Software timestamps include queue, driver, and radio variability; not a demonstrated microsecond solution |
| Dedicated sub-GHz beacon | Promising coverage over the intended area; hardware sync-event observation | Receiver delay/jitter must be characterized; a sync GPIO alone does not guarantee accuracy |
| UWB hardware timestamps | Strong precision-timing candidate | Site range/obstructions require validation; may need several anchors |
| Hardware-timestamped Ethernet/PTP | Defensible precision approach with installed infrastructure | Requires capable endpoint/network hardware and a connection to ADC timing |
| Dedicated differential/fiber timing | Direct clock/pulse distribution | Cabling, propagation calibration, and installation effort |

TI documents sub-GHz synchronization-related hardware signals [S6]; Qorvo/Decawave describes wireless clock synchronization using radio timestamps [S7]. These establish technical approaches, not achieved detector-system accuracy. Ordinary Wi-Fi association, NTP, or an Ethernet adapter should not be treated as hardware sample synchronization.

### Recommended timing structure

1. A stable base oscillator establishes network time.
2. Each node samples continuously, with one local conversion clock for all axes.
3. Hardware timing events are related to ADC sample count and fractional sample phase, not only to software time.
4. Estimate a local mapping `base_time = offset + scale x local_time`, updating both offset and frequency error.
5. Resample at the base onto a common timeline when required, or discipline the conversion clock if continuous hardware alignment is necessary.

Account for ADC digital-filter latency and analog phase calibration. Avoid periodic ADC restarts as a synchronization method: they create gaps and filter settling. An asynchronous beacon captured only by a firmware interrupt retains interrupt-latency uncertainty; use deterministic capture hardware/PIO or the radio's timestamps with a verified mapping to the acquisition clock.

Radio propagation is about 1 us over 300 m. Distance differences and receiver delays therefore matter at a 1 us target; use surveyed geometry/calibration or two-way exchanges. A residual relative frequency error of 1 ppm adds 1 us uncertainty per second of holdover. A 20 ppm relative error adds 20 us/s. Oscillator temperature behavior, aging, and measurement noise must be included in the actual holdover budget.

Record timing uncertainty and mark data when it exceeds the processing limit. GPS denial does not necessarily disable local communications, but loss of all timing/data links requires local recording and oscillator holdover. Initialization, timing-anchor loss, reacquisition, and any master change need explicit handling.

## 7. Wireless coverage and fleet scaling

A centrally placed access point is approximately 212 m from the corners of a 300 m square and 354 m from the corners of a 500 m square. Base placement at an edge or corner increases the longest path.

For an illustrative 300 m, 2.4 GHz path, free-space loss is approximately 89.6 dB. The midpoint first Fresnel-zone radius is about 3.1 m; 60% clearance is about 1.8 m. Thus, line of sight near ground level is not equivalent to a clear radio path. Vegetation, ground reflections, terrain, enclosures, and antenna orientation can dominate. See outdoor planning guidance [S8].

Use elevated outdoor access point(s) and measure the node-to-base uplink at every intended location. One base computer can aggregate several sectors/access points connected by Ethernet, fiber, or dedicated backhaul. The Pico's four-client SoftAP mode is not the proposed fleet access point [S1].

Directional antennas at stationary nodes aimed toward a central omni/sector antenna are a reasonable candidate. Antenna gain helps both transmit and receive link budgets, but narrows coverage; high-gain omnis have narrower elevation patterns. Keep RF cables short and include their loss. Assess the actual antenna/power configuration against applicable radio requirements during detailed design.

Measure all nodes transmitting together. Slow edge links, retries, and nodes unable to hear one another can consume shared airtime. As an initial planning margin, target aggregate traffic below roughly half the measured application throughput in representative conditions; this is a heuristic, not a guaranteed capacity rule. Do not use advertised PHY rates as a sustained payload budget.

## 8. Stock board and custom derivative

### Preferred development path

1. Use stock Pico 2 W hardware for acquisition, buffering, protocol, and near-range streaming development.
2. Validate the required triaxial payload and sensor noise while the radio is active.
3. Develop a compatible RP2350/CYW43439 board with an external antenna interface for field deployment.

Preserve wireless host pins and the SDK driver arrangement where practical. Replace the onboard antenna section with a correctly matched 50-ohm feed and U.FL connector, potentially using a short pigtail to an enclosure-mounted antenna. Do not assume the printed-antenna matching network transfers unchanged to the connector. Review RF stackup, matching, return path, decoupling, and emissions, then measure the assembled board.

The board can also incorporate storage, isolated ADC connections, timing capture inputs, and suitable power distribution while retaining PIO and the Pico programming environment.

### Design reuse qualification

Raspberry Pi's website grants broad design-reuse permission, while the Pico 2 W datasheet section 1.1 explicitly excludes the patented Abracon/Proant Niche antenna and directs users to obtain licensing information [S1, S9]. Treat that specific antenna exception as applicable until clarified. Replacing the onboard antenna with an independently designed external-antenna interface avoids relying on copying that antenna geometry.

Verify the actual downloadable CAD package, license notices, radio/firmware terms, and manufacturing parts before implementation. This discussion checked published documentation; it did not import or audit the complete CAD sources. Product radio approvals do not automatically transfer to a modified board/antenna combination.

## 9. Alternatives retained for comparison

| Option | Reason to consider | Reason not selected as baseline |
|---|---|---|
| Stock Pico 2 W | Fast prototype; familiar SDK/PIO | No stock external antenna connector |
| Custom RP2350/CYW43439 derivative | Preserves firmware while improving antenna flexibility | RF design and validation work |
| ESP32-S3-WROOM-1U | Integrated Wi-Fi with external antenna connector [S10] | User prefers Pico programming/documentation and requires PIO capability |
| Pico + network module | Can preserve MCU while offloading networking | Host API, sustained throughput, and driver integration need checking |
| Pico + Ethernet + outdoor Wi-Fi bridge | Flexible radio/antenna placement and independent radio configuration | Added power, hardware, and enclosure cost |
| MCU + Wi-Fi HaLow | Sub-GHz option worth testing over the site | New module/AP and driver work; long-range modes can have inadequate aggregate throughput |
| Local processing + LoRa | Low-rate detection/health/feature telemetry | Cannot carry the assumed continuous raw stream |
| Wired/fiber network | Useful fixed-site reference or deployment option | Installation effort |

HaLow is not supported by the Pico's onboard radio. It requires compatible radios at both ends. Morse Micro lists a robust 1 MHz mode near 0.3 Mbit/s, below even one 0.6 Mbit/s raw axis; select modes by measured throughput at distance, not maximum-range demonstrations [S11]. The SX1262's advertised maximum LoRa rate is 62.5 kbit/s, also below one raw axis [S12].

No specific third-party external-antenna RP2350 board or network module has been selected or validated.

## 10. Existing projects and reusable software

| Reference | Verified relevance | Evidence limitation |
|---|---|---|
| BirdNET-Wifi-Pico-mic [S13] | Documents Pico 2 W I2S microphone acquisition and TCP streaming; 16 kSPS, 16-bit mono, approximately 32 kB/s | Much lower payload than our 225 kB/s; author-reported behavior, not independently reproduced |
| Raspberry Pi pico-examples [S14] | Official ADC/DMA, TCP, UDP, and iperf building blocks | Not an integrated precision triaxial DAQ |
| CrashOverride85/PicoAdcDmaWifi [S15] | Older Pico W ADC/DMA and wireless coexistence code | No verified Pico 2 W or complete streaming performance result found |

No ready-made project matching three ADS127L11 channels at 25 kSPS with precision cross-node timing was identified. The search is not exhaustive. Source repositories were inspected as references; none was built or run for this analysis.

Use the official C/C++ SDK examples as the primary firmware foundation. The BirdNET implementation is useful operational context but includes sample-modifying noise suppression and reports configuration-dependent wireless behavior. Do not transplant its blanking/clamping into scientific acquisition or interpret its throughput as the Pico hardware ceiling. Preserve original samples and explicit error flags.

## 11. Noise, power, and isolation

Digital isolation does not prevent radiated magnetic coupling or all capacitive/RF coupling. Radio transmit-current bursts, converter/MCU clocks, storage writes, and switching regulators can affect the measured band. A high-gain antenna does not eliminate local conducted interference.

Keep the radio and power converter away from the coil/JFET input, minimize current-loop area, and maintain the intended isolated power domains. A shared battery-negative connection across the boundary bypasses galvanic isolation. Isolation components do not supply isolated power. Do not extend raw SPI over a long cable to separate the radio; use a suitable differential/network interface if long separation is needed.

Compare spectra for radio off, associated idle, continuous streaming, weak-link retries, reconnects, and storage writes. Include batching patterns: periodic packet activity can produce structured interference. Any shielding must accommodate antenna clearance and the magnetic sensor's operating principle.

Battery life must be calculated from measured combined analog, ADC, MCU, radio, timing, and storage power. Continuous acquisition limits sleep opportunities. No battery endurance has been established.

## 12. Validation plan and decision gates

1. **Acquisition integrity:** generate/obtain known three-channel data, verify counters and CRC, and sustain 25 kSPS per axis through concurrent networking/storage. Test reset and overflow behavior.
2. **Payload benchmark:** sustain 225 kB/s packed or 300 kB/s in 32-bit form. Begin with a counter-pattern generator, then repeat with real ADC acquisition active. Network-only iperf results are insufficient.
3. **Outage behavior:** disconnect/reconnect the access point, exercise local recording, identify gaps, and recover data without stalling live acquisition.
4. **Fleet test:** repeat with the intended node count, representative antennas/heights, farthest links, vegetation, and simultaneous upload/recovery traffic.
5. **Noise test:** measure input-referred spectra and spurs across radio/storage modes against the quiet reference setup.
6. **Timing test:** operate without GPS/internet from startup; measure sample alignment, drift, temperature effects, propagation corrections, holdover, and recovery. Test with timing packets competing against full data traffic.
7. **Power test:** measure average/peak current, supply disturbances, and endurance with all three axes running.
8. **Custom-board gate:** proceed after the stock-board data path is credible; finalize RF connector/matching, power/isolation, resource allocation, and timing interface before layout.

Immediate engineering priorities are the triaxial SPI/isolation pin map, a C/C++ sustained-streaming benchmark, and a timing error budget tied to the intended detection/fusion algorithm. The external antenna connector is an accepted direction for an eventual custom derivative; it does not, by itself, establish site coverage.

## 13. Laptop base-station software

The laptop must support multiple sensor units, record incoming raw data for later analysis, and provide live processing for functional evaluation. The companion [Base-station software plan](Base_Station_Software_Plan.md) specifies the application architecture, protocol, recording format, user interface, processing, replay, and implementation stages.

The proposed implementation uses a C#/.NET acquisition/recording process and WPF interface, with an optional separate Python analysis worker. The authoritative acquisition files are checksummed binary logs; Python provides direct readers and HDF5 export. The [staged implementation plan](Software_Implementation_Plan.md) defines deliverables and acceptance gates, beginning with simulators and two Pico 2 W synthetic-data units. Recording receives priority over live plots. Unit identity, reconnects, gaps, calibration, and timing quality remain explicit. Live and replay sources use the same processing functions for reproducibility. This is a plan, not an implemented application.

## Sources

Sources checked during the discussion on 12 September 2026. Specifications and project claims are distinguished above from calculations and recommendations.

- **S1:** [Pico 2 W datasheet](https://datasheets.raspberrypi.com/picow/pico-2-w-datasheet.pdf). User-supplied copy: [parts/RP-008304-DS-3-pico-2-w-datasheet.pdf](../Coil_Design/parts/RP-008304-DS-3-pico-2-w-datasheet.pdf).
- **S2:** [TI ADS127L11 datasheet](https://www.ti.com/lit/ds/symlink/ads127l11.pdf). Local copy: [parts/ads127l11.pdf](../Coil_Design/parts/ads127l11.pdf).
- **S3:** [Pico SDK CYW43 PIO-SPI driver](https://github.com/raspberrypi/pico-sdk/blob/master/src/rp2_common/pico_cyw43_driver/cyw43_bus_pio_spi.c).
- **S4:** [TI multi-ADC simultaneous-sampling design guide, SBAA520](https://www.ti.com/lit/pdf/SBAA520).
- **S5:** [NIST: UTC source and IEEE 1588](https://www.nist.gov/el/intelligent-systems-division-73500/utc-source-1588); [IEEE 1588 systems](https://www.nist.gov/el/intelligent-systems-division-73500/ieee-1588-systems).
- **S6:** [TI CC120X user guide](https://www.ti.com/lit/pdf/swru346).
- **S7:** [Qorvo/Decawave: Comparison of wireless clock synchronization algorithms](https://www.qorvo.com/resources/d/comparison-of-wireless-clock-synchronization-algorithms-for-indoor-location-systems).
- **S8:** [Cisco outdoor site preparation and Fresnel-zone planning](https://www.cisco.com/c/en/us/td/docs/wireless/technology/mesh/8-0/design/guide/mesh80/m_site-preparation-and-planning.html).
- **S9:** [Raspberry Pi board documentation and design-reuse statements](https://www.raspberrypi.com/documentation/microcontrollers/pico-series.html).
- **S10:** [Espressif ESP32-S3-WROOM-1/1U datasheet](https://www.espressif.com/sites/default/files/documentation/esp32-s3-wroom-1_wroom-1u_datasheet_en.pdf).
- **S11:** [Morse Micro HaLow chips and modulation/rate table](https://wp.morsemicro.com/chips/).
- **S12:** [Semtech SX1262 specifications](https://www.semtech.com/products/wireless-rf/lora-connect/sx1262).
- **S13:** [BirdNET-Wifi-Pico-mic](https://github.com/pR13S7/BirdNET-Wifi-Pico-mic).
- **S14:** [Official pico-examples](https://github.com/raspberrypi/pico-examples).
- **S15:** [PicoAdcDmaWifi](https://github.com/CrashOverride85/PicoAdcDmaWifi/).
