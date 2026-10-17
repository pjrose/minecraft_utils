# Developing Pearl Pull

The supported product is the pair of installed tray apps. PowerShell is used
only by the developer build script; there is no second script-based app.

| Folder | Contents |
| --- | --- |
| `src/PearlPull.App/` | Windows Forms UI, pairing settings, tray/startup integration; one project builds Button and Receiver. |
| `src/PearlPull.Core/` | Shared authentication, UDP transport, Neo HTTP trigger, mouse input, bounded logging. |
| `assets/` | Pearl artwork used by the apps and Stream Deck. |
| `tests/` | A development-only .NET test host and Python integration tests; never sends desktop input. |
| `installer/` | NSIS installer definition and build script. |
| `docs/` | The single setup guide and troubleshooting instructions. |

## Tests

Install **.NET SDK 8** and Python 3. From the repository root:

```sh
python -m unittest discover -s tests -v
```

The suite compiles its test host once and exercises the same C# source as the
tray apps. It covers 25 protocol/input-handler assertions, real UDP and local
HTTP requests, duplicate/retry handling, authentication and clock rejection,
log limits/redaction, saved-setting migration, and graceful receiver shutdown.
It uses generated public fixtures and dry-run receivers, never the family's
pairing code or actual mouse input.

Readiness probes tolerate `ConnectionResetError`/Windows error 10054 while a
UDP receiver is starting. Windows reports the ICMP response when the port is
not listening yet. They keep a deadline and still fail on unrelated errors.
A regression test covers that exact response. Once a receiver is ready, normal
network assertions remain strict.

Set `PEARL_PULL_DOTNET` to the SDK executable if it is not named `dotnet` on PATH.
The test host is built under ignored `artifacts/tests/`. There is no PowerShell
runtime or dynamic `Add-Type` compilation in these tests.

## Apps and installers

Install .NET SDK 8, PowerShell 7, and NSIS 3.11. From the repository root:

```powershell
./installer/build.ps1 -Makensis 'C:\Program Files (x86)\NSIS\makensis.exe'
```

The script builds both roles with warnings treated as errors and creates
`artifacts/PearlPull-Button-Setup.exe`, `artifacts/PearlPull-Receiver-Setup.exe`,
`START-HERE.txt`, and `SHA256SUMS.txt`. The app target is .NET Framework 4.8, so
end users do not need the .NET SDK or PowerShell.

Linux can cross-compile using Microsoft's NuGet reference assemblies and an
NSIS installation. Pass that `makensis` path to the same script; set `NSISDIR`
if NSIS was extracted outside its standard location. The Linux build does not
validate Windows UI, Task Scheduler, firewall elevation, or Minecraft input.

## Windows CI

The [Windows workflow](../.github/workflows/windows.yml) runs the network tests,
compiles both installers, then launches each app with `--smoke-test` to check
form construction, close-to-tray behavior, and shutdown. Smoke mode starts no
network listeners, changes no startup settings, and injects no input. Only a
successful run uploads the installer artifact. On failure, app logs are uploaded
as **Pearl-Pull-Failure-Logs** when available.

After installing on Windows, confirm startup/restart, parent firewall setup,
Neo background requests, and one actual trapdoor click with an expendable pearl.
Compilation and dry-run tests cannot establish a successful in-game teleport.

## Signing

The current build produces unsigned executables and installers. A trusted
publisher certificate and signing step are still required for deployment to
PCs where Smart App Control blocks unsigned apps. Keep signing credentials out
of the repository and test fixtures.
