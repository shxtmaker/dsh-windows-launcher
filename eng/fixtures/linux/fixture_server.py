#!/usr/bin/env python3
"""Deterministic credential-free network fixtures for DSH launcher tests."""

from __future__ import annotations

import argparse
import base64
import hashlib
import ipaddress
import json
import socketserver
import ssl
import struct
import threading
import time
from http.server import BaseHTTPRequestHandler, ThreadingHTTPServer
from pathlib import Path
from urllib.parse import urlsplit


RFC1918_NETWORKS = (
    ipaddress.ip_network("10.0.0.0/8"),
    ipaddress.ip_network("172.16.0.0/12"),
    ipaddress.ip_network("192.168.0.0/16"),
)
DESCRIPTOR_PATH = "/.well-known/dsh-webui-compatibility.json"
MAX_DESCRIPTOR_BYTES = 64 * 1024
LEGACY_ROOT_BODY = (
    Path(__file__).with_name("legacy-root-0.1.1-rc.2.html").read_bytes()
)
FIXTURE_DESCRIPTOR = {
    "schemaVersion": 1,
    "contractVersion": "1.0.0",
    "components": [
        {
            "uiId": "org.dshwindowslauncher.fixture",
            "uiVersion": "1.0.0",
            "sourceRev": "fixture-p3-0001",
            "adapterKeys": ["fixture-observation"],
        }
    ],
}
FIXTURE_DESCRIPTOR_BODY = json.dumps(
    FIXTURE_DESCRIPTOR,
    ensure_ascii=True,
    separators=(",", ":"),
).encode("utf-8")
OVERSIZED_DESCRIPTOR_BODY = FIXTURE_DESCRIPTOR_BODY + b" " * (
    MAX_DESCRIPTOR_BYTES + 1 - len(FIXTURE_DESCRIPTOR_BODY)
)
PIXEL_GIF = base64.b64decode(
    "R0lGODlhAQABAIAAAAAAAP///ywAAAAAAQABAAACAUwAOw=="
)

WEBUI_MODES = frozenset(
    {
        "descriptor-valid",
        "descriptor-missing",
        "descriptor-wrong-content-type",
        "descriptor-oversized",
        "descriptor-redirect",
        "webui-peer",
    }
)
ALL_MODES = (
    "wrong-200",
    "fence-403",
    "old-unauthenticated",
    "redirect-302",
    "notfound-404",
    "non-http",
    "timeout",
    *sorted(WEBUI_MODES),
)

INDEX_BODY = b"""<!doctype html>
<meta charset="utf-8">
<title>DSH launcher compatibility fixture</title>
<h1>Credential-free compatibility fixture</h1>
<ul>
  <li><a href="/tests/cross-port.html">same-host different-port resource</a></li>
  <li><a href="/tests/worker.html">Worker probes</a></li>
  <li><a href="/tests/websocket.html">WebSocket probe</a></li>
  <li><a href="/tests/oopif.html">cross-origin frame probe</a></li>
  <li><a href="/redirect/hop/1">two-hop redirect</a></li>
</ul>
<p>This page is a test fixture, not a supported WebUI identity.</p>
"""

CROSS_PORT_BODY = b"""<!doctype html>
<meta charset="utf-8">
<title>Different-port resource probe</title>
<pre id="result">pending</pre>
<script>
const params = new URL(location.href).searchParams;
const peerPort = params.get("peerPort");
const peerHost = params.get("peerHost") || location.hostname;
if (!/^[1-9][0-9]{0,4}$/.test(peerPort || "") || Number(peerPort) > 65535 ||
    !/^[0-9A-Za-z.:-]+$/.test(peerHost)) {
  document.querySelector("#result").textContent = "set a valid peerPort";
} else {
  const image = new Image();
  image.onload = () => document.querySelector("#result").textContent = "loaded";
  image.onerror = () => document.querySelector("#result").textContent = "failed";
  image.src = `${location.protocol}//${peerHost}:${peerPort}/resources/pixel.gif?case=cross-port`;
  document.body.append(image);
}
</script>
"""

WORKER_BODY = b"""<!doctype html>
<meta charset="utf-8">
<title>Worker probes</title>
<pre id="result">pending</pre>
<script>
const lines = [];
const render = () => document.querySelector("#result").textContent = lines.join("\\n");
try {
  const worker = new Worker("/workers/dedicated.js");
  worker.onmessage = event => { lines.push(event.data); render(); worker.terminate(); };
  worker.postMessage("probe");
} catch (error) { lines.push(`dedicated-error:${error.name}`); render(); }
try {
  const shared = new SharedWorker("/workers/shared.js");
  shared.port.onmessage = event => { lines.push(event.data); render(); shared.port.close(); };
  shared.port.start();
  shared.port.postMessage("probe");
} catch (error) { lines.push(`shared-error:${error.name}`); render(); }
</script>
"""

WEBSOCKET_BODY = b"""<!doctype html>
<meta charset="utf-8">
<title>WebSocket probe</title>
<pre id="result">pending</pre>
<script>
const result = document.querySelector("#result");
const scheme = location.protocol === "https:" ? "wss" : "ws";
const socket = new WebSocket(`${scheme}://${location.host}/websocket/echo`);
socket.onopen = () => socket.send("fixture-probe");
socket.onmessage = event => { result.textContent = event.data; socket.close(1000); };
socket.onerror = () => result.textContent = "failed";
</script>
"""

OOPIF_BODY = b"""<!doctype html>
<meta charset="utf-8">
<title>Cross-origin frame probe</title>
<pre id="result">pending</pre>
<script>
const params = new URL(location.href).searchParams;
const peerPort = params.get("peerPort");
const peerHost = params.get("peerHost") || location.hostname;
if (!/^[1-9][0-9]{0,4}$/.test(peerPort || "") || Number(peerPort) > 65535 ||
    !/^[0-9A-Za-z.:-]+$/.test(peerHost)) {
  document.querySelector("#result").textContent = "set a valid peerPort";
} else {
  const frame = document.createElement("iframe");
  frame.src = `${location.protocol}//${peerHost}:${peerPort}/tests/oopif-child.html`;
  frame.onload = () => document.querySelector("#result").textContent = "frame-loaded";
  document.body.append(frame);
}
</script>
"""

OOPIF_CHILD_BODY = b"""<!doctype html>
<meta charset="utf-8">
<title>Cross-origin frame child</title>
<p id="fixture-child">fixture child arrived</p>
"""

DEDICATED_WORKER_BODY = (
    b'self.onmessage = event => self.postMessage("dedicated:" + String(event.data));\n'
)
SHARED_WORKER_BODY = b"""self.onconnect = event => {
  const port = event.ports[0];
  port.onmessage = message => port.postMessage("shared:" + String(message.data));
  port.start();
};
"""


def parse_rfc1918_ipv4(value: str) -> str:
    address = ipaddress.ip_address(value)
    if not isinstance(address, ipaddress.IPv4Address):
        raise argparse.ArgumentTypeError("bind address must be IPv4")
    if not any(address in network for network in RFC1918_NETWORKS):
        raise argparse.ArgumentTypeError("bind address must be RFC1918")
    return str(address)


def parse_tcp_port(value: str) -> int:
    try:
        port = int(value)
    except ValueError as error:
        raise argparse.ArgumentTypeError("port must be an integer") from error
    if not 1 <= port <= 65535:
        raise argparse.ArgumentTypeError("port must be between 1 and 65535")
    return port


class ArrivalCounter:
    """Counts fixed fixture events without retaining request-derived data."""

    def __init__(self) -> None:
        self._counts: dict[str, int] = {}
        self._lock = threading.Lock()

    def increment(self, event: str) -> None:
        with self._lock:
            self._counts[event] = self._counts.get(event, 0) + 1

    def snapshot(self) -> dict[str, int]:
        with self._lock:
            return dict(sorted(self._counts.items()))

    def reset(self) -> None:
        with self._lock:
            self._counts.clear()


class FixtureHttpHandler(BaseHTTPRequestHandler):
    server_version = "DshLauncherFixture/2"
    protocol_version = "HTTP/1.1"

    def do_GET(self) -> None:  # noqa: N802 - BaseHTTPRequestHandler API
        mode = self.server.fixture_mode  # type: ignore[attr-defined]
        if mode in WEBUI_MODES:
            self._serve_webui(mode)
        elif mode == "wrong-200":
            self._send(200, b"not a Harness service\n", "text/plain; charset=utf-8")
        elif mode == "fence-403":
            self._send(403, b"request rejected by fixture fence\n", "text/plain; charset=utf-8")
        elif mode == "old-unauthenticated":
            path = urlsplit(self.path).path
            if path == "/api":
                self._send(404, b"not found", "text/plain; charset=utf-8")
            elif path == "/":
                self._send(200, LEGACY_ROOT_BODY, "text/html; charset=utf-8")
            else:
                self._send(404, b"not found", "text/plain; charset=utf-8")
        elif mode == "redirect-302":
            self._redirect("/redirect-target")
        elif mode == "notfound-404":
            self._send(404, b"fixture not found\n", "text/plain; charset=utf-8")
        else:
            self._send(500, b"invalid fixture mode\n", "text/plain; charset=utf-8")

    def do_POST(self) -> None:  # noqa: N802 - BaseHTTPRequestHandler API
        mode = self.server.fixture_mode  # type: ignore[attr-defined]
        path = urlsplit(self.path).path
        if mode in WEBUI_MODES and path == "/__fixture/reset":
            self._discard_request_body()
            self.server.arrival_counter.reset()  # type: ignore[attr-defined]
            self._send(204, b"", None)
            return
        self._discard_request_body()
        self._send(404, b"not found\n", "text/plain; charset=utf-8")

    def _serve_webui(self, mode: str) -> None:
        path = urlsplit(self.path).path
        if path == DESCRIPTOR_PATH:
            self._count("descriptor")
            self._serve_descriptor(mode)
        elif path == "/descriptor-redirect-target":
            self._count("descriptor-redirect-target")
            self._send_descriptor(FIXTURE_DESCRIPTOR_BODY)
        elif path == "/":
            self._count("root")
            self._send(200, INDEX_BODY, "text/html; charset=utf-8")
        elif path == "/tests/cross-port.html":
            self._count("cross-port-page")
            self._send(200, CROSS_PORT_BODY, "text/html; charset=utf-8")
        elif path == "/tests/worker.html":
            self._count("worker-page")
            self._send(200, WORKER_BODY, "text/html; charset=utf-8")
        elif path == "/tests/websocket.html":
            self._count("websocket-page")
            self._send(200, WEBSOCKET_BODY, "text/html; charset=utf-8")
        elif path == "/tests/oopif.html":
            self._count("oopif-parent")
            self._send(200, OOPIF_BODY, "text/html; charset=utf-8")
        elif path == "/tests/oopif-child.html":
            self._count("oopif-child")
            self._send(200, OOPIF_CHILD_BODY, "text/html; charset=utf-8")
        elif path == "/workers/dedicated.js":
            self._count("worker-dedicated-script")
            self._send(200, DEDICATED_WORKER_BODY, "text/javascript; charset=utf-8")
        elif path == "/workers/shared.js":
            self._count("worker-shared-script")
            self._send(200, SHARED_WORKER_BODY, "text/javascript; charset=utf-8")
        elif path == "/resources/pixel.gif":
            self._count("resource-pixel")
            self._send(200, PIXEL_GIF, "image/gif")
        elif path == "/resources/value.json":
            self._count("resource-value")
            self._send_json({"fixture": "value"})
        elif path == "/redirect/hop/1":
            self._count("redirect-hop-1")
            self._redirect("/redirect/hop/2")
        elif path == "/redirect/hop/2":
            self._count("redirect-hop-2")
            self._redirect("/redirect/arrival")
        elif path == "/redirect/arrival":
            self._count("redirect-arrival")
            self._send(200, b"redirect arrived\n", "text/plain; charset=utf-8")
        elif path == "/websocket/echo" and self.headers.get("Upgrade", "").lower() == "websocket":
            self._serve_websocket()
        elif path == "/__fixture/counts":
            counts = self.server.arrival_counter.snapshot()  # type: ignore[attr-defined]
            self._send_json({"schemaVersion": 1, "counts": counts})
        else:
            self._send(404, b"not found\n", "text/plain; charset=utf-8")

    def _serve_descriptor(self, mode: str) -> None:
        if mode in {"descriptor-valid", "webui-peer"}:
            self._send_descriptor(FIXTURE_DESCRIPTOR_BODY)
        elif mode == "descriptor-missing":
            self._send(404, b"not found\n", "text/plain; charset=utf-8")
        elif mode == "descriptor-wrong-content-type":
            self._send(200, FIXTURE_DESCRIPTOR_BODY, "text/plain; charset=utf-8")
        elif mode == "descriptor-oversized":
            self._send_descriptor(OVERSIZED_DESCRIPTOR_BODY)
        elif mode == "descriptor-redirect":
            self._redirect("/descriptor-redirect-target")
        else:
            self._send(500, b"invalid descriptor fixture mode\n", "text/plain; charset=utf-8")

    def _serve_websocket(self) -> None:
        key = self.headers.get("Sec-WebSocket-Key", "")
        version = self.headers.get("Sec-WebSocket-Version", "")
        try:
            decoded_key = base64.b64decode(key, validate=True)
        except (ValueError, TypeError):
            decoded_key = b""
        if version != "13" or len(decoded_key) != 16:
            self._send(400, b"invalid websocket handshake\n", "text/plain; charset=utf-8")
            return

        accept = base64.b64encode(
            hashlib.sha1(
                (key + "258EAFA5-E914-47DA-95CA-C5AB0DC85B11").encode("ascii")
            ).digest()
        ).decode("ascii")
        self.send_response(101, "Switching Protocols")
        self.send_header("Upgrade", "websocket")
        self.send_header("Connection", "Upgrade")
        self.send_header("Sec-WebSocket-Accept", accept)
        self.end_headers()
        self.close_connection = True
        self._count("websocket-upgrade")

        self.connection.settimeout(10)
        try:
            for _ in range(16):
                frame = self._read_websocket_frame()
                if frame is None:
                    return
                opcode, payload = frame
                if opcode == 0x8:
                    self._write_websocket_frame(0x8, payload[:125])
                    return
                if opcode == 0x9:
                    self._write_websocket_frame(0xA, payload[:125])
                elif opcode in {0x1, 0x2}:
                    self._count("websocket-message")
                    self._write_websocket_frame(opcode, payload)
        except (ConnectionError, OSError, TimeoutError):
            return

    def _read_websocket_frame(self) -> tuple[int, bytes] | None:
        header = self.rfile.read(2)
        if len(header) != 2:
            return None
        first, second = header
        opcode = first & 0x0F
        is_masked = (second & 0x80) != 0
        length = second & 0x7F
        if length == 126:
            raw_length = self.rfile.read(2)
            if len(raw_length) != 2:
                return None
            length = struct.unpack("!H", raw_length)[0]
        elif length == 127:
            raw_length = self.rfile.read(8)
            if len(raw_length) != 8:
                return None
            length = struct.unpack("!Q", raw_length)[0]
        if not is_masked or length > MAX_DESCRIPTOR_BYTES:
            self._write_websocket_frame(0x8, struct.pack("!H", 1009))
            return None
        mask = self.rfile.read(4)
        payload = self.rfile.read(length)
        if len(mask) != 4 or len(payload) != length:
            return None
        return opcode, bytes(value ^ mask[index % 4] for index, value in enumerate(payload))

    def _write_websocket_frame(self, opcode: int, payload: bytes) -> None:
        if len(payload) < 126:
            header = bytes((0x80 | opcode, len(payload)))
        else:
            header = bytes((0x80 | opcode, 126)) + struct.pack("!H", len(payload))
        self.wfile.write(header + payload)
        self.wfile.flush()

    def _count(self, event: str) -> None:
        self.server.arrival_counter.increment(event)  # type: ignore[attr-defined]

    def _send_descriptor(self, body: bytes) -> None:
        self._send(200, body, "application/json; charset=utf-8")

    def _send_json(self, value: object) -> None:
        body = json.dumps(value, ensure_ascii=True, separators=(",", ":")).encode("utf-8")
        self._send(200, body, "application/json; charset=utf-8")

    def _redirect(self, location: str) -> None:
        self.send_response(302)
        self.send_header("Location", location)
        self.send_header("Content-Length", "0")
        self.send_header("Cache-Control", "no-store")
        self.end_headers()

    def _discard_request_body(self) -> None:
        try:
            content_length = int(self.headers.get("Content-Length", "0"))
        except ValueError:
            content_length = 0
        if 0 < content_length <= MAX_DESCRIPTOR_BYTES:
            self.rfile.read(content_length)
        elif content_length > MAX_DESCRIPTOR_BYTES:
            self.close_connection = True

    def _send(self, status: int, body: bytes, content_type: str | None) -> None:
        self.send_response(status)
        if content_type is not None:
            self.send_header("Content-Type", content_type)
        self.send_header("Content-Length", str(len(body)))
        self.send_header("Cache-Control", "no-store")
        self.send_header("X-Content-Type-Options", "nosniff")
        self.end_headers()
        if body:
            self.wfile.write(body)

    def log_message(self, _format: str, *_args: object) -> None:
        # Request targets are intentionally never persisted. They may contain credentials
        # when a tester points the wrong client at a fixture.
        return


class CredentialFreeHttpServer(ThreadingHTTPServer):
    daemon_threads = True
    allow_reuse_address = True

    def __init__(
        self,
        server_address: tuple[str, int],
        mode: str,
        *,
        tls_cert: str | None = None,
        tls_key: str | None = None,
    ) -> None:
        self.fixture_mode = mode
        self.arrival_counter = ArrivalCounter()
        super().__init__(server_address, FixtureHttpHandler)
        if tls_cert is not None and tls_key is not None:
            context = ssl.SSLContext(ssl.PROTOCOL_TLS_SERVER)
            context.minimum_version = ssl.TLSVersion.TLSv1_2
            context.load_cert_chain(tls_cert, tls_key)
            self.socket = context.wrap_socket(self.socket, server_side=True)

    def server_bind(self) -> None:
        # HTTPServer.server_bind performs a reverse DNS lookup for server_name.
        # Fixtures only need an explicit numeric address, so avoid DNS-dependent startup.
        socketserver.TCPServer.server_bind(self)
        host, port = self.server_address[:2]
        self.server_name = host
        self.server_port = port


class NonHttpHandler(socketserver.BaseRequestHandler):
    def handle(self) -> None:
        self.request.sendall(b"DSHWL-NON-HTTP-FIXTURE\x00\xff\n")


class TimeoutHandler(socketserver.BaseRequestHandler):
    def handle(self) -> None:
        time.sleep(90)


class ThreadingTcpServer(socketserver.ThreadingTCPServer):
    allow_reuse_address = True
    daemon_threads = True


def main() -> None:
    parser = argparse.ArgumentParser()
    parser.add_argument("--mode", required=True, choices=ALL_MODES)
    parser.add_argument("--bind", required=True, type=parse_rfc1918_ipv4)
    parser.add_argument("--port", required=True, type=parse_tcp_port)
    parser.add_argument("--tls-cert")
    parser.add_argument("--tls-key")
    args = parser.parse_args()

    if (args.tls_cert is None) != (args.tls_key is None):
        parser.error("--tls-cert and --tls-key must be supplied together")
    if args.mode in {"non-http", "timeout"} and args.tls_cert is not None:
        parser.error("TLS is only supported by HTTP fixture modes")

    if args.mode == "non-http":
        server: socketserver.BaseServer = ThreadingTcpServer(
            (args.bind, args.port), NonHttpHandler
        )
    elif args.mode == "timeout":
        server = ThreadingTcpServer((args.bind, args.port), TimeoutHandler)
    else:
        server = CredentialFreeHttpServer(
            (args.bind, args.port),
            args.mode,
            tls_cert=args.tls_cert,
            tls_key=args.tls_key,
        )

    scheme = "https" if args.tls_cert is not None else "http"
    print(
        f"fixture={args.mode} scheme={scheme} bind={args.bind} port={args.port}",
        flush=True,
    )
    try:
        server.serve_forever(poll_interval=0.25)
    except KeyboardInterrupt:
        pass
    finally:
        server.server_close()


if __name__ == "__main__":
    main()
