"""Exercise production C# and PowerShell without generating desktop input."""

import hashlib
import hmac
import json
import os
from pathlib import Path
import shutil
import socket
import subprocess
import tempfile
import threading
import time
import unittest
import uuid


ROOT = Path(__file__).resolve().parents[1]
PWSH = os.environ.get("PEARL_PULL_PWSH") or shutil.which("pwsh")
KEY = "a" * 64  # Public test fixture, never a real pairing code.


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


class PearlPullTests(unittest.TestCase):
    @classmethod
    def setUpClass(cls):
        if not PWSH:
            raise RuntimeError("PowerShell is required: install pwsh or set PEARL_PULL_PWSH.")

    def run_ps(self, *args, check=True):
        result = subprocess.run([PWSH, "-NoLogo", "-NoProfile", *map(str, args)], capture_output=True, text=True, timeout=20)
        if check:
            self.assertEqual(result.returncode, 0, result.stdout + result.stderr)
        return result

    def test_core_checks_and_powershell_syntax(self):
        # Paths are passed through the environment, not interpolated into PowerShell code.
        previous = os.environ.get("PEARL_PULL_TEST_ROOT")
        os.environ["PEARL_PULL_TEST_ROOT"] = str(ROOT)
        try:
            result = self.run_ps("-Command", """
                $ErrorActionPreference = 'Stop'
                $root = $env:PEARL_PULL_TEST_ROOT
                Add-Type -Path @((Join-Path $root 'PearlPull.cs'), (Join-Path $root 'tests/CoreChecks.cs')) -CompilerOptions '/langversion:5'
                Write-Output "Core checks passed: $([CoreChecks]::Run())"
                Get-ChildItem $root -Filter '*.ps1' | ForEach-Object {
                    $tokens = $null; $errors = $null
                    [void][System.Management.Automation.Language.Parser]::ParseFile($_.FullName, [ref]$tokens, [ref]$errors)
                    if ($errors.Count) { throw ($errors | Out-String) }
                }
            """)
            self.assertIn("Core checks passed: 25", result.stdout)
        finally:
            if previous is None:
                os.environ.pop("PEARL_PULL_TEST_ROOT", None)
            else:
                os.environ["PEARL_PULL_TEST_ROOT"] = previous

    def test_live_receiver_and_sender_scripts(self):
        with tempfile.TemporaryDirectory() as directory:
            with socket.socket(socket.AF_INET, socket.SOCK_DGRAM) as reservation:
                reservation.bind(("127.0.0.1", 0))
                port = reservation.getsockname()[1]
            config = {"Port": port, "PairingCode": KEY, "Address": "127.0.0.1"}
            config_path = Path(directory) / "settings.json"
            config_path.write_text(json.dumps(config))
            log_path = Path(directory) / "receiver.log"
            with log_path.open("w+") as log:
                process = subprocess.Popen([
                    PWSH, "-NoLogo", "-NoProfile", "-File", str(ROOT / "Receiver.ps1"),
                    "-DryRun", "-BindAddress", "127.0.0.1", "-ConfigPath", str(config_path)
                ], stdout=log, stderr=subprocess.STDOUT)
                try:
                    with socket.socket(socket.AF_INET, socket.SOCK_DGRAM) as client:
                        client.settimeout(0.2)
                        deadline = time.monotonic() + 15
                        while True:
                            ping = request()
                            client.sendto(ping, ("127.0.0.1", port))
                            try:
                                self.assertEqual(verify_reply(client.recv(1024), ping), "ready")
                                break
                            except socket.timeout:
                                if process.poll() is not None or time.monotonic() > deadline:
                                    log.seek(0)
                                    self.fail("Receiver failed to start: " + log.read())
                        click = request("click")
                        client.sendto(click, ("127.0.0.1", port))
                        ack = client.recv(1024)
                        self.assertEqual(verify_reply(ack, click), "dry-run")
                        client.sendto(click, ("127.0.0.1", port))
                        self.assertEqual(client.recv(1024), ack)
                        bad = bytearray(request("click"))
                        bad[-1] = ord("z")
                        client.sendto(bad, ("127.0.0.1", port))
                        with self.assertRaises(socket.timeout):
                            client.recv(1024)
                    result = self.run_ps("-File", ROOT / "Sender.ps1", "-ConfigPath", config_path, "-Test")
                    self.assertIn("Network check: ready", result.stdout)
                    result = self.run_ps("-File", ROOT / "Sender.ps1", "-ConfigPath", config_path, "-Once")
                    self.assertIn("dry-run", result.stdout)
                finally:
                    process.terminate()
                    try:
                        process.wait(timeout=5)
                    except subprocess.TimeoutExpired:
                        process.kill()
                        process.wait(timeout=5)

    def test_sender_retry_reuses_id_and_ignores_forged_reply(self):
        with tempfile.TemporaryDirectory() as directory, socket.socket(socket.AF_INET, socket.SOCK_DGRAM) as server:
            server.bind(("127.0.0.1", 0))
            server.settimeout(15)
            path = Path(directory) / "sender.json"
            path.write_text(json.dumps({"Address": "127.0.0.1", "Port": server.getsockname()[1], "PairingCode": KEY}))
            received = []
            failures = []

            def respond():
                try:
                    packet, peer = server.recvfrom(1024)
                    received.append(packet)
                    rid = packet.decode("ascii").split("|")[1]
                    server.sendto(f"PP1-ACK|{rid}|clicked|{'0' * 64}".encode(), peer)
                    second, peer = server.recvfrom(1024)
                    received.append(second)
                    server.sendto(sign(f"PP1-ACK|{rid}|unfocused"), peer)
                except Exception as exc:
                    failures.append(exc)

            thread = threading.Thread(target=respond)
            thread.start()
            try:
                result = self.run_ps("-File", ROOT / "Sender.ps1", "-ConfigPath", path, "-Once", check=False)
            finally:
                thread.join(timeout=16)
            self.assertFalse(thread.is_alive())
            self.assertEqual(failures, [])
            self.assertEqual(result.returncode, 1, result.stdout + result.stderr)
            self.assertIn("unfocused", result.stdout)
            self.assertEqual(len(received), 2)
            self.assertEqual(received[0], received[1], "A retry must reuse the exact request to prevent a second click")


if __name__ == "__main__":
    unittest.main()
