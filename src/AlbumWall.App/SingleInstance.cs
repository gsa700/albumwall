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

namespace AlbumWall.App;

public static class SingleInstance
{
    private static FileStream? _lock;
    private static CancellationTokenSource? _listening;

    private static string ConfigDir => Path.GetDirectoryName(Settings.Path)!;

    /// One name per user and settings folder. Short, because a Unix socket's
    /// path has a limit of about a hundred bytes and the Mac's temp folder is long.
    private static string PipeName
    {
        get
        {
            var key = Encoding.UTF8.GetBytes(Environment.UserName + "\n" + Path.GetFullPath(ConfigDir));
            return "albumwall-" + Convert.ToHexString(SHA256.HashData(key))[..16].ToLowerInvariant();
        }
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

        // On Unix the pipe is a socket file in the temp folder. One left by a copy
        // that crashed would stop the server binding; holding the lock means it is
        // nobody's, so it goes.
        if (!OperatingSystem.IsWindows())
            try { File.Delete(Path.Combine(Path.GetTempPath(), "CoreFxPipe_" + name)); } catch { }

        _ = Task.Run(async () =>
        {
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
                    var args = JsonSerializer.Deserialize<string[]>(line ?? "[]") ?? [];
                    Console.WriteLine($"[instance] from a second launch: {(args.Length == 0 ? "(nothing: come forward)" : string.Join(' ', args))}");
                    var reply = await handle(args);
                    await writer.WriteLineAsync(reply);
                }
                catch (OperationCanceledException) when (stop.IsCancellationRequested) { return; }
                catch (Exception ex)
                {
                    Console.WriteLine($"[instance] a second launch was not answered: {ex.Message}");
                    try { await Task.Delay(200, stop); } catch (OperationCanceledException) { return; }
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
                writer.WriteLine(JsonSerializer.Serialize(args));
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
