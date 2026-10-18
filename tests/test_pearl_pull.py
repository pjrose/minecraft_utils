"""Exercise the tray apps' compiled core without generating desktop input."""

import hashlib
import hmac
import http.client
import json
import os
from pathlib import Path
import re
import shutil
import socket
import subprocess
import tempfile
import threading
import time
import unittest
import uuid
from unittest.mock import Mock

ROOT = Path(__file__).resolve().parents[1]
DOTNET = os.environ.get("PEARL_PULL_DOTNET") or shutil.which("dotnet")
HOST = ROOT / "artifacts" / "tests" / "PearlPull.TestHost.dll"
KEY = "a" * 64  # Public fixtures, never real pairing credentials.
TOKEN = "b" * 64


def sign(body):
    return (body + "|" + hmac.new(bytes.fromhex(KEY), body.encode("ascii"), hashlib.sha256).hexdigest()).encode("ascii")


def request(command="ping"):
    return sign(f"PP1|{uuid.uuid4().hex}|{int(time.time() * 1000)}|{command}")


def verify_reply(packet, request_packet):
    fields = packet.decode("ascii").split("|")
    assert len(fields) == 4 and fields[0] == "PP1-ACK"
    assert fields[1] == request_packet.decode("ascii").split("|")[1]
    assert hmac.compare_digest(packet, sign("|".join(fields[:3])))
    return fields[2]


def wait_for_receiver(client, port, is_running=lambda: True, timeout=15):
    """Windows can report ICMP port-unreachable as WinError 10054 before bind."""
    deadline = time.monotonic() + timeout
    while is_running() and time.monotonic() < deadline:
        ping = request()
        try:
            client.sendto(ping, ("127.0.0.1", port))
            packet = client.recv(1024)
        except ConnectionResetError:
            time.sleep(0.02)  # Avoid flooding a port Windows says is not bound yet.
            continue
        except socket.timeout:
            continue  # Only startup probes may retry these transient responses.
        fields = packet.decode("ascii").split("|")
        if len(fields) == 4 and fields[1] != ping.decode("ascii").split("|")[1]:
            continue  # A reply to an earlier readiness probe arrived late.
        assert verify_reply(packet, ping) == "ready"
        return
    raise TimeoutError("Receiver did not become ready before the startup deadline")


def unused_port(kind=socket.SOCK_DGRAM):
    with socket.socket(socket.AF_INET, kind) as reservation:
        reservation.bind(("127.0.0.1", 0))
        return reservation.getsockname()[1]


def write_settings(folder, port):
    path = folder / "settings.json"
    path.write_text(json.dumps({"Address": "127.0.0.1", "Port": port, "PairingCode": KEY, "StreamDeckToken": TOKEN}), encoding="utf-8")
    return path


def stop(process):
    if process.poll() is None:
        process.terminate()
        try:
            process.wait(timeout=5)
        except subprocess.TimeoutExpired:
            process.kill()
            process.wait(timeout=5)


def read_logs(folder, role):
    return "\n".join(path.read_text(encoding="utf-8") for path in folder.glob(f"{role}-*.log"))


class PearlPullTests(unittest.TestCase):
    @classmethod
    def setUpClass(cls):
        if not DOTNET:
            raise RuntimeError(".NET SDK 8 is required: install dotnet or set PEARL_PULL_DOTNET.")
        result = subprocess.run([DOTNET, "build", str(ROOT / "tests/PearlPull.TestHost.csproj"), "-c", "Release", "-o", str(HOST.parent), "--nologo"], capture_output=True, text=True, timeout=120)
        if result.returncode:
            raise RuntimeError(result.stdout + result.stderr)

    def run_host(self, *args, check=True):
        result = subprocess.run([DOTNET, str(HOST), *map(str, args)], capture_output=True, text=True, encoding="utf-8", timeout=20)
        if check:
            self.assertEqual(result.returncode, 0, result.stdout + result.stderr)
        return result

    def spawn_host(self, output, *args):
        process = subprocess.Popen([DOTNET, str(HOST), *map(str, args)], stdout=output, stderr=subprocess.STDOUT)
        return process

    def test_core_checks(self):
        self.assertIn("Core checks passed: 25", self.run_host("core").stdout)

    def test_pairing_approval_crypto_retry_expiry_and_request_binding(self):
        result = self.run_host("pairing-core")
        self.assertIn("Pairing checks passed: 30", result.stdout)
        self.assertNotIn(KEY, result.stdout + result.stderr)

    def test_pairing_retries_lost_approval_and_ignores_forged_replies(self):
        result = self.run_host("pairing-replies", unused_port())
        self.assertIn("Pairing retry and forged-reply checks passed", result.stdout)
        self.assertNotIn(KEY, result.stdout + result.stderr)

    def test_offline_pairing_has_bounded_timeout_and_setup_guidance(self):
        self.assertIn("Offline pairing timeout passed", self.run_host("pairing-offline", unused_port()).stdout)

    def test_live_pairing_accept_decline_cancel_and_existing_connection(self):
        for decision in ("accept", "decline", "cancel"):
            with self.subTest(decision=decision), tempfile.TemporaryDirectory() as directory:
                result = self.run_host("pairing-live", decision, unused_port(), directory)
                self.assertIn(f"Live pairing {decision} passed", result.stdout)
                trace = read_logs(Path(directory), "pairing")
                self.assertIn("pairing.requested", trace)
                self.assertNotIn(KEY, trace + result.stdout + result.stderr)
                self.assertNotIn("command=click", trace)
                self.assertEqual("pairing.completed" in trace, decision == "accept")

    def test_readiness_retries_windows_connection_reset_but_not_other_errors(self):
        client = Mock()
        def ready(_):
            ping = client.sendto.call_args.args[0]
            return sign(f"PP1-ACK|{ping.decode().split('|')[1]}|ready")
        responses = iter([ConnectionResetError(10054, "UDP receiver not yet bound"), socket.timeout()])
        def receive(size):
            try:
                raise next(responses)
            except StopIteration:
                return ready(size)
        client.recv.side_effect = receive
        wait_for_receiver(client, 48123)
        self.assertEqual(client.sendto.call_count, 3)
        client.recv.side_effect = PermissionError("a real failure must not be hidden")
        with self.assertRaises(PermissionError):
            wait_for_receiver(client, 48123)
        client.recv.side_effect = ConnectionResetError(10054, "not yet bound")
        with self.assertRaises(TimeoutError):
            wait_for_receiver(client, 48123, timeout=0.01)

    def test_live_receiver_and_sender(self):
        with tempfile.TemporaryDirectory() as directory:
            folder = Path(directory)
            port = unused_port()
            config = write_settings(folder, port)
            with (folder / "process-output.txt").open("w+", encoding="utf-8") as output:
                receiver = self.spawn_host(output, "receiver", config, folder)
                try:
                    with socket.socket(socket.AF_INET, socket.SOCK_DGRAM) as client:
                        client.settimeout(0.2)
                        try:
                            wait_for_receiver(client, port, lambda: receiver.poll() is None)
                        except TimeoutError:
                            output.seek(0)
                            self.fail("Receiver failed to start: " + output.read())
                        click = request("click")
                        client.sendto(click, ("127.0.0.1", port))
                        ack = client.recv(1024)
                        self.assertEqual(verify_reply(ack, click), "dry-run")
                        client.sendto(click, ("127.0.0.1", port))
                        self.assertEqual(client.recv(1024), ack)
                        bad = bytearray(request("click")); bad[-1] = ord("z")
                        client.sendto(bad, ("127.0.0.1", port))
                        with self.assertRaises(socket.timeout): client.recv(1024)
                        expired = sign(f"PP1|{uuid.uuid4().hex}|{int(time.time() * 1000) - 60000}|click")
                        client.sendto(expired, ("127.0.0.1", port))
                        with self.assertRaises(socket.timeout): client.recv(1024)
                    self.assertIn("STATUS=ready", self.run_host("send", config, folder, "ping").stdout)
                    # Cooldown has elapsed during the authentication/clock checks above.
                    time.sleep(0.1)
                    self.assertIn("STATUS=dry-run", self.run_host("send", config, folder, "click").stdout)
                    trace = read_logs(folder, "receiver")
                    for event in ("[RECEIVER]", "request.duplicate", "reason=authentication_failed", "reason=clock_skew", "reply.sent"):
                        self.assertIn(event, trace)
                    self.assertNotIn(KEY, trace)
                finally:
                    stop(receiver)

    def test_streamdeck_direct_trigger_and_correlated_logs(self):
        with tempfile.TemporaryDirectory() as directory:
            folder = Path(directory)
            receiver_port, local_port = unused_port(), unused_port(socket.SOCK_STREAM)
            config = write_settings(folder, receiver_port)
            trigger_path = "/pull/" + TOKEN
            def get(path, method="GET", headers=None):
                connection = http.client.HTTPConnection("127.0.0.1", local_port, timeout=3)
                try:
                    connection.request(method, path, headers=headers or {})
                    reply = connection.getresponse()
                    return reply.status, reply.read().decode()
                finally:
                    connection.close()
            with (folder / "process-output.txt").open("w+", encoding="utf-8") as output:
                children = [self.spawn_host(output, "receiver", config, folder), self.spawn_host(output, "streamdeck", config, folder, local_port)]
                try:
                    deadline = time.monotonic() + 15
                    while True:
                        try:
                            self.assertEqual(get("/health")[0], 200)
                            break
                        except (OSError, http.client.HTTPException):
                            if any(child.poll() is not None for child in children) or time.monotonic() > deadline:
                                output.seek(0); self.fail("Direct trigger failed to start: " + output.read())
                            time.sleep(0.05)
                    with socket.socket(socket.AF_INET, socket.SOCK_DGRAM) as client:
                        client.settimeout(0.2)
                        try:
                            wait_for_receiver(client, receiver_port, lambda: children[0].poll() is None)
                        except TimeoutError:
                            output.seek(0)
                            self.fail("Receiver failed to start: " + output.read())
                    self.assertEqual(get("/pull/" + "c" * 64)[0], 403)
                    self.assertEqual(get(trigger_path, headers={"Host": "untrusted.example"})[0], 403)
                    self.assertEqual(get(trigger_path, method="POST")[0], 405)
                    self.assertEqual(get(trigger_path), (200, "dry-run\n"))
                    sender_logs, receiver_logs = read_logs(folder, "sender"), read_logs(folder, "receiver")
                    ids = re.findall(r"streamdeck.triggered request_id=([a-f0-9]{32})", sender_logs)
                    self.assertEqual(len(ids), 1, "Only the authorized GET may request a click")
                    self.assertIn(f"request.result request_id={ids[0]} command=click status=dry-run", receiver_logs)
                    self.assertIn("streamdeck.completed", sender_logs)
                    self.assertIn("keyboard_input_required=false", sender_logs)
                    self.assertEqual(receiver_logs.count("command=click"), 2)
                    for secret in (KEY, TOKEN, trigger_path): self.assertNotIn(secret, sender_logs + receiver_logs)
                    stop(children[0])
                    with socket.socket(socket.AF_INET, socket.SOCK_DGRAM) as delayed:
                        delayed.bind(("127.0.0.1", receiver_port)); delayed.settimeout(3)
                        replies = []
                        pending = threading.Thread(target=lambda: replies.append(get(trigger_path)))
                        pending.start()
                        try:
                            packet, peer = delayed.recvfrom(1024)
                            rid = packet.decode().split("|")[1]
                            self.assertEqual(get(trigger_path)[0], 409)
                            delayed.sendto(sign(f"PP1-ACK|{rid}|dry-run"), peer)
                        finally:
                            pending.join(timeout=5)
                        self.assertFalse(pending.is_alive())
                        self.assertEqual(replies, [(200, "dry-run\n")])
                        delayed.settimeout(0.3)
                        with self.assertRaises(socket.timeout): delayed.recvfrom(1024)
                finally:
                    for child in children: stop(child)

    def test_tray_settings_migrate_and_save_without_losing_pairing(self):
        with tempfile.TemporaryDirectory() as directory:
            path = Path(directory) / "settings.json"
            path.write_text(json.dumps({"Address": "127.0.0.1", "Port": 48123, "PairingCode": KEY}), encoding="utf-8-sig")
            result = self.run_host("settings", path)
            self.assertIn("Migration and atomic save passed", result.stdout)
            self.assertNotIn(KEY, result.stdout + result.stderr)
            self.assertEqual(list(Path(directory).glob("*.tmp")), [])

    def test_log_redaction_rotation_retention_and_fallback(self):
        with tempfile.TemporaryDirectory() as directory:
            result = self.run_host("logs", directory)
            self.assertIn("log.file_unavailable", result.stdout)
            self.assertIn("fallback.succeeded", result.stdout)
            folder = Path(directory)
            redacted = read_logs(folder, "redaction")
            self.assertNotIn(KEY, redacted)
            self.assertIn("secret=[REDACTED]  injected-line", redacted)
            self.assertIn("win32_error=5", redacted)
            self.assertNotIn("\ninjected-line", redacted)
            self.assertEqual(len(list(folder.glob("rotation-*.log"))), 5)
            self.assertEqual(len(list(folder.glob("rotation-*.log.previous"))), 5)
            for path in folder.glob("rotation-*.log*"):
                self.assertLess(path.stat().st_size, 1024 * 1024 + 8192)

    def test_sender_retry_reuses_id_and_ignores_forged_reply(self):
        with tempfile.TemporaryDirectory() as directory, socket.socket(socket.AF_INET, socket.SOCK_DGRAM) as server:
            server.bind(("127.0.0.1", 0)); server.settimeout(15)
            config = write_settings(Path(directory), server.getsockname()[1])
            received, failures = [], []
            def respond():
                try:
                    packet, peer = server.recvfrom(1024); received.append(packet)
                    rid = packet.decode("ascii").split("|")[1]
                    server.sendto(f"PP1-ACK|{rid}|clicked|{'0' * 64}".encode(), peer)
                    second, peer = server.recvfrom(1024); received.append(second)
                    server.sendto(sign(f"PP1-ACK|{rid}|unfocused"), peer)
                except Exception as exc: failures.append(exc)
            thread = threading.Thread(target=respond); thread.start()
            try: result = self.run_host("send", config, directory, "click", check=False)
            finally: thread.join(timeout=16)
            self.assertFalse(thread.is_alive()); self.assertEqual(failures, [])
            self.assertEqual(result.returncode, 1, result.stdout + result.stderr)
            self.assertIn("STATUS=unfocused", result.stdout)
            self.assertEqual(len(received), 2)
            self.assertEqual(received[0], received[1], "Retries must not become a second click")

    def test_offline_ping_fails_without_suggesting_a_click(self):
        with tempfile.TemporaryDirectory() as directory, socket.socket(socket.AF_INET, socket.SOCK_DGRAM) as silent:
            silent.bind(("127.0.0.1", 0))
            config = write_settings(Path(directory), silent.getsockname()[1])
            result = self.run_host("send", config, directory, "ping", check=False)
            self.assertNotEqual(result.returncode, 0)
            self.assertIn("No reply from PC 2", result.stdout + result.stderr)
            self.assertIn("no click was requested", result.stdout + result.stderr)
            self.assertNotIn("click may have happened", result.stdout + result.stderr)
            self.assertNotIn(KEY, result.stdout + result.stderr)

    def test_graceful_receiver_stop_releases_port(self):
        with tempfile.TemporaryDirectory() as directory:
            self.assertIn("Graceful stop released the port", self.run_host("stop", unused_port(), directory).stdout)


if __name__ == "__main__":
    unittest.main()
