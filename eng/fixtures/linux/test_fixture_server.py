#!/usr/bin/env python3
"""Self-checks for the credential-free Linux fixture server."""

from __future__ import annotations

import base64
import http.client
import json
import os
import socket
import struct
import sys
import threading
import unittest
from contextlib import contextmanager
from pathlib import Path
from typing import Iterator

sys.path.insert(0, str(Path(__file__).resolve().parent))
import fixture_server


@contextmanager
def running_fixture(mode: str) -> Iterator[tuple[str, int]]:
    server = fixture_server.CredentialFreeHttpServer(("127.0.0.1", 0), mode)
    thread = threading.Thread(target=server.serve_forever, daemon=True)
    thread.start()
    try:
        yield server.server_address[:2]
    finally:
        server.shutdown()
        server.server_close()
        thread.join(timeout=2)


def request(
    endpoint: tuple[str, int],
    path: str,
    *,
    method: str = "GET",
) -> tuple[int, dict[str, str], bytes]:
    connection = http.client.HTTPConnection(*endpoint, timeout=2)
    try:
        connection.request(method, path)
        response = connection.getresponse()
        return response.status, dict(response.getheaders()), response.read()
    finally:
        connection.close()


class FixtureServerTests(unittest.TestCase):
    def test_valid_descriptor_is_strict_contract_shape(self) -> None:
        with running_fixture("descriptor-valid") as endpoint:
            status, headers, body = request(endpoint, fixture_server.DESCRIPTOR_PATH)

        self.assertEqual(200, status)
        self.assertEqual("application/json; charset=utf-8", headers["Content-Type"])
        self.assertEqual("no-store", headers["Cache-Control"])
        self.assertLessEqual(len(body), fixture_server.MAX_DESCRIPTOR_BYTES)
        self.assertEqual(fixture_server.FIXTURE_DESCRIPTOR, json.loads(body))
        self.assertEqual(
            {
                "schemaVersion",
                "contractVersion",
                "components",
            },
            set(json.loads(body)),
        )
        component = json.loads(body)["components"][0]
        self.assertEqual(
            {"uiId", "uiVersion", "sourceRev", "adapterKeys"},
            set(component),
        )
        self.assertNotIn("dsh-web", body.decode("utf-8"))

    def test_descriptor_failure_modes_are_single_dimension(self) -> None:
        cases = (
            ("descriptor-missing", 404, "text/plain; charset=utf-8"),
            ("descriptor-wrong-content-type", 200, "text/plain; charset=utf-8"),
            ("descriptor-oversized", 200, "application/json; charset=utf-8"),
            ("descriptor-redirect", 302, None),
        )
        for mode, expected_status, expected_type in cases:
            with self.subTest(mode=mode), running_fixture(mode) as endpoint:
                status, headers, body = request(endpoint, fixture_server.DESCRIPTOR_PATH)
                self.assertEqual(expected_status, status)
                self.assertEqual(expected_type, headers.get("Content-Type"))
                if mode == "descriptor-wrong-content-type":
                    self.assertEqual(fixture_server.FIXTURE_DESCRIPTOR, json.loads(body))
                elif mode == "descriptor-oversized":
                    self.assertEqual(fixture_server.MAX_DESCRIPTOR_BYTES + 1, len(body))
                    self.assertEqual(fixture_server.FIXTURE_DESCRIPTOR, json.loads(body))
                elif mode == "descriptor-redirect":
                    self.assertEqual("/descriptor-redirect-target", headers["Location"])
                    target_status, target_headers, target_body = request(
                        endpoint, headers["Location"]
                    )
                    self.assertEqual(200, target_status)
                    self.assertEqual(
                        "application/json; charset=utf-8",
                        target_headers["Content-Type"],
                    )
                    self.assertEqual(
                        fixture_server.FIXTURE_DESCRIPTOR,
                        json.loads(target_body),
                    )

    def test_redirect_hops_and_arrival_are_counted_independently(self) -> None:
        with running_fixture("descriptor-valid") as endpoint:
            status, headers, _ = request(endpoint, "/redirect/hop/1")
            self.assertEqual(302, status)
            self.assertEqual("/redirect/hop/2", headers["Location"])
            status, headers, _ = request(endpoint, headers["Location"])
            self.assertEqual(302, status)
            self.assertEqual("/redirect/arrival", headers["Location"])
            self.assertEqual(200, request(endpoint, headers["Location"])[0])
            counts = json.loads(request(endpoint, "/__fixture/counts")[2])["counts"]

        self.assertEqual(
            {
                "redirect-arrival": 1,
                "redirect-hop-1": 1,
                "redirect-hop-2": 1,
            },
            counts,
        )

    def test_counter_never_uses_request_target_or_query_as_a_key(self) -> None:
        marker = "credential-marker-never-retain"
        with running_fixture("webui-peer") as endpoint:
            self.assertEqual(
                200,
                request(endpoint, f"/resources/value.json?token={marker}")[0],
            )
            counts_body = request(endpoint, "/__fixture/counts")[2]
            self.assertNotIn(marker.encode("ascii"), counts_body)
            self.assertEqual(
                {"resource-value": 1},
                json.loads(counts_body)["counts"],
            )
            self.assertEqual(204, request(endpoint, "/__fixture/reset", method="POST")[0])
            self.assertEqual(
                {},
                json.loads(request(endpoint, "/__fixture/counts")[2])["counts"],
            )

    def test_cross_port_worker_and_cross_origin_frame_pages_are_available(self) -> None:
        expected_fragments = {
            "/tests/cross-port.html": b"peerPort",
            "/tests/worker.html": b"SharedWorker",
            "/tests/websocket.html": b"WebSocket",
            "/tests/oopif.html": b"iframe",
            "/tests/oopif-child.html": b"fixture child arrived",
            "/workers/dedicated.js": b"dedicated:",
            "/workers/shared.js": b"shared:",
            "/resources/pixel.gif?case=cross-port": b"GIF89a",
        }
        with running_fixture("webui-peer") as endpoint:
            for path, fragment in expected_fragments.items():
                with self.subTest(path=path):
                    status, _, body = request(endpoint, path)
                    self.assertEqual(200, status)
                    self.assertIn(fragment, body)

    def test_websocket_handshake_and_echo(self) -> None:
        with running_fixture("descriptor-valid") as endpoint:
            key = base64.b64encode(os.urandom(16)).decode("ascii")
            client = socket.create_connection(endpoint, timeout=2)
            client.settimeout(2)
            try:
                request_bytes = (
                    "GET /websocket/echo HTTP/1.1\r\n"
                    f"Host: {endpoint[0]}:{endpoint[1]}\r\n"
                    "Upgrade: websocket\r\n"
                    "Connection: Upgrade\r\n"
                    f"Sec-WebSocket-Key: {key}\r\n"
                    "Sec-WebSocket-Version: 13\r\n\r\n"
                ).encode("ascii")
                client.sendall(request_bytes)
                response = b""
                while b"\r\n\r\n" not in response:
                    response += client.recv(4096)
                self.assertIn(b"HTTP/1.1 101 Switching Protocols", response)

                payload = b"fixture-echo"
                mask = b"\x01\x02\x03\x04"
                masked = bytes(
                    value ^ mask[index % 4] for index, value in enumerate(payload)
                )
                client.sendall(bytes((0x81, 0x80 | len(payload))) + mask + masked)
                header = client.recv(2)
                self.assertEqual(0x81, header[0])
                length = header[1] & 0x7F
                if length == 126:
                    length = struct.unpack("!H", client.recv(2))[0]
                echoed = b""
                while len(echoed) < length:
                    echoed += client.recv(length - len(echoed))
                self.assertEqual(payload, echoed)
            finally:
                client.close()

            counts = json.loads(request(endpoint, "/__fixture/counts")[2])["counts"]
            self.assertEqual(1, counts["websocket-upgrade"])
            self.assertEqual(1, counts["websocket-message"])


if __name__ == "__main__":
    unittest.main(verbosity=2)
