# Following a Pearl Pull request

**PC 1 is the SENDER/client.** It receives F8 through Windows' global hotkey
handler, or a direct Stream Deck Website request. **PC 2 is the RECEIVER/server.**
It receives authenticated network requests and sends the right-click. It does
not listen for F8. Keep Minecraft focused on PC 2 when testing real clicks.

## Open the logs

In the installed tray apps, click **Open logs** in the app window or its tray
menu. The app uses the same sender/receiver events described below and adds
`tray.waiting` heartbeats and `startup.configured` recovery details.

Run **Open-Logs.cmd** on each PC, or open
`%LOCALAPPDATA%\MinecraftUtils\logs` in Explorer. Sender and receiver files
have separate names containing their role, UTC start time, and process ID.
The console prints the same trace lines, including the log filename, build
version, role, runtime, and saved configuration path at startup.

Each line includes a UTC timestamp with milliseconds, role, level, event, and
details. Click requests have a **`request_id` that matches on both PCs**. Compare
that ID to follow a single attempt; UDP retries reuse it to avoid a second click.
Startup connectivity probes have `command=ping` and never request a click.

Logs are flushed after each line. Each session rotates at approximately 1 MiB,
keeping one `.log.previous` backup. The latest five sessions per role are kept;
files still open by another Windows process may remain until a later cleanup.
If files cannot be written, console logging continues. You can override the
folder with `-LogDirectory` on Sender.ps1, Receiver.ps1, or Check-Connection.ps1.

Pairing codes, local trigger tokens, signatures, raw packets, and HTTP URLs are
excluded. Hex strings of 64 characters are also redacted from trace details.
Logs include local IP addresses, file paths, and foreground process names, so
review them before sharing. They do not record window titles or other keys.
The pairing code and local trigger URL are shown only by the relevant setup
prompts, outside the trace logger. Avoid sharing those setup-window screenshots.
Errors that prevent PowerShell or the C# helper from loading happen before the
logger can start; use the [Windows security guide](STARTUP-AND-WINDOWS-SECURITY.md).

## Expected sequence

| PC / event | What it establishes |
| --- | --- |
| PC 1 `streamdeck.listening` | The local direct trigger is ready for the Neo Website action. |
| PC 1 `streamdeck.triggered` | An authorized local GET arrived. Keyboard input was not involved. |
| PC 1 `hotkey.registered` | Windows accepted the F8 registration. This alone does not prove a press was detected. |
| PC 1 `hotkey.detected` | Windows delivered the hotkey. Includes key, count, queue age, and test mode. Windows does not identify the physical device in this message. |
| PC 1 `hotkey.discarded` | A stale queued hotkey was dropped. No click request is sent for it. |
| PC 1 `request.started` / `packet.sent` | Includes request ID, command, destination, local socket, and retry number. |
| PC 2 `packet.received` | The packet reached the receiver. Includes source address and byte count. |
| PC 2 `request.accepted` | Format, authentication, and clock checks passed. |
| PC 2 `request.rejected` | The reason identifies malformed input, failed authentication, or authenticated clock skew. No click occurs. |
| PC 2 `request.duplicate` | This ID was already handled; the cached result is returned without another click. |
| PC 2 `foreground.inspected` / `focus.checked` | Shows the foreground process and whether Minecraft passed the focus check. |
| PC 2 `input.injected` | Windows accepted right-button down and up. This does not establish that the trapdoor or teleport succeeded. |
| PC 2 `request.result` / `reply.sent` | The result and authenticated response were produced. |
| PC 1 `reply.accepted` | The matching authenticated reply arrived, with status and elapsed time. |
| PC 1 `streamdeck.completed` or `hotkey.completed` | The trigger finished; inspect `receiver_status`. |

While idle, the sender prints `hotkey.waiting` and/or `streamdeck.waiting` every
30 seconds; the receiver prints `receiver.waiting` with packet count. These
heartbeats establish that their loops are alive, not that the chamber is armed.

If PC 1 logs `packet.sent` but PC 2 never logs `packet.received`, use
[Check-Connection.cmd](CONNECTION-TROUBLESHOOTING.md) to check address, port,
firewall, and network isolation. `authentication_failed` suggests different
pairing settings. `clock_skew` includes signed request age; sync both Windows
clocks. Replies rejected on PC 1 also include a reason. Errors include exception
type and native Windows/socket error codes where available.

A timeout after a click request can mean the reply was lost after the click.
Check the chamber before retrying. A ping timeout explicitly says no click
was requested.

## Compare physical F8 and the Neo without clicking

1. On PC 1, close the normal sender. If it runs as a startup task, run
   `Manage-Startup.cmd -Role Sender -Action Stop` first.
2. Open **Test-Hotkey.cmd**. Look for `hotkey.registered` and `test_only=True`.
3. Press physical F8 once, then your Neo's old Hotkey F8 button once. Each
   delivered hotkey produces `hotkey.detected` and `hotkey.test_passed`.
   The test never contacts PC 2 and needs no pairing settings.
4. If only the physical key is detected, use the recommended
   [Neo Website action](STREAM-DECK-NEO.md). A browser tester sees browser key
   events; it does not prove Windows will deliver a global `WM_HOTKEY` message.
5. Close the tester and restart the normal sender or its startup task.

With the direct Website button, use normal mode and look for
`streamdeck.triggered`; the hotkey tester intentionally does not start that
endpoint. For a no-click end-to-end check, start PC 2's receiver with
`Start-Receiver.cmd -DryRun`. Neo presses should return `dry-run`. Restart the
receiver normally before playing.
