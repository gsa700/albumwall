// AlbumWall - the meters' owner: one timer reads the player and feeds every strip
// on screen (compact, the kiosk's front panel, the play bar), and the clicks that
// change their style land here. The strips themselves are in Meters.cs.

using Avalonia.Controls;
using Avalonia.Threading;
using AlbumWall.Playback;

namespace AlbumWall.App;

public partial class MainWindow
{
    private DispatcherTimer? _meterTimer;
    private readonly MeterStrip _barMeters = new(MeterPlace.Bar);
    private readonly float[] _bands = new float[Spectrum.BandCount];

    private void SetUpMeters()
    {
        BarMetersHost.Child = _barMeters;
        _barMeters.Show(MeterStyles.Parse(_settings.BarMeter), flash: false);
        _barMeters.Cycled += () =>
        {
            var style = _barMeters.Style.Next();
            _barMeters.Show(style, flash: true);
            _settings.BarMeter = style.Key();
            ScheduleSave();
        };
        Compact.Meters.Show(CompactMeterStyle, flash: false);
        Compact.MetersCycled += CycleCompactMeter;
        BarMetersHost.IsVisible = BarMetersOn;
    }

    /// Compact's choice, which the front panel shares: it is the same view.
    private MeterStyle CompactMeterStyle => MeterStyles.Parse(_settings.CompactMeter);

    private void CycleCompactMeter()
    {
        var style = CompactMeterStyle.Next();
        _settings.CompactMeter = style.Key();
        ScheduleSave();
        foreach (var v in LiveViews()) v.Meters.Show(style, flash: true);
        // A view not on screen now (compact, while the panel is clicked) catches up.
        Compact.Meters.Show(style, flash: false);
    }

    /// Preferences > Playback: the play bar's meters, on unless turned off.
    internal bool BarMetersOn
    {
        get => _settings.BarMeters != false;
        set
        {
            _settings.BarMeters = value ? null : false;
            _settings.Save();
            BarMetersHost.IsVisible = value;
            if (value && _player?.IsPlaying == true) StartMeters();
        }
    }

    /// Preferences > Playback: whether every meter reads after ReplayGain (what
    /// reaches the volume) or before it (the record as mastered, the default).
    internal bool MetersAfterGain
    {
        get => _settings.MetersAfterGain == true;
        set
        {
            _settings.MetersAfterGain = value ? true : null;
            _settings.Save();
        }
    }

    /// The play bar's strip is there to be seen: on, the bar up, the wall showing.
    private bool BarMetersLive =>
        BarMetersOn && _transportShown && !_compact && WindowState != WindowState.Minimized;

    /// Reads the levels about thirty times a second while a meter is on screen and
    /// the music plays, and hands them to every strip as read: Hymie measures the
    /// block sounding now. (mpv measured where it decoded, 200 ms ahead of the
    /// speakers, and the readings were held back that long until mpv went, 2026-10-22.)
    ///
    /// Whether there are meters at all is decided HERE, on every tick, not once
    /// on entering: a window that opens straight into compact gets there before
    /// the player exists (restored session, 2026-09-28: compact at 0.96 s, the
    /// player at 1.01 s), and a decision taken then hid the meters all session.
    ///
    /// The analyzer's bands are taken only while one is showing: a transform 30
    /// times a second is cheap (Spectrum), but nothing at all is cheaper.
    private void StartMeters()
    {
        if (_meterTimer is not null) return;        // one reading, every strip
        _meterTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(33) };
        var quietTicks = 0;
        var clock = System.Diagnostics.Stopwatch.StartNew();
        _meterTimer.Tick += (_, _) =>
        {
            var seconds = clock.Elapsed.TotalSeconds;
            clock.Restart();
            var metering = _player is not null;
            var any = false;
            foreach (var v in LiveViews())
            {
                any = true;
                if (v.ShowMeters != metering) v.ShowMeters = metering;
            }
            var bar = BarMetersLive;
            if (_player is null || (!any && !bar))
            {
                // Nothing to show (minimized, or the bar's meters off). Playing, the
                // timer keeps its place for when there is; otherwise it sleeps.
                if (_player?.IsPlaying != true) StopMeters();
                return;
            }
            var playing = _player.IsPlaying;
            var after = MetersAfterGain;
            var levels = playing ? _player.Meter(after) : MeterLevels.Silent;
            var wantsBands = (bar && _barMeters.WantsBands) || LiveViews().Any(v => v.Meters.WantsBands);
            var bands = playing && wantsBands && _player.Bands(_bands, after) ? _bands : null;
            if (bar) _barMeters.Feed(levels, bands, seconds);
            foreach (var v in LiveViews()) v.Meters.Feed(levels, bands, seconds);
            // Paused, stopped or at the end: once the meters have had time to fall (they
            // reach rest in well under a second, the held peaks in two), nothing changes
            // until the music starts again, and thirty wake-ups a second were for nothing.
            // On the kiosk, whose front panel never closes, that was every paused hour.
            // OnPlaybackState starts it again.
            quietTicks = playing ? 0 : quietTicks + 1;
            if (quietTicks > 90) StopMeters();
        };
        _meterTimer.Start();
    }

    private void StopMeters()
    {
        _meterTimer?.Stop();
        _meterTimer = null;
    }
}
