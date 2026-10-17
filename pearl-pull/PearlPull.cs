using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.Net;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;

namespace PearlPull
{
    public static class Protocol
    {
        public const int DefaultPort = 48123;
        public const long MaxAgeMs = 5000;
        private static readonly DateTime Epoch = new DateTime(1970, 1, 1, 0, 0, 0, DateTimeKind.Utc);

        public static long NowMs() { return (long)(DateTime.UtcNow - Epoch).TotalMilliseconds; }
        public static byte[] ParseKey(string value)
        {
            if (value == null || value.Length != 64) throw new ArgumentException("Pairing code must contain 64 hexadecimal characters.");
            byte[] bytes = new byte[32];
            for (int i = 0; i < bytes.Length; i++)
                bytes[i] = byte.Parse(value.Substring(i * 2, 2), NumberStyles.HexNumber, CultureInfo.InvariantCulture);
            return bytes;
        }
        public static string NewKey()
        {
            byte[] bytes = new byte[32];
            using (RandomNumberGenerator rng = RandomNumberGenerator.Create()) { rng.GetBytes(bytes); }
            return Hex(bytes);
        }
        private static string Hex(byte[] value) { return BitConverter.ToString(value).Replace("-", "").ToLowerInvariant(); }
        private static string Sign(string value, byte[] key)
        {
            using (HMACSHA256 hmac = new HMACSHA256(key)) { return Hex(hmac.ComputeHash(Encoding.ASCII.GetBytes(value))); }
        }
        private static bool Verify(string value, string signature, byte[] key)
        {
            string expected = Sign(value, key);
            if (signature.Length != expected.Length) return false;
            int difference = 0;
            for (int i = 0; i < expected.Length; i++) difference |= expected[i] ^ signature[i];
            return difference == 0;
        }
        public static byte[] Request(string id, long now, string command, byte[] key)
        {
            string body = "PP1|" + id + "|" + now.ToString(CultureInfo.InvariantCulture) + "|" + command;
            return Encoding.ASCII.GetBytes(body + "|" + Sign(body, key));
        }
        public static string[] ReadRequest(byte[] packet, byte[] key, long now)
        {
            if (packet == null || packet.Length > 256) return null;
            string[] parts = Encoding.ASCII.GetString(packet).Split('|');
            Guid id;
            long issued;
            if (parts.Length != 5 || parts[0] != "PP1" || parts[1].Length != 32 ||
                !Guid.TryParseExact(parts[1], "N", out id) ||
                !long.TryParse(parts[2], NumberStyles.None, CultureInfo.InvariantCulture, out issued) ||
                (parts[3] != "click" && parts[3] != "ping")) return null;
            if (issued < now - MaxAgeMs || issued > now + MaxAgeMs) return null;
            string body = string.Join("|", parts, 0, 4);
            return Verify(body, parts[4], key) ? parts : null;
        }
        public static byte[] Reply(string id, string status, byte[] key)
        {
            string body = "PP1-ACK|" + id + "|" + status;
            return Encoding.ASCII.GetBytes(body + "|" + Sign(body, key));
        }
        public static string ReadReply(byte[] packet, string id, byte[] key)
        {
            if (packet == null || packet.Length > 256) return null;
            string[] parts = Encoding.ASCII.GetString(packet).Split('|');
            if (parts.Length != 4 || parts[0] != "PP1-ACK" || parts[1] != id) return null;
            return Verify(string.Join("|", parts, 0, 3), parts[3], key) ? parts[2] : null;
        }
    }

    // Shared by the live receiver and tests, which supply a fake desktop.
    public sealed class RequestHandler
    {
        private sealed class CachedReply { public byte[] Packet; public long Expires; }
        private readonly byte[] key;
        private readonly bool dryRun;
        private readonly Func<bool> focused;
        private readonly Action click;
        private readonly Dictionary<string, CachedReply> replies = new Dictionary<string, CachedReply>();
        private long lastClick = long.MinValue;

        public RequestHandler(byte[] key, bool dryRun, Func<bool> focused, Action click)
        {
            this.key = key; this.dryRun = dryRun; this.focused = focused; this.click = click;
        }
        public byte[] Handle(byte[] packet, long now, long uptimeMs)
        {
            string[] request = Protocol.ReadRequest(packet, key, now);
            if (request == null) return null;
            List<string> expired = new List<string>();
            foreach (KeyValuePair<string, CachedReply> entry in replies)
                if (entry.Value.Expires < uptimeMs) expired.Add(entry.Key);
            foreach (string id in expired) replies.Remove(id);
            CachedReply cached;
            if (replies.TryGetValue(request[1], out cached)) return cached.Packet;

            string status;
            if (request[3] == "ping") status = "ready";
            else if (lastClick != long.MinValue && uptimeMs - lastClick < 500) status = "cooldown";
            else if (dryRun) { status = "dry-run"; lastClick = uptimeMs; }
            else if (!focused()) status = "unfocused";
            else
            {
                // Failed injections also consume the cooldown; never queue another click.
                lastClick = uptimeMs;
                try { click(); status = "clicked"; }
                catch (Exception) { status = "input-error"; }
            }
            byte[] reply = Protocol.Reply(request[1], status, key);
            replies[request[1]] = new CachedReply { Packet = reply, Expires = uptimeMs + 15000 };
            return reply;
        }
    }

    public static class Transport
    {
        public static string Send(string address, int port, string pairingCode, string command)
        {
            byte[] key = Protocol.ParseKey(pairingCode);
            string id = Guid.NewGuid().ToString("N");
            byte[] request = Protocol.Request(id, Protocol.NowMs(), command, key);
            using (UdpClient socket = new UdpClient(AddressFamily.InterNetwork))
            {
                socket.Connect(IPAddress.Parse(address), port);
                socket.Client.ReceiveTimeout = 200;
                for (int attempt = 0; attempt < 3; attempt++)
                {
                    socket.Send(request, request.Length);
                    Stopwatch wait = Stopwatch.StartNew();
                    while (wait.ElapsedMilliseconds < 200)
                    {
                        try
                        {
                            IPEndPoint peer = new IPEndPoint(IPAddress.Any, 0);
                            string result = Protocol.ReadReply(socket.Receive(ref peer), id, key);
                            if (result != null) return result;
                        }
                        catch (SocketException ex)
                        {
                            if (ex.SocketErrorCode == SocketError.TimedOut || ex.SocketErrorCode == SocketError.WouldBlock) break;
                            throw;
                        }
                    }
                }
            }
            throw new TimeoutException("No reply from PC 2. Check its receiver, IP address, pairing code, firewall, and Windows clock synchronization. The click may have happened if replies were lost; check before retrying.");
        }

        public static void Listen(string address, int port, string pairingCode, bool dryRun)
        {
            byte[] key = Protocol.ParseKey(pairingCode);
            RequestHandler handler = new RequestHandler(key, dryRun, DesktopInput.IsMinecraftFocused, DesktopInput.RightClick);
            using (UdpClient socket = new UdpClient(new IPEndPoint(IPAddress.Parse(address), port)))
            {
                Stopwatch uptime = Stopwatch.StartNew();
                Console.WriteLine("Receiver listening on UDP port {0}. Mode: {1}.", port, dryRun ? "DRY RUN (no input)" : "right-click");
                while (true)
                {
                    IPEndPoint peer = new IPEndPoint(IPAddress.Any, 0);
                    byte[] packet;
                    try { packet = socket.Receive(ref peer); }
                    catch (SocketException ex)
                    {
                        // Windows reports an ICMP reply from a departed sender on the next receive.
                        if (ex.SocketErrorCode == SocketError.ConnectionReset) continue;
                        throw;
                    }
                    byte[] reply = handler.Handle(packet, Protocol.NowMs(), uptime.ElapsedMilliseconds);
                    if (reply == null) continue;
                    socket.Send(reply, reply.Length, peer);
                    string[] fields = Encoding.ASCII.GetString(reply).Split('|');
                    Console.WriteLine("{0:T} {1}: {2}", DateTime.Now, peer.Address, fields[2]);
                }
            }
        }
    }

    public static class DesktopInput
    {
        [StructLayout(LayoutKind.Sequential)] private struct MouseInput
        {
            public int Dx, Dy;
            public uint MouseData, Flags, Time;
            public UIntPtr ExtraInfo;
        }
        [StructLayout(LayoutKind.Sequential)] private struct Input
        {
            public uint Type;
            public MouseInput Mouse;
        }
        [StructLayout(LayoutKind.Sequential)] private struct Point { public int X, Y; }
        [StructLayout(LayoutKind.Sequential)] private struct Message
        {
            public IntPtr Window;
            public uint Id;
            public UIntPtr WParam;
            public IntPtr LParam;
            public uint Time;
            public Point Position;
            public uint Private;
        }
        [DllImport("user32.dll")] private static extern IntPtr GetForegroundWindow();
        [DllImport("user32.dll")] private static extern uint GetWindowThreadProcessId(IntPtr window, out uint processId);
        private delegate bool EnumWindowCallback(IntPtr window, IntPtr parameter);
        [DllImport("user32.dll")] private static extern bool EnumChildWindows(IntPtr parent, EnumWindowCallback callback, IntPtr parameter);
        [DllImport("user32.dll")] private static extern bool IsWindowVisible(IntPtr window);
        [DllImport("user32.dll", SetLastError = true)] private static extern uint SendInput(uint count, Input[] inputs, int size);
        [DllImport("user32.dll", SetLastError = true)] private static extern bool RegisterHotKey(IntPtr window, int id, uint modifiers, uint key);
        [DllImport("user32.dll")] private static extern bool UnregisterHotKey(IntPtr window, int id);
        [DllImport("user32.dll", SetLastError = true)] private static extern int GetMessage(out Message message, IntPtr window, uint min, uint max);

        private static string ProcessName(IntPtr window)
        {
            uint pid;
            GetWindowThreadProcessId(window, out pid);
            if (pid == 0) return "";
            try
            {
                using (Process process = Process.GetProcessById((int)pid))
                    return process.ProcessName;
            }
            catch (ArgumentException) { return ""; }
            catch (InvalidOperationException) { return ""; }
            catch (Win32Exception) { return ""; }
        }
        public static bool IsMinecraftFocused()
        {
            IntPtr window = GetForegroundWindow();
            string name = ProcessName(window);
            if (string.Equals(name, "Minecraft.Windows", StringComparison.OrdinalIgnoreCase)) return true;
            // Older Bedrock installations can use a UWP frame belonging to this Windows host.
            if (!string.Equals(name, "ApplicationFrameHost", StringComparison.OrdinalIgnoreCase)) return false;
            bool found = false;
            EnumChildWindows(window, delegate(IntPtr child, IntPtr unused)
            {
                found = IsWindowVisible(child) && string.Equals(ProcessName(child), "Minecraft.Windows", StringComparison.OrdinalIgnoreCase);
                return !found;
            }, IntPtr.Zero);
            return found;
        }
        public static void RightClick()
        {
            // Check again immediately before sending input. Never activate a window or move the aim.
            if (!IsMinecraftFocused()) throw new InvalidOperationException("Minecraft lost focus.");
            Input down = new Input { Type = 0, Mouse = new MouseInput { Flags = 0x0008 } };
            Input up = new Input { Type = 0, Mouse = new MouseInput { Flags = 0x0010 } };
            int size = Marshal.SizeOf(typeof(Input));
            if (SendInput(1, new Input[] { down }, size) != 1)
                throw new Win32Exception(Marshal.GetLastWin32Error(), "Windows did not accept the mouse press.");
            try { System.Threading.Thread.Sleep(50); }
            finally
            {
                if (SendInput(1, new Input[] { up }, size) != 1)
                {
                    SendInput(1, new Input[] { up }, size);
                    throw new Win32Exception(Marshal.GetLastWin32Error(), "Windows did not accept the mouse release.");
                }
            }
        }
        public static void RunHotkey(string address, int port, string pairingCode, int functionKey)
        {
            if (functionKey < 1 || functionKey > 24) throw new ArgumentOutOfRangeException("functionKey");
            const int id = 1;
            // MOD_NOREPEAT: holding the key generates one request until released.
            if (!RegisterHotKey(IntPtr.Zero, id, 0x4000, (uint)(0x70 + functionKey - 1)))
                throw new Win32Exception(Marshal.GetLastWin32Error(), "That hotkey is already registered. Try another function key.");
            try
            {
                Console.WriteLine("Press F{0} to request one right-click on PC 2. Close this window to stop.", functionKey);
                Message message;
                int result;
                while ((result = GetMessage(out message, IntPtr.Zero, 0, 0)) > 0)
                {
                    if (message.Id != 0x0312 || message.WParam.ToUInt64() != (ulong)id) continue;
                    // Do not execute keypresses that backed up while an earlier request timed out.
                    if (unchecked((uint)Environment.TickCount - message.Time) > 200) continue;
                    try { Console.WriteLine("{0:T} {1}", DateTime.Now, Transport.Send(address, port, pairingCode, "click")); }
                    catch (Exception ex) { Console.WriteLine(ex.Message); }
                }
                if (result == -1) throw new Win32Exception(Marshal.GetLastWin32Error());
            }
            finally { UnregisterHotKey(IntPtr.Zero, id); }
        }
    }
}
