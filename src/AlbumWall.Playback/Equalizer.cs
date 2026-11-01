// AlbumWall - a parametric equalizer between Hymie and the sound output.
//
// His ask, 2026-11-01: "I'd like to add an EQ for the app audio", then "I
// actually prefer a parametric EQ for this. The need for eq in an app like
// this is due to the variations in the sound systems they have ... you can
// drift the crossover point by ear to boost or cut at specific frequencies. A
// three or four band with adjustable crossover should do it?" So: four bands
// for fitting the music to a room and its speakers, not presets for taste.
//
//   Low       shelf  20 Hz-500 Hz   gain +-12 dB
//   Low-mid   peak   40 Hz-2 kHz    gain +-12 dB, width (Q) 0.3-8
//   High-mid  peak   500 Hz-10 kHz  gain +-12 dB, width (Q) 0.3-8
//   High      shelf  2 kHz-20 kHz   gain +-12 dB
//
// WHERE: between Hymie and the platform's output (his placement, "Between
// Hymie and the platform specific output?"). Each output is opened with a
// Source that pulls from Hymie; the equalizer is a stage in that pull
// (HymieEngine.Begin), after decoding, gapless joining and ReplayGain, before
// PulseAudio, WASAPI or AudioQueue, so one equalizer serves all three. A new
// output is opened for a new sample rate or channel count, and a new stage
// with it, its filters set for that rate.
//
// NO CLIPPING: the whole curve is lowered by its highest point (Headroom), so
// a boost lowers everything else rather than pushing past full scale.
//
// The filters are the RBJ "Audio EQ Cookbook" biquads (Robert Bristow-Johnson),
// shelves with a slope of 1, in transposed direct form II, one state per band
// per channel, in double precision.

namespace AlbumWall.Playback;

public enum EqKind { LowShelf, Peak, HighShelf }

/// One band as it is kept in the settings. A plain class with setters, for
/// the settings file's source-generated JSON (and Native AOT).
public sealed class EqBand
{
    public double Frequency { get; set; }
    public double Gain { get; set; }
    /// Width of a peak (Q); shelves ignore it.
    public double Q { get; set; } = 1;
}

/// The equalizer as it is kept: on or off, and its four bands.
public sealed class EqSettings
{
    public bool On { get; set; }
    public List<EqBand> Bands { get; set; } = Default();

    public static readonly EqKind[] Kinds = [EqKind.LowShelf, EqKind.Peak, EqKind.Peak, EqKind.HighShelf];
    public static readonly string[] Names = ["Low", "Low-mid", "High-mid", "High"];
    public static readonly (double Min, double Max)[] Ranges = [(20, 500), (40, 2000), (500, 10000), (2000, 20000)];
    public const double MaxGain = 12;
    public const double MinQ = 0.3, MaxQ = 8;

    public static List<EqBand> Default() =>
    [
        new() { Frequency = 100, Gain = 0, Q = 0.7 },
        new() { Frequency = 250, Gain = 0, Q = 1 },
        new() { Frequency = 3000, Gain = 0, Q = 1 },
        new() { Frequency = 8000, Gain = 0, Q = 0.7 },
    ];

    /// A copy, so a change is made to a fresh object the audio thread swaps in.
    public EqSettings Copy() => new()
    {
        On = On,
        Bands = Bands.Select(b => new EqBand { Frequency = b.Frequency, Gain = b.Gain, Q = b.Q }).ToList(),
    };

    /// Four bands, each in range: whatever a hand-edited settings file says.
    public EqSettings Checked()
    {
        var copy = Copy();
        var defaults = Default();
        while (copy.Bands.Count < 4) copy.Bands.Add(defaults[copy.Bands.Count]);
        if (copy.Bands.Count > 4) copy.Bands.RemoveRange(4, copy.Bands.Count - 4);
        for (var i = 0; i < 4; i++)
        {
            var b = copy.Bands[i];
            b.Frequency = double.IsFinite(b.Frequency) ? Math.Clamp(b.Frequency, Ranges[i].Min, Ranges[i].Max) : defaults[i].Frequency;
            b.Gain = double.IsFinite(b.Gain) ? Math.Clamp(b.Gain, -MaxGain, MaxGain) : 0;
            b.Q = double.IsFinite(b.Q) ? Math.Clamp(b.Q, MinQ, MaxQ) : 1;
        }
        return copy;
    }

    public bool IsFlat => Bands.All(b => Math.Abs(b.Gain) < 0.05);
}

public static class Equalizer
{
    /// The response of all four bands at `frequency`, in dB, at a sample rate.
    public static double ResponseDb(EqSettings eq, double frequency, double rate = 48000)
    {
        var db = 0.0;
        for (var i = 0; i < 4 && i < eq.Bands.Count; i++)
            db += Coefficients(EqSettings.Kinds[i], eq.Bands[i], rate).MagnitudeDb(frequency, rate);
        return db;
    }

    /// How far the curve must be lowered so that nothing passes full scale:
    /// its highest point, found across the audible range, or 0 when it never
    /// rises.
    public static double HeadroomDb(EqSettings eq, double rate = 48000)
    {
        var top = 0.0;
        for (var f = 20.0; f <= 20000; f *= 1.02) top = Math.Max(top, ResponseDb(eq, f, rate));
        return top;
    }

    internal readonly record struct Biquad(double B0, double B1, double B2, double A1, double A2)
    {
        public double MagnitudeDb(double f, double rate)
        {
            var w = 2 * Math.PI * f / rate;
            var (c1, s1, c2, s2) = (Math.Cos(w), Math.Sin(w), Math.Cos(2 * w), Math.Sin(2 * w));
            var nr = B0 + B1 * c1 + B2 * c2; var ni = -(B1 * s1 + B2 * s2);
            var dr = 1 + A1 * c1 + A2 * c2; var di = -(A1 * s1 + A2 * s2);
            return 10 * Math.Log10((nr * nr + ni * ni) / (dr * dr + di * di));
        }
    }

    internal static Biquad Coefficients(EqKind kind, EqBand band, double rate)
    {
        // Above the Nyquist frequency nothing can be done; held just under it.
        var f = Math.Min(band.Frequency, rate * 0.45);
        var a = Math.Pow(10, band.Gain / 40);
        var w0 = 2 * Math.PI * f / rate;
        var (cos, sin) = (Math.Cos(w0), Math.Sin(w0));
        double b0, b1, b2, a0, a1, a2;
        if (kind == EqKind.Peak)
        {
            var alpha = sin / (2 * Math.Max(EqSettings.MinQ, band.Q));
            b0 = 1 + alpha * a; b1 = -2 * cos; b2 = 1 - alpha * a;
            a0 = 1 + alpha / a; a1 = -2 * cos; a2 = 1 - alpha / a;
        }
        else
        {
            // Shelf slope S = 1.
            var alpha = sin / 2 * Math.Sqrt(2);
            var sq = 2 * Math.Sqrt(a) * alpha;
            if (kind == EqKind.LowShelf)
            {
                b0 = a * ((a + 1) - (a - 1) * cos + sq); b1 = 2 * a * ((a - 1) - (a + 1) * cos); b2 = a * ((a + 1) - (a - 1) * cos - sq);
                a0 = (a + 1) + (a - 1) * cos + sq; a1 = -2 * ((a - 1) + (a + 1) * cos); a2 = (a + 1) + (a - 1) * cos - sq;
            }
            else
            {
                b0 = a * ((a + 1) + (a - 1) * cos + sq); b1 = -2 * a * ((a - 1) + (a + 1) * cos); b2 = a * ((a + 1) + (a - 1) * cos - sq);
                a0 = (a + 1) - (a - 1) * cos + sq; a1 = 2 * ((a - 1) - (a + 1) * cos); a2 = (a + 1) - (a - 1) * cos - sq;
            }
        }
        return new Biquad(b0 / a0, b1 / a0, b2 / a0, a1 / a0, a2 / a0);
    }
}

/// The stage in one output's pull: filters the samples Hymie hands over, in
/// place, by whatever settings are current when each block comes.
internal sealed class EqualizerStage(int rate, int channels, Func<EqSettings?> current)
{
    private EqSettings? _for;
    private Equalizer.Biquad[] _bands = [];
    private double _preamp = 1;
    private readonly double[] _z1 = new double[4 * channels], _z2 = new double[4 * channels];

    public void Process(Span<float> samples)
    {
        var eq = current();
        if (eq is null || !eq.On || eq.IsFlat) { _for = null; return; }
        if (!ReferenceEquals(eq, _for))
        {
            _for = eq;
            _bands = Enumerable.Range(0, 4).Select(i => Equalizer.Coefficients(EqSettings.Kinds[i], eq.Bands[i], rate)).ToArray();
            _preamp = Math.Pow(10, -Equalizer.HeadroomDb(eq, rate) / 20);
        }
        for (var i = 0; i < samples.Length; i++)
        {
            var ch = i % channels;
            double x = samples[i] * _preamp;
            for (var b = 0; b < 4; b++)
            {
                var q = _bands[b];
                var k = b * channels + ch;
                var y = q.B0 * x + _z1[k];
                _z1[k] = q.B1 * x - q.A1 * y + _z2[k];
                _z2[k] = q.B2 * x - q.A2 * y;
                x = y;
            }
            samples[i] = (float)x;
        }
    }
}
