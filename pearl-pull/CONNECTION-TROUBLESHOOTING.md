# Fixing “No reply from PC 2”

This means PC 1 did not receive an authenticated response. It does not identify
which prerequisite failed: the receiver process, destination address, firewall,
pairing code, and clock synchronization all matter.

The connection check does **not** click anything. Minecraft does not have to
be focused while you check networking. Both PCs need to be awake and connected
to the same reachable home network.

## Use the current files

Download the current [main branch](https://github.com/pjrose/minecraft_utils/archive/refs/heads/main.zip)
or updated Windows package. Close both tools before replacing the files. If
using startup tasks, stop the task with `Manage-Startup.cmd -Role Sender
-Action Stop` on PC 1 and `-Role Receiver -Action Stop` on PC 2 first.

Keep all files from the new `pearl-pull` folder together. Copy them over the
existing utility folder, or reinstall startup tasks if you move to a different
folder. Pairing settings remain in the Windows user's Local AppData folder,
so updating the program does not require re-pairing.

An older sender exits with a red exception when the initial check fails. The
current sender prints a warning and keeps F8 active. The diagnostic below
prints the folder it is actually using, which helps spot an older download.

If Windows blocks the extracted scripts as unsigned downloaded files, run
this from PowerShell **inside the trusted `pearl-pull` folder**:

```powershell
Get-ChildItem -LiteralPath . -Recurse -File | Unblock-File
```

This removes the downloaded-file mark within this folder. Smart App Control
and managed execution policies still apply; see the [Windows security guide](STARTUP-AND-WINDOWS-SECURITY.md).

## Check PC 2 first

1. Run **Start-Receiver.cmd** and leave it open. Look for
   **Receiver listening on UDP port 48123**. If it fails before that line,
   resolve the receiver's error first. A duplicate receiver can cause a port
   conflict; run one copy.
2. In a separate PowerShell window opened in the same folder, run:

   ```powershell
   .\Check-Connection.cmd -Role Receiver
   ```

3. The loopback result should be **PASS**. This proves the running receiver
   answers using the pairing settings saved on PC 2. It does not prove that
   traffic from PC 1 can get through the firewall.
4. Note the home Wi-Fi/Ethernet IPv4 address, UDP port, pairing fingerprint,
   and UTC time. The diagnostic does not print the pairing code.

If loopback fails, inspect the receiver window. Check that it uses the same
Windows account and saved settings, that its port matches, and that it is
listening on the default `0.0.0.0` address. Do not start another receiver on
top of an existing one.

## Check PC 1

Run in PowerShell from the `pearl-pull` folder:

```powershell
.\Check-Connection.cmd -Role Sender
```

Compare the two diagnostic outputs:

| Check | Expected / fix |
| --- | --- |
| Destination IPv4 address | PC 1's destination matches PC 2's home Wi-Fi/Ethernet address. Avoid VPN/virtual adapter addresses. |
| UDP port | Both use the same port, normally 48123. |
| Pairing fingerprint | The 12-character fingerprints match exactly. This lets you compare settings without posting the pairing code. |
| Clock | Both clocks are synchronized within five seconds. Compare current clocks, not timestamps from checks run several minutes apart. Use **Settings → Time & language → Date & time → Sync now** on both PCs. Different time zones are fine. |
| PC 2 network profile | The home connection is **Private**, matching the supplied firewall rule. Only label a trusted home network Private. |
| PC 2 firewall rule | Enabled, inbound, Allow, UDP port 48123, Private profile, RemoteAddress LocalSubnet. The diagnostic shows the rule's actual settings; reading a rule does not prove every other firewall policy allows traffic. |

If the named firewall rule is missing, use the administrator command in
[first setup](README.md#first-setup). If the home connection is Public, open
**Windows Settings → Network & internet → Wi-Fi/Ethernet → connected network**
and select **Private** for that home connection. Keep Windows Firewall enabled.

If the saved destination or pairing fingerprint is wrong, close the sender,
delete only `%LOCALAPPDATA%\MinecraftUtils\pearl-pull-sender.json` on PC 1,
then run its normal launcher and pair again with PC 2. Do not paste the pairing
code into diagnostic reports. The fingerprint is enough for comparing them.

If PC 2 passes loopback and all settings match but PC 1 still fails, check
guest Wi-Fi/client isolation, VPN routing, and any additional security software
on the PCs. A basic Windows ping can help check reachability, but it does not
test this application's UDP port. `Test-NetConnection -Port 48123` tests TCP
and cannot establish whether Pearl Pull's UDP connection works.

Once **PC 1's diagnostic passes**, close it, start the normal sender, return to
Minecraft on PC 2, aim at the trapdoor, and try one F8/Neo press with an
expendable pearl. Networking success does not verify the in-game chamber.
