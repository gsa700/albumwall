// AlbumWall - one copy at a time, and a command line to it.
//
// Roadmap, "One instance, and a command line to it" (2026-09-20). "Never run both"
// was a rule to be remembered: two copies fight over the settings and the session,
// and whichever closes last wins. Now it cannot happen. A second launch hands its
// arguments to the copy that is running and goes; with none, that copy comes
// forward.
//
// WHO IS FIRST is a lock file held for the life of the process (instance.lock,
// beside the settings). The system lets go of it when the process ends, however it
// ends, so a crash never leaves the app refusing to start. Beside the settings,
// so a scratch ALBUMWALL_CONFIG_DIR is a separate copy, as a test run should be.
//
// THE COMMANDS go over a named pipe: a Unix socket on Linux and the Mac, a pipe on
// Windows, both CurrentUserOnly, so nobody else on the machine can drive it. One
// line of JSON in (the arguments), one line of text back (what was done), which
// the second launch prints. Nothing listens on the network: a remote from a phone
// is not this (roadmap, step 3).

using System.IO.Pipes;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace AlbumWall.App;

public static class SingleInstance
{
    private static FileStream? _lock;
    private static CancellationTokenSource? _listening;

    private static string ConfigDir => Path.GetDirectoryName(Settings.Path)!;

    /// One name per user and settings folder.
    ///
    /// On Linux and the Mac the pipe is a socket FILE, and given a bare name .NET
    /// puts it in the temp folder, which is not the same for everyone who asks:
    /// on the Mac the desktop's TMPDIR is a per-user folder under /var/folders,
    /// and a command over SSH may have none and look in /tmp, so `ssh mac
    /// AlbumWall --next` said "not running" with it open (2026-10-21, found
    /// before anyone relied on it). So the socket sits beside the settings, a
    /// path every login agrees on. A full path is used as is. A socket path has
    /// a limit of about a hundred bytes; a settings folder too deep for it (a
    /// test run's scratch folder) gets one in /tmp, which every login also
    /// shares, named for the user and the folder: inside a folder of this
    /// user's own (PrivateTmpDir), never loose in /tmp, where anyone could take
    /// the name first (security review 2026-10-26, L13).
    private static string PipeName
    {
        get
        {
            var key = Encoding.UTF8.GetBytes(Environment.UserName + "\n" + Path.GetFullPath(ConfigDir));
            var name = "albumwall-" + Convert.ToHexString(SHA256.HashData(key))[..16].ToLowerInvariant();
            if (OperatingSystem.IsWindows()) return name;
            var beside = Path.Combine(Path.GetFullPath(ConfigDir), "instance.sock");
            return Encoding.UTF8.GetByteCount(beside) <= 100 ? beside : Path.Combine(PrivateTmpDir, name + ".sock");
        }
    }

    /// /tmp/albumwall-&lt;user&gt;: the fallback socket's folder, one per user.
    private static string PrivateTmpDir
    {
        get
        {
            var user = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(Environment.UserName)))[..12].ToLowerInvariant();
            return $"/tmp/albumwall-{user}";
        }
    }

    /// Makes PrivateTmpDir 0700, or says why it cannot be used: a link, or a
    /// folder someone else made open to others. One another user made 0700
    /// cannot be written, and the bind says so.
    private static string? PreparePrivateTmpDir()
    {
        if (OperatingSystem.IsWindows()) return null;
        var dir = PrivateTmpDir;
        try
        {
            if (!Directory.Exists(dir))
                Directory.CreateDirectory(dir, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
            var info = new DirectoryInfo(dir);
            if (info.LinkTarget is not null) return $"{dir} is a link";
            if ((info.UnixFileMode & (UnixFileMode)0x3F) != 0) return $"{dir} is open to other users";
            return null;
        }
        catch (Exception ex) { return ex.Message; }
    }

    /// Takes the lock, or says another copy has it. Never throws: a lock that
    /// cannot be made at all (a read-only settings folder) lets this copy run,
    /// as every copy did before there was a lock.
    public static bool TryBecomeFirst()
    {
        try
        {
            Directory.CreateDirectory(ConfigDir);
            _lock = new FileStream(Path.Combine(ConfigDir, "instance.lock"),
                                   FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
            return true;
        }
        catch (IOException)
        {
            return false;
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[instance] no lock ({ex.Message}); running anyway");
            return true;
        }
    }

    /// Lets go early, for a copy that is starting its successor: the installed
    /// copy launched by a loose one must find the lock free, not hand its
    /// arguments to a window that is about to close.
    public static void Release()
    {
        _listening?.Cancel();
        _listening = null;
        _lock?.Dispose();
        _lock = null;
    }

    /// Answers second launches until Release or exit. `handle` is called on a
    /// worker thread with the arguments and returns the line to send back.
    public static void Listen(Func<string[], Task<string>> handle)
    {
        if (_lock is null) return;      // not first, or no lock: nothing to answer for
        var stop = (_listening = new CancellationTokenSource()).Token;
        var name = PipeName;

        // On Unix the pipe is a socket file (PipeName says where). One left by a
        // copy that crashed would stop the server binding; holding the lock means
        // it is nobody's, so it goes.
        if (!OperatingSystem.IsWindows())
        {
            if (name.StartsWith(PrivateTmpDir + "/", StringComparison.Ordinal) && PreparePrivateTmpDir() is { } why)
            {
                Console.WriteLine($"[instance] not answering second launches: {why}");
                return;
            }
            try { File.Delete(name); } catch { }
            // Gone with the process, so a fallback in /tmp is not left behind (L13).
            AppDomain.CurrentDomain.ProcessExit += (_, _) =>
            {
                if (_lock is not null) try { File.Delete(name); } catch { }
            };
        }

        _ = Task.Run(async () =>
        {
            // A failure again and again (the name taken, the folder not ours) waits
            // longer each time, up to half a minute, and is logged when it changes,
            // not five times a second for ever (L13).
            var wait = TimeSpan.FromMilliseconds(200);
            string? lastError = null;
            while (!stop.IsCancellationRequested)
            {
                try
                {
                    await using var server = new NamedPipeServerStream(name, PipeDirection.InOut, 1,
                        PipeTransmissionMode.Byte, PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
                    await server.WaitForConnectionAsync(stop);
                    using var reader = new StreamReader(server, leaveOpen: true);
                    await using var writer = new StreamWriter(server, leaveOpen: true) { AutoFlush = true };
                    var line = await reader.ReadLineAsync(stop).AsTask().WaitAsync(TimeSpan.FromSeconds(5), stop);
                    var args = JsonSerializer.Deserialize(line ?? "[]", InstanceJson.Default.StringArray) ?? [];
                    Console.WriteLine($"[instance] from a second launch: {(args.Length == 0 ? "(nothing: come forward)" : string.Join(' ', args))}");
                    var reply = await handle(args);
                    await writer.WriteLineAsync(reply);
                    wait = TimeSpan.FromMilliseconds(200);
                    lastError = null;
                }
                catch (OperationCanceledException) when (stop.IsCancellationRequested) { return; }
                catch (Exception ex)
                {
                    if (ex.Message != lastError)
                        Console.WriteLine($"[instance] a second launch was not answered: {ex.Message}");
                    lastError = ex.Message;
                    try { await Task.Delay(wait, stop); } catch (OperationCanceledException) { return; }
                    wait = TimeSpan.FromTicks(Math.Min(wait.Ticks * 2, TimeSpan.FromSeconds(30).Ticks));
                }
            }
        });
    }

    /// Hands `args` to the running copy and returns its answer, or null when
    /// none answered within `wait` (it may be starting, or closing).
    public static string? Send(string[] args, TimeSpan wait)
    {
        var until = DateTime.UtcNow + wait;
        while (true)
        {
            try
            {
                using var client = new NamedPipeClientStream(".", PipeName, PipeDirection.InOut,
                                                             PipeOptions.CurrentUserOnly);
                client.Connect(500);
                using var reader = new StreamReader(client, leaveOpen: true);
                using var writer = new StreamWriter(client, leaveOpen: true) { AutoFlush = true };
                writer.WriteLine(JsonSerializer.Serialize(args, InstanceJson.Default.StringArray));
                // No line back is no answer: on Unix a launch that queued behind
                // another is cut off when that one's pipe closes, and tries again.
                return reader.ReadLine() ?? throw new IOException("no answer");
            }
            catch (Exception) when (DateTime.UtcNow < until)
            {
                Thread.Sleep(200);
            }
            catch (Exception)
            {
                return null;
            }
        }
    }
}

// The arguments a second launch hands over, source-generated as SettingsJson is.
[JsonSerializable(typeof(string[]))]
internal sealed partial class InstanceJson : JsonSerializerContext;
