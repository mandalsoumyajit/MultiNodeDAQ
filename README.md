# MultiNodeDAQ

Distributed sensor acquisition, recording, and replay.

MultiNodeDAQ is an early-stage C#/.NET project for collecting sample streams from multiple sensor units on one base computer. It provides a TCP simulator, a headless receiver, rotating binary recordings, integrity verification, and sample-range replay. An independent Python implementation checks the shared binary contracts.

The first application is a distributed triaxial magnetic-field detector. The architecture is intended to support other sensor applications; the current sample protocol uses XYZ channels and has not yet been generalized to arbitrary channel layouts.

## Resume development

For the current Stage 5 hardware checkpoint and setup on another computer, start with [RESUME.md](RESUME.md). The Pico joins Wi-Fi; the first end-to-end streaming test is still pending.

## Current status

Stages 0–3 are qualified with software simulators. The Stage 2 two-hour, sixteen-node recording run passed, including independent verification of all 144 segments: 2,879,978,240 XYZ rows with no missing samples or sample errors. See the [qualification report](software/docs/stage2-test-report.md). Stage 3 adds Python recording access, lossless HDF5 export, live SCF/PSD analysis, and runtime settings for the GUI. See [Python setup and operation](software/docs/stage3-operation.md). Stage 4 now provides the WPF operator GUI, replay controls, live diagnostics, selectable spectral settings and a self-contained Windows package with versioned per-user installation. Pico 2 W firmware remains Stage 5. Extended offline/field qualification remains Stage 6.

The solution is `software/MultiNodeDAQ.slnx`, C# projects use `MultiNodeDAQ.*`, and the Python package is `multinodedaq`. The ELF-prefixed wire magic and `.elflog` format retain their existing v1 identifiers for compatibility. Local development is at `C:\dev\MultiNodeDAQ`.

## Get started

Use Windows x64, .NET SDK 10.0.401 and Python 3.12 for the pinned analysis environment. From a checkout:

```powershell
cd software
.\scripts\setup-stage3.ps1
.\scripts\test.ps1
.\scripts\demo-stage4.ps1
```

The [software guide](software/README.md) includes SDK setup and commands for running separate simulator and receiver processes. Tests use loopback TCP; they do not establish Wi-Fi range or hardware timing performance.

## Documentation

- [Recording, verification and replay](software/docs/stage2-operation.md)
- [Implementation roadmap](Software_Implementation_Plan.md)
- [Base-station architecture](Base_Station_Software_Plan.md)
- [Deployment and version control](Deployment_and_Version_Control.md)
- [ELF detector application and hardware options](Architecture_and_Design_Options.md)
- [Contributing](CONTRIBUTING.md)

Sensor transport currently assumes a trusted network and does not implement authentication or encryption. See [security scope](SECURITY.md).

## Stage 4 desktop

The Windows operator GUI is implemented: fleet status, recording controls, XYZ envelopes and C# diagnostics, optional spectral views/settings, replay and reports. See [operator guide](software/docs/stage4-operation.md) and [test results](software/docs/stage4-test-report.md).

From the software directory, run scripts/demo-stage4.ps1 in PowerShell 7 after a Release build. Self-contained Windows x64 packaging and per-user versioned installation are available through scripts/package-stage4.ps1. Hardware firmware remains Stage 5; extended field and offline deployment qualification remains Stage 6.
