using System;
using System.ComponentModel;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;

namespace PearlPull.Tray
{
    internal sealed class MainWindow : Form
    {
        private readonly Label status = new Label { AutoSize = true, MaximumSize = new Size(550, 0), Padding = new Padding(12), BackColor = Color.FromArgb(232, 245, 242) };
        private readonly Label inputStatus = new Label { AutoSize = true, MaximumSize = new Size(550, 0) };
        private readonly TextBox address = new TextBox { Width = 250 };
        private readonly TextBox pairing = new TextBox { Width = 490, UseSystemPasswordChar = true };
        private readonly NumericUpDown port = new NumericUpDown { Minimum = 1024, Maximum = 65535, Value = 48123, Width = 110 };
        private readonly Button save = new Button { Text = "Save pairing", AutoSize = true };
        private readonly Button test = new Button { Text = "Check connection", AutoSize = true };
        private readonly CheckBox startup = new CheckBox { Text = "Start with Windows", AutoSize = true };
        private readonly Label startupStatus = new Label { AutoSize = true, MaximumSize = new Size(550, 0), ForeColor = Color.DimGray };
        private readonly NotifyIcon tray;
        private readonly Icon pearlIcon;
        private readonly System.Windows.Forms.Timer timer = new System.Windows.Forms.Timer { Interval = 1000 };
        private readonly EventWaitHandle showSignal, quitSignal;
        private readonly bool background, smokeTest;
        private Settings settings;
        private StreamDeckTrigger trigger;
        private HotkeyWindow hotkey;
        private CancellationTokenSource receiverStop;
        private Task receiverTask;
        private bool exiting, checking;
        private int seconds;
        private int generation;
        [DllImport("user32.dll")] private static extern bool DestroyIcon(IntPtr icon);

        public MainWindow(bool background, EventWaitHandle show, EventWaitHandle quit, bool smokeTest)
        {
            this.background = background; this.smokeTest = smokeTest;
            showSignal = show; quitSignal = quit;
            Text = Program.Title;
            Font = new Font("Segoe UI", 10);
            AutoScaleMode = AutoScaleMode.Dpi;
            ClientSize = new Size(620, 640);
            MinimumSize = new Size(600, 600);
            StartPosition = FormStartPosition.CenterScreen;
            BackColor = Color.White;
            using (Stream stream = Assembly.GetExecutingAssembly().GetManifestResourceStream("PearlPull.png"))
            using (Bitmap image = new Bitmap(stream))
            using (Bitmap small = new Bitmap(image, new Size(32, 32)))
            {
                IntPtr handle = small.GetHicon();
                try { using (Icon source = Icon.FromHandle(handle)) pearlIcon = (Icon)source.Clone(); }
                finally { DestroyIcon(handle); }
            }
            Icon = pearlIcon;
            FlowLayoutPanel body = new FlowLayoutPanel { Dock = DockStyle.Fill, FlowDirection = FlowDirection.TopDown, WrapContents = false, AutoScroll = true, Padding = new Padding(22) };
            Controls.Add(body);
            body.Controls.Add(new Label { Text = Program.IsReceiver ? "PC 2 · Pearl Pull Receiver" : "PC 1 · Pearl Pull Button", AutoSize = true, Font = new Font(Font.FontFamily, 19, FontStyle.Bold), Margin = new Padding(0, 0, 0, 12) });
            body.Controls.Add(new Label { Text = Program.IsReceiver ? "Leave Minecraft open and aim at the trapdoor.\nThis app stays in the tray while you play." : "Press F8 or your Neo pearl button while playing.\nThis app stays in the tray; no command window needed.", AutoSize = true, Margin = new Padding(0, 0, 0, 14) });
            status.Text = "Let's connect the two PCs.";
            body.Controls.Add(status);
            body.Controls.Add(inputStatus);
            body.Controls.Add(LabelFor(Program.IsReceiver ? "This PC's home IPv4 addresses" : "PC 2 home IPv4 address"));
            if (Program.IsReceiver)
            {
                string addresses = string.Join("\n", NetworkInterface.GetAllNetworkInterfaces()
                    .Where(n => n.OperationalStatus == OperationalStatus.Up && n.NetworkInterfaceType != NetworkInterfaceType.Loopback)
                    .SelectMany(n => n.GetIPProperties().UnicastAddresses)
                    .Where(a => a.Address.AddressFamily == AddressFamily.InterNetwork)
                    .Select(a => a.Address.ToString()).ToArray());
                TraceLog.Write("INFO", "network.addresses", "pc=PC2 ipv4=" + addresses.Replace("\n", ","));
                body.Controls.Add(new TextBox { Text = addresses, Multiline = true, ReadOnly = true, Width = 490, Height = 56 });
            }
            else body.Controls.Add(address);
            body.Controls.Add(LabelFor(Program.IsReceiver ? "Pairing code for PC 1 (parent setup)" : "Pairing code shown on PC 2"));
            pairing.ReadOnly = Program.IsReceiver;
            body.Controls.Add(pairing);
            CheckBox reveal = new CheckBox { Text = "Show pairing code", AutoSize = true };
            reveal.CheckedChanged += delegate { pairing.UseSystemPasswordChar = !reveal.Checked; };
            body.Controls.Add(reveal);
            if (Program.IsReceiver)
            {
                body.Controls.Add(Row(ActionButton("Copy pairing code", delegate { if (settings != null) Clipboard.SetText(settings.PairingCode); }),
                    ActionButton("Allow home network (parent)", ConfigureFirewall)));
                body.Controls.Add(ActionButton("Create a new pairing code", ResetPairing));
                body.Controls.Add(ActionButton("Open Windows network settings", delegate { WindowsIntegration.Open("ms-settings:network-status"); }));
                body.Controls.Add(new Label { Text = "Use a Private home network. Allow access once with a parent.\nThe receiver only clicks while Minecraft is in front.", AutoSize = true });
            }
            else
            {
                FlowLayoutPanel portRow = Row(new Label { Text = "Network port", AutoSize = true, Padding = new Padding(0, 5, 0, 0) }, port);
                body.Controls.Add(portRow);
                save.Click += delegate { SavePairing(); };
                test.Click += async delegate { await CheckConnection(); };
                body.Controls.Add(Row(save, test));
                body.Controls.Add(ActionButton("Copy Neo button URL", CopyNeoUrl));
                body.Controls.Add(new Label { Text = "Neo: System > Website, paste the URL, then enable\nGET request in background. Use the included pearl icon.", AutoSize = true });
                body.Controls.Add(ActionButton("Open button artwork", delegate { WindowsIntegration.Open(Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "icons")); }));
            }
            startup.Checked = !smokeTest && WindowsIntegration.StartsWithWindows();
            startup.CheckedChanged += delegate { TryAction(ConfigureStartup); };
            body.Controls.Add(startup);
            body.Controls.Add(startupStatus);
            body.Controls.Add(Row(ActionButton("Open logs", OpenLogs), ActionButton("Hide to tray", delegate { Hide(); })));
            body.Controls.Add(new Label { Text = "Closing this window keeps Pearl Pull running.\nTo stop it, right-click the pearl tray icon and choose Quit.", AutoSize = true, ForeColor = Color.DimGray, Margin = new Padding(0, 12, 0, 0) });
            ContextMenuStrip menu = new ContextMenuStrip();
            menu.Items.Add("Open Pearl Pull", null, delegate { ShowWindow(); });
            menu.Items.Add("Open logs", null, delegate { TryAction(OpenLogs); });
            menu.Items.Add("Quit", null, delegate { Quit(); });
            tray = new NotifyIcon { Icon = pearlIcon, Text = Program.Title, ContextMenuStrip = menu, Visible = true };
            tray.DoubleClick += delegate { ShowWindow(); };
            TraceLog.Entry += OnTrace;
            timer.Tick += async delegate
            {
                if (quitSignal.WaitOne(0)) { Quit(); return; }
                if (showSignal.WaitOne(0)) ShowWindow();
                if (settings != null && !Program.IsReceiver && !smokeTest && seconds % 5 == 0 && (trigger == null || hotkey == null)) StartSenderInputs();
                if (++seconds % 30 == 0)
                {
                    TraceLog.Write("INFO", "tray.waiting", "role=" + Program.Role + " hotkey_active=" + (hotkey != null));
                    if (!Program.IsReceiver && !smokeTest) await CheckConnection();
                }
            };
            Shown += async delegate
            {
                if (!smokeTest)
                {
                    if (startup.Checked) TryAction(ConfigureStartup);
                    LoadSettings();
                }
                timer.Start();
                if (background && settings != null) Hide();
                if (!Program.IsReceiver && !smokeTest) await CheckConnection();
            };
        }
        private static Label LabelFor(string text) { return new Label { Text = text, AutoSize = true, Margin = new Padding(0, 12, 0, 3) }; }
        private static FlowLayoutPanel Row(params Control[] controls)
        {
            FlowLayoutPanel row = new FlowLayoutPanel { AutoSize = true, MaximumSize = new Size(560, 0), Margin = new Padding(0, 3, 0, 3) };
            row.Controls.AddRange(controls); return row;
        }
        private Button ActionButton(string text, Action action)
        {
            Button button = new Button { Text = text, AutoSize = true, Padding = new Padding(4) };
            button.Click += delegate { TryAction(action); }; return button;
        }
        private void TryAction(Action action)
        {
            try { action(); }
            catch (Exception ex)
            {
                TraceLog.Failure("ui.action_failed", ex, "role=" + Program.Role);
                MessageBox.Show(this, "That action could not finish. Check the settings and try again. Open logs for details.", Program.Title, MessageBoxButtons.OK, MessageBoxIcon.Warning);
            }
        }
        private void LoadSettings()
        {
            try
            {
                settings = Settings.Load(Program.IsReceiver);
                if (settings == null && Program.IsReceiver)
                {
                    settings = new Settings { PairingCode = Protocol.NewKey() };
                    settings.Save(true);
                }
                if (settings == null) { SetStatus("Enter PC 2's address and pairing code, then Save pairing.", false); return; }
                address.Text = settings.Address;
                pairing.Text = settings.PairingCode;
                port.Value = settings.Port;
                StartServices();
            }
            catch (Exception ex)
            {
                TraceLog.Failure("settings.load_failed", ex, "role=" + Program.Role);
                SetStatus("Could not load saved settings. Open logs for details.", false);
            }
        }
        private void StartServices()
        {
            StopServices();
            TraceLog.Write("INFO", "config.loaded", "role=" + Program.Role + " port=" + settings.Port + " pairing_fingerprint=" + Protocol.Fingerprint(settings.PairingCode));
            if (Program.IsReceiver)
            {
                receiverStop = new CancellationTokenSource();
                CancellationToken cancellation = receiverStop.Token;
                Settings current = settings;
                receiverTask = Task.Factory.StartNew(delegate
                {
                    while (!cancellation.IsCancellationRequested)
                    {
                        try { Transport.Listen("0.0.0.0", current.Port, current.PairingCode, false, cancellation); }
                        catch (Exception ex) { TraceLog.Failure("receiver.failed", ex, "retry_in_seconds=5 close_old_receiver_if_port_in_use=true"); }
                        if (cancellation.WaitHandle.WaitOne(5000)) break;
                    }
                }, CancellationToken.None, TaskCreationOptions.LongRunning, TaskScheduler.Default);
                inputStatus.Text = "Network receiver · no F8 listener on this PC";
            }
            else StartSenderInputs();
        }
        private void StartSenderInputs()
        {
            if (string.IsNullOrEmpty(settings.StreamDeckToken)) { settings.StreamDeckToken = Protocol.NewKey(); settings.Save(false); }
            if (trigger == null)
            {
                try
                {
                    trigger = new StreamDeckTrigger(settings.Address, settings.Port, settings.PairingCode, settings.StreamDeckToken, 48124);
                    trigger.Start();
                }
                catch (Exception ex)
                {
                    if (trigger != null) trigger.Dispose();
                    trigger = null;
                    TraceLog.Failure("streamdeck.start_failed", ex, "close_old_sender=true");
                }
            }
            if (hotkey == null)
            {
                try { hotkey = new HotkeyWindow(settings); }
                catch (Exception ex) { TraceLog.Failure("hotkey.registration_failed", ex, "neo_independent=true retry_in_seconds=5"); }
            }
            inputStatus.Text = (hotkey != null ? "F8 active" : "F8 unavailable") + " · " + (trigger != null ? "Neo direct button active" : "Neo unavailable") +
                ((hotkey == null || trigger == null) ? "\nClose any old sender/script windows. Retrying automatically." : "");
        }
        private void ResetPairing()
        {
            if (MessageBox.Show(this, "Create a new pairing code? PC 1 must then be paired again.", Program.Title, MessageBoxButtons.YesNo, MessageBoxIcon.Question) != DialogResult.Yes) return;
            Settings next = new Settings { PairingCode = Protocol.NewKey(), Port = settings == null ? Protocol.DefaultPort : settings.Port };
            next.Save(true); settings = next; pairing.Text = next.PairingCode; StartServices();
        }
        private void ConfigureStartup()
        {
            bool recovery = WindowsIntegration.SetStartup(startup.Checked);
            startupStatus.Text = !startup.Checked ? "Automatic startup is off." : recovery ? "Starts at sign-in. Crash recovery is active." : "Starts at sign-in. Crash recovery is unavailable on this account.";
        }
        private void StopServices()
        {
            generation++;
            if (hotkey != null) { hotkey.Dispose(); hotkey = null; }
            if (trigger != null) { trigger.Dispose(); trigger = null; }
            if (receiverStop != null)
            {
                receiverStop.Cancel();
                if (receiverTask != null) receiverTask.Wait(2000);
                receiverStop.Dispose(); receiverStop = null; receiverTask = null;
            }
        }
        private async void SavePairing()
        {
            try
            {
                Settings next = new Settings { Address = address.Text.Trim(), PairingCode = pairing.Text.Trim(), Port = (int)port.Value,
                    StreamDeckToken = settings == null ? Protocol.NewKey() : settings.StreamDeckToken };
                next.Validate(false);
                next.Save(false);
                settings = next;
                StartServices();
                await CheckConnection();
            }
            catch (Exception ex)
            {
                TraceLog.Failure("settings.save_failed", ex, "role=Button");
                MessageBox.Show(this, "Check PC 2's IPv4 address, network port, and 64-character pairing code. Settings could not be saved.", Program.Title, MessageBoxButtons.OK, MessageBoxIcon.Warning);
            }
        }
        private async Task CheckConnection()
        {
            if (checking || settings == null || exiting) return;
            checking = true; test.Enabled = false;
            int currentGeneration = generation;
            Settings current = settings;
            try
            {
                string reply = await Task.Run(delegate { return Transport.Send(current.Address, current.Port, current.PairingCode, "ping"); });
                if (!exiting && currentGeneration == generation) SetStatus(reply == "ready" ? "PC 2 connected. Keep its Minecraft aimed at the trapdoor." : "PC 2 returned an unexpected reply.", reply == "ready");
            }
            catch (Exception ex)
            {
                TraceLog.Failure("connection.check_failed", ex, "no_click_requested=true");
                if (!exiting && currentGeneration == generation) SetStatus("PC 2 is not answering. Check its receiver and home network.", false);
            }
            finally { checking = false; if (!exiting) test.Enabled = true; }
        }
        private void CopyNeoUrl()
        {
            if (settings == null || string.IsNullOrEmpty(settings.StreamDeckToken)) { MessageBox.Show(this, "Save pairing first.", Program.Title); return; }
            Clipboard.SetText(settings.NeoUrl);
            MessageBox.Show(this, "Neo URL copied. In Stream Deck, choose System > Website, paste into URL, and enable GET request in background.\n\nKeep this URL private.", Program.Title);
        }
        private async void ConfigureFirewall()
        {
            if (settings == null) return;
            try
            {
                using (Process process = Process.Start(new ProcessStartInfo(Application.ExecutablePath, "--configure-firewall " + settings.Port) { Verb = "runas", UseShellExecute = true }))
                {
                    await Task.Run(delegate { process.WaitForExit(); });
                    TraceLog.Write(process.ExitCode == 0 ? "INFO" : "ERROR", "firewall.setup_finished", "exit_code=" + process.ExitCode + " scope=Private_LocalSubnet");
                    if (!exiting) MessageBox.Show(this, process.ExitCode == 0 ? "Home-network access is allowed. Make sure Windows labels your home network Private. No router changes are needed." : "The firewall rule could not be added.", Program.Title);
                }
            }
            catch (Win32Exception ex)
            {
                TraceLog.Failure("firewall.setup_failed", ex, "administrator_approval_may_have_been_cancelled=true");
                if (!exiting) MessageBox.Show(this, "A parent needs to approve the Windows administrator prompt to allow home-network access.", Program.Title);
            }
        }
        private void OnTrace(string level, string name, string details)
        {
            if (exiting || !IsHandleCreated) return;
            string text = null; bool good = false;
            if (name == "receiver.listening") { text = "Receiver ready. Keep Minecraft aimed at the trapdoor."; good = true; }
            if (name == "receiver.failed") text = "Receiver is reconnecting. Close any old receiver/script windows; it will retry automatically.";
            if (name == "hotkey.request_failed" || name == "streamdeck.request_failed") text = "No reply from PC 2. Check the chamber before trying again.";
            if (name == "hotkey.completed" || name == "streamdeck.completed" || (name == "request.result" && details.Contains("command=click")))
            {
                if (details.Contains("status=clicked")) { text = "Right-click sent. Check the teleport and re-arm the chamber."; good = true; }
                else if (details.Contains("status=unfocused")) text = "Bring Minecraft to the front on PC 2 and aim at the trapdoor.";
                else if (details.Contains("status=cooldown")) text = "That press was too soon after the last one.";
                else if (details.Contains("status=dry-run")) text = "Test mode: PC 2 received the request without clicking.";
                else text = "PC 2 could not send the click. Check Minecraft and open logs.";
            }
            if (text == null) return;
            string display = text;
            try { BeginInvoke((Action)delegate { if (!exiting) SetStatus(display, good); }); }
            catch (InvalidOperationException) { }
        }
        private void SetStatus(string text, bool good)
        {
            status.Text = text;
            status.BackColor = good ? Color.FromArgb(232, 245, 242) : Color.FromArgb(255, 242, 219);
            tray.Text = Program.Title + (good ? " - ready" : " - check status");
        }
        private void OpenLogs()
        {
            string path = Path.Combine(Settings.DirectoryPath, "logs");
            Directory.CreateDirectory(path); WindowsIntegration.Open(path);
        }
        private void ShowWindow() { Show(); WindowState = FormWindowState.Normal; Activate(); }
        public void Quit() { exiting = true; Close(); }
        protected override void OnFormClosing(FormClosingEventArgs e)
        {
            if (!exiting && e.CloseReason == CloseReason.UserClosing) { e.Cancel = true; Hide(); return; }
            exiting = true;
            timer.Stop(); TraceLog.Entry -= OnTrace; StopServices();
            tray.Visible = false;
            base.OnFormClosing(e);
        }
        protected override void Dispose(bool disposing)
        {
            if (disposing) { timer.Dispose(); tray.Dispose(); pearlIcon.Dispose(); }
            base.Dispose(disposing);
        }
    }
}
