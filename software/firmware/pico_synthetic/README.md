# Firmware boundary (Stage 0)

No firmware is implemented yet. Stage 5 will use the Pico C/C++ SDK, protocol-v1, a hardware-paced sample producer, independent finite buffers and explicit overflow counters. Use the shared fixtures before radio integration. Do not copy native C structs onto the wire: encode little-endian fields explicitly and UUIDs in RFC/network byte order.
