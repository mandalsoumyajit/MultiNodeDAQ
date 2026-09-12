# Stage 3 qualification

Passed on the development Windows x64 laptop on 12 September 2026.

- Locked .NET restore and Release build: zero warnings/errors.
- 550 C# contract assertions, 64 integration assertions (including bounded slow-subscriber retention), and 4,159 recording assertions passed.
- Twelve Python test methods passed, including independent log reading, semantic corruption rejection with recomputed checksums, exact HDF5 archive round trip, calibrated PSD and SCF power, direct FAM accumulation at zero and nonzero residual cyclic bins, high-counter phase, batching equivalence, live/offline agreement and gap resets.
- Real two-node recording with two Python worker kills/restarts and one stalled subscriber passed. The final run independently verified **1,196,032 XYZ rows** with no recording errors. A requested FAM settings revision was applied; malformed settings and an incorrect token were rejected. [Evidence](evidence/stage3/live-report.json).
- The offline notebook code cells executed successfully against a C# recording, producing full SCF/NPZ results and HDF5 export.
- Offline wheel installation succeeded into a fresh virtual environment with `--no-index` and hash verification. The isolated installed package computed SCF and read a C# recording without importing the development source tree. Python 3.12 itself must already be installed.
- Twenty repeated three-axis default FAM calls: **35.8 ms median, 37.6 ms maximum**, against a 275 ms input window; plan creation 22 ms. Full complex SCF array 9,657,648 bytes plus a 201,201-byte validity mask. [Benchmark settings and results](evidence/stage3/spectral-benchmark.json).

The live test is a short failure-isolation gate, not a replacement for the completed Stage 2 two-hour test or the future eight-hour full-system qualification. Recording remains independent of Python. The optional wheel bundle does not qualify deployment of the complete recorder/GUI application.

The manuscript's overlap ambiguity, chosen FAM normalization, frequency-coordinate convention, supported grid and computational bounds are documented in [spectral-method.md](spectral-method.md). Tests validate the estimator; no equivalence to unavailable training images, VGG16 inference or drone-classification accuracy is claimed.
