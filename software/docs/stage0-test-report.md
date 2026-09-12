# Stage 0 test report

Date: 12 September 2026  
Result: **PASS for Stage 0 contracts and solution skeleton.**

## Environment

- Windows x64 workspace.
- Official .NET SDK 10.0.401 installed under `software/.tools/dotnet`; version pinned by `global.json`.
- SDK archive verified against the SHA-512 value from Microsoft's release metadata. Bootstrap script records URL and expected digest.
- CPython 3.12.14 from the available workspace runtime.
- No third-party NuGet or Python packages required by Stage 0. Locked NuGet restore succeeds with package sources disabled.

## Reproduction

From the software directory, run:

```powershell
.\scripts\test.ps1 -Python 'C:\path\to\python.exe'
```

The local SDK is selected automatically if present. See [README](../README.md) for setup and standalone commands.

## Observed results

| Check | Result |
|---|---|
| Locked restore of eight projects | Passed |
| Release solution build including WPF project boundary | Passed; zero warnings, zero errors |
| C# executable contract harness | 550 assertions passed |
| Python unittest suite | Eight test methods passed, including parameterized fixture and truncation cases |
| Shared fixture collection | 43 binary fixtures; manifest SHA-256 checked by both languages |
| All seven sensor message kinds | Valid envelopes round-trip byte-for-byte |
| Int24/int32 sign conversion and XYZ ordering | Matches expected edge values and normalized golden fixture |
| CRC independent implementation | C# polynomial implementation and Python zlib match fixtures and standard check vector |
| Corruption and bounds | Invalid CRC, lengths, reserved fields, kind/version, identity, channel/encoding/count, sample range and counter overflow rejected |
| JSON syntax | Duplicate keys, invalid UTF-8, non-object root and nonfinite literal rejected |
| Truncated envelopes | Every truncation of representative wire/log-header/log-record/IPC blocks rejected |

Fixture bytes were generated explicitly before testing; neither test suite regenerates expected data. Full C# and Python encoders independently reproduce valid fixture bytes. The fixture generator is Python, so the independent C# implementation and standard CRC check vector provide the separate implementation check.

## Scope boundaries

Stage 0 implements binary serialization/deserialization, field/sample bounds, strict JSON syntax, log/IPC envelope codecs, protocol documentation, and project boundaries. Mandatory per-message metadata validation, command dispatch, authentication handshake, incremental socket buffers, deduplication and commit lifecycle are specified but remain handler work for Stages 1/2. There is no running network receiver, durable writer, simulator, GUI or firmware yet.

No acquisition throughput, memory endurance, power-loss durability, RF performance or precise synchronization has been tested. Log record fixtures are standalone envelope examples, not a complete ordered recording session.

The workspace does not currently resolve as a usable Git repository (`git status` reported not a repository). Files are saved locally; no commit was made. A software-level `.gitignore` covers `.tools`, build outputs and Python caches for future source control.

## Next stage

Stage 1: implement protocol metadata validators/state machine, multi-unit simulator, and bounded network receiver. Use these fixtures as a compatibility gate and add fragmentation/coalescing, reconnect, command/replay identity and finite-buffer tests.
