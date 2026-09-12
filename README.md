# MultiNodeDAQ

Distributed sensor acquisition, recording, and replay.

MultiNodeDAQ is an early-stage C#/.NET project for collecting sample streams from multiple sensor units on one base computer. It provides a TCP simulator, a headless receiver, rotating binary recordings, integrity verification, and sample-range replay. An independent Python implementation checks the shared binary contracts.

The first application is a distributed triaxial magnetic-field detector. The architecture is intended to support other sensor applications; the current sample protocol uses XYZ channels and has not yet been generalized to arbitrary channel layouts.

## Current status

Stages 0 and 1 are qualified with software simulators. Stage 2 recording and replay pass short regression tests; the full two-hour recording qualification is in progress. A desktop GUI, live Python analysis, and Pico 2 W firmware are planned. This is a development preview, with no portable release package yet.

Existing source projects use the original `Elf.*` namespaces and `ElfDaq.slnx` solution name. The public project name is MultiNodeDAQ; source naming will be updated after the running qualification test finishes. Existing protocol identifiers and recording formats will remain versioned compatibility contracts.

## Get started

Use Windows x64, .NET SDK 10.0.401 and Python 3.11–3.14. From a checkout:

```powershell
cd software
.\scripts\test.ps1
.\scripts\demo.ps1
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
