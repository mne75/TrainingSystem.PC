using TrainingSystem.PC.Models;

namespace TrainingSystem.PC.Services;

/// <summary>
/// Finder perioder uden gyldige målinger (FR-10) og opgør, hvor stor en andel af
/// sessionen der mangler (NFR-R-02) og hvor mange pakker der er gået tabt (NFR-R-01).
///
/// Der skelnes mellem to ting, som er lette at blande sammen:
///
/// <para><b>Pakketab</b> er beskeder, der aldrig nåede frem. Det ses på huller i
/// løbenummeret og hører til transporten. Det opgøres pr. datastrøm, fordi hver
/// afsender tæller sit eget løbenummer op.</para>
///
/// <para><b>Datagab</b> er perioder uden en gyldig måling af et bestemt signal. Det kan
/// skyldes pakketab, men oftest skyldes det, at sensoren ikke havde noget at give:
/// Pi'en sender "quat": null, når rotationen ikke kunne læses, og "pos": null, når
/// probemarkøren er dækket. Beskeden kom frem; målingen fandtes bare ikke.</para>
///
/// Sondringen betyder noget for NFR-R-02: en session kan have nul pakketab og alligevel
/// være ubrugelig, fordi markøren var dækket det halve af tiden.
/// </summary>
public static class GapDetector
{
    /// <summary>
    /// Længste tilladte afstand mellem to gyldige målinger af samme signal, før det
    /// tæller som et gab. Pi'en sender med 40 Hz, altså hvert 25. ms, og kameraet
    /// leverer langsommere end det. Grænsen er sat, så kameraets egen takt og
    /// almindelig jitter ikke registreres som huller.
    /// </summary>
    public const double MaxSampleIntervalSeconds = 0.20;

    /// <summary>Pakketab for én datastrøm, opgjort på løbenumrene.</summary>
    public sealed class StreamLoss
    {
        public string Stream { get; set; } = "";
        public long Expected { get; set; }
        public long Missing { get; set; }
        public int Restarts { get; set; }
        public double Ratio => Expected > 0 ? (double)Missing / Expected : 0;
    }

    public sealed class GapReport
    {
        public List<DataGap> Gaps { get; } = new();
        public List<StreamLoss> Loss { get; } = new();
        public double TotalGapSeconds { get; set; }
        public double GapRatio { get; set; }
        public List<MeasurementSource> SourcesWithoutData { get; } = new();

        public double MaxLossRatio => Loss.Count == 0 ? 0 : Loss.Max(l => l.Ratio);
    }

    private static readonly MeasurementSource[] Signals =
    {
        MeasurementSource.Position,
        MeasurementSource.Orientation,
        MeasurementSource.ContactForce
    };

    public static GapReport Detect(
        IReadOnlyList<SensorMessage> measurements,
        DateTimeOffset startedAt,
        DateTimeOffset endedAt)
    {
        var report = new GapReport();
        double duration = (endedAt - startedAt).TotalSeconds;
        if (duration <= 0) return report;

        DetectPacketLoss(measurements, report);

        double worstGapSeconds = 0;

        foreach (var signal in Signals)
        {
            var present = measurements
                .Where(m => HasSignal(m, signal))
                .OrderBy(m => TimeOf(m, signal))
                .ToList();

            if (present.Count == 0)
            {
                // Signalet kom aldrig. Det registreres, men tæller ikke med i gabandelen:
                // NFR-R-02 handler om huller i noget, der kørte, ikke om en sensor, der
                // aldrig blev tilsluttet. Ellers var enhver session uden tryksensor tabt.
                report.Gaps.Add(new DataGap
                {
                    Source = signal,
                    StartUtc = startedAt,
                    EndUtc = endedAt,
                    DurationSeconds = duration,
                    Reason = $"Ingen {Name(signal)} i hele sessionen."
                });
                report.SourcesWithoutData.Add(signal);
                continue;
            }

            double gapSeconds = 0;

            double leading = (TimeOf(present[0], signal) - startedAt).TotalSeconds;
            if (leading > MaxSampleIntervalSeconds)
            {
                report.Gaps.Add(Gap(signal, startedAt, TimeOf(present[0], signal), leading,
                    $"Ingen {Name(signal)} i starten af sessionen."));
                gapSeconds += leading;
            }

            for (int i = 1; i < present.Count; i++)
            {
                var previous = TimeOf(present[i - 1], signal);
                var current = TimeOf(present[i], signal);
                double seconds = (current - previous).TotalSeconds;
                if (seconds <= MaxSampleIntervalSeconds) continue;

                // Pi'en fortæller selv hvorfor. Forklaringen står i de beskeder, der kom
                // MENS hullet stod på — ikke i den sidste gode måling før det, som er
                // den eneste, der er tilbage i "present".
                string? status = signal == MeasurementSource.Position
                    ? StatusInsideGap(measurements, previous, current)
                    : null;
                string reason = status ?? $"Ingen {Name(signal)} i {seconds:F2} s.";

                report.Gaps.Add(Gap(signal, previous, current, seconds, reason));
                gapSeconds += seconds;
            }

            double trailing = (endedAt - TimeOf(present[^1], signal)).TotalSeconds;
            if (trailing > MaxSampleIntervalSeconds)
            {
                report.Gaps.Add(Gap(signal, TimeOf(present[^1], signal), endedAt, trailing,
                    $"Ingen {Name(signal)} i slutningen af sessionen."));
                gapSeconds += trailing;
            }

            // Kontaktkraften er en Could-funktion og må ikke kunne gøre en session
            // ikke-vurderbar. Position og rotation kan.
            if (signal != MeasurementSource.ContactForce)
                worstGapSeconds = Math.Max(worstGapSeconds, gapSeconds);
        }

        report.TotalGapSeconds = worstGapSeconds;
        report.GapRatio = Math.Min(1.0, worstGapSeconds / duration);
        report.Gaps.Sort((a, b) => a.StartUtc.CompareTo(b.StartUtc));
        return report;
    }

    /// <summary>
    /// Tæller huller i løbenumrene pr. datastrøm. Et løbenummer, der starter forfra,
    /// betyder at afsenderen er genstartet midt i sessionen. Det er ikke tab, men det
    /// skal fremgå, fordi tallene før og efter ikke hører til samme forløb.
    /// </summary>
    private static void DetectPacketLoss(IReadOnlyList<SensorMessage> measurements, GapReport report)
    {
        foreach (var group in measurements.GroupBy(m => m.StreamKey))
        {
            var numbered = group
                .Where(m => m.SequenceNumber.HasValue)
                .OrderBy(m => m.EffectiveTimestampUtc)
                .Select(m => m.SequenceNumber!.Value)
                .ToList();

            if (numbered.Count < 2) continue;

            var loss = new StreamLoss { Stream = group.Key };
            for (int i = 1; i < numbered.Count; i++)
            {
                long jump = numbered[i] - numbered[i - 1];
                if (jump == 1)
                {
                    loss.Expected++;
                }
                else if (jump > 1)
                {
                    loss.Expected += jump;
                    loss.Missing += jump - 1;
                }
                else if (numbered[i] < 5 && numbered[i - 1] > 50)
                {
                    loss.Restarts++;
                }
                // Ellers: en forsinket eller dubleret pakke. UDP giver ingen
                // rækkefølgegaranti, og sådan en er hverken tabt eller forventet.
            }

            if (loss.Expected > 0) report.Loss.Add(loss);
        }
    }

    /// <summary>
    /// Finder den forklaring, afsenderen gav, mens hullet stod på. Pi'en sender fortsat
    /// beskeder under en okklusion — de har bare "pos": null og en track_status, der
    /// siger hvorfor.
    /// </summary>
    private static string? StatusInsideGap(
        IReadOnlyList<SensorMessage> measurements, DateTimeOffset from, DateTimeOffset to)
    {
        foreach (var m in measurements)
        {
            if (m.HasPositionXY || string.IsNullOrWhiteSpace(m.TrackStatus)) continue;
            var t = m.EffectiveTimestampUtc;
            if (t > from && t < to) return m.TrackStatus;
        }
        return null;
    }

    public static bool HasSignal(SensorMessage m, MeasurementSource signal) => signal switch
    {
        MeasurementSource.Position => m.HasPositionXY,
        MeasurementSource.Orientation => m.HasOrientation,
        MeasurementSource.ContactForce => m.Pressure.HasValue,
        _ => false
    };

    /// <summary>
    /// Tidspunktet for selve målingen. For positionen er det kameraets billedtidspunkt
    /// og ikke tidspunktet for den besked, den blev sendt med.
    /// </summary>
    public static DateTimeOffset TimeOf(SensorMessage m, MeasurementSource signal) =>
        signal == MeasurementSource.Position ? m.PositionTimestampUtc : m.EffectiveTimestampUtc;

    /// <summary>
    /// Hvilket signal en besked bærer. En samlet besked fra Pi'en bærer flere; her
    /// vælges det vigtigste, og resultatet bruges kun til visning.
    /// </summary>
    public static MeasurementSource SourceOf(SensorMessage m)
    {
        if (string.Equals(m.Type, "position", StringComparison.OrdinalIgnoreCase)) return MeasurementSource.Position;
        if (string.Equals(m.Type, "orientation", StringComparison.OrdinalIgnoreCase)) return MeasurementSource.Orientation;
        if (m.HasPositionXY) return MeasurementSource.Position;
        if (m.HasOrientation) return MeasurementSource.Orientation;
        if (m.Pressure.HasValue) return MeasurementSource.ContactForce;
        return MeasurementSource.Unknown;
    }

    /// <summary>Kort beskrivelse af, hvad en besked indeholdt. Bruges i målingstabellen.</summary>
    public static string Describe(SensorMessage m)
    {
        var parts = new List<string>();
        if (m.HasOrientation) parts.Add("rotation");
        if (m.HasPositionXY) parts.Add("position");
        if (m.Pressure.HasValue) parts.Add("kraft");
        return parts.Count == 0 ? "tom" : string.Join(" + ", parts);
    }

    public static string Name(MeasurementSource source) => source switch
    {
        MeasurementSource.Position => "positionsmålinger",
        MeasurementSource.Orientation => "rotationsmålinger",
        MeasurementSource.ContactForce => "kraftmålinger",
        _ => "målinger"
    };

    private static DataGap Gap(MeasurementSource source, DateTimeOffset from, DateTimeOffset to,
        double seconds, string reason) => new()
    {
        Source = source,
        StartUtc = from,
        EndUtc = to,
        DurationSeconds = seconds,
        Reason = reason
    };
}
