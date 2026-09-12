# ELF acquisition log v1

Stage 0 defines/implements byte envelopes, not an active writer, scanner or durability guarantee. The Stage 2 implementation must enforce the lifecycle below.

All integers little-endian. UUID bytes use RFC order, as in [sensor protocol](protocol-v1.md). CRC is CRC-32/ISO-HDLC over every byte before the trailing CRC of that header/record; check `123456789` = `CBF43926`.

## File header: 36 bytes

| Offset | Type | Value |
|---:|---|---|
| 0 | byte[8] | ASCII `ELFLOG1` followed by NUL |
| 8 | u16 | version 1 |
| 10 | u16 | flags 0 |
| 12 | u32 | header size 36 |
| 16 | byte[16] | nonzero laptop recording-session UUID |
| 32 | u32 | CRC of bytes 0..31 |

The recording-session UUID groups all files/units in a laptop session; it differs from each node's acquisition-session UUID. All configuration/timing definitions needed for independent reading of a rotated segment must be repeated at its start.

## Record: 16-byte prefix, payload, 4-byte CRC

| Offset | Type | Value |
|---:|---|---|
| 0 | byte[4] | ASCII `ELR1` |
| 4 | u16 | version 1 |
| 6 | u16 | kind: 1 FRAME, 2 EVENT, 3 COMMIT, 4 FOOTER |
| 8 | u32 | total inclusive length, 20..2,097,152 |
| 12 | u32 | payload length = total - 20 |
| 16 | bytes | payload |
| total-4 | u32 | CRC of prefix + payload |

FRAME embeds a complete valid ELD1 frame including its CRC. DATA is normalized to encoding 2/int32, with flags, IDs, counters and values unchanged; update lengths and the embedded CRC. This is lossless code-value preservation, not byte-for-byte preservation of the original network packet. Non-DATA frames retain their JSON bytes. Sender sequence remains original; records may arrive out of sample-time order.

Other record payloads follow the strict JSON grammar from the sensor protocol:

- EVENT: `event` string and `monotonic_ns` decimal u64 string required; optional unit/session/counter context and structured `details`. Examples include recording_started, node_disconnected, config_changed, recording_failed, operator_marker. Times are laptop monotonic observations, not synchronized sample times.
- COMMIT: `through_offset` decimal u64 byte offset of the exclusive end of the preceding validated record (36 allowed); `unit`, `acquisition_session` UUID hex; `next_sample` decimal u64 exclusive highest contiguous durable sample. The commit references only already written bytes. Gaps prevent contiguous advancement. Multiple acquisition sessions/units need separate commit records. Semantic validity requires scanning earlier records, not just validating this envelope.
- FOOTER: `status` complete/incomplete; `records` decimal u64 count of preceding records excluding footer; `sample_rows` decimal u64 count of unique DATA sample rows; optional integrity/diagnostic details. Footer is last, and clean close emits exactly one. Complete does not mean gap-free: known gaps/recovered ranges are preserved in the summary.

All u64 JSON values are decimal strings. Unknown metadata keys are retained for compatible extensions. Duplicate JSON keys/nonfinite literals are rejected. Optional indices are rebuildable and never authoritative.

## Commit and close ordering

1. Append metadata and data records under one writer owner.
2. Append a COMMIT covering a validated prefix and consistent contiguous ranges.
3. Perform the selected OS flush-to-disk operation and wait for successful completion.
4. Only then send commit_through to a node that advertises support.

A parsed COMMIT after restart is a structural marker, not proof of physical power-loss protection. Validate both the prefix and actual storage semantics. There is no data release based on a TCP ACK. Closing requires drain, footer, flush and final status update. Failed close leaves an incomplete session. Raw recording never waits for Python export.

## Recovery and corruption rules

An incomplete header is an invalid/incomplete file. Incomplete final record prefix/body is a truncated tail; retain originals and report its byte offset. A checksum error is corruption, even at the tail; never call it committed merely because a footer exists. Interior invalid lengths/magic/CRC halt the scan with an error, not a magic-byte search through payload. An explicit salvage tool may later create a new artifact, but ordinary verification is read-only. A missing footer is incomplete. Data identity is (unit, acquisition-session, sample range), not file position. Exact duplicate ranges may be deduplicated by readers; conflicting bytes/metadata require an explicit conflict result.

Golden header and record fixtures are independent envelope tests, **not a complete ordered/durable recording session**. A reader/writer integration test belongs to Stage 2.

## Stage 2 implementation profile

The binary envelopes and original fixtures are unchanged. Rotated files start with a recording_started EVENT whose details include unit/acquisition-session identity, segment number, prior_extent, prior_ranges (first/end decimal strings), and compiled software build. Definition frames are repeated before DATA. Directory verification validates the declared prior coverage against preceding segments; standalone segment inspection labels it as prior context. COMMIT through_offset equals the immediately preceding record end. FOOTER sample_rows counts unique rows newly written in that segment. Read [Stage 2 operation](stage2-operation.md) for limits and recovery behavior.
