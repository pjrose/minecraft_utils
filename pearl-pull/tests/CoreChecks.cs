using System;
using System.Runtime.InteropServices;
using System.Text;
using PearlPull;

public static class CoreChecks
{
    private static int checks;
    private static void Check(bool condition, string label)
    {
        if (!condition) throw new Exception("FAILED: " + label);
        checks++;
    }
    private static string Status(byte[] packet, string id, byte[] key) { return Protocol.ReadReply(packet, id, key); }
    public static int Run()
    {
        checks = 0;
        byte[] key = Protocol.ParseKey(new string('a', 64));
        byte[] other = Protocol.ParseKey(new string('b', 64));
        const long now = 1700000000000;
        string id = Guid.NewGuid().ToString("N");
        byte[] packet = Protocol.Request(id, now, "click", key);
        Check(Protocol.ReadRequest(packet, key, now) != null, "valid signature");
        Check(Protocol.ReadRequest(packet, other, now) == null, "wrong key");
        Check(Protocol.ReadRequest(packet, key, now + 5001) == null, "expired request");
        Check(Protocol.ReadRequest(packet, key, now - 5001) == null, "future request");
        Check(Protocol.ReadRequest(Encoding.ASCII.GetBytes("broken"), key, now) == null, "malformed request");
        Check(Protocol.ReadRequest(new byte[257], key, now) == null, "oversized request");
        Check(Protocol.ReadRequest(Protocol.Request(id, now, "shell", key), key, now) == null, "unsupported command");
        byte[] modified = (byte[])packet.Clone();
        modified[10] = modified[10] == (byte)'a' ? (byte)'b' : (byte)'a';
        Check(Protocol.ReadRequest(modified, key, now) == null, "tampered request");
        byte[] ack = Protocol.Reply(id, "clicked", key);
        Check(Status(ack, id, key) == "clicked", "valid reply");
        Check(Status(ack, Guid.NewGuid().ToString("N"), key) == null, "wrong reply ID");
        Check(Status(ack, id, other) == null, "forged reply");
        Check(Protocol.ParseKey(Protocol.NewKey()).Length == 32, "generated key length");

        int clicks = 0;
        bool focused = true;
        RequestHandler handler = new RequestHandler(key, false, delegate { return focused; }, delegate { clicks++; });
        ack = handler.Handle(packet, now, 0);
        Check(Status(ack, id, key) == "clicked" && clicks == 1, "one authenticated click");
        focused = false;
        Check(Status(handler.Handle(packet, now + 1000, 1000), id, key) == "clicked" && clicks == 1, "duplicate returns original result without clicking");
        string unfocusedId = Guid.NewGuid().ToString("N");
        byte[] unfocused = Protocol.Request(unfocusedId, now + 1000, "click", key);
        Check(Status(handler.Handle(unfocused, now + 1000, 1000), unfocusedId, key) == "unfocused" && clicks == 1, "foreground gating");
        focused = true;
        Check(Status(handler.Handle(unfocused, now + 1200, 1200), unfocusedId, key) == "unfocused" && clicks == 1, "failed click is never queued for later focus");
        string nextId = Guid.NewGuid().ToString("N");
        Check(Status(handler.Handle(Protocol.Request(nextId, now + 1200, "click", key), now + 1200, 1200), nextId, key) == "clicked" && clicks == 2, "later new request clicks");
        string fastId = Guid.NewGuid().ToString("N");
        byte[] fast = Protocol.Request(fastId, now + 1300, "click", key);
        Check(Status(handler.Handle(fast, now + 1300, 1300), fastId, key) == "cooldown" && clicks == 2, "rapid separate press suppressed");
        Check(Status(handler.Handle(fast, now + 3000, 3000), fastId, key) == "cooldown" && clicks == 2, "cooldown retry never becomes a click");
        Check(handler.Handle(packet, now + 20000, 20000) == null && clicks == 2, "replay rejected after cache expires");
        Check(handler.Handle(Protocol.Request(id, now, "click", other), now, 20001) == null && clicks == 2, "invalid auth never reaches input");
        string pingId = Guid.NewGuid().ToString("N");
        Check(Status(handler.Handle(Protocol.Request(pingId, now, "ping", key), now, 20002), pingId, key) == "ready" && clicks == 2, "ping never clicks");

        RequestHandler dry = new RequestHandler(key, true, delegate { throw new Exception("must not inspect desktop"); }, delegate { throw new Exception("must not click"); });
        Check(Status(dry.Handle(packet, now, 0), id, key) == "dry-run", "dry run avoids desktop APIs");
        RequestHandler failed = new RequestHandler(key, false, delegate { return true; }, delegate { throw new Exception("input denied"); });
        Check(Status(failed.Handle(packet, now, 0), id, key) == "input-error", "input failure reported");

        Type nativeInput = typeof(DesktopInput).GetNestedType("Input", System.Reflection.BindingFlags.NonPublic);
        Check(Marshal.SizeOf(nativeInput) == (IntPtr.Size == 8 ? 40 : 28), "Win32 INPUT structure layout");
        return checks;
    }
}
