using System.Net.Sockets;
using System.Text;

namespace AlbumWall.App;

/// <summary>
/// Whether the Wayland compositor has a screen to show a window on, asked before Avalonia starts.
///
/// With none at all, Avalonia's Wayland backend aborts the process at start ("Expected at least
/// one wl_output at this point"). A desktop always has one; the kiosk does not when its screen is
/// off or unplugged at boot: labwc runs with no output, AlbumWall aborted, and the kiosk launcher,
/// after three quick exits, stopped restarting it, so plugging the screen in later showed nothing
/// (found on Pi5-POE, headless, 2026-10-27; both the single file and Native AOT). Now the app waits
/// for a screen and starts when one appears.
///
/// The question is a dozen bytes of the Wayland wire protocol on the compositor's socket: ask for
/// the registry, then a sync, and count the wl_output globals announced before the sync's done.
/// Anything unexpected answers "cannot tell", and the app starts as it always did.
/// </summary>
internal static class WaylandScreens
{
    /// <summary>The compositor's screens (wl_output globals), or null when it cannot be asked.</summary>
    public static int? Count()
    {
        var display = Environment.GetEnvironmentVariable("WAYLAND_DISPLAY");
        if (string.IsNullOrEmpty(display)) return null;
        var path = display.StartsWith('/')
            ? display
            : Path.Combine(Environment.GetEnvironmentVariable("XDG_RUNTIME_DIR") ?? "", display);
        try
        {
            using var socket = new Socket(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified)
            {
                ReceiveTimeout = 2000,
            };
            socket.Connect(new UnixDomainSocketEndPoint(path));
            // wl_display (object 1): get_registry (opcode 1) as object 2, then sync (opcode 0) as
            // object 3. A message is its object, then its size and opcode in one word, then its
            // arguments, all in the machine's byte order.
            Span<uint> requests = [1, 12u << 16 | 1, 2, 1, 12u << 16 | 0, 3];
            socket.Send(System.Runtime.InteropServices.MemoryMarshal.AsBytes(requests));

            var outputs = 0;
            var buffer = new byte[1 << 16];
            int have = 0, at = 0;
            while (true)
            {
                if (have - at < 8 || have - at < (int)(BitConverter.ToUInt32(buffer, at + 4) >> 16))
                {
                    // Too little for the next message: move what is left to the front and read on.
                    Buffer.BlockCopy(buffer, at, buffer, 0, have - at);
                    have -= at;
                    at = 0;
                    if (have == buffer.Length) return null;
                    var n = socket.Receive(buffer, have, buffer.Length - have, SocketFlags.None);
                    if (n <= 0) return null;
                    have += n;
                    continue;
                }
                var obj = BitConverter.ToUInt32(buffer, at);
                var word = BitConverter.ToUInt32(buffer, at + 4);
                int size = (int)(word >> 16), opcode = (int)(word & 0xFFFF);
                if (size < 8) return null;
                if (obj == 2 && opcode == 0 && size >= 16)
                {
                    // wl_registry.global: name, interface (length with its NUL, then the bytes), version.
                    var length = (int)BitConverter.ToUInt32(buffer, at + 12);
                    if (length > 0 && 16 + length <= size
                        && Encoding.ASCII.GetString(buffer, at + 16, length - 1) == "wl_output")
                        outputs++;
                }
                else if (obj == 3 && opcode == 0) return outputs;     // wl_callback.done: all announced
                else if (obj == 1 && opcode == 0) return null;        // wl_display.error
                at += size;
            }
        }
        catch
        {
            return null;
        }
    }

    /// <summary>Waits until the compositor has a screen, if it has none; returns at once otherwise,
    /// and whenever it cannot tell. Logged once on the way in and once on the way out.</summary>
    public static void WaitForOne()
    {
        if (Count() != 0) return;
        Console.WriteLine("[wall] Wayland offers no screen yet: waiting for one");
        var started = DateTime.UtcNow;
        while (Count() == 0) Thread.Sleep(2000);
        Console.WriteLine($"[wall] a screen appeared after {(DateTime.UtcNow - started).TotalSeconds:0} s");
    }
}
