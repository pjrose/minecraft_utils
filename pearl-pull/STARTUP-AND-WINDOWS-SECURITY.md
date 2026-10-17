# Windows security and automatic startup

The [installed tray apps](TRAY-APPS.md) start automatically by default and
include startup controls in their windows. The manual Task Scheduler commands
below are for the older script version. Both versions are currently unsigned.

Smart App Control and automatic startup are separate settings. A startup task
still has to pass Windows' security checks. This version of Pearl Pull is
unsigned and compiles its C# input helper at runtime, so it can be blocked by
Smart App Control.

## Smart App Control

Smart App Control does not provide a normal per-app **Run anyway** exception.
Running as administrator or changing PowerShell's execution policy does not
grant an exception to Smart App Control.

To keep Smart App Control enabled, the appropriate distribution would be a
precompiled application and helper binaries signed by a trusted publisher.
Microsoft's [Smart App Control signing guidance](https://learn.microsoft.com/en-us/windows/apps/develop/smart-app-control/code-signing-for-smart-app-control)
requires a certificate from a trusted provider. A locally generated,
self-signed certificate does not establish that trust. This repository does
not yet include such a signed release. The new tray apps compile the helper
ahead of time, but their executables and installers still need trusted signing.
Signing just the PowerShell launcher
does not make the dynamically compiled helper a trusted, signed binary.

If you choose to turn Smart App Control off on your own PC, open:

**Windows Security → App & browser control → Smart App Control settings → Off**

This turns off that protection for the whole PC, not just Pearl Pull. Read
Windows' confirmation first: depending on the Windows version, enabling it
again can require resetting or reinstalling Windows. Microsoft's
[overview](https://learn.microsoft.com/en-us/windows/apps/develop/smart-app-control/overview)
documents the clean-install requirement. The startup installer does not change
Smart App Control, Defender, firewall rules, or system-wide execution policy.

For downloaded files, right-click the ZIP, choose **Properties → Unblock →
Apply**, then extract it again. This removes the downloaded-file mark and can
resolve restrictions caused by that mark, but it does not establish publisher
trust or guarantee that Smart App Control will permit the scripts or helper.
Our launchers already request the process-scoped `RemoteSigned` execution
policy; adding `Bypass` is not a fix for a Smart App Control denial.

The messages below describe different mechanisms:

| Message | What to check |
| --- | --- |
| “Smart App Control blocked…” | Use the Smart App Control guidance above. Startup cannot grant an exception. |
| “Windows protected your PC” | This is usually Microsoft Defender SmartScreen. Its dialog may offer **More info → Run anyway** for a file you trust. |
| Scripts disabled / file not digitally signed | Check PowerShell execution policy and the ZIP's download mark. Use the included launcher after unblocking the ZIP. Managed policies can still prohibit execution. |
| Add-Type / language-mode restriction | In PowerShell, run `$ExecutionContext.SessionState.LanguageMode`. `ConstrainedLanguage` cannot run this utility's runtime C# compilation. Application-control policy needs to permit the application; setting an execution-policy flag does not change that. |

## Start automatically when the player signs in

First get the ordinary launcher working and verify one pearl pull manually.
Pair the two PCs and finish the existing firewall setup before enabling
startup. Keep the extracted folder in a permanent location. If you move it,
repeat installation from its new location.

Close any manually running sender/receiver on the PC you are configuring.
Open PowerShell **in the `pearl-pull` folder as the Windows user who plays
Minecraft**, then run the command for that PC:

**PC 1 — keyboard / Stream Deck Neo sender:**

```powershell
.\Manage-Startup.cmd -Role Sender
```

**PC 2 — right-click receiver:**

```powershell
.\Manage-Startup.cmd -Role Receiver
```

This launcher uses the same process-scoped execution policy as the normal
launchers; Smart App Control still applies. It installs a named Task Scheduler
task for the current user and requests a start immediately. Check its minimized window for successful
startup. On subsequent Windows sign-ins it starts automatically, minimized,
with normal user permissions. It uses the saved pairing settings and does not
store a Windows password. The sender uses **F8** and also starts the direct Neo
trigger on port 48124. Configure the Neo with
[Setup-StreamDeck.cmd and the Website action](STREAM-DECK-NEO.md).
To use a different physical function key, install with, for example,
`-Role Sender -FunctionKey 9`; the direct Neo URL stays the same.

If task registration reports **Access denied**, have an administrator help
with Task Scheduler permissions. The task must remain assigned to the actual
player's Windows account, with **Run only when user is logged on** and normal
privileges. Registering it under a different administrator's account would use
that account's desktop and pairing settings.

The task has no execution time limit and permits operation on battery power.
Task Scheduler is configured to retry a failed process up to three times, one
minute apart. After repeated failures, inspect the minimized window or Task
Scheduler or open the [log files](DIAGNOSTICS.md), fix the cause, then start
the task again. It does not restart a
task you explicitly stop through Task Scheduler.

PC 1 can start before PC 2: a failed initial network check now prints a warning
and leaves the sender's hotkey running. Each new F8/Neo press makes a fresh
request. A press while the receiver is unavailable is not saved for later;
wait for PC 2 to be ready before pressing again. `Sender.ps1 -Test` still fails
if the receiver cannot be reached.

## Keep it available while playing

- Keep the relevant Windows user signed in and PC 2's desktop unlocked, with
  Minecraft in front and the crosshair aimed at the trapdoor. The utility does
  not launch Minecraft, join the server, or re-arm the chamber.
- On PC 2, use **Settings → System → Power / Power & battery → Screen and sleep**
  to prevent the PC sleeping while plugged in if it needs to stay available.
  Labels vary with the Windows version. Sleeping PCs cannot process requests.
- Keep the Stream Deck app running on PC 1 and the pearl button on its active
  profile. Once startup is installed, the daily `.cmd` launchers are optional;
  starting a second copy can cause a port or hotkey conflict.

This runs in the interactive desktop after sign-in. It cannot click Minecraft
at the Windows sign-in screen or while the desktop is locked. A Windows
service running before sign-in cannot provide that desktop input either.

## Check, stop, restart, or remove startup

Run these from the same folder and account. Substitute `Receiver` on PC 2:

```powershell
.\Manage-Startup.cmd -Role Sender -Action Status
.\Manage-Startup.cmd -Role Sender -Action Stop
.\Manage-Startup.cmd -Role Sender -Action Start
.\Manage-Startup.cmd -Role Sender -Action Remove
```

`Stop` stops the scheduled copy for this session. `Start` requests another
start. `Remove` stops and removes the startup task; it preserves pairing files
and firewall settings. It does not stop a copy you launched manually.

You can also find the task in **Task Scheduler → Task Scheduler Library**.
Its name starts with `MinecraftUtils-PearlPull-Sender-` or
`MinecraftUtils-PearlPull-Receiver-` and ends with the Windows user's SID.

For a local validation, sign out and back in on each PC. Confirm the task is
running, start Minecraft and aim at the trapdoor on PC 2, then try one F8/Neo
press with an expendable pearl. The cloud tests cover network behavior; they
cannot verify your Windows security policy, Task Scheduler, or game input.
