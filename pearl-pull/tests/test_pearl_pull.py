"""Exercise production C# and PowerShell without generating desktop input."""

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
                    "-DryRun", "-BindAddress", "127.0.0.1", "-ConfigPath", str(config_path), "-LogDirectory", directory
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
                        expired = sign(f"PP1|{uuid.uuid4().hex}|{int(time.time() * 1000) - 60000}|click")
                        client.sendto(expired, ("127.0.0.1", port))
                        with self.assertRaises(socket.timeout):
                            client.recv(1024)
                    result = self.run_ps("-File", ROOT / "Sender.ps1", "-ConfigPath", config_path, "-Test")
                    self.assertIn("Network check: ready", result.stdout)
                    for role in ("Sender", "Receiver"):
                        result = self.run_ps("-File", ROOT / "Check-Connection.ps1", "-ConfigPath", config_path, "-Role", role)
                        self.assertIn("PASS: received an authenticated ready reply", result.stdout)
                        fingerprint = hashlib.sha256(bytes.fromhex(KEY)).hexdigest()[:12].upper()
                        self.assertIn("Pairing fingerprint: " + fingerprint, result.stdout)
                        self.assertNotIn(KEY, result.stdout + result.stderr)
                    result = self.run_ps("-File", ROOT / "Sender.ps1", "-ConfigPath", config_path, "-Once")
                    self.assertIn("dry-run", result.stdout)
                    trace = "\n".join(path.read_text() for path in Path(directory).glob("receiver-*.log"))
                    self.assertIn("[RECEIVER]", trace)
                    self.assertIn("request.duplicate", trace)
                    self.assertIn("reason=authentication_failed", trace)
                    self.assertIn("reason=clock_skew", trace)
                    self.assertIn("reply.sent", trace)
                    self.assertNotIn(KEY, trace)
                finally:
                    process.terminate()
                    try:
                        process.wait(timeout=5)
                    except subprocess.TimeoutExpired:
                        process.kill()
                        process.wait(timeout=5)

    def test_streamdeck_direct_trigger_and_correlated_logs(self):
        with tempfile.TemporaryDirectory() as directory:
            folder = Path(directory)
            with socket.socket(socket.AF_INET, socket.SOCK_DGRAM) as udp, socket.socket() as tcp:
                udp.bind(("127.0.0.1", 0))
                tcp.bind(("127.0.0.1", 0))
                receiver_port, local_port = udp.getsockname()[1], tcp.getsockname()[1]
            config_path = folder / "settings.json"
            config_path.write_text(json.dumps({"Port": receiver_port, "PairingCode": KEY, "Address": "127.0.0.1"}))
            args = ["-ConfigPath", str(config_path), "-LogDirectory", directory]
            setup = self.run_ps("-File", ROOT / "Sender.ps1", *args, "-ShowStreamDeckUrl", "-StreamDeckPort", local_port)
            token = json.loads(config_path.read_text(encoding="utf-8-sig"))["StreamDeckToken"]
            self.assertNotEqual(token, KEY)
            trigger_path = "/pull/" + token
            self.assertIn(f"http://127.0.0.1:{local_port}{trigger_path}", setup.stdout)
            setup_again = self.run_ps("-File", ROOT / "Sender.ps1", *args, "-ShowStreamDeckUrl", "-StreamDeckPort", local_port)
            self.assertIn(token, setup_again.stdout, "Setup must preserve the button's saved URL")

            def get(path, method="GET", headers=None):
                connection = http.client.HTTPConnection("127.0.0.1", local_port, timeout=3)
                try:
                    connection.request(method, path, headers=headers or {})
                    reply = connection.getresponse()
                    return reply.status, reply.read().decode()
                finally:
                    connection.close()

            children = []
            with (folder / "process-output.txt").open("w+") as output:
                try:
                    children.append(subprocess.Popen([
                        PWSH, "-NoLogo", "-NoProfile", "-File", str(ROOT / "Receiver.ps1"),
                        *args, "-DryRun", "-BindAddress", "127.0.0.1"
                    ], stdout=output, stderr=subprocess.STDOUT))
                    children.append(subprocess.Popen([
                        PWSH, "-NoLogo", "-NoProfile", "-File", str(ROOT / "Sender.ps1"),
                        *args, "-StreamDeckOnly", "-StreamDeckPort", str(local_port)
                    ], stdout=output, stderr=subprocess.STDOUT))
                    deadline = time.monotonic() + 15
                    while True:
                        try:
                            self.assertEqual(get("/health")[0], 200)
                            break
                        except (OSError, http.client.HTTPException):
                            if any(child.poll() is not None for child in children) or time.monotonic() > deadline:
                                output.seek(0)
                                self.fail("Direct trigger failed to start: " + output.read())
                            time.sleep(0.05)
                    # Wait for the receiver too; the resident sender can start while it is offline.
                    with socket.socket(socket.AF_INET, socket.SOCK_DGRAM) as client:
                        client.settimeout(0.2)
                        while True:
                            ping = request()
                            client.sendto(ping, ("127.0.0.1", receiver_port))
                            try:
                                self.assertEqual(verify_reply(client.recv(1024), ping), "ready")
                                break
                            except socket.timeout:
                                if time.monotonic() > deadline:
                                    self.fail("Receiver did not become ready")
                    self.assertEqual(get("/pull/" + "b" * 64)[0], 403)
                    self.assertEqual(get(trigger_path, headers={"Host": "untrusted.example"})[0], 403)
                    self.assertEqual(get(trigger_path, method="POST")[0], 405)
                    self.assertEqual(get(trigger_path), (200, "dry-run\n"))
                    sender_logs = "\n".join(p.read_text() for p in folder.glob("sender-*.log"))
                    receiver_logs = "\n".join(p.read_text() for p in folder.glob("receiver-*.log"))
                    ids = re.findall(r"streamdeck.triggered request_id=([a-f0-9]{32})", sender_logs)
                    self.assertEqual(len(ids), 1, "Only the authorized GET may request a click")
                    self.assertIn(f"request.result request_id={ids[0]} command=click status=dry-run", receiver_logs)
                    self.assertIn("streamdeck.completed", sender_logs)
                    self.assertIn("keyboard_input_required=false", sender_logs)
                    self.assertNotIn("hotkey.registered", sender_logs)
                    self.assertEqual(receiver_logs.count("command=click"), 2)  # accepted + result
                    for secret in (KEY, token, trigger_path):
                        self.assertNotIn(secret, sender_logs + receiver_logs)
                    # Hold a real request open, then prove another press is discarded, not queued.
                    children[0].terminate()
                    children[0].wait(timeout=5)
                    with socket.socket(socket.AF_INET, socket.SOCK_DGRAM) as delayed:
                        delayed.bind(("127.0.0.1", receiver_port))
                        delayed.settimeout(3)
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
                        with self.assertRaises(socket.timeout):
                            delayed.recvfrom(1024)
                finally:
                    for child in children:
                        child.terminate()
                    for child in children:
                        try:
                            child.wait(timeout=5)
                        except subprocess.TimeoutExpired:
                            child.kill()
                            child.wait(timeout=5)

    def test_tray_settings_migrate_and_save_without_losing_pairing(self):
        with tempfile.TemporaryDirectory() as directory:
            path = Path(directory) / "sender.json"
            path.write_text(json.dumps({"Address": "127.0.0.1", "Port": 48123, "PairingCode": KEY}), encoding="utf-8-sig")
            environment = os.environ.copy()
            environment.update(PEARL_PULL_TEST_ROOT=str(ROOT), PEARL_PULL_TEST_CONFIG=str(path))
            result = subprocess.run([PWSH, "-NoLogo", "-NoProfile", "-Command", """
                $ErrorActionPreference = 'Stop'
                Add-Type -Path @((Join-Path $env:PEARL_PULL_TEST_ROOT 'PearlPull.cs'), (Join-Path $env:PEARL_PULL_TEST_ROOT 'tray/Settings.cs'))
                $value = [PearlPull.Tray.Settings]::LoadFrom($env:PEARL_PULL_TEST_CONFIG, $false)
                $key = $value.PairingCode
                $value.StreamDeckToken = [PearlPull.Protocol]::NewKey()
                $value.SaveTo($env:PEARL_PULL_TEST_CONFIG, $false)
                $loaded = [PearlPull.Tray.Settings]::LoadFrom($env:PEARL_PULL_TEST_CONFIG, $false)
                if ($loaded.PairingCode -ne $key -or $loaded.StreamDeckToken -ne $value.StreamDeckToken) { throw 'Migration lost a secret.' }
                $value.Address = 'not an IP'
                try { $value.SaveTo($env:PEARL_PULL_TEST_CONFIG, $false); throw 'Invalid configuration was saved.' }
                catch { if ($_.Exception.Message -eq 'Invalid configuration was saved.') { throw } }
                $unchanged = [PearlPull.Tray.Settings]::LoadFrom($env:PEARL_PULL_TEST_CONFIG, $false)
                if ($unchanged.Address -ne '127.0.0.1') { throw 'Failed save damaged existing settings.' }
                Write-Output 'Migration and atomic save passed'
            """], env=environment, capture_output=True, text=True, timeout=20)
            self.assertEqual(result.returncode, 0, result.stdout + result.stderr)
            self.assertIn("Migration and atomic save passed", result.stdout)
            self.assertNotIn(KEY, result.stdout + result.stderr)
            self.assertEqual(list(Path(directory).glob("*.tmp")), [])

    def test_log_redaction_rotation_retention_and_fallback(self):
        with tempfile.TemporaryDirectory() as directory:
            environment = os.environ.copy()
            environment.update(PEARL_PULL_TEST_ROOT=str(ROOT), PEARL_PULL_TEST_LOGS=directory)
            result = subprocess.run([PWSH, "-NoLogo", "-NoProfile", "-Command", """
                $ErrorActionPreference = 'Stop'
                Add-Type -Path (Join-Path $env:PEARL_PULL_TEST_ROOT 'PearlPull.cs')
                $console = [Console]::Out
                try {
                    [Console]::SetOut([IO.TextWriter]::Null)
                    [PearlPull.TraceLog]::Initialize('redaction', $env:PEARL_PULL_TEST_LOGS)
                    [PearlPull.TraceLog]::Write('INFO', 'fixture', "secret=$('a' * 64)`r`ninjected-line")
                    [PearlPull.TraceLog]::Failure('native.failure', [ComponentModel.Win32Exception]::new(5), 'test=true')
                    for ($session = 0; $session -lt 7; $session++) {
                        [PearlPull.TraceLog]::Initialize('rotation', $env:PEARL_PULL_TEST_LOGS)
                        for ($line = 0; $line -lt 270; $line++) {
                            [PearlPull.TraceLog]::Write('TRACE', 'large', ('x' * 4096))
                        }
                    }
                    [PearlPull.TraceLog]::Close()
                }
                finally { [Console]::SetOut($console) }
                $blocked = Join-Path $env:PEARL_PULL_TEST_LOGS 'not-a-directory'
                Set-Content -LiteralPath $blocked -Value 'file'
                [PearlPull.TraceLog]::Initialize('fallback', $blocked)
                [PearlPull.TraceLog]::Write('INFO', 'fallback.succeeded', 'still_running=true')
                [PearlPull.TraceLog]::Close()
            """], env=environment, capture_output=True, text=True, timeout=20)
            self.assertEqual(result.returncode, 0, result.stdout + result.stderr)
            self.assertIn("log.file_unavailable", result.stdout)
            self.assertIn("fallback.succeeded", result.stdout)
            folder = Path(directory)
            redacted = next(folder.glob("redaction-*.log")).read_text()
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

    def test_offline_probe_returns_failure_but_explicit_test_exits_nonzero(self):
        with tempfile.TemporaryDirectory() as directory, socket.socket(socket.AF_INET, socket.SOCK_DGRAM) as silent_receiver:
            # Bound but deliberately silent: exercise real retry/timeout behavior without an input helper.
            silent_receiver.bind(("127.0.0.1", 0))
            path = Path(directory) / "sender.json"
            path.write_text(json.dumps({
                "Address": "127.0.0.1", "Port": silent_receiver.getsockname()[1], "PairingCode": KEY
            }))
            environment = os.environ.copy()
            environment["PEARL_PULL_TEST_ROOT"] = str(ROOT)
            environment["PEARL_PULL_TEST_CONFIG"] = str(path)
            result = subprocess.run([PWSH, "-NoLogo", "-NoProfile", "-Command", """
                $ErrorActionPreference = 'Stop'
                . (Join-Path $env:PEARL_PULL_TEST_ROOT 'Common.ps1')
                $config = Read-Config $env:PEARL_PULL_TEST_CONFIG
                $connection = Test-ReceiverConnection $config
                if ($connection.Ready) { throw 'An offline receiver was marked ready.' }
                if (-not $connection.Message.Contains('No reply from PC 2')) { throw 'Timeout diagnosis was lost.' }
                if (-not $connection.Message.Contains('no click was requested')) { throw 'Ping timeout did not identify a non-click request.' }
                if ($connection.Message.Contains('click may have happened')) { throw 'Ping timeout suggested a click.' }
                Write-Output 'Offline probe returned normally; caller can continue.'
            """], env=environment, capture_output=True, text=True, timeout=20)
            self.assertEqual(result.returncode, 0, result.stdout + result.stderr)
            self.assertIn("Offline probe returned normally", result.stdout)
            result = self.run_ps("-File", ROOT / "Sender.ps1", "-ConfigPath", path, "-Test", check=False)
            self.assertNotEqual(result.returncode, 0)
            self.assertNotIn("Network check: ready", result.stdout)
            self.assertIn("No reply from PC 2", result.stdout + result.stderr)
            result = self.run_ps("-File", ROOT / "Check-Connection.ps1", "-ConfigPath", path, "-Role", "Sender", check=False)
            self.assertEqual(result.returncode, 1)
            self.assertIn("FAIL: no authenticated ready reply", result.stdout)
            self.assertNotIn(KEY, result.stdout + result.stderr)


if __name__ == "__main__":
    unittest.main()
