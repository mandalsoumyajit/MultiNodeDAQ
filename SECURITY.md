# Security scope

MultiNodeDAQ is an experimental acquisition system. The current sensor TCP transport has no authentication or encryption. Use it on a trusted, isolated network; do not expose its listener directly to the internet. The default host binding is loopback, and remote connections require explicitly selecting a network interface.

Input validation, resource bounds, and checksums help detect malformed or damaged data. Checksums do not authenticate a sender. Optional Python IPC binds only to IPv4 loopback and requires a per-launch token from the environment. It has separate connection and queue limits. This local boundary does not authenticate sensor-network clients; keep the token out of source control and logs.

Do not put credentials or private recordings in public issues. Until a private vulnerability-reporting channel is configured, open an issue requesting a private contact without posting exploit details or sensitive data.
