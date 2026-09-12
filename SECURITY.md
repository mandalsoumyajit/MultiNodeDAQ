# Security scope

MultiNodeDAQ is an experimental acquisition system. The current sensor TCP transport has no authentication or encryption. Use it on a trusted, isolated network; do not expose its listener directly to the internet. The default host binding is loopback, and remote connections require explicitly selecting a network interface.

Input validation, resource bounds, and checksums help detect malformed or damaged data. Checksums do not authenticate a sender. Local Python IPC security described in the design documents is planned, not an implemented live service.

Do not put credentials or private recordings in public issues. Until a private vulnerability-reporting channel is configured, open an issue requesting a private contact without posting exploit details or sensitive data.
