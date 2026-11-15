"""Read-only Dev Tunnel route checks for external phone watchdogs."""

import urllib.parse


def probe_tunnel_route(tunnel, expected_tunnel_id, port, host_pid, http_status):
    """Classify a CLI `show --json` tunnel; callers retain process ownership checks."""
    hosts = tunnel.get("hostConnections")
    if (tunnel.get("tunnelId") != expected_tunnel_id
            or type(hosts) is not int or hosts < 0):
        raise RuntimeError("Unexpected authenticated tunnel response")
    ports = [item for item in tunnel.get("ports", []) if item.get("portNumber") == port]
    if len(ports) != 1:
        raise RuntimeError("Expected phone port is missing or ambiguous")

    origin = ports[0].get("portUri")
    public_url = None
    if origin is not None:
        if not isinstance(origin, str):
            raise RuntimeError("Unexpected public tunnel route")
        uri = urllib.parse.urlsplit(origin)
        if (uri.scheme != "https" or not uri.hostname
                or not uri.hostname.endswith(".devtunnels.ms")
                or uri.username or uri.password or uri.query or uri.fragment):
            raise RuntimeError("Unexpected public tunnel route")
        public_url = origin.rstrip("/") + "/app/"
    elif hosts != 0:
        raise RuntimeError("Connected tunnel response is missing its public route")

    local = http_status(f"http://127.0.0.1:{port}/lumi/hello")
    gateway = http_status(public_url) if public_url is not None else None
    if local not in (200, 403, 405):
        state = "local-server-unavailable"
    elif hosts == 0:
        # A disconnected tunnel can omit portUri; recovery must still see it.
        state = "disconnected"
    elif not host_pid:
        state = "host-owner-unverified"
    elif gateway in (200, 301, 302, 303, 307, 308, 401, 403):
        state = "connected"
    else:
        state = "gateway-unavailable"

    result = dict(hostConnections=hosts, localStatus=local, gatewayStatus=gateway, state=state)
    if public_url is not None:
        result["publicUrl"] = public_url
    return result
