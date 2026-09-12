# Contributing to MultiNodeDAQ

Please open an issue describing a bug or proposed change, including the build version, reproduction steps, and expected behavior. For recordings, share a small synthetic reproduction rather than private field data.

Use the pinned SDK in `software/global.json`. Run `software/scripts/test.ps1` from PowerShell before submitting a pull request. These are executable test harnesses; `dotnet test` alone does not run them. Include relevant test results in the pull request.

Keep acquisition and recording independent of optional display and analysis consumers. Preserve explicit gaps, bounded queues, and recording provenance. Protocol or log-format changes require a documented compatibility decision and independently checked fixture updates.

Do not commit SDKs, build outputs, credentials, or large recordings. Long qualification runs write ignored artifacts under `software/.artifacts`; commit only compact evidence and reports. A short smoke test does not replace the documented full-duration qualification.

Source code is currently transitioning from the original ELF-specific naming to MultiNodeDAQ. Avoid unrelated format or namespace changes in functional pull requests.
