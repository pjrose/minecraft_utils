using System;
using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Security.Principal;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;
using Microsoft.Win32;

namespace PearlPull.Tray
{
    internal static class WindowsIntegration
    {
        private const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
        private const string PreferencesKey = @"Software\MinecraftUtils\PearlPull";
        public static string StartupName { get { return "MinecraftUtils-PearlPull-" + Program.Role; } }
        private static string TaskName { get { return "MinecraftUtils-PearlPull-Tray-" + Program.Role + "-" + WindowsIdentity.GetCurrent().User.Value; } }
        public static bool StartsWithWindows()
        {
            using (RegistryKey key = Registry.CurrentUser.OpenSubKey(PreferencesKey))
                return key == null || Convert.ToInt32(key.GetValue(Program.Role + "StartupDisabled", 0)) != 1;
        }
        public static bool SetStartup(bool enabled)
        {
            bool scheduled = false;
            string previousMethod;
            using (RegistryKey key = Registry.CurrentUser.OpenSubKey(PreferencesKey))
                previousMethod = key == null ? "registry" : Convert.ToString(key.GetValue(Program.Role + "StartupMethod", "registry"));
            try
            {
                dynamic service = Activator.CreateInstance(Type.GetTypeFromProgID("Schedule.Service"));
                service.Connect();
                dynamic folder = service.GetFolder("\\");
                if (enabled)
                {
                    dynamic definition = service.NewTask(0);
                    definition.RegistrationInfo.Description = "Pearl Pull " + Program.Role + ": start at sign-in and recover after a crash.";
                    definition.Principal.UserId = WindowsIdentity.GetCurrent().Name;
                    definition.Principal.LogonType = 3; // InteractiveToken: user's desktop, no password.
                    definition.Principal.RunLevel = 0;
                    definition.Settings.Enabled = true;
                    definition.Settings.DisallowStartIfOnBatteries = false;
                    definition.Settings.StopIfGoingOnBatteries = false;
                    definition.Settings.ExecutionTimeLimit = "PT0S";
                    definition.Settings.RestartInterval = "PT1M";
                    definition.Settings.RestartCount = 999;
                    definition.Settings.MultipleInstances = 2; // IgnoreNew.
                    dynamic trigger = definition.Triggers.Create(9); // Logon.
                    trigger.UserId = WindowsIdentity.GetCurrent().Name;
                    dynamic action = definition.Actions.Create(0);
                    action.Path = Application.ExecutablePath;
                    action.Arguments = "--background";
                    action.WorkingDirectory = AppDomain.CurrentDomain.BaseDirectory;
                    folder.RegisterTaskDefinition(TaskName, definition, 6, WindowsIdentity.GetCurrent().Name, null, 3);
                    scheduled = true;
                    TraceLog.Write("INFO", "startup.configured", "method=scheduled_task restart_after_crash_seconds=60 retries=999 time_limit=none");
                }
                else
                {
                    try { folder.DeleteTask(TaskName, 0); }
                    catch (COMException ex) { if ((uint)ex.ErrorCode != 0x80070002) throw; }
                }
            }
            catch (Exception ex)
            {
                TraceLog.Failure("startup.task_unavailable", ex, "registry_fallback=" + enabled);
                if (!enabled && previousMethod == "task") throw;
            }
            using (RegistryKey key = Registry.CurrentUser.CreateSubKey(RunKey))
            {
                if (enabled && !scheduled) key.SetValue(StartupName, "\"" + Application.ExecutablePath + "\" --background");
                else key.DeleteValue(StartupName, false);
            }
            using (RegistryKey key = Registry.CurrentUser.CreateSubKey(PreferencesKey))
            {
                key.SetValue(Program.Role + "StartupDisabled", enabled ? 0 : 1);
                key.SetValue(Program.Role + "StartupMethod", !enabled ? "off" : scheduled ? "task" : "registry");
            }
            return scheduled;
        }
        public static void RemoveLegacyStartup()
        {
            // Only replace the earlier utility's own task for this Windows user.
            try
            {
                string role = Program.IsReceiver ? "Receiver" : "Sender";
                string sid = WindowsIdentity.GetCurrent().User.Value;
                string name = "MinecraftUtils-PearlPull-" + role + "-" + sid;
                dynamic service = Activator.CreateInstance(Type.GetTypeFromProgID("Schedule.Service"));
                service.Connect();
                dynamic folder = service.GetFolder("\\");
                dynamic task;
                try { task = folder.GetTask(name); }
                catch (COMException ex) { if ((uint)ex.ErrorCode == 0x80070002) return; throw; }
                string expected = "MinecraftUtils Pearl Pull " + role + " for " + sid + ". Managed by Manage-Startup.ps1.";
                if ((string)task.Definition.RegistrationInfo.Description != expected) return;
                if ((int)task.State == 4) task.Stop(0);
                folder.DeleteTask(name, 0);
                TraceLog.Write("INFO", "startup.legacy_replaced", "role=" + role);
            }
            catch (Exception ex) { TraceLog.Failure("startup.legacy_check_failed", ex, "startup_continues=true"); }
        }
        public static bool LaunchScheduled()
        {
            try
            {
                dynamic service = Activator.CreateInstance(Type.GetTypeFromProgID("Schedule.Service"));
                service.Connect();
                dynamic task = service.GetFolder("\\").GetTask(TaskName);
                if ((int)task.State != 4) task.Run(null);
                return true;
            }
            catch (Exception ex) { TraceLog.Failure("startup.launch_failed", ex, "launching_directly=true"); return false; }
        }
        public static int ConfigureFirewall(int port)
        {
            try
            {
                if (port < 1024 || port > 65535) throw new ArgumentException("Invalid port.");
                dynamic policy = Activator.CreateInstance(Type.GetTypeFromProgID("HNetCfg.FwPolicy2"));
                dynamic rule = Activator.CreateInstance(Type.GetTypeFromProgID("HNetCfg.FWRule"));
                rule.Name = "MinecraftUtils-PearlPull";
                rule.Description = "Pearl Pull receiver: home subnet, Private networks only.";
                rule.Protocol = 17; // UDP
                rule.LocalPorts = port.ToString(System.Globalization.CultureInfo.InvariantCulture);
                rule.RemoteAddresses = "LocalSubnet";
                rule.Direction = 1; // Inbound
                rule.Profiles = 2; // Private only
                rule.Action = 1; // Allow
                rule.Enabled = true;
                policy.Rules.Add(rule);
                return 0;
            }
            catch (Exception)
            {
                MessageBox.Show("Windows could not add the home-network rule. A parent or administrator may need to check Firewall settings.", "Pearl Pull", MessageBoxButtons.OK, MessageBoxIcon.Error);
                return 1;
            }
        }
        public static void Open(string path) { Process.Start(new ProcessStartInfo(path) { UseShellExecute = true }); }
    }

    internal sealed class HotkeyWindow : NativeWindow, IDisposable
    {
        [DllImport("user32.dll", SetLastError = true)] private static extern bool RegisterHotKey(IntPtr window, int id, uint modifiers, uint key);
        [DllImport("user32.dll")] private static extern bool UnregisterHotKey(IntPtr window, int id);
        [DllImport("user32.dll")] private static extern int GetMessageTime();
        private readonly Settings settings;
        private int sending;
        private bool registered;
        public HotkeyWindow(Settings settings)
        {
            this.settings = settings;
            CreateHandle(new CreateParams());
            registered = RegisterHotKey(Handle, 1, 0x4000, 0x77); // F8, MOD_NOREPEAT
            if (!registered)
            {
                int error = Marshal.GetLastWin32Error();
                DestroyHandle();
                throw new Win32Exception(error, "F8 is already in use. Close the old sender or another hotkey app. The Neo button can still work.");
            }
            TraceLog.Write("INFO", "hotkey.registered", "key=F8 listener_pc=PC1 no_repeat=true");
        }
        protected override void WndProc(ref Message message)
        {
            if (message.Msg == 0x0312 && message.WParam == (IntPtr)1)
            {
                string id = Guid.NewGuid().ToString("N");
                uint age = unchecked((uint)Environment.TickCount - (uint)GetMessageTime());
                TraceLog.Write("INFO", "hotkey.detected", "request_id=" + id + " key=F8 queue_age_ms=" + age + " source=Windows_WM_HOTKEY device=not_reported");
                if (age > 200 || Interlocked.CompareExchange(ref sending, 1, 0) != 0)
                {
                    TraceLog.Write("WARN", "hotkey.discarded", "request_id=" + id + " reason=stale_or_busy");
                    return;
                }
                Task.Run(delegate
                {
                    try
                    {
                        string result = Transport.Send(settings.Address, settings.Port, settings.PairingCode, "click", id);
                        TraceLog.Write("INFO", "hotkey.completed", "request_id=" + id + " receiver_status=" + result);
                    }
                    catch (Exception ex) { TraceLog.Failure("hotkey.request_failed", ex, "request_id=" + id); }
                    finally { Interlocked.Exchange(ref sending, 0); }
                });
                return;
            }
            base.WndProc(ref message);
        }
        public void Dispose()
        {
            if (registered) { UnregisterHotKey(Handle, 1); registered = false; }
            DestroyHandle();
        }
    }
}
