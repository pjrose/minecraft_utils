# Pearl Pull

Press a Stream Deck Neo button or **F8 on PC 1** to request one right-click in
Minecraft Bedrock on **PC 2**, where the second account is aimed at the pearl
chamber's trapdoor.

**[Download Windows installers](https://github.com/pjrose/minecraft_utils/actions/workflows/windows.yml)**
— open the latest successful run and download **Pearl-Pull-Windows-Installers**
under Artifacts. Extract the ZIP, then install:

| PC | Installer |
| --- | --- |
| PC 1: player and Stream Deck Neo | `PearlPull-Button-Setup.exe` |
| PC 2: trapdoor account | `PearlPull-Receiver-Setup.exe` |

The apps start at Windows sign-in and stay beside the clock when their windows
are closed. Pair them once using the app windows. No command line is needed.
For the Neo, click **Copy Neo button URL** in Button, then use **System → Website**
with **GET request in background** in Stream Deck.

- **[Setup guide](docs/SETUP.md)** — pairing, Neo artwork, automatic startup, updates.
- **[Troubleshooting and logs](docs/DIAGNOSTICS.md)** — connection checks and bounded log files.
- **[Development](docs/DEVELOPMENT.md)** — source layout, tests, and installer builds.

These builds are currently unsigned; Smart App Control may block them. PC 2
must remain awake, signed in, and focused on Minecraft. The app does not aim or
re-arm the chamber. Practice with an expendable pearl before relying on it.

The tray apps are the only supported version. The old PowerShell launchers have
been removed; existing pairing settings are preserved when upgrading.
