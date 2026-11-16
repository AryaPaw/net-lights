"""Bounded HTTPS release fixture for an unmodified installed updater in hosted CI.

Only process-scoped HTTPS_PROXY clients use this server. It never forwards traffic.
The ephemeral CA is trusted by the disposable Windows user, not by bypassing TLS.
"""

import argparse
import datetime
import getpass
import hashlib
import http.server
import json
import os
import re
from pathlib import Path
import socketserver
import ssl
import threading
import time
import unittest


HOSTS = {"github.com", "api.github.com", "release-assets.githubusercontent.com"}
LATEST = "/repos/AryaPaw/net-lights/releases/latest"
LIST = "/repos/AryaPaw/net-lights/releases?per_page=20"


def hosted():
    return (
        os.name == "nt"
        and getpass.getuser().lower() == "runneradmin"
        and all(os.environ.get(k) == v for k, v in {
            "GITHUB_ACTIONS": "true", "RUNNER_ENVIRONMENT": "github-hosted", "CI": "true"
        }.items())
    )


def release(version, payload):
    name = f"NetLights-Setup-win-x64-{version}.exe"
    return {
        "tag_name": f"v{version}", "draft": False, "prerelease": False,
        "assets": [{"name": name, "size": payload["bytes"],
                    "digest": "sha256:" + payload["sha256"].lower(),
                    "browser_download_url": f"https://github.com/AryaPaw/net-lights/releases/download/v{version}/{name}"}],
    }


def native_version(value):
    if not isinstance(value, str) or not re.fullmatch(r"(0|[1-9][0-9]{0,4})\.(0|[1-9][0-9]{0,4})\.(0|[1-9][0-9]{0,4})", value):
        raise ValueError("Fixture requires an exact native version")
    parts = tuple(map(int, value.split(".")))
    if parts < (1, 7, 0) or any(part > 65535 for part in parts):
        raise ValueError("Fixture native version is outside the supported channel")
    return value


def route(host, method, path, version):
    version = native_version(version)
    if host == "github.com" and method == "HEAD" and path == "/":
        return "probe", None
    if host == "api.github.com" and method == "GET":
        if path == LATEST:
            return "latest", "1.6.1"
        if path == LIST:
            return "stable", version
    for version in ("1.6.1", version):
        name = f"NetLights-Setup-win-x64-{version}.exe"
        if method == "GET" and host == "github.com" and path == f"/AryaPaw/net-lights/releases/download/v{version}/{name}":
            return "redirect", version
        if method == "GET" and host == "release-assets.githubusercontent.com" and path == f"/netlights-ci/{version}":
            return "payload", version
    return "reject", None


class Fixture:
    def __init__(self, config, directory):
        self.directory = directory
        self.events = []
        self.lock = threading.Lock()
        self.started = time.monotonic()
        self.native_version = native_version(config.get("nativeVersion"))
        self.payloads = config["payloads"]
        if not isinstance(self.payloads, dict) or set(self.payloads) != {"1.6.1", self.native_version}:
            raise ValueError("Fixture requires exactly the bridge and native payloads")
        for version in ("1.6.1", self.native_version):
            payload = self.payloads[version]
            path = Path(payload["path"])
            if path.stat().st_size != payload["bytes"] or not 0 < payload["bytes"] <= 80 * 1024 * 1024:
                raise ValueError("Fixture payload size mismatch")
            with path.open("rb") as stream:
                digest = hashlib.file_digest(stream, "sha256").hexdigest()
            if digest.lower() != payload["sha256"].lower():
                raise ValueError("Fixture payload digest mismatch")
        self.context = ssl.SSLContext(ssl.PROTOCOL_TLS_SERVER)
        self.context.minimum_version = ssl.TLSVersion.TLSv1_2
        self.context.load_cert_chain(directory / "leaf.pem", directory / "key.pem")

    def record(self, code, version=None):
        with self.lock:
            if len(self.events) >= 128:
                raise RuntimeError("Fixture request budget exceeded")
            self.events.append({"code": code, "version": version,
                                "utc": datetime.datetime.now(datetime.timezone.utc).isoformat(),
                                "elapsedSeconds": round(time.monotonic() - self.started, 3)})
            temporary = self.directory / "events.tmp"
            temporary.write_text(json.dumps(self.events), encoding="utf-8")
            os.replace(temporary, self.directory / "events.json")

    def wait_armed(self, version):
        deadline = time.monotonic() + 15
        while not (self.directory / ("armed-" + version)).is_file():
            if time.monotonic() >= deadline:
                raise TimeoutError("Fixture wasn't armed before the production query timeout")
            time.sleep(0.025)


class Handler(http.server.BaseHTTPRequestHandler):
    protocol_version = "HTTP/1.1"

    def log_message(self, *_args):
        pass  # Never log headers, arbitrary URLs or connection details.

    def do_CONNECT(self):
        if self.path not in {h + ":443" for h in HOSTS}:
            self.server.fixture.record("rejected-connect")
            self.send_error(403)
            return
        host = self.path[:-4]
        self.send_response(200)
        self.end_headers()
        self.wfile.flush()
        # Process a single request inside the verified TLS connection. Closing it
        # avoids sharing TLS/HTTP parser state between different CONNECT hosts.
        with self.server.fixture.context.wrap_socket(self.connection, server_side=True) as connection:
            connection.settimeout(25)
            inner = InnerHandler.__new__(InnerHandler)
            inner.expected_host = host
            inner.__init__(connection, self.client_address, self.server)
        self.close_connection = True


class InnerHandler(Handler):
    def do_CONNECT(self):
        self.send_error(403)

    def do_HEAD(self):
        self.respond()

    def do_GET(self):
        self.respond()

    def respond(self):
        fixture = self.server.fixture
        if self.headers.get("Host") not in (self.expected_host, self.expected_host + ":443"):
            fixture.record("rejected-host")
            self.send_error(403)
            return
        code, version = route(self.expected_host, self.command, self.path, fixture.native_version)
        fixture.record(code + "-request", version)
        if code == "reject":
            self.send_error(403)
            return
        if code in ("latest", "stable"):
            if code == "latest":
                fixture.wait_armed(version)
            value = release(version, fixture.payloads[version])
            if code == "stable":
                value = [value, release("1.6.1", fixture.payloads["1.6.1"])]
            body = json.dumps(value).encode()
            self.send_response(200)
            self.send_header("Content-Type", "application/json")
            self.send_header("Content-Length", str(len(body)))
            self.send_header("Connection", "close")
            self.end_headers()
            self.wfile.write(body)
        elif code == "redirect":
            self.send_response(302)
            self.send_header("Location", f"https://release-assets.githubusercontent.com/netlights-ci/{version}")
            self.send_header("Content-Length", "0")
            self.send_header("Connection", "close")
            self.end_headers()
        elif code == "payload":
            payload = fixture.payloads[version]
            self.send_response(200)
            self.send_header("Content-Type", "application/octet-stream")
            self.send_header("Content-Length", str(payload["bytes"]))
            self.send_header("Connection", "close")
            self.end_headers()
            with Path(payload["path"]).open("rb") as stream:
                while chunk := stream.read(64 * 1024):
                    self.wfile.write(chunk)
            self.wfile.flush()
            fixture.record("payload-complete", version)
        else:
            self.send_response(204)
            self.send_header("Content-Length", "0")
            self.send_header("Connection", "close")
            self.end_headers()
        self.close_connection = True


class Server(socketserver.ThreadingMixIn, http.server.HTTPServer):
    daemon_threads = True
    request_queue_size = 8

    def get_request(self):
        connection, address = super().get_request()
        connection.settimeout(25)
        return connection, address

    def handle_error(self, _request, _address):
        self.fixture.record("handler-error")


class Controls(unittest.TestCase):
    def test_exact_routes(self):
        for version in ("1.7.0", "1.7.1", "1.8.0"):
            with self.subTest(version=version):
                self.assertEqual(route("api.github.com", "GET", LATEST, version), ("latest", "1.6.1"))
                self.assertEqual(route("api.github.com", "GET", LIST, version), ("stable", version))
                for payload in ("1.6.1", version):
                    path = f"/AryaPaw/net-lights/releases/download/v{payload}/NetLights-Setup-win-x64-{payload}.exe"
                    self.assertEqual(route("github.com", "GET", path, version), ("redirect", payload))
                    self.assertEqual(route("release-assets.githubusercontent.com", "GET", f"/netlights-ci/{payload}", version), ("payload", payload))
                for host, method, path in [
                    ("evil.invalid", "GET", LATEST), ("api.github.com", "POST", LIST),
                    ("api.github.com", "GET", LIST + "&page=2"),
                    ("github.com", "GET", "/other/releases/download/v1.7.0/setup.exe"),
                    ("github.com", "GET", f"/AryaPaw/net-lights/releases/download/v{version}/../setup.exe"),
                    ("release-assets.githubusercontent.com", "GET", "/netlights-ci/9.9.9"),
                ]:
                    self.assertEqual(route(host, method, path, version), ("reject", None))
                if version != "1.7.0":
                    self.assertEqual(route("github.com", "GET", "/AryaPaw/net-lights/releases/download/v1.7.0/NetLights-Setup-win-x64-1.7.0.exe", version), ("reject", None))

    def test_invalid_versions_and_payload_sets(self):
        for value in (None, 171, "1.6.1", "1.7", "1.7.01", "1.7.1-beta", "65536.0.0", "1.7.1/../"):
            with self.subTest(value=value), self.assertRaises(ValueError):
                native_version(value)
        for payloads in ({}, {"1.6.1": {}}, {"1.6.1": {}, "1.7.0": {}}, {"1.6.1": {}, "1.7.1": {}, "9.9.9": {}}):
            with self.subTest(payloads=payloads), self.assertRaises(ValueError):
                Fixture({"nativeVersion": "1.7.1", "payloads": payloads}, Path("unused"))

    def test_release_binding(self):
        result = release("1.6.1", {"bytes": 12, "sha256": "A" * 64})
        self.assertEqual(result["tag_name"], "v1.6.1")
        self.assertEqual(result["assets"][0]["digest"], "sha256:" + "a" * 64)
        self.assertFalse(result["draft"] or result["prerelease"])
        self.assertTrue(result["assets"][0]["browser_download_url"].endswith("/v1.6.1/NetLights-Setup-win-x64-1.6.1.exe"))


def main():
    parser = argparse.ArgumentParser()
    parser.add_argument("--directory", type=Path)
    parser.add_argument("--self-test", action="store_true")
    args = parser.parse_args()
    if args.self_test:
        if args.directory:
            parser.error("Self-test doesn't start a server")
        return not unittest.TextTestRunner().run(unittest.defaultTestLoader.loadTestsFromTestCase(Controls)).wasSuccessful()
    if not hosted():
        raise SystemExit("Automatic update fixture requires a disposable hosted Windows runner")
    directory = args.directory.resolve(strict=True)
    if not directory.name.startswith("NetLightsAutoUpdate-"):
        raise SystemExit("Invalid fixture directory")
    config = json.loads((directory / "config.json").read_text(encoding="utf-8"))
    with Server(("127.0.0.1", 0), Handler) as server:
        server.fixture = Fixture(config, directory)
        (directory / "ready.json").write_text(json.dumps({"port": server.server_port}), encoding="utf-8")
        server.serve_forever(poll_interval=0.2)
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
