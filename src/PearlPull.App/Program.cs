using System;
using System.IO;
using System.Diagnostics;
using System.Security.Principal;
using System.Threading;
using System.Windows.Forms;

namespace PearlPull.Tray
{
    internal static class Program
    {
#if RECEIVER
        public static readonly bool IsReceiver = true;
        public const string Role = "Receiver";
#else
        public static readonly bool IsReceiver = false;
        public const string Role = "Button";
#endif
        public static readonly string Title = "Pearl Pull " + Role;
        [STAThread]
        private static int Main(string[] args)
        {
            Application.EnableVisualStyles();
            Application.SetCompatibleTextRenderingDefault(false);
            if (args.Length == 2 && args[0] == "--configure-firewall")
            {
                int port;
                return int.TryParse(args[1], out port) ? WindowsIntegration.ConfigureFirewall(port) : 1;
            }
            string name = "Local\\MinecraftUtils.PearlPull." + Role + "." + WindowsIdentity.GetCurrent().User.Value;
            if (Array.IndexOf(args, "--remove-startup") >= 0)
            {
                try { WindowsIntegration.SetStartup(false); return 0; }
                catch (Exception) { return 1; }
            }
            if (Array.IndexOf(args, "--launch") >= 0)
            {
                // Launch through Task Scheduler so crash recovery applies from the first session.
                TraceLog.Initialize("launcher", Path.Combine(Settings.DirectoryPath, "logs"));
                try
                {
                    WindowsIntegration.RemoveLegacyStartup();
                    if (WindowsIntegration.StartsWithWindows())
                    {
                        WindowsIntegration.SetStartup(true);
                        if (WindowsIntegration.LaunchScheduled())
                        {
                            for (int attempt = 0; attempt < 30; attempt++)
                            {
                                try { using (EventWaitHandle signal = EventWaitHandle.OpenExisting(name + ".show")) signal.Set(); return 0; }
                                catch (WaitHandleCannotBeOpenedException) { Thread.Sleep(100); }
                            }
                        }
                    }
                    Process.Start(new ProcessStartInfo(Application.ExecutablePath) { UseShellExecute = true });
                    return 0;
                }
                catch (Exception ex)
                {
                    TraceLog.Failure("launcher.failed", ex, "opening_app_directly=true");
                    Process.Start(new ProcessStartInfo(Application.ExecutablePath) { UseShellExecute = true });
                    return 0;
                }
                finally { TraceLog.Close(); }
            }
            if (Array.IndexOf(args, "--quit") >= 0)
            {
                try { using (EventWaitHandle signal = EventWaitHandle.OpenExisting(name + ".quit")) signal.Set(); }
                catch (WaitHandleCannotBeOpenedException) { }
                return 0;
            }
            bool created;
            using (Mutex mutex = new Mutex(true, name, out created))
            {
                if (!created)
                {
                    try { using (EventWaitHandle signal = EventWaitHandle.OpenExisting(name + ".show")) signal.Set(); }
                    catch (WaitHandleCannotBeOpenedException) { }
                    return 0;
                }
                using (EventWaitHandle show = new EventWaitHandle(false, EventResetMode.AutoReset, name + ".show"))
                using (EventWaitHandle quit = new EventWaitHandle(false, EventResetMode.AutoReset, name + ".quit"))
                {
                    TraceLog.Initialize(IsReceiver ? "receiver" : "sender", Path.Combine(Settings.DirectoryPath, "logs"));
                    TraceLog.Write("INFO", "runtime.details", "role=" + Role + " os=" + Environment.OSVersion + " runtime=" + Environment.Version + " folder=" + AppDomain.CurrentDomain.BaseDirectory);
                    Application.SetUnhandledExceptionMode(UnhandledExceptionMode.CatchException);
                    Application.ThreadException += delegate(object sender, ThreadExceptionEventArgs eventArgs)
                    {
                        TraceLog.Failure("ui.failed", eventArgs.Exception, "role=" + Role);
                        if (Array.IndexOf(args, "--smoke-test") >= 0) Environment.Exit(1);
                        MessageBox.Show("Pearl Pull ran into a problem. Open logs from the pearl tray icon for details.", Title, MessageBoxButtons.OK, MessageBoxIcon.Error);
                    };
                    try
                    {
                        if (Array.IndexOf(args, "--smoke-test") >= 0)
                        {
                            // CI renders both forms and checks hide-to-tray/cleanup without settings, input, or listeners.
                            using (MainWindow window = new MainWindow(false, show, quit, true))
                            using (System.Windows.Forms.Timer timer = new System.Windows.Forms.Timer { Interval = 500 })
                            {
                                timer.Tick += delegate { timer.Stop(); window.Close(); if (window.Visible) throw new InvalidOperationException("Window did not hide to tray."); window.Quit(); };
                                timer.Start();
                                Application.Run(window);
                            }
                        }
                        else
                        {
                            WindowsIntegration.RemoveLegacyStartup();
                            Application.Run(new MainWindow(Array.IndexOf(args, "--background") >= 0, show, quit, false));
                        }
                        return 0;
                    }
                    catch (Exception ex)
                    {
                        TraceLog.Failure("app.failed", ex, "role=" + Role);
                        if (Array.IndexOf(args, "--smoke-test") < 0) MessageBox.Show("Pearl Pull could not start. Close the old script windows and try again. Logs are in " + Path.Combine(Settings.DirectoryPath, "logs"), Title, MessageBoxButtons.OK, MessageBoxIcon.Error);
                        return 1;
                    }
                    finally { TraceLog.Close(); mutex.ReleaseMutex(); }
                }
            }
        }
    }
}
