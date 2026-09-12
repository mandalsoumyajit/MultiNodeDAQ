# Stage 4 test report

Date: 2026-09-12. Windows x64 development laptop; .NET 10.0.12. The approved GUI mockup preceded implementation.

## Verification

- Existing 550 protocol assertions, 64 acquisition/IPC assertions, 4,159 recording assertions and 12 Python tests pass.
- Stage 4 adds 31 assertions covering real WPF status/preview polling, six spectral views, bounded previews, age below 500 ms, clipping/gaps, authentication/session rejection, recording start/stop/restart, independent replay equality, CSV metadata export and drawing budgets.
- Actual desktop process exercised with sixteen 25 ksample/s XYZ simulator units. GUI forcibly terminated and restarted while host continued recording; optional Python killed and staleness observed. Valid settings applied, intentionally excessive FAM settings rejected, rejection retained despite subsequent old-revision valid results.
- Latest process run: 9,328,896 XYZ rows independently verified; zero missing rows and zero synthetic counter errors. Preview host-receive age median 8.60 ms, maximum 27.49 ms across 100 measurements. These are short local-process tests, not wireless range or sustained field qualification.
- Replay compares a 10,000-row seek window against the existing full verification reader. Sample values match exactly. Export retains the requested range and metadata/gap sidecar.
- Native WPF rendering was inspected from captured application content. The initial 32 x 64 SCF render benchmark at 800 x 220 pixels over 50 runs was 7.31 ms median, 20.24 ms maximum. Per-run reports and images remain in .artifacts/stage4.
- A self-contained Windows x64 package was produced. Packaging uses separate platform lock files so publishing does not invalidate the normal locked development restore.

## Scope and remaining qualification

Replay GUI currently offers waveforms, seeking/playback and CSV with metadata. Full offline spectral processing and HDF5 export use the existing Python API. Analysis worker launch covers the selected unit; additional workers can be launched separately. Spectral settings are global and worker-validated.

The closing dialog's background option is supported by independent process lifetime, verified by actual GUI process termination/restart. Orderly shutdown is verified through IPC and independent recording scanning.

This does not replace Stage 6 eight-hour/endurance, clean-machine/offline, power-loss, RF, accessibility and operator field qualification. Packages are unsigned. No GPS synchronization, calibration, RSSI or battery measurement is fabricated.

## Packaged build validation

Source build: 96b332189b95fb12a88686228e7b25ce934d15bf, clean tree, version 0.4.0-stage4.

- Windows x64 ZIP: package-96b3321.zip, 139,519,398 bytes.
- SHA-256: 856BEE75162F119E15B1A1A9BC99FF81752366FCAABB11E6CE28CC9E9B709FAC.
- Installer verified package hashes and copied files into a separate version directory using NoShortcut for this test.
- Installed host reported the expected source identity. Installed desktop launched with DOTNET_ROOT pointing to a nonexistent directory, demonstrating use of the included runtime; it remained running until explicitly terminated by the smoke test.
- Optional multinodedaq 0.4.0 Python wheel bundle was built from the same clean commit, installed into a fresh environment using --no-index and --require-hashes, and instantiated the default 6,875-row FAM worker successfully.

Artifacts are local under software/.artifacts/stage4 and excluded from Git. This install/launch check used the development laptop, not a clean-machine qualification.
