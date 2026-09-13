# MultiNodeDAQ.Acquisition

Stage 0 project boundary only. Runtime implementation is scheduled in the staged plan.


`ReceiverOptions.ConnectAddresses` selects outbound acquisition instead of a
TCP listener. It accepts up to MaxConnections distinct literal IP addresses at
one configured port, retries disconnected nodes once per second, and preserves
the existing HELLO, command, frame-validation, recording and shutdown paths.
The host exposes this as `--connect IP[,IP]`; omitting it preserves listener mode.
