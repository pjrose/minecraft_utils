# Pearl Pull on Stream Deck Neo

**Using the installed tray apps?** In **Pearl Pull Button**, click **Copy Neo
button URL**, then configure **System → Website → GET request in background**.
Use **Open button artwork** to find the PNG. See the [tray app guide](TRAY-APPS.md).
The launchers below are for the optional script version.

<img src="icons/pearl-pull.png" alt="Teal pixel-art pearl floating above a purple teleport ring" width="144" height="144">

Connect the Neo to **PC 1**, where your son plays. Its button sends a local
request to the resident sender, which requests one right-click from **PC 2**.
PC 2 must have Minecraft in front, aimed at the trapdoor.

Use **System → Website** with **GET request in background**. This avoids the
synthetic F8 input that can appear in a browser keyboard tester without reaching
Windows' global hotkey handler. The physical keyboard's F8 still works.

## Set up or update the Neo button

1. Update the entire `pearl-pull` folder on **both PCs** and restart their
   utilities. If using a downloaded ZIP, unblock it before extraction. Existing
   pairing settings are kept outside the project folder.
2. Complete the [network pairing and firewall setup](README.md#first-setup)
   if needed. On **PC 1**, double-click **Setup-StreamDeck.cmd**. It reuses
   saved pairing settings or prompts for PC 2's address and pairing code.
3. Copy the local URL it displays. It starts with `http://127.0.0.1:48124/pull/`
   and ends with a private token. Use the URL exactly as shown; the address is
   PC 1's loopback address, not PC 2's home IP. Setup does not send a click.
4. In the Elgato **Stream Deck** desktop app on PC 1, select your Neo and drag
   **System → Website** onto a key. Replace the previous Hotkey action if you
   already made one. Paste the copied URL into **URL** and enable
   **GET request in background**. This keeps the game focused when pressed.
5. Leave **Title** blank or use `PEARL`. Open the button's icon menu, choose
   **Set from File**, and select **[icons/pearl-pull.png](icons/pearl-pull.png)**.
   Keep it on the profile/page active while playing Minecraft.

The built-in Website action needs no custom plugin or device profile. Keep the
URL private: it authorizes a click through the sender on this PC. It uses a
separate token from the PC 2 pairing code and is excluded from the utility's
log files. Re-running Setup shows the same URL. Deleting the sender settings
creates a new token and requires updating the button.

## Start playing

1. On **PC 2**, run **Start-Receiver.cmd**. Return to Minecraft, close menus/chat,
   and leave the crosshair aimed at the trapdoor within reach.
2. On **PC 1**, run **Start-StreamDeck-Neo.cmd**. Look for
   `streamdeck.listening bind=127.0.0.1:48124` and `Network check: ready`.
   Keep this window open; you can minimize it. Run one sender at a time.
3. Press the pearl button once. PC 1 logs **`streamdeck.triggered`**, then
   **`streamdeck.completed`** with the receiver's result. Physical F8 instead
   logs **`hotkey.detected`** and **`hotkey.completed`**.

Practice with an expendable pearl and confirm the actual teleport. Re-arm the
chamber after use. The static icon does not report chamber readiness or success.

The normal sender and [sign-in startup task](STARTUP-AND-WINDOWS-SECURITY.md#start-automatically-when-the-player-signs-in)
both start this local endpoint automatically. If the startup task already runs
the sender, restart that task after updating; do not launch a second copy.
No new firewall rule or administrator rights are needed on PC 1: the endpoint
only listens on `127.0.0.1`.

## If pressing the Neo key does nothing

- Confirm **System → Website**, the copied URL, and **GET request in background**
  in the active Neo profile. The button does not launch the sender itself.
- Look for **`streamdeck.triggered` on PC 1**. If missing, inspect the sender's
  startup errors and `streamdeck.listening` line. `streamdeck.rejected` means
  a request reached it but had the wrong URL, token, host, or HTTP method.
- `unfocused` means PC 2 needs Minecraft in front. `input-error` means focus
  changed or Windows rejected mouse input. See the [log guide](DIAGNOSTICS.md)
  for the complete request sequence and where to find logs on both PCs.
- If F8 is already claimed by another app and you only want the Neo, close the
  sender and run `Start-StreamDeck-Neo.cmd -StreamDeckOnly` from PowerShell.
  This runs the direct trigger without registering a hotkey.
- If TCP port 48124 is in use, close duplicate senders first. To deliberately
  use another port, run both `Setup-StreamDeck.cmd -StreamDeckPort 48125` and
  `Start-StreamDeck-Neo.cmd -StreamDeckPort 48125`, then update the button URL.
  The startup task uses the default port.

If you prefer **System → Hotkey: F8**, it remains an optional path, but depends
on Windows receiving the generated hotkey. Close the sender before recording
F8 in the Stream Deck editor to avoid triggering a click. Use
**Test-Hotkey.cmd** to compare physical and generated F8 without contacting PC 2.

## Artwork

The included icon is original AI-generated artwork made for this utility.
The Stream Deck app scales the square PNG to fit the Neo display. It contains
no configuration or pairing information.
