# Sensor protocol v1

Status: Stage 0 baseline, 12 September 2026. Binary layouts are implemented and cross-tested in C# and Python. Connection state, command dispatch, metadata-schema enforcement and incremental socket buffering are Stage 1 implementation work. No device/server connection exists yet.

## Transport and framing

Each sensor initiates a TCP connection to the laptop sensor listener. Default development port: 45100, configurable. No cloud/time-server connection is needed. One connection per unit carries frames in both directions; each sender has its own frame sequence. First node frame must be HELLO. The laptop responds with ACK acknowledging HELLO; its initial session sequence is 0 and reconnects continue that sequence. Only then may data/status be sent. Laptop control frames copy the target unit and current acquisition-session identities.

All integers in binary headers are unsigned little-endian except sample payloads, which are signed two's complement. No native struct padding. UUID fields are **16 RFC/network-order bytes**, not the default mixed-endian `.NET Guid.ToByteArray()` representation. The display UUID `00112233-4455-6677-8899-aabbccddeeff` occupies `00 11 22 33 44 55 66 77 88 99 aa bb cc dd ee ff`. JSON identities use exactly 32 lowercase hex digits without separators.

Read at least 12 bytes into a small fixed prefix buffer. Verify magic/version/kind and bound total length before allocating the rest. Then read precisely that total length. A TCP read may yield part of a frame or several frames; consume complete frames in order and retain the tail. No scanning for magic within sample bytes. Invalid binary framing/checksum closes only the offending connection and produces a diagnostic. A wrong semantic command yields a negative ACK when the frame itself is valid.

## Fixed 96-byte header

| Offset | Bytes | Field | Rule |
|---:|---:|---|---|
| 0 | 4 | magic | ASCII `ELD1` |
| 4 | 2 | version | 1 |
| 6 | 2 | kind | 1 HELLO, 2 DATA, 3 STATUS, 4 COMMAND, 5 ACK, 6 TIMING, 7 GAP |
| 8 | 4 | total length | 100 + payload bytes; inclusive header/payload/CRC; 100..1,048,576 |
| 12 | 4 | quality flags | DATA only; bit 0 ADC error, bit 1 producer overflow, bit 2 settling, bit 3 clipping; others zero |
| 16 | 16 | unit ID | persistent, nonzero |
| 32 | 16 | acquisition-session ID | nonzero, fresh after reboot or explicit acquisition restart |
| 48 | 8 | sender frame sequence | monotonic within direction/session; HELLO or corresponding ACK may be first zero |
| 56 | 8 | first sample counter | sample row, not byte or axis index; for control/metadata, effective/observed counter |
| 64 | 4 | sample count | DATA: 1..4096; otherwise zero |
| 68 | 4 | nominal rate Hz | DATA: 1..1,000,000, initial 25,000; other frames may report current rate or zero |
| 72 | 4 | configuration ID | DATA: nonzero; references acknowledged configuration |
| 76 | 4 | calibration ID | zero = no calibration |
| 80 | 4 | timing model ID | zero = unsynchronized |
| 84 | 2 | channels | DATA: exactly 3; otherwise zero |
| 86 | 2 | encoding | 0 UTF-8 JSON; 1 packed int24; 2 int32 sign-extended from 24 bits |
| 88 | 4 | payload length | total length minus 100 |
| 92 | 4 | reserved | zero |
| 96 | variable | payload | exactly payload length bytes |
| total-4 | 4 | CRC | CRC-32/ISO-HDLC of all preceding bytes, stored little-endian |

CRC parameters: width 32, normal polynomial `0x04C11DB7`, reflected implementation polynomial `0xEDB88320`, init `0xFFFFFFFF`, refin/refout true, xorout `0xFFFFFFFF`. Check vector ASCII `123456789` = `0xCBF43926`. No authentication is implied by this checksum.

### DATA payload

Rows are `[X0,Y0,Z0,X1,Y1,Z1,...]`. Payload size = count x 3 x (3 or 4). Int24 limits are -8,388,608..8,388,607. Encoding 2 MUST contain values within those limits, not arbitrary int32. A row increments the counter once. For any frame, `first + count <= UINT64_MAX`; the exclusive end must be representable. Counters/sequences do not wrap: create a new acquisition session before exhausting the range.

Initial 256-row frame: 2,304 payload bytes packed, 2,404 total bytes, 10.24 ms at 25 kSPS. Int32 version is 3,072 payload/3,172 total. TCP segmentation is independent of frame boundaries. Rate is nominal, not a precision timestamp. The sender continues its sample schedule during network stalls and reports discarded intervals; do not slow acquisition to hide missing samples.

Flags apply to the whole block and conservatively invalidate it for processing as appropriate. Per-sample quality masks are not part of v1. Calibration/timing zero remains valid for raw acquisition, but never permits a calibrated/coherent claim.

## JSON payload grammar

Kinds other than DATA use encoding 0 with channels/count/quality flags zero. Strict UTF-8; JSON object root; maximum nesting 16; no duplicate object keys, NaN or Infinity. Header limits still apply. JSON u64 counters/times/request IDs are decimal **strings** to avoid 53-bit-number interoperability loss; u32 values use JSON integers. IDs have the hex form above. No binary sample arrays in sensor JSON. Mandatory keys below are case sensitive; unknown top-level keys are reserved for compatible extensions and must not change existing meaning. Receivers must validate mandatory types before use. Configuration/calibration/timing IDs are scoped to unit + acquisition session and immutable once used.

| Kind | Mandatory payload keys and meanings |
|---|---|
| HELLO | `firmware` string; `protocol` integer 1; `synthetic` bool; `axes` exactly `["X","Y","Z"]`; `encodings` nonempty subset of [1,2]; `capabilities` array of supported operation strings; optional `label` string. Header carries current nominal rate/config ID. |
| STATUS | `state`: idle/armed/sampling; `next_sample` decimal u64 string; `buffer_rows` u32; `dropped_rows` decimal u64 string. Optional battery/temperature/RSSI only when measured. |
| COMMAND | `request_id` decimal u64 string, `op` string, `args` object. Defined operations below. |
| ACK | `request_id` decimal u64 string; `ok` bool; `state` idle/armed/sampling; `effective_sample` decimal u64 string; `error` null on success or string code on failure; `details` object. HELLO acknowledgment uses request ID `0`, reserved from command use. |
| TIMING | `model_id` positive u32 equal to header timing ID; `reference_epoch` UUID hex; `anchor_sample` decimal u64 string; `anchor_time_ns` signed decimal i64 string; `period_num_ns` positive decimal u64 string; `period_den` positive u32; `uncertainty_ns` decimal u64 string; `valid_from_sample`, `valid_to_sample_exclusive` decimal u64 strings defining a nonempty interval. |
| GAP | `first_sample`, `count` decimal u64 strings; count > 0 and representable exclusive end; `reason` string; `recoverable` bool. Header first counter equals payload first counter. |

TIMING mapping: `t_ns(n) = anchor_time_ns + (n-anchor_sample)*period_num_ns/period_den` within the valid interval. Time is local network-epoch time, not asserted UTC. The anchor refers to effective sample time with known ADC digital delay accounted for; capture/filter-delay assumptions must be supplied in the referenced configuration. Unknown delay means timing remains unqualified. Use integer/rational arithmetic until conversion for analysis. Refined models get new IDs and never silently overwrite earlier observations. Metadata must arrive/persist before data relying on it is published as qualified.

### Commands and acknowledgment semantics

- `status`: empty args, no state change; details contain current STATUS fields.
- `arm`: args `config` object (`id` positive u32, `sample_rate_hz` u32, `encoding` 1/2, `synthetic` bool); allowed from idle; ACK details echo applied config. Raw ADC-specific configuration is a future capability, not an implicit field here.
- `start`: empty args; armed -> sampling; ACK gives actual first produced counter. This is not a synchronized group trigger.
- `stop`: empty args; sampling/armed -> idle after completion/discard accounting for the current block; ACK effective counter is exclusive end. Buffered old-session data may still arrive with original identities.
- `recover`: args `first_sample`, `count` decimal u64 strings; return accepted/unsupported/unavailable. Historical retransmissions retain original first counter and content but have a new sender frame sequence. Their data must be deduplicated by sample range, not frame sequence.
- `commit_through`: args `next_sample` decimal u64 string; this declares that samples below the exclusive bound are durably stored and may be released by the node. It is supported only after Stage 2 durability validation. No out-of-order ACK advances past a missing range. The firmware must advertise this capability before it is used.

Request IDs are nonzero and unique within the acquisition session; retransmission of a request repeats the same ID and identical payload. Cache completed responses during the session to avoid repeating side effects. A conflicting repeated ID is an error. ACKs describe actual applied state, never merely socket receipt. Valid error codes: unsupported, invalid_args, invalid_state, unavailable, internal_error. Commands and statuses are not implemented by Stage 0 codecs; dispatch is Stage 1.

## Reconnect and validity

A network reconnect retains acquisition-session ID and sample counter; HELLO can repeat without resetting sequence. A restart creates a fresh session, counter starts at zero, and metadata must be declared again. Duplicate unit IDs from concurrent endpoints are rejected/quarantined, not combined. Data sequence gaps are transport diagnostics, while sample counter ranges define measurement loss. Late recovery never rewinds live plots. Conflicting duplicate ranges are surfaced as errors, preserving original evidence.

No local-buffer retention, precision timing, durable ACK, or authenticated/encrypted field transport implementation is claimed yet. The synthetic prototype uses an isolated test LAN; deployment transport authentication is a separate integration decision.


## Stage 1 implementation profile

The binary layout and Stage 0 fixtures are unchanged. The implemented prototype validates mandatory metadata and command state. New command request IDs increase monotonically; 256 completed replies are cached, and older expired IDs are rejected without executing again. Receiver deduplication retains 1,024 recent ranges; older duplicates outside a known missing interval are rejected as unverifiable. These limits are explicit until the Stage 2 durable index supports extended history.

HELLO extensions used by the simulator: `mode` and `label` strings, `seed` u32, `preferred_encoding` 1/2, `state` idle/armed/sampling and `next_sample` decimal u64. Mode/seed/synthetic identity cannot change on a same-session reconnect. An idle STATUS with zero buffered rows declares its final produced counter, enabling terminal-loss accounting. Stop/arm/start resumes the same prototype session; reboot produces the fresh session required for a reset sample counter. See [Stage 1 operation](stage1-operation.md) for supported capabilities and resource bounds.
