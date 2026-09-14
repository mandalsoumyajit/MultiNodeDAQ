# Pico TCP pump tests

The native test compiles the same tcp_pump.h used by stream.c against a fake
TCP backend. It exercises output failure with zero send space, bounded retries,
partial writes, transient allocation failure, byte continuity, partial/cumulative
ACK accounting, no ACK timer before accepted bytes, and work budgets.

From a Visual Studio x64 Native Tools command prompt at the repository root:

    cl /nologo /std:c11 /W4 /WX /Od /Fe:software\.artifacts\stage5\test_tcp_pump.exe /Fo:software\.artifacts\stage5\test_tcp_pump.obj software\firmware\pico_synthetic\tests\test_tcp_pump.c
    software\.artifacts\stage5\test_tcp_pump.exe

Create software/.artifacts/stage5 first if needed. Alternatively compile
test_tcp_pump.c using a C11 host compiler. Assertions must remain enabled.

This fake backend checks application servicing and byte accounting. It does
not simulate lwIP congestion control, real radio faults, or the application
DATA-frame retirement ledger. Hardware counter/CRC checks remain necessary.