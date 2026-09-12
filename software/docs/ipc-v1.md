# Laptop IPC v1

Use separate loopback TCP connections for control/status and bulk analysis subscriptions. Default development endpoint 127.0.0.1:45101, configurable; IPv6 may be added explicitly. Sensor listener is separate. Stage 3 implements authenticated sockets, selected-unit subscriptions, worker results and analysis settings. See [operation and implemented subset](stage3-operation.md).

## Envelope

| Offset | Type | Meaning |
|---:|---|---|
| 0 | byte[4] | ASCII `ELI1` |
| 4 | u16 | version 1 |
| 6 | u16 | 1 CONTROL, 2 BLOCK, 3 RESULT |
| 8 | u32 | total inclusive bytes = payload + 28; 28..2,097,152 |
| 12 | u64 | request/correlation ID; unaligned by design, no padding |
| 20 | u32 | payload bytes |
| 24 | bytes | payload |
| total-4 | u32 | CRC-32/ISO-HDLC of all prior bytes |

Integers little-endian, CRC as in sensor protocol. Prefix validation bounds allocation before reading a payload. Close only the offending local connection on binary corruption. CONTROL and RESULT contain strict UTF-8 JSON objects with maximum depth 16, no duplicate keys or nonfinite literals. BLOCK contains one complete DATA ELD1 frame with int32 encoding 2. Rows are XYZ; payload data is NumPy-compatible little-endian `<i4` shaped (count,3). Explicitly copy/reference with ownership; no Python pickle.

## Control messages

Every CONTROL has `op` string and `request_id` decimal u64 string matching the envelope correlation. Nonzero requests are unique per connection. Responses echo the ID, include `ok` bool and `error` null/string. Unsolicited events/results use zero. Initial operation is `authenticate`, args contain protocol 1, client role (`gui`, `analysis`, `reader`) and per-launch token. Host rejects any unauthenticated other operation. Tokens stay out of logs; loopback-only binding and the token are a local access boundary, not field-radio security.

Operations after authentication:

| Operation | Arguments | Result |
|---|---|---|
| status | none | unit and recorder state snapshot |
| start_recording | output directory, selected unit IDs, label | actual session UUID/path/state or error |
| stop_recording | recording session UUID | actual drain/close state; completion may be a later event |
| node_command | target unit + acquisition session, sensor op/args | applied per-node response; no group atomicity |
| subscribe | units, stream `samples` or `health`, history_seconds 0..2 | subscription ID and byte limits |
| unsubscribe | subscription ID | acknowledged removal |
| read_range | recording session locator, unit/acquisition IDs, first/count decimal strings | bounded stream of BLOCKs plus explicit validity/gap metadata |

Implement only stage-appropriate operations; unsupported returns an explicit error. `read_range` on a separate bulk connection cannot block control. Stage 0 tests framing rather than handler behavior. Output-directory validation is a service responsibility, not delegated to the Python analysis worker.

## Result metadata and correlation

RESULT requires `result` string, `unit` and `acquisition_session` UUID hex, `first_sample` decimal u64 string, `count` u32, `valid` bool, `algorithm` versioned string, `parameters` object, `values` object. Spectra add frequency-bin definition, units, window/averaging and timing/calibration IDs. Arrays in RESULT JSON are bounded reduced results, not raw sample transport. Sample/timing IDs identify coverage even if plot delivery is delayed. Invalid windows return validity/reason instead of misleading numerical output. A subscription/status event names skipped samples/windows explicitly.

Python failure or slow consumption cannot block acquisition, recording or basic C# health checks. A bounded recent-history subscription may evict old work and must report that fact. Recorder shutdown does not depend on Python. A future shared-memory transport can use a new capability while retaining message semantics; it is not part of v1.

## Stage 3 settings extension

CONTROL `analysis_settings` returns `details` containing `revision` and a settings object (null until configured). CONTROL `configure_analysis` takes `args.settings` with all fields: `rate`, `df`, `dalpha`, `max_frequency`, `max_alpha`, `hop_fraction`, and integer `pair_batch`. Successful reply reports the requested revision; application remains asynchronous. Worker RESULT `parameters.revision` and actual resolutions identify the applied plan. Rejected plans generate an invalid RESULT naming `values.rejected_revision`; the previous plan continues. A client should compare desired and applied revisions and result age. Settings currently apply globally.

Subscription CONTROL events have correlation zero: `subscription_status.details` contains queue bounds and cumulative skipped rows; `context` carries unit/session and Base64 metadata frames (not sample arrays), followed by a binary BLOCK. Stream sockets are dedicated and unsubscribed by closing them. Status and result traffic use a separate authenticated connection. Nonzero control IDs must increase monotonically on each connection. Responses carry operation-specific data in `details`.
