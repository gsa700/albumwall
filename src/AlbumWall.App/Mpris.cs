// AlbumWall — MPRIS, so the keyboard's media keys reach the app.
//
// WHY NOT JUST HANDLE THE KEYS. On GNOME the shell grabs XF86AudioPlay and its
// friends globally and does not pass them to the focused window at all, so an
// in-app key handler would do nothing here however correct it looked. What the
// shell does instead is call methods on whichever application is sitting on the
// bus as an MPRIS player. That is the interface, so that is what this speaks.
//
// The same decision pays twice: implementing MPRIS also puts the app in GNOME's
// own media controls — the panel, the lock screen — and makes the keys work
// whether or not the window has focus, which is the behavior a music player is
// expected to have while you are doing something else.
//
// This is Linux-only. Windows has an equivalent (SystemMediaTransportControls)
// and it is a separate piece of work; the app simply goes without there, which
// is why every failure in here is swallowed rather than surfaced.

using Tmds.DBus.Protocol;

namespace AlbumWall.App;

public sealed class Mpris : IPathMethodHandler
{
    /// The bus name the shell looks for. Everything after the final dot is ours
    /// to choose; the prefix is what identifies this as a media player at all.
    public const string BusName = "org.mpris.MediaPlayer2.albumwall";

    public string Path => "/org/mpris/MediaPlayer2";
    public bool HandlesChildPaths => false;

    /// What the app has to tell the shell about itself.
    ///
    /// Answered fresh on every request, AND pushed when it changes. The pull
    /// side alone was the original design and it was wrong: see NotifyChanged.
    ///
    /// TrackKey is the playing file's path, or empty. It is not shown anywhere
    /// — it exists so that mpris:trackid can differ from one track to the next,
    /// which is how a client tells "the same song, still going" from "a new
    /// song started".
    public sealed record State(
        bool Playing,
        bool HasTrack,
        string Title,
        string Artist,
        string Album,
        string ArtUrl,
        long LengthMicros,
        long PositionMicros,
        double Volume,
        string TrackKey = "");

    /// Keeps the connection rooted for the life of the process. See StartAsync.
    private static DBusConnection? Held;

    private readonly Func<State> _state;
    private State? _lastAnnounced;
    private readonly Action<string> _command;
    private DBusConnection? _connection;

    private readonly Action<TimeSpan> _seek;

    private Mpris(Func<State> state, Action<string> command, Action<TimeSpan> seek)
    {
        _state = state;
        _command = command;
        _seek = seek;
    }

    /// Connects and claims the name, or returns null and leaves the app alone.
    ///
    /// Every failure here is non-fatal by design: no session bus, no permission,
    /// another copy of the app already holding the name. A music player that
    /// refuses to start because the media keys are unavailable would be a worse
    /// bargain than one whose media keys are unavailable.
    public static async Task<Mpris?> StartAsync(Func<State> state, Action<string> command,
                                                Action<TimeSpan> seek)
    {
        try
        {
            // THE SHARED SESSION CONNECTION, not one of our own.
            //
            // A privately constructed DBusConnection is torn down almost at once
            // here — the name is granted, the app reports success, and the whole
            // connection is off the bus a moment later, leaving a player that
            // says it is serving and answers nothing. The shared one is owned by
            // the library, lives as long as the process, and is the same
            // connection Avalonia is already using for the desktop portals.
            var address = Environment.GetEnvironmentVariable("DBUS_SESSION_BUS_ADDRESS");
            if (string.IsNullOrEmpty(address)) return null;

            // OWN CONNECTION, AND AutoConnect OFF.
            //
            // The shared DBusConnection.Session cannot be used: requesting a name
            // on it fails with "Method cannot be used on autoconnect connections",
            // which is also what an options object with AutoConnect left on does.
            // Claiming a bus name means owning the connection that holds it.
            var connection = new DBusConnection(new DBusConnectionOptions(address)
            {
                AutoConnect = false,
                OnException = ctx => Console.WriteLine($"[mpris] connection error [{ctx.Source}]: {ctx.Exception}"),
            });
            await connection.ConnectAsync();

            // Held statically. The window holds the Mpris too, but the connection
            // is the thing the bus cares about and a collected one takes the name
            // with it — the app then reports success and answers nothing.
            Held = connection;

            var mpris = new Mpris(state, command, seek);
            connection.AddMethodHandler(mpris);
            var granted = await connection.TryRequestNameAsync(BusName, RequestNameOptions.None);

            mpris._connection = connection;
            Console.WriteLine($"[mpris] {connection.UniqueName} owns {BusName}: {granted}");

            return granted ? mpris : null;
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[mpris] unavailable: {ex.Message}");
            return null;
        }
    }

    public ValueTask HandleMethodAsync(MethodContext context)
    {
        try { Handle(context); }
        catch (Exception ex) { Console.WriteLine($"[mpris] {ex.Message}"); }
        return default;
    }

    private void Handle(MethodContext context)
    {
        var request = context.Request;
        var member = request.MemberAsString ?? "";

        switch (request.InterfaceAsString)
        {
            case "org.freedesktop.DBus.Introspectable" when member == "Introspect":
                ReplyString(context, "s", IntrospectXml);
                return;

            case "org.freedesktop.DBus.Properties":
                Properties(context, member);
                return;

            case "org.mpris.MediaPlayer2":
                // Raise is what clicking the shell's media widget does.
                if (member is "Raise" or "Quit") { _command(member); ReplyEmpty(context); return; }
                break;

            case "org.mpris.MediaPlayer2.Player":
                if (member is "Next" or "Previous" or "Pause" or "PlayPause" or "Stop" or "Play")
                {
                    Console.WriteLine($"[mpris] {member}");
                    _command(member);
                    ReplyEmpty(context);
                    return;
                }
                if (member is "Seek" or "SetPosition")
                {
                    Seek(request, member);
                    ReplyEmpty(context);
                    return;
                }
                // OpenUri is not offered: SupportedUriSchemes is empty.
                break;
        }

        context.ReplyUnknownMethodError();
    }

    /// Seek(offset) moves by that much from where it is; SetPosition(track, at)
    /// goes to a place in a NAMED track, and is ignored if that track is no
    /// longer the one playing, since the shell may be answering an old
    /// Metadata. The spec's rules for going out of range: before the start is
    /// the start; past the end, Seek means the next track and SetPosition
    /// means nothing.
    private void Seek(Message request, string member)
    {
        var s = _state();
        if (!s.HasTrack || s.LengthMicros <= 0) return;

        var reader = request.GetBodyReader();
        long to;
        if (member == "Seek")
        {
            to = s.PositionMicros + reader.ReadInt64();
            if (to >= s.LengthMicros) { _command("Next"); return; }
        }
        else
        {
            var track = reader.ReadObjectPathAsString();
            to = reader.ReadInt64();
            if (track != TrackId(s) || to < 0 || to > s.LengthMicros) return;
        }

        Console.WriteLine($"[mpris] {member} -> {to / 1_000_000.0:0.0} s");
        _seek(TimeSpan.FromMicroseconds(Math.Max(0, to)));
    }

    private void Properties(MethodContext context, string member)
    {
        var reader = context.Request.GetBodyReader();

        switch (member)
        {
            case "Get":
            {
                var iface = reader.ReadString();
                var name = reader.ReadString();
                // Not "using": a using variable cannot be passed by ref, and it
                // must be passed by ref. See WriteAll.
                var writer = context.CreateReplyWriter("v");
                try
                {
                    Write(ref writer, name, _state());
                    context.Reply(writer.CreateMessage());
                }
                finally { writer.Dispose(); }
                return;
            }

            case "GetAll":
            {
                var iface = reader.ReadString();
                var writer = context.CreateReplyWriter("a{sv}");
                try
                {
                    WriteAll(ref writer, iface);
                    context.Reply(writer.CreateMessage());
                }
                finally { writer.Dispose(); }
                return;
            }

            // Volume and Rate are the only writable properties MPRIS defines, and
            // neither is offered here, so accepting a Set silently is the honest
            // no-op: the values it would write are already declared fixed.
            case "Set":
                ReplyEmpty(context);
                return;
        }

        context.ReplyUnknownMethodError();
    }

    private static readonly string[] RootProperties =
    [
        "CanQuit", "CanRaise", "HasTrackList", "Identity", "DesktopEntry",
        "SupportedUriSchemes", "SupportedMimeTypes",
    ];

    private static readonly string[] PlayerProperties =
    [
        "PlaybackStatus", "Metadata", "Position", "Rate", "MinimumRate", "MaximumRate",
        "Volume", "CanGoNext", "CanGoPrevious", "CanPlay", "CanPause", "CanSeek", "CanControl",
    ];

    /// EVERY METHOD THAT WRITES TAKES THE WRITER BY REF. THIS IS NOT STYLE.
    ///
    /// MessageWriter is a ref struct. Pass it to a helper by value and the helper
    /// writes into a COPY, which is then discarded — so the reply goes out with
    /// its signature declaring a{sv} and no body behind it. dbus-broker calls
    /// that "a message with an invalid body" and closes the connection, which
    /// looks from inside the app like the bus mysteriously hanging up: the name
    /// is granted, the log says it is serving, and it is off the bus a moment
    /// later, answering nothing.
    ///
    /// It cost an afternoon. What found it in the end was building the same
    /// service twice — an isolated probe that worked, and the real one that did
    /// not — and moving code between them until the only difference left was
    /// that the working one wrote inline and the broken one wrote in a method.
    private void WriteAll(ref MessageWriter writer, string iface)
    {
        var names = iface == "org.mpris.MediaPlayer2" ? RootProperties : PlayerProperties;
        var state = _state();

        var dict = writer.WriteDictionaryStart();
        foreach (var name in names)
        {
            writer.WriteDictionaryEntryStart();
            writer.WriteString(name);
            Write(ref writer, name, state);
        }
        writer.WriteDictionaryEnd(dict);
    }

    private static void Write(ref MessageWriter writer, string name, State s)
    {
        if (name == "Metadata") { WriteMetadata(ref writer, s); return; }
        writer.WriteVariant(Value(name, s));
    }

    /// The track the shell shows in its media controls: a dictionary inside a
    /// variant, which is why it cannot be a VariantValue like everything else —
    /// this library's VariantValue has no dictionary case, so the variant is
    /// written out by hand as a signature followed by its value.
    private static void WriteMetadata(ref MessageWriter writer, State s)
    {
        writer.WriteSignature("a{sv}");
        var dict = writer.WriteDictionaryStart();

        // Required, and must be a valid object path even with nothing playing:
        // shells discard the whole map when it is missing or malformed.
        //
        // A DIFFERENT PATH PER TRACK. It was one constant path for every track
        // here, which is what the spec calls a unique identifier and is not one:
        // a client watching for the next song sees the identity it already has
        // and can reasonably decide nothing happened.
        writer.WriteDictionaryEntryStart();
        writer.WriteString("mpris:trackid");
        writer.WriteVariant(VariantValue.ObjectPath(new ObjectPath(TrackId(s))));

        if (s.HasTrack)
        {
            writer.WriteDictionaryEntryStart();
            writer.WriteString("xesam:title");
            writer.WriteVariant(VariantValue.String(s.Title));

            writer.WriteDictionaryEntryStart();
            writer.WriteString("xesam:artist");
            writer.WriteVariant(VariantValue.Array(new[] { s.Artist }));

            writer.WriteDictionaryEntryStart();
            writer.WriteString("xesam:album");
            writer.WriteVariant(VariantValue.String(s.Album));

            writer.WriteDictionaryEntryStart();
            writer.WriteString("mpris:length");
            writer.WriteVariant(VariantValue.Int64(s.LengthMicros));

            if (!string.IsNullOrEmpty(s.ArtUrl))
            {
                writer.WriteDictionaryEntryStart();
                writer.WriteString("mpris:artUrl");
                writer.WriteVariant(VariantValue.String(s.ArtUrl));
            }
        }

        writer.WriteDictionaryEnd(dict);
    }

    private static VariantValue Value(string name, State s) => name switch
    {
        "CanQuit" or "CanRaise" => VariantValue.Bool(true),
        "HasTrackList" => VariantValue.Bool(false),
        "Identity" => VariantValue.String(App.DisplayName),
        "DesktopEntry" => VariantValue.String("albumwall"),
        "SupportedUriSchemes" or "SupportedMimeTypes" => VariantValue.Array(System.Array.Empty<string>()),

        "PlaybackStatus" => VariantValue.String(Status(s)),
        "Position" => VariantValue.Int64(s.PositionMicros),
        "Rate" or "MinimumRate" or "MaximumRate" => VariantValue.Double(1.0),

        // The real figure, not a polite fiction: the shell shows what the app
        // is actually playing at. Still read-only here — accepting a Set means
        // parsing a variant out of the request body, which is work for the day
        // something actually tries to set it.
        "Volume" => VariantValue.Double(s.Volume),
        "CanGoNext" or "CanGoPrevious" or "CanPlay" or "CanPause" or "CanControl" => VariantValue.Bool(true),

        // Only when there is a track whose length is known: with no length
        // there is no bar to click, and nowhere to say "the end" is.
        "CanSeek" => VariantValue.Bool(s.HasTrack && s.LengthMicros > 0),

        _ => VariantValue.String(""),
    };

    private static string Status(State s) => !s.HasTrack ? "Stopped" : s.Playing ? "Playing" : "Paused";

    /// An object path standing for the playing track. Only the last element is
    /// ours; it may hold nothing but [A-Za-z0-9_], so the file path becomes a
    /// hash rather than travelling as itself — which also keeps the listener's
    /// directory layout off the session bus.
    private static string TrackId(State s)
    {
        const string root = "/org/mpris/MediaPlayer2/albumwall/track";
        if (!s.HasTrack || s.TrackKey.Length == 0) return root + "/none";

        var hash = System.Security.Cryptography.SHA256.HashData(
            System.Text.Encoding.UTF8.GetBytes(s.TrackKey));
        return root + "/" + System.Convert.ToHexString(hash, 0, 8);
    }

    /// The properties that move while the app runs, and so the ones a change is
    /// announced for. Position is deliberately absent: MPRIS says it must not
    /// be reported this way, because a value that changes continuously would
    /// have every client redrawing on every tick. Clients estimate it from Rate
    /// and correct on the Seeked signal.
    private static readonly string[] ChangingProperties =
    [
        "PlaybackStatus", "Metadata", "Volume", "CanGoNext", "CanGoPrevious", "CanPlay", "CanPause",
        "CanSeek",
    ];

    /// Where playback jumped to. Clients estimate Position from the clock, so
    /// every jump has to be told, whoever made it: a drag on the app's own bar
    /// that went unannounced would leave the shell's bar running on from the
    /// old place.
    public void NotifySeeked(TimeSpan at)
    {
        var connection = _connection;
        if (connection is null) return;

        try
        {
            var writer = connection.GetMessageWriter();
            try
            {
                writer.WriteSignalHeader(
                    destination: null,
                    path: Path,
                    @interface: "org.mpris.MediaPlayer2.Player",
                    member: "Seeked",
                    signature: "x");
                writer.WriteInt64((long)at.TotalMicroseconds);
                connection.TrySendMessage(writer.CreateMessage());
            }
            finally { writer.Dispose(); }
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[mpris] seeked failed: {ex.Message}");
        }
    }

    /// PUSHED, NOT PULLED — and this is why the shell's media controls sat there
    /// showing nothing.
    ///
    /// Every property in here is answered fresh on request, which made the whole
    /// thing look right when it was tested the way it was tested: `gdbus call`
    /// asks, and gets a true answer every time. Nothing that actually consumes
    /// MPRIS works that way. GNOME's media controls build a proxy, read the
    /// properties ONCE when the player appears on the bus, and from that moment
    /// believe only what PropertiesChanged tells them. Emit it never and the
    /// shell keeps forever whatever was true the instant it first looked —
    /// which, for an app that claims the bus name at startup, is "Stopped,
    /// nothing playing", the state in which the shell shows no player at all.
    ///
    /// So: the pull side was never broken and was never the whole interface.
    /// The lesson is the one the Windows side already had by construction —
    /// PushToSystem exists because SMTC has to be TOLD. So does every MPRIS
    /// client. Test a D-Bus service with a client that behaves like the real
    /// ones, not with a command that re-asks.
    public void NotifyChanged()
    {
        var connection = _connection;
        if (connection is null) return;

        try
        {
            var state = _state();

            // Several paths call RefreshMprisState for one user action, so the
            // same news arrived on the bus two and three times over. Position is
            // dropped from the comparison because it always differs and is never
            // in the message: what is compared is exactly what would be sent.
            var announced = state with { PositionMicros = 0 };
            if (announced == _lastAnnounced) return;
            _lastAnnounced = announced;

            // Not "using", for the reason WriteAll sets out at length: this is a
            // ref struct and every writer here is passed by ref.
            var writer = connection.GetMessageWriter();
            try
            {
                writer.WriteSignalHeader(
                    destination: null,
                    path: Path,
                    @interface: "org.freedesktop.DBus.Properties",
                    member: "PropertiesChanged",
                    signature: "sa{sv}as");

                writer.WriteString("org.mpris.MediaPlayer2.Player");

                var dict = writer.WriteDictionaryStart();
                foreach (var name in ChangingProperties)
                {
                    writer.WriteDictionaryEntryStart();
                    writer.WriteString(name);
                    Write(ref writer, name, state);
                }
                writer.WriteDictionaryEnd(dict);

                // Nothing is invalidated: every changed property is sent with
                // its new value rather than being flagged for a re-read.
                writer.WriteArray(System.Array.Empty<string>());

                connection.TrySendMessage(writer.CreateMessage());
            }
            finally { writer.Dispose(); }
        }
        catch (Exception ex)
        {
            // Same bargain as everything else here: the player keeps playing.
            Console.WriteLine($"[mpris] notify failed: {ex.Message}");
        }
    }

    private static void ReplyEmpty(MethodContext context)
    {
        using var writer = context.CreateReplyWriter("");
        context.Reply(writer.CreateMessage());
    }

    private static void ReplyString(MethodContext context, string signature, string value)
    {
        using var writer = context.CreateReplyWriter(signature);
        writer.WriteString(value);
        context.Reply(writer.CreateMessage());
    }

    /// Hand-written rather than generated: it is fixed, it is short, and the
    /// shell reads it to find out what this player can be asked to do.
    private const string IntrospectXml = """
        <!DOCTYPE node PUBLIC "-//freedesktop//DTD D-BUS Object Introspection 1.0//EN"
        "http://www.freedesktop.org/standards/dbus/1.0/introspect.dtd">
        <node>
          <interface name="org.freedesktop.DBus.Properties">
            <method name="Get">
              <arg name="interface" type="s" direction="in"/>
              <arg name="property" type="s" direction="in"/>
              <arg name="value" type="v" direction="out"/>
            </method>
            <method name="GetAll">
              <arg name="interface" type="s" direction="in"/>
              <arg name="properties" type="a{sv}" direction="out"/>
            </method>
            <method name="Set">
              <arg name="interface" type="s" direction="in"/>
              <arg name="property" type="s" direction="in"/>
              <arg name="value" type="v" direction="in"/>
            </method>
            <signal name="PropertiesChanged">
              <arg name="interface" type="s"/>
              <arg name="changed" type="a{sv}"/>
              <arg name="invalidated" type="as"/>
            </signal>
          </interface>
          <interface name="org.mpris.MediaPlayer2">
            <method name="Raise"/>
            <method name="Quit"/>
            <property name="CanQuit" type="b" access="read"/>
            <property name="CanRaise" type="b" access="read"/>
            <property name="HasTrackList" type="b" access="read"/>
            <property name="Identity" type="s" access="read"/>
            <property name="DesktopEntry" type="s" access="read"/>
            <property name="SupportedUriSchemes" type="as" access="read"/>
            <property name="SupportedMimeTypes" type="as" access="read"/>
          </interface>
          <interface name="org.mpris.MediaPlayer2.Player">
            <method name="Next"/>
            <method name="Previous"/>
            <method name="Pause"/>
            <method name="PlayPause"/>
            <method name="Stop"/>
            <method name="Play"/>
            <method name="Seek">
              <arg name="Offset" type="x" direction="in"/>
            </method>
            <method name="SetPosition">
              <arg name="TrackId" type="o" direction="in"/>
              <arg name="Position" type="x" direction="in"/>
            </method>
            <signal name="Seeked">
              <arg name="Position" type="x"/>
            </signal>
            <property name="PlaybackStatus" type="s" access="read"/>
            <property name="Metadata" type="a{sv}" access="read"/>
            <property name="Position" type="x" access="read"/>
            <property name="Rate" type="d" access="read"/>
            <property name="MinimumRate" type="d" access="read"/>
            <property name="MaximumRate" type="d" access="read"/>
            <property name="Volume" type="d" access="readwrite"/>
            <property name="CanGoNext" type="b" access="read"/>
            <property name="CanGoPrevious" type="b" access="read"/>
            <property name="CanPlay" type="b" access="read"/>
            <property name="CanPause" type="b" access="read"/>
            <property name="CanSeek" type="b" access="read"/>
            <property name="CanControl" type="b" access="read"/>
          </interface>
        </node>
        """;
}
