# Troubleshooting Pearl Pull

**PC 1 runs Button (SENDER/client in the logs).** It receives F8 or the Neo's
local Website request. **PC 2 runs Receiver (RECEIVER/server in the logs).** It
receives network requests and sends the right-click. PC 2 does not listen for F8.

## Check a connection without clicking

1. Open **Receiver** on PC 2. Its status should say it is ready. Close any old
   script windows or duplicate receiver apps if it is retrying the listener.
2. On PC 1, open **Button** and click **Check connection**. This sends a ping,
   never a mouse click. Confirm its saved address matches PC 2's home IPv4 address.
3. If PC 2 is not answering, check both pairing codes and the network port. On
   PC 2, use **Allow home network (parent)** and make sure the trusted home
   network is marked **Private**. Guest Wi-Fi or a VPN can isolate the PCs.
4. Keep Windows clocks synchronized on both PCs. Use **Settings → Time &
   language → Date & time → Sync now** if the logs report `clock_skew`.

The sender checks the connection every 30 seconds and keeps working when PC 2
comes back. A `ready` response verifies network and pairing, not Minecraft focus
or the chamber. Bring Minecraft to the front on PC 2, close menus/chat, and aim
at the trapdoor before pressing the pearl button.

## If the Neo button does nothing

In Button on PC 1, click **Copy Neo button URL**. In Stream Deck, use **System →
Website**, paste it into URL, and enable **GET request in background**. Keep the
Neo connected to PC 1, the Stream Deck app running, and the correct profile open.
The app should show **Neo direct button active**.

This path does not synthesize F8. A browser keyboard tester seeing F8 does not
prove that Windows will deliver a global hotkey. The physical keyboard shortcut
is still available; the log records the two paths separately.

## Open and follow the logs

Click **Open logs** in either app window or its pearl tray menu. Files are under
`%LOCALAPPDATA%\MinecraftUtils\logs`. Each name includes its role, UTC start
time, and process ID. Lines contain millisecond UTC timestamps, role, level,
event, and details. Build and runtime details appear at startup.

Find **`request_id`** in the sender log, then the same ID in the receiver log to
follow one attempt. UDP retries reuse the ID to prevent a second click. Pings
have `command=ping` and never click.

| Event | What it establishes |
| --- | --- |
| PC 1 `streamdeck.listening` | The local Neo endpoint is ready. |
| PC 1 `streamdeck.triggered` | An authorized local GET arrived. |
| PC 1 `hotkey.registered` | Windows accepted F8 registration. |
| PC 1 `hotkey.detected` | Windows delivered a hotkey; it does not identify the keyboard device. |
| PC 1 `hotkey.discarded` / `streamdeck.rejected` | A stale/busy event or invalid HTTP request was discarded. No click was requested. |
| PC 1 `request.started` / `packet.sent` | Shows command, destination, retry number, and request ID. |
| PC 2 `packet.received` | A packet reached the receiver. |
| PC 2 `request.accepted` | Authentication and clock checks passed. |
| PC 2 `request.rejected` | Shows malformed input, authentication failure, or clock skew. |
| PC 2 `request.duplicate` | Cached result returned without another click. |
| PC 2 `foreground.inspected` / `focus.checked` | Shows the foreground process and whether Minecraft passed. |
| PC 2 `input.injected` | Windows accepted right-button down and up. |
| PC 2 `request.result` / `reply.sent` | The receiver's result and response were produced. |
| PC 1 `reply.accepted` | Matching authenticated reply arrived. |
| PC 1 `streamdeck.completed` / `hotkey.completed` | Trigger finished, with `receiver_status`. |

`unfocused` means PC 2 needs Minecraft in front. `input-error` means focus changed
or Windows rejected input. `cooldown` means a separate press arrived within
500 ms of the previous click. `clicked` does not establish that the trapdoor or
teleport succeeded. A lost reply can cause a timeout after a click, so check the
chamber before trying again.

`tray.waiting`, `streamdeck.waiting`, and `receiver.waiting` are periodic health
messages. `startup.configured` shows scheduled crash recovery;
`startup.task_unavailable` means Windows required ordinary sign-in startup.
Native/socket failures include error codes where available.

## Size limits and privacy

Each role keeps five sessions, each approximately 1 MiB plus one previous-file
backup: roughly **10 MiB per role**. The launcher has its own bounded files.
Active files from an old process may remain until that process closes. Logs
flush after each line and include no pairing codes, Neo tokens, raw packets,
or HTTP trigger URLs. They contain local IP addresses, file paths, and foreground
process names, so review them before sharing. They do not record other keys or
window titles. Avoid screenshots that expose the pairing code or Neo URL.

If Smart App Control prevents the executable from starting, there may be no
app log. These builds are unsigned; see [setup and Windows security](SETUP.md#windows-security).
