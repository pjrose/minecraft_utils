# Pearl Pull tray apps

Install **Pearl Pull Button on PC 1** (the player/Neo PC) and **Pearl Pull
Receiver on PC 2** (the trapdoor PC). These are compiled Windows apps with
normal installers, graphical setup, and a pearl icon beside the Windows clock.
Your child does not need PowerShell or a command line.

## Parent setup, once

1. Download the **Pearl-Pull-Windows-Installers** ZIP from the latest successful
   [Windows build](https://github.com/pjrose/minecraft_utils/actions/workflows/windows.yml).
   Extract it. Run **PearlPull-Receiver-Setup.exe** on PC 2 and
   **PearlPull-Button-Setup.exe** on PC 1. Install while signed in as the Windows
   user who plays Minecraft. The installers use that user's profile.
2. If upgrading from an earlier script release, close its windows first. Saved
   pairing is reused, and this utility's earlier scheduled startup tasks are
   replaced when the account permits it.
3. Open **Receiver** on PC 2. Click **Allow home network (parent)** and approve
   Windows' administrator prompt. Set the trusted home network to **Private**
   using **Open Windows network settings** if necessary. The rule allows only
   the configured UDP port from the local subnet on Private networks.
4. Open **Button** on PC 1. Existing pairing is loaded automatically. Otherwise,
   enter PC 2's displayed IPv4 address, then click **Pair with PC 2**. Receiver
   on PC 2 opens an **Accept pairing** request showing PC 1's address. Compare
   the eight letters/numbers on both screens (displayed as `ABCD-2345`), then
   click **Accept pairing** on PC 2. There is nothing to type or copy. Only
   accept if the codes match and you started the request. Finish within one
   minute; expired or declined requests can be retried from PC 1.
   **Check connection** tests the network without clicking.
5. Click **Copy Neo button URL**. In Stream Deck, replace the old Hotkey action
   with **System → Website**, paste the URL, and enable **GET request in
   background**. Click **Open button artwork** in the app to find the pearl PNG.
   The Neo must be connected to PC 1. Keep its trigger URL private.
6. On PC 2, bring Minecraft to the foreground, close menus/chat, and aim at the
   trapdoor. Practice one pearl pull. Confirm the trapdoor and teleport, then
   re-arm the chamber.

The apps require .NET Framework 4.8, included with Windows 11 and recent
Windows 10. Setup checks for it. No PowerShell execution policy or runtime C#
compilation is involved in the installed apps.

Approval pairing requires **version 0.6.0 or newer on both PCs**. The apps
exchange their long authentication key automatically, encrypted for PC 1;
the short code is only a visual check and is not the saved password. Existing
pairings and Neo URLs are preserved on upgrade and when pairing again.
**Cancel pairing** on PC 1 preserves its previous settings; PC 2's prompt will
expire automatically. **Forget paired PCs** on PC 2 revokes all existing
pairings, so each PC must request approval again. After setup, bring Minecraft
back to the front on PC 2.

## Windows security

**These installers and executables are unsigned.** Smart App Control
may still block them. A trusted publisher certificate and a signed release
are needed to address that deployment requirement; an installer alone cannot
grant an exception. No security settings are changed by installation. Running
as administrator
or changing PowerShell execution policy does not grant an exception to Smart
App Control. A self-signed certificate does not establish publisher trust.

## Your child's daily steps

Open Minecraft on both PCs and aim PC 2 at the trapdoor. Press the Neo pearl
button or physical **F8 on PC 1**. Pearl Pull starts automatically when the
Windows user signs in. Closing its window hides it to the tray and keeps it
running. Re-arm the chamber after each use.

Double-click the pearl beside the Windows clock to see status. It may be under
Windows' tray up-arrow. Right-click it and choose **Quit** to deliberately stop
it. Open the desktop shortcut to start it again. Both PCs must stay awake and
signed in; PC 2's desktop must be unlocked. The app does not aim, launch
Minecraft, detect danger, or reset the chamber.

## Staying available and debugging

- **Automatic startup is on by default.** Where the account permits it, a
  per-user Task Scheduler task starts the app at sign-in with no time limit and
  restarts a failed process after one minute, up to 999 attempts. The desktop
  shortcut also launches through that task, so recovery applies from the first
  installed session. Deliberate **Quit** exits normally and is respected.
- If task registration is denied, ordinary Windows sign-in startup is used.
  The window explicitly reports that crash recovery is unavailable. This
  fallback still starts at sign-in but cannot restart a crashed process.
- A second launch opens the existing window. The receiver retries a failed
  listener every five seconds. The sender retries unavailable hotkeys/local
  Neo listeners every five seconds and checks PC 2 every 30 seconds. Turning
  PC 2 back on needs no sender restart.
- A press sends an immediate authenticated request. Failed requests are not
  saved for reconnection. Busy Neo requests and stale hotkey events are dropped;
  UDP retries reuse one request ID, and the receiver applies its existing
  500 ms cooldown and duplicate suppression.
- **Open logs** is in both the window and the tray menu. Files are under
  `%LOCALAPPDATA%\MinecraftUtils\logs`. There are five sessions per role, each
  roughly 1 MiB plus one previous-file backup: approximately **10 MiB per role**.
  The small launcher has its own bounded files. Active files from an old script
  process can temporarily add to retention until that process is closed.
- Logs include build, role, timestamps, request IDs, detection source, network
  attempts, authentication/clock rejections, foreground process, input result,
  and replies. Pairing codes and Neo tokens are excluded. See the
  [event guide](DIAGNOSTICS.md) for interpreting a request across both PCs.

A `clicked` result means Windows accepted the right-button input, not that a
pearl teleport succeeded. A timeout can occur after a click if its reply was
lost. Check the chamber before repeating. Keep an expendable-pearl practice
check after updates or changes to the chamber.

## Updates and removal

Run the newer installer on the same PC and Windows account. It requests a clean
exit from the installed app, replaces its files, and preserves pairing settings.
Finish by opening the app. The installer enables automatic startup by default.

Use **Windows Settings → Apps** to uninstall the relevant app. The uninstaller
removes its startup task/entry and shortcuts. Pairing settings, logs, and the
shared Private-network firewall rule remain for reinstalls. You can turn off
startup without uninstalling by clearing **Start with Windows** in the app.

See [development](DEVELOPMENT.md) for source layout, tests, and installer builds.
