using TrainingSystem.PC.Models;

namespace TrainingSystem.PC.Services;

/// <summary>
/// Finder perioder uden gyldige målinger (FR-10) og opgør, hvor stor en andel af
/// sessionen der mangler (NFR-R-02), samt hvor mange pakker der er gået tabt (NFR-R-01).
///
/// De to målestrømme har hver sin løbenummerserie, fordi de kommer fra hver sin afsender.
/// Derfor opgøres gab og tab pr. kilde og ikke samlet.
/// </summary>
public static class GapDetector
{
    /// <summary>
    /// Længste tilladte afstand mellem to målinger fra samme kilde, før det tæller som et gab.
    /// NFR-P-02 sætter opdateringen til 40 Hz, altså 25 ms. Grænsen her er fire gange så lang,
    /// så almindelig jitter ikke registreres som et hul.
    /// </summary>
    public const double MaxSampleIntervalSeconds = 0.10;

    public sealed class GapReport
    {
        public List<DataGap> Gaps { get; } = new();
        public double TotalGapSeconds { get; set; }
        public double GapRatio { get; set; }
        public double PositionLossRatio { get; set; }
        public double OrientationLossRatio { get; set; }

        /// <summary>
        /// Kilder, der ikke leverede én eneste måling. De tæller ikke med i gabandelen:
        /// en kilde, der aldrig blev tilsluttet, er en anden fejl end en kilde, der faldt
        /// ud midt i sessionen, og NFR-R-02 handler om den sidste.
        /// </summary>
        public List<MeasurementSource> SourcesWithoutData { get; } = new();
    }

    public static GapReport Detect(
        IReadOnlyList<SensorMessage> measurements,
        DateTimeOffset startedAt,
        DateTimeOffset endedAt)
    {
        var report = new GapReport();
        double duration = (endedAt - startedAt).TotalSeconds;
        if (duration <= 0) return report;

        double worstSourceGapSeconds = 0;

        foreach (var source in new[] { MeasurementSource.Position, MeasurementSource.Orientation })
        {
            var samples = measurements
                .Where(m => SourceOf(m) == source)
                .OrderBy(m => m.EffectiveTimestampUtc)
                .ToList();

            if (samples.Count == 0)
            {
                // Kilden leverede intet. Det registreres som et datagab, så det er synligt,
                // men det indgår ikke i gabandelen: NFR-R-02 handler om huller i en strøm,
                // der kørte, ikke om en kilde, der aldrig blev tilsluttet. Ellers ville en
                // session uden tryksensor altid være ikke-vurderbar.
                report.Gaps.Add(new DataGap
                {
                    Source = source,
                    StartUtc = startedAt,
                    EndUtc = endedAt,
                    DurationSeconds = duration,
                    Reason = $"Ingen målinger modtaget fra {Name(source)} i hele sessionen."
                });
                report.SourcesWithoutData.Add(source);
                continue;
            }

            double sourceGapSeconds = 0;

            // Gab før den første måling.
            double leading = (samples[0].EffectiveTimestampUtc - startedAt).TotalSeconds;
            if (leading > MaxSampleIntervalSeconds)
            {
                report.Gaps.Add(Gap(source, startedAt, samples[0].EffectiveTimestampUtc, leading, 0,
                    $"Ingen {Name(source)} i starten af sessionen."));
                sourceGapSeconds += leading;
            }

            long missingSequence = 0;
            long expectedSequence = 0;

            for (int i = 1; i < samples.Count; i++)
            {
                var previous = samples[i - 1];
                var current = samples[i];
                double seconds = (current.EffectiveTimestampUtc - previous.EffectiveTimestampUtc).TotalSeconds;

                int missingHere = 0;
                if (previous.SequenceNumber.HasValue && current.SequenceNumber.HasValue)
                {
                    long jump = current.SequenceNumber.Value - previous.SequenceNumber.Value;
                    if (jump > 1)
                    {
                        missingHere = (int)(jump - 1);
                        missingSequence += missingHere;
                    }
                    if (jump > 0) expectedSequence += jump;
                }

                if (seconds > MaxSampleIntervalSeconds)
                {
                    string reason = missingHere > 0
                        ? $"{missingHere} manglende løbenumre i {Name(source)}."
                        : $"Ingen {Name(source)} i {seconds:F2} s.";
                    report.Gaps.Add(Gap(source, previous.EffectiveTimestampUtc,
                        current.EffectiveTimestampUtc, seconds, missingHere, reason));
                    sourceGapSeconds += seconds;
                }
            }

            // Gab efter den sidste måling.
            double trailing = (endedAt - samples[^1].EffectiveTimestampUtc).TotalSeconds;
            if (trailing > MaxSampleIntervalSeconds)
            {
                report.Gaps.Add(Gap(source, samples[^1].EffectiveTimestampUtc, endedAt, trailing, 0,
                    $"Ingen {Name(source)} i slutningen af sessionen."));
                sourceGapSeconds += trailing;
            }

            double lossRatio = expectedSequence > 0 ? (double)missingSequence / expectedSequence : 0;
            if (source == MeasurementSource.Position) report.PositionLossRatio = lossRatio;
            else report.OrientationLossRatio = lossRatio;

            worstSourceGapSeconds = Math.Max(worstSourceGapSeconds, sourceGapSeconds);
        }

        // Blandt de kilder, der rent faktisk leverede data, afgør den med flest huller,
        // om sessionen kan vurderes. En kilde, der faldt ud midt i en session, gør
        // resultatet misvisende; en kilde, der aldrig kørte, gør det blot ufuldstændigt.
        report.TotalGapSeconds = worstSourceGapSeconds;
        report.GapRatio = Math.Min(1.0, worstSourceGapSeconds / duration);
        report.Gaps.Sort((a, b) => a.StartUtc.CompareTo(b.StartUtc));
        return report;
    }

    public static MeasurementSource SourceOf(SensorMessage m)
    {
        if (string.Equals(m.Type, "position", StringComparison.OrdinalIgnoreCase)) return MeasurementSource.Position;
        if (string.Equals(m.Type, "orientation", StringComparison.OrdinalIgnoreCase)) return MeasurementSource.Orientation;
        if (m.HasPositionXY) return MeasurementSource.Position;
        if (m.HasOrientation) return MeasurementSource.Orientation;
        if (m.Pressure.HasValue) return MeasurementSource.ContactForce;
        return MeasurementSource.Unknown;
    }

    public static string Name(MeasurementSource source) => source switch
    {
        MeasurementSource.Position => "positionsmålinger",
        MeasurementSource.Orientation => "orienteringsmålinger",
        MeasurementSource.ContactForce => "kraftmålinger",
        _ => "målinger"
    };

    private static DataGap Gap(MeasurementSource source, DateTimeOffset from, DateTimeOffset to,
        double seconds, int missing, string reason) => new()
    {
        Source = source,
        StartUtc = from,
        EndUtc = to,
        DurationSeconds = seconds,
        MissingSequenceNumbers = missing,
        Reason = reason
    };
}
