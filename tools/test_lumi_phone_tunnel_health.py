import unittest
from unittest.mock import Mock, call

from lumi_phone_tunnel_health import probe_tunnel_route


class PhoneTunnelHealthTests(unittest.TestCase):
    def setUp(self):
        self.tunnel_id = "lumi-" + "a" * 32 + ".eun1"
        self.port = 47653
        self.origin = "https://example-47653.eun1.devtunnels.ms/"
        self.tunnel = {
            "tunnelId": self.tunnel_id,
            "hostConnections": 0,
            "ports": [{"portNumber": self.port}],
        }

    def probe(self, http, host_pid=None):
        return probe_tunnel_route(self.tunnel, self.tunnel_id, self.port, host_pid, http)

    def test_disconnected_without_uri_reaches_recovery_state(self):
        for host_pid in (None, 202):
            with self.subTest(host_pid=host_pid):
                http = Mock(return_value=200)
                self.assertEqual({
                    "hostConnections": 0, "localStatus": 200,
                    "gatewayStatus": None, "state": "disconnected",
                }, self.probe(http, host_pid))
                http.assert_called_once_with("http://127.0.0.1:47653/lumi/hello")

    def test_unavailable_local_server_is_not_eligible_for_disconnected_recovery(self):
        http = Mock(return_value=None)
        self.assertEqual("local-server-unavailable", self.probe(http)["state"])
        self.assertEqual(1, http.call_count)

    def test_disconnected_with_uri_keeps_public_route(self):
        self.tunnel["ports"][0]["portUri"] = self.origin
        result = self.probe(Mock(side_effect=[200, 503]))
        self.assertEqual("disconnected", result["state"])
        self.assertEqual(self.origin + "app/", result["publicUrl"])

    def test_connected_requires_valid_route_and_verified_host(self):
        self.tunnel["hostConnections"] = 1
        http = Mock()
        with self.assertRaisesRegex(RuntimeError, "missing its public route"):
            self.probe(http, 202)
        http.assert_not_called()
        self.tunnel["ports"][0]["portUri"] = self.origin
        self.assertEqual("host-owner-unverified", self.probe(Mock(side_effect=[200, 401]))["state"])
        for gateway, state in ((401, "connected"), (503, "gateway-unavailable"), (None, "gateway-unavailable")):
            with self.subTest(gateway=gateway):
                http = Mock(side_effect=[200, gateway])
                self.assertEqual(state, self.probe(http, 202)["state"])
                self.assertEqual([
                    call("http://127.0.0.1:47653/lumi/hello"), call(self.origin + "app/"),
                ], http.call_args_list)

    def test_unexpected_identity_or_port_is_rejected_before_http(self):
        http = Mock()
        for change in (
            {"tunnelId": "another-tunnel"},
            {"hostConnections": True},
            {"ports": []},
            {"ports": [{"portNumber": self.port}, {"portNumber": self.port}]},
        ):
            with self.subTest(change=change):
                original = self.tunnel
                self.tunnel = dict(original, **change)
                try:
                    with self.assertRaises(RuntimeError):
                        self.probe(http)
                finally:
                    self.tunnel = original
        http.assert_not_called()

    def test_untrusted_routes_are_never_probed_even_when_disconnected(self):
        http = Mock()
        for origin in (
            "http://example.devtunnels.ms/",
            "https://example.devtunnels.ms.evil.invalid/",
            "https://user:password@example.devtunnels.ms/",
            "https://example.devtunnels.ms/?token=secret",
            123,
        ):
            with self.subTest(origin=origin):
                self.tunnel["ports"][0]["portUri"] = origin
                with self.assertRaises(RuntimeError):
                    self.probe(http)
        http.assert_not_called()


if __name__ == "__main__":
    unittest.main()
