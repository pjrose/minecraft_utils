// Development-only host for the production core. Never injects mouse input.
using System;
using System.ComponentModel;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Threading;
using System.Threading.Tasks;
using PearlPull;
using PearlPull.Tray;

internal static class TestHost
{
    private static int Main(string[] args)
    {
        try
        {
            switch (args[0])
            {
                case "core": Console.WriteLine("Core checks passed: " + CoreChecks.Run()); return 0;
                case "settings": CheckSettings(args[1]); return 0;
                case "logs": CheckLogs(args[1]); return 0;
                case "stop": CheckReceiverStop(int.Parse(args[1]), args[2]); return 0;
                case "receiver":
                    Settings receiver = Settings.LoadFrom(args[1], true);
                    TraceLog.Initialize("receiver", args[2]);
                    Transport.Listen("127.0.0.1", receiver.Port, receiver.PairingCode, true);
                    return 0;
                case "streamdeck":
                    Settings sender = Settings.LoadFrom(args[1], false);
                    TraceLog.Initialize("sender", args[2]);
                    using (StreamDeckTrigger trigger = new StreamDeckTrigger(sender.Address, sender.Port, sender.PairingCode, sender.StreamDeckToken, int.Parse(args[3])))
                    {
                        trigger.Start(); trigger.Wait();
                    }
                    return 0;
                case "send":
                    Settings destination = Settings.LoadFrom(args[1], false);
                    TraceLog.Initialize("sender", args[2]);
                    string status = Transport.Send(destination.Address, destination.Port, destination.PairingCode, args[3]);
                    Console.WriteLine("STATUS=" + status);
                    return status == "ready" || status == "dry-run" || status == "clicked" ? 0 : 1;
                default: throw new ArgumentException("Unknown test command.");
            }
        }
        catch (Exception ex) { Console.Error.WriteLine(ex); return 1; }
        finally { TraceLog.Close(); }
    }
    private static void CheckSettings(string path)
    {
        Settings value = Settings.LoadFrom(path, false);
        string key = value.PairingCode;
        value.StreamDeckToken = Protocol.NewKey();
        value.SaveTo(path, false);
        Settings loaded = Settings.LoadFrom(path, false);
        if (loaded.PairingCode != key || loaded.StreamDeckToken != value.StreamDeckToken) throw new Exception("Migration lost a secret.");
        value.Address = "not an IP";
        bool rejected = false;
        try { value.SaveTo(path, false); } catch (ArgumentException) { rejected = true; }
        if (!rejected) throw new Exception("Invalid settings were saved.");
        if (Settings.LoadFrom(path, false).Address != "127.0.0.1") throw new Exception("Failed save damaged existing settings.");
        Console.WriteLine("Migration and atomic save passed");
    }
    private static void CheckLogs(string folder)
    {
        TextWriter console = Console.Out;
        try
        {
            Console.SetOut(TextWriter.Null);
            TraceLog.Initialize("redaction", folder);
            TraceLog.Write("INFO", "fixture", "secret=" + new string('a', 64) + "\r\ninjected-line");
            TraceLog.Failure("native.failure", new Win32Exception(5), "test=true");
            for (int session = 0; session < 7; session++)
            {
                TraceLog.Initialize("rotation", folder);
                for (int line = 0; line < 270; line++) TraceLog.Write("TRACE", "large", new string('x', 4096));
            }
            TraceLog.Close();
        }
        finally { Console.SetOut(console); }
        string blocked = Path.Combine(folder, "not-a-directory");
        File.WriteAllText(blocked, "file");
        TraceLog.Initialize("fallback", blocked);
        TraceLog.Write("INFO", "fallback.succeeded", "still_running=true");
    }
    private static void CheckReceiverStop(int port, string logs)
    {
        TraceLog.Initialize("receiver", logs);
        using (CancellationTokenSource stop = new CancellationTokenSource())
        using (ManualResetEvent ready = new ManualResetEvent(false))
        {
            Action<string, string, string> onTrace = delegate(string level, string name, string details) { if (name == "receiver.listening") ready.Set(); };
            TraceLog.Entry += onTrace;
            Task receiver = Task.Run(delegate { Transport.Listen("127.0.0.1", port, new string('a', 64), true, stop.Token); });
            try
            {
                if (!ready.WaitOne(5000)) throw new Exception("Receiver did not listen.");
                stop.Cancel();
                if (!receiver.Wait(3000)) throw new Exception("Receiver did not stop.");
                using (UdpClient rebound = new UdpClient(new IPEndPoint(IPAddress.Loopback, port))) { }
                Console.WriteLine("Graceful stop released the port");
            }
            finally { stop.Cancel(); TraceLog.Entry -= onTrace; receiver.Wait(3000); }
        }
    }
}
