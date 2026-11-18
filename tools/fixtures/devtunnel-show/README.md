# Dev Tunnel CLI fixtures

These are sanitized extracts of actual `devtunnel show <id> --json` output
captured with CLI **1.0.2094+24665e6583** on macOS on November 18, 2026.
The connected sample came from a running Lumi tunnel; the disconnected sample
came from a separate private, unhosted tunnel with an explicitly configured port.
Only fields consumed by the classifier are retained. The IDs and gateway
hostname are replaced with placeholders that preserve the observed formats;
field names, JSON types, host counts, port number and the absence of `portUri`
in the disconnected sample are unchanged. Tests mock HTTP and do not probe any
tunnel.
