#!/usr/bin/env python3
"""Deterministic credential-free network fixtures for DSH launcher tests."""

from __future__ import annotations

import argparse
import ipaddress
import socketserver
import time
from http.server import BaseHTTPRequestHandler, ThreadingHTTPServer
from pathlib import Path


RFC1918_NETWORKS = (
    ipaddress.ip_network("10.0.0.0/8"),
    ipaddress.ip_network("172.16.0.0/12"),
    ipaddress.ip_network("192.168.0.0/16"),
)
LEGACY_ROOT_BODY = (
    Path(__file__).with_name("legacy-root-0.1.1-rc.2.html").read_bytes()
)


def parse_rfc1918_ipv4(value: str) -> str:
    address = ipaddress.ip_address(value)
    if not isinstance(address, ipaddress.IPv4Address):
        raise argparse.ArgumentTypeError("bind address must be IPv4")
    if not any(address in network for network in RFC1918_NETWORKS):
        raise argparse.ArgumentTypeError("bind address must be RFC1918")
    return str(address)


class FixtureHttpHandler(BaseHTTPRequestHandler):
    server_version = "DshLauncherFixture/1"
    protocol_version = "HTTP/1.1"

    def do_GET(self) -> None:  # noqa: N802 - BaseHTTPRequestHandler API
        mode = self.server.fixture_mode  # type: ignore[attr-defined]
        if mode == "wrong-200":
            self._send(200, b"not a Harness service\n", "text/plain; charset=utf-8")
        elif mode == "fence-403":
            self._send(403, b"request rejected by fixture fence\n", "text/plain; charset=utf-8")
        elif mode == "old-unauthenticated":
            if self.path == "/api":
                self._send(404, b"not found", "text/plain; charset=utf-8")
            elif self.path == "/":
                self._send(200, LEGACY_ROOT_BODY, "text/html; charset=utf-8")
            else:
                self._send(404, b"not found", "text/plain; charset=utf-8")
        elif mode == "redirect-302":
            self.send_response(302)
            self.send_header("Location", "/redirect-target")
            self.send_header("Content-Length", "0")
            self.end_headers()
        elif mode == "notfound-404":
            self._send(404, b"fixture not found\n", "text/plain; charset=utf-8")
        else:
            self._send(500, b"invalid fixture mode\n", "text/plain; charset=utf-8")

    def _send(self, status: int, body: bytes, content_type: str) -> None:
        self.send_response(status)
        self.send_header("Content-Type", content_type)
        self.send_header("Content-Length", str(len(body)))
        self.send_header("Cache-Control", "no-store")
        self.end_headers()
        self.wfile.write(body)

    def log_message(self, _format: str, *_args: object) -> None:
        # Request targets are intentionally never persisted. They may contain credentials
        # when a tester points the wrong client at a fixture.
        return


class CredentialFreeHttpServer(ThreadingHTTPServer):
    daemon_threads = True

    def __init__(self, server_address: tuple[str, int], mode: str) -> None:
        self.fixture_mode = mode
        super().__init__(server_address, FixtureHttpHandler)

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
    parser.add_argument(
        "--mode",
        required=True,
        choices=(
            "wrong-200",
            "fence-403",
            "old-unauthenticated",
            "redirect-302",
            "notfound-404",
            "non-http",
            "timeout",
        ),
    )
    parser.add_argument("--bind", required=True, type=parse_rfc1918_ipv4)
    parser.add_argument("--port", required=True, type=int, choices=range(1, 65536))
    args = parser.parse_args()

    if args.mode == "non-http":
        server: socketserver.BaseServer = ThreadingTcpServer(
            (args.bind, args.port), NonHttpHandler
        )
    elif args.mode == "timeout":
        server = ThreadingTcpServer((args.bind, args.port), TimeoutHandler)
    else:
        server = CredentialFreeHttpServer((args.bind, args.port), args.mode)

    print(f"fixture={args.mode} bind={args.bind} port={args.port}", flush=True)
    server.serve_forever(poll_interval=0.25)


if __name__ == "__main__":
    main()
