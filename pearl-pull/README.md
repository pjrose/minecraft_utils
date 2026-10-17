# Pearl Pull for two Windows PCs

Press **F8 on PC 1** to request one right-click on **PC 2**. This uses Windows'
built-in PowerShell and your home network; no Python, paid app, or Stream Deck
is required. A Stream Deck can send the same F8 key if you have one.

For a pearl stasis chamber, the second account must already be standing still
with its crosshair aimed at the correct trapdoor, within reach. Minecraft must
be in the foreground, with menus/chat closed. The pearl must belong to the
player who should teleport. This tool only clicks: it does not aim, detect
danger, build/reset the chamber, or check whether the teleport succeeded.

## First setup

1. Put this entire `pearl-pull` folder on **both PCs**. Keep the scripts and
   `PearlPull.cs` together. If you downloaded a ZIP, right-click the ZIP, choose
   **Properties → Unblock → Apply**, then extract it.
2. On **PC 2**, double-click **Start-Receiver.cmd**. The first launch shows a
   pairing code and its IPv4 addresses. Keep the window open. Use the address
   for your home Ethernet or Wi-Fi connection, typically `192.168.x.x` or
   `10.x.x.x`, rather than a VPN/virtual adapter address.
3. On PC 2, allow this port through Windows Firewall. On a trusted home network
   set the Windows network profile to **Private**, then run the following once
   in **PowerShell as Administrator**:

   ```powershell


   if (-not (Get-NetFirewallRule -Name 'MinecraftUtils-PearlPull' -ErrorAction SilentlyContinue)) {
       New-NetFirewallRule -Name 'MinecraftUtils-PearlPull' -DisplayName 'Minecraft Pearl Pull' -Direction Inbound -Action Allow -Protocol UDP -LocalPort 48123 -Profile Private -RemoteAddress LocalSubnet
   }
   ```

   Run the sender, receiver, and Minecraft normally; they do not need admin
   rights. No router port forwarding is needed.
4. On **PC 1**, double-click **Start-Hotkey.cmd**. Enter PC 2's IPv4 address and
   pairing code. It should report `Network check: ready`, then register F8.
   Keep this window open, too. The network check does not click anything.
5. On PC 2, return to Minecraft, close menus, and aim at the trapdoor. Leave
   that account still. On PC 1, return to the game and press **F8 once**.

**Practice with an expendable pearl before relying on this in combat.** Confirm
that one F8 press closes the intended trapdoor and teleports the expected
player. Also confirm that holding F8 only sends one click, and switching PC 2
to another app results in `unfocused` without a click. Windows input and the
Donut SMP teleport need this local check; automated networking tests cannot
establish that the chamber works.

## Each play session

- PC 2: open **Start-Receiver.cmd**, switch to Minecraft, and aim at the trapdoor.
- PC 1: open **Start-Hotkey.cmd**, then play. Press F8 when needed.
- Close the utility windows to stop. Re-arm the chamber after using the pearl.

The pairing/address settings are saved separately for each Windows user in
`%LOCALAPPDATA%\MinecraftUtils\pearl-pull-receiver.json` and
`%LOCALAPPDATA%\MinecraftUtils\pearl-pull-sender.json`. They stay outside the
project folder. Keep the pairing code private. To pair a new sender, open
PowerShell in this folder on PC 2 and run:

```powershell
.\Receiver.ps1 -ShowPairingCode
```

Close the existing receiver before starting another one. To change the target
IP address, edit `Address` in the sender's JSON file, or delete only that file
and run the sender again. Reserving PC 2's address in your router avoids changes
after restarts. To replace a pairing code, close both tools, delete both JSON
files, and repeat setup.

## Stream Deck and other keys

With the sender running on PC 1, configure a Stream Deck **System → Hotkey**
action to send **F8**. This uses the existing connection and avoids launching a
new PowerShell process for each press. The Stream Deck must be connected to
PC 1 for this setup.

To use another function key, close the sender and run in PowerShell:

```powershell
.\Sender.ps1 -FunctionKey 9
```

This uses F9; supported values are 1–24. F13–F24 are useful for a Stream Deck.
The chosen key is a global hotkey while the sender runs and may conflict with
other apps. Holding a key does not repeat. Separate clicks within 500 ms are
discarded instead of queued. Release and press again for a new request.

For a launcher that needs a one-shot command, `Sender.ps1 -Once` sends a single
request using the saved settings. A resident sender with a hotkey is faster.

## Troubleshooting

| Result | Meaning / next step |
| --- | --- |
| `ready` | The network and pairing work. It does not prove Minecraft is focused or the chamber is ready. |
| `clicked` | Windows accepted the mouse input. Check the actual trapdoor and teleport. |
| `unfocused` | Bring Minecraft Bedrock to the foreground on PC 2, close menus/chat, and aim again. |
| `cooldown` | A separate press arrived within 500 ms of the last click; it was discarded. |
| `input-error` | Minecraft lost focus or Windows rejected input. Run Minecraft and the receiver as the same normal Windows user and inspect the local game. |
| `dry-run` | Receiver test mode: no mouse input was sent. Restart it without `-DryRun` to play. |
| No reply | Check PC 2's receiver, IP, pairing code, Private firewall rule, and that both PCs have synchronized Windows clocks. Guest Wi-Fi may isolate devices. A lost reply does not prove the click failed: check the chamber before pressing again. |
| Hotkey already registered | Close a duplicate sender or select another function key. |
| Script execution blocked | Unblock the downloaded ZIP before extraction. Organization-managed execution policies may require your administrator; the utility does not override them. |

The foreground check recognizes `Minecraft.Windows.exe` directly and inside
the older Windows UWP frame host. It cannot distinguish gameplay from a menu
inside Minecraft, and a focus change at the instant of a click is still
possible. It does not switch windows or move the mouse. A locked/disconnected
Windows session is unsuitable; keep the second PC awake and its desktop active.

## How it works

The sender registers F8 with Windows. On each press, it sends a signed UDP
request to port 48123. The receiver authenticates it, checks Minecraft focus,
and sends one right-button press/release, held for 50 ms. A signed reply reports
the result. It accepts only `ping` and `click`, never shell commands.

Requests use a randomly generated 256-bit pairing key and HMAC-SHA256. Requests
older than five seconds (or more than five seconds ahead of the receiver's
clock) are rejected. Retries reuse the same request ID; the receiver caches
the reply for 15 seconds to prevent a retry/replay from clicking twice during
a receiver session. A restart clears that memory, so stop the sender before
restarting the receiver. UDP and operating-system scheduling do not guarantee
delivery time. The signed traffic is authenticated, not encrypted.

## Development checks

The scripts target Windows PowerShell 5.1 and use C# supported by its compiler.
Network-only tests also run on PowerShell 7 for Linux:

```sh
python3 -m unittest discover -s pearl-pull/tests -v
```

Set `PEARL_PULL_PWSH` to the PowerShell executable if it is not named `pwsh`.
The suite exercises the actual C# handler and the sender/receiver scripts on
loopback, including authentication, replay rejection, focus gating, cooldown,
and retry behavior. It uses `-DryRun`, temporary settings, and fake desktop
callbacks; it never injects mouse input. Actual Win32 input and hotkey behavior
must be tested on the Windows PCs using the first-setup checks above.
