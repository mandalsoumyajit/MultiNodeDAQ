# Pico 2 W firmware — Stage 5 bring-up

The first target, multinodedaq_diagnostic, initializes the CYW43 interface, toggles the onboard LED once per second, and emits newline-delimited JSON over USB CDC. It reports the RP2350 unique ID, source build, SDK version, system clock, uptime and initialization status. It does not yet acquire or stream samples over Wi-Fi.

Build from the repository root using software/scripts/build-pico-diagnostic.ps1. The script uses the official VS Code extension's installed SDK 2.2.0, ARM GCC 14.2.Rel1, CMake 3.31.5, Ninja 1.12.1, pioasm 2.2.0 and picotool 2.2.0-a4. Override SdkRoot if installed elsewhere. Output is software/.artifacts/stage5/build-diagnostic/multinodedaq_diagnostic.uf2.

Connect one board in BOOTSEL mode. Inspect it with picotool info -a; target an explicit USB serial number when flashing. picotool load -v -x FILE --ser SERIAL writes, verifies and executes the diagnostic. USB CDC appears as a COM port afterward; open it at 115200 baud with DTR enabled. JSON is emitted once per second while connected. SDK USB reset support permits later picotool commands with -f.

Keep firmware, build scripts and test reports in Git; firmware binaries, flash backups, serial captures and future private Wi-Fi configuration belong in ignored local artifacts. Use explicit little-endian encoders for protocol v1; never send native C structures directly.

Remaining Stage 5 work: shared wire fixtures, hardware-paced synthetic XYZ producer, finite queues and overflow accounting, Wi-Fi/TCP command and streaming integration, two-board endurance and disconnection/recovery tests.
