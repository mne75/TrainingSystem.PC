using TrainingSystem.PC.Models;

namespace TrainingSystem.PC.Services;

/// <summary>Hvordan filtervinduet lægges omkring en måling.</summary>
public enum FilterMode
{
    /// <summary>
    /// Vinduet ligger symmetrisk omkring målingen. Det kræver, at data allerede er optaget,
    /// og giver til gengæld ingen faseforskydning: en centreret middelværdi er nulfase.
    /// Det er den rigtige form her, fordi analysen først kører efter sessionen (FR-08).
    /// </summary>
    Centred,

    /// <summary>
    /// Vinduet bruger kun målinger, der ligger før den aktuelle. Det er den form, et filter
    /// skal have, hvis det skal køre live, og det koster en forsinkelse på cirka det halve
    /// vindue. Den er med, så forsinkelseskravet i FR-12 kan efterprøves på noget, der
    /// faktisk forsinker.
    /// </summary>
    Causal
}

/// <summary>Indstillinger for støjfiltreringen (FR-12).</summary>
public sealed class FilterSettings
{
    public bool Enabled { get; set; } = true;

    /// <summary>Vinduets samlede længde i millisekunder.</summary>
    public double WindowMs { get; set; } = DefaultWindowMs;

    public FilterMode Mode { get; set; } = FilterMode.Centred;

    public bool FilterPosition { get; set; } = true;

    /// <summary>
    /// Slået fra som udgangspunkt. Se bemærkningen i <see cref="NoiseFilter"/> om, hvorfor
    /// orienteringen ikke bør glattes, når tremor er en af de parametre, der skal måles.
    /// </summary>
    public bool FilterOrientation { get; set; }

    /// <summary>
    /// 100 ms svarer til fire målinger ved 40 Hz. Probens bane er langsom (under 2 Hz),
    /// så et vindue på 100 ms fjerner målestøj uden at røre selve bevægelsen.
    /// </summary>
    public const double DefaultWindowMs = 100.0;

    public static FilterSettings Off => new() { Enabled = false };
}

/// <summary>Før og efter for ét signal.</summary>
public sealed class SignalStatistics
{
    public string Name { get; set; } = "";
    public int Samples { get; set; }

    /// <summary>Spredning på selve signalet. Meningsfuld, når proben ligger i ro (acceptkriteriet i FR-12).</summary>
    public double StdDevBefore { get; set; }
    public double StdDevAfter { get; set; }

    /// <summary>Spredning på forskellen mellem to målinger i træk. Et mål for støj, også når proben bevæger sig.</summary>
    public double StepStdDevBefore { get; set; }
    public double StepStdDevAfter { get; set; }

    public double ReductionRatio =>
        StdDevBefore > 0 ? 1.0 - StdDevAfter / StdDevBefore : 0;

    public double StepReductionRatio =>
        StepStdDevBefore > 0 ? 1.0 - StepStdDevAfter / StepStdDevBefore : 0;
}

/// <summary>Et punkt i før/efter-kurven, så filtreringen kan vises grafisk.</summary>
public sealed class FilterComparisonSample
{
    public double OffsetSeconds { get; set; }
    public double Raw { get; set; }
    public double Filtered { get; set; }
}

/// <summary>Dokumentationen af, hvad filtreringen gjorde (FR-12).</summary>
public sealed class FilterReport
{
    public bool Applied { get; set; }
    public double WindowMs { get; set; }
    public FilterMode Mode { get; set; }
    public int SegmentCount { get; set; }

    public List<SignalStatistics> Signals { get; set; } = new();

    /// <summary>Målt forsinkelse i millisekunder, fundet ved krydskorrelation mellem rå og filtreret signal.</summary>
    public double MeasuredDelayMs { get; set; }

    /// <summary>
    /// Falsk, når datasættet ikke indeholder en bevægelse at måle forskydningen på.
    /// Et hvilesæt kan dokumentere støjreduktionen, men ikke forsinkelsen; til den del
    /// af FR-12 skal der optages en kendt bevægelse.
    /// </summary>
    public bool DelayMeasurable { get; set; }

    public const double MaxAllowedDelayMs = 50.0;

    public bool DelayWithinLimit => MeasuredDelayMs <= MaxAllowedDelayMs;

    public string ComparisonSignalName { get; set; } = "";
    public List<FilterComparisonSample> Comparison { get; set; } = new();

    public List<string> Notes { get; set; } = new();
}

/// <summary>
/// Glidende middelværdi over et tidsvindue (FR-12).
///
/// To ting er værd at bemærke om implementeringen:
///
/// 1. Filteret arbejder i segmenter og aldrig hen over et datagab. Et vindue, der strakte
///    sig over et hul, ville lave en middelværdi af to målinger med luft imellem og dermed
///    fremstille en beregnet position som målt. Det forbyder FR-10.
///
/// 2. Vinduet regnes i tid og ikke i antal målinger, fordi de to datastrømme har forskellig
///    frekvens og UDP ikke leverer jævnt fordelte pakker.
/// </summary>
public static class NoiseFilter
{
    public static (List<SensorMessage> Filtered, FilterReport Report) Apply(
        IReadOnlyList<SensorMessage> measurements, FilterSettings settings)
    {
        var report = new FilterReport
        {
            Applied = settings.Enabled,
            WindowMs = settings.WindowMs,
            Mode = settings.Mode
        };

        if (!settings.Enabled || measurements.Count == 0)
        {
            report.Notes.Add("Filtrering er slået fra. Analysen bruger rådata.");
            return (measurements.ToList(), report);
        }

        var output = new List<SensorMessage>(measurements.Count);
        var untouched = measurements.Where(m =>
            GapDetector.SourceOf(m) is not (MeasurementSource.Position or MeasurementSource.Orientation));
        output.AddRange(untouched);

        if (settings.FilterPosition)
            FilterSource(measurements, MeasurementSource.Position, settings, output, report);
        else
            Copy(measurements, MeasurementSource.Position, output);

        if (settings.FilterOrientation)
            FilterSource(measurements, MeasurementSource.Orientation, settings, output, report);
        else
        {
            Copy(measurements, MeasurementSource.Orientation, output);
            report.Notes.Add(
                "Orienteringen er ikke filtreret. Ved 40 Hz ligger tremorbåndet 4–12 Hz så tæt på " +
                "Nyquist-grænsen på 20 Hz, at et glattende filter ville fjerne netop det signal, " +
                "samplingfrekvensen blev valgt for at kunne måle.");
        }

        output.Sort((a, b) => a.EffectiveTimestampUtc.CompareTo(b.EffectiveTimestampUtc));

        if (report.Applied && !report.DelayMeasurable)
            report.Notes.Add(
                "Forsinkelsen kunne ikke måles: datasættet indeholder ingen bevægelse at " +
                "genkende forskydningen på. Et hvilesæt dokumenterer støjreduktionen; " +
                "forsinkelseskravet i FR-12 skal måles på en optagelse med en kendt bevægelse.");

        if (report.Applied)
            report.Notes.Add(
                settings.Mode == FilterMode.Centred
                    ? "Vinduet er centreret, og filteret er derfor nulfase. Det er muligt, fordi " +
                      "filtreringen sker efter sessionen og ikke undervejs."
                    : "Vinduet er kausalt og bruger kun tidligere målinger. Det koster en " +
                      "forsinkelse på cirka det halve vindue.");

        return (output, report);
    }

    private static void Copy(IReadOnlyList<SensorMessage> source, MeasurementSource kind, List<SensorMessage> target)
        => target.AddRange(source.Where(m => GapDetector.SourceOf(m) == kind));

    private static void FilterSource(
        IReadOnlyList<SensorMessage> measurements,
        MeasurementSource kind,
        FilterSettings settings,
        List<SensorMessage> output,
        FilterReport report)
    {
        var samples = measurements
            .Where(m => GapDetector.SourceOf(m) == kind)
            .OrderBy(m => m.EffectiveTimestampUtc)
            .ToList();

        if (samples.Count == 0) return;
        if (samples.Count < 3)
        {
            output.AddRange(samples);
            return;
        }

        var segments = Split(samples);
        report.SegmentCount += segments.Count;

        // Signalerne hentes ud, glattes segment for segment og lægges tilbage.
        var signals = kind == MeasurementSource.Position
            ? new[] { "x", "y" }
            : new[] { "vinkel x", "vinkel y", "vinkel z" };

        var rawSeries = new Dictionary<string, List<double>>();
        var filteredSeries = new Dictionary<string, List<double>>();
        foreach (var name in signals)
        {
            rawSeries[name] = new List<double>();
            filteredSeries[name] = new List<double>();
        }

        var clones = new List<SensorMessage>(samples.Count);

        foreach (var segment in segments)
        {
            var times = segment.Select(m => (m.EffectiveTimestampUtc - segment[0].EffectiveTimestampUtc).TotalMilliseconds).ToList();

            var values = new Dictionary<string, List<double>>();
            foreach (var name in signals) values[name] = new List<double>();

            foreach (var m in segment)
            {
                if (kind == MeasurementSource.Position)
                {
                    values["x"].Add(m.X ?? 0);
                    values["y"].Add(m.Y ?? 0);
                }
                else
                {
                    var e = m.GetEulerDegrees();
                    values["vinkel x"].Add(e?.X ?? 0);
                    values["vinkel y"].Add(e?.Y ?? 0);
                    values["vinkel z"].Add(e?.Z ?? 0);
                }
            }

            var smoothed = new Dictionary<string, List<double>>();
            foreach (var name in signals)
                smoothed[name] = Smooth(values[name], times, settings.WindowMs, settings.Mode);

            for (int i = 0; i < segment.Count; i++)
            {
                var clone = Clone(segment[i]);
                if (kind == MeasurementSource.Position)
                {
                    clone.X = smoothed["x"][i];
                    clone.Y = smoothed["y"][i];
                }
                else
                {
                    clone.AngleX = smoothed["vinkel x"][i];
                    clone.AngleY = smoothed["vinkel y"][i];
                    clone.AngleZ = smoothed["vinkel z"][i];
                }
                clones.Add(clone);
            }

            foreach (var name in signals)
            {
                rawSeries[name].AddRange(values[name]);
                filteredSeries[name].AddRange(smoothed[name]);
            }
        }

        output.AddRange(clones);

        foreach (var name in signals)
            report.Signals.Add(Statistics(name, rawSeries[name], filteredSeries[name]));

        // Forsinkelsen måles på det signal, der varierer mest; et fladt signal kan ikke
        // sige noget om faseforskydning.
        string best = signals.OrderByDescending(n => StdDev(rawSeries[n])).First();
        double medianStepMs = MedianStepMs(samples);
        double delay = EstimateDelayMs(rawSeries[best], filteredSeries[best], medianStepMs);
        report.MeasuredDelayMs = Math.Max(report.MeasuredDelayMs, delay);
        if (IsDelayMeasurable(rawSeries[best], filteredSeries[best])) report.DelayMeasurable = true;

        if (report.Comparison.Count == 0)
        {
            report.ComparisonSignalName = $"{GapDetector.Name(kind)}: {best}";
            var start = samples[0].EffectiveTimestampUtc;
            int step = Math.Max(1, rawSeries[best].Count / 1500);
            for (int i = 0; i < rawSeries[best].Count; i += step)
                report.Comparison.Add(new FilterComparisonSample
                {
                    OffsetSeconds = i < samples.Count
                        ? (samples[i].EffectiveTimestampUtc - start).TotalSeconds
                        : i * medianStepMs / 1000.0,
                    Raw = rawSeries[best][i],
                    Filtered = filteredSeries[best][i]
                });
        }
    }

    /// <summary>Deler målingerne i sammenhængende stykker, så filteret aldrig regner hen over et datagab.</summary>
    private static List<List<SensorMessage>> Split(List<SensorMessage> samples)
    {
        var segments = new List<List<SensorMessage>>();
        var current = new List<SensorMessage> { samples[0] };

        for (int i = 1; i < samples.Count; i++)
        {
            double seconds = (samples[i].EffectiveTimestampUtc - samples[i - 1].EffectiveTimestampUtc).TotalSeconds;
            if (seconds > GapDetector.MaxSampleIntervalSeconds)
            {
                segments.Add(current);
                current = new List<SensorMessage>();
            }
            current.Add(samples[i]);
        }

        segments.Add(current);
        return segments;
    }

    private static List<double> Smooth(List<double> values, List<double> timesMs, double windowMs, FilterMode mode)
    {
        var result = new List<double>(values.Count);
        double half = windowMs / 2.0;

        for (int i = 0; i < values.Count; i++)
        {
            double from = mode == FilterMode.Centred ? timesMs[i] - half : timesMs[i] - windowMs;
            double to = mode == FilterMode.Centred ? timesMs[i] + half : timesMs[i];

            double sum = 0;
            int count = 0;
            for (int j = i; j >= 0 && timesMs[j] >= from; j--)
            {
                sum += values[j];
                count++;
            }
            for (int j = i + 1; j < values.Count && timesMs[j] <= to; j++)
            {
                sum += values[j];
                count++;
            }

            result.Add(count > 0 ? sum / count : values[i]);
        }

        return result;
    }

    private static SignalStatistics Statistics(string name, List<double> before, List<double> after) => new()
    {
        Name = name,
        Samples = before.Count,
        StdDevBefore = StdDev(before),
        StdDevAfter = StdDev(after),
        StepStdDevBefore = StdDev(Steps(before)),
        StepStdDevAfter = StdDev(Steps(after))
    };

    private static List<double> Steps(List<double> values)
    {
        var steps = new List<double>(Math.Max(0, values.Count - 1));
        for (int i = 1; i < values.Count; i++) steps.Add(values[i] - values[i - 1]);
        return steps;
    }

    public static double StdDev(IReadOnlyList<double> values)
    {
        if (values.Count < 2) return 0;
        double mean = values.Average();
        double variance = values.Average(v => (v - mean) * (v - mean));
        return Math.Sqrt(variance);
    }

    private static double MedianStepMs(List<SensorMessage> samples)
    {
        var steps = new List<double>();
        for (int i = 1; i < samples.Count; i++)
            steps.Add((samples[i].EffectiveTimestampUtc - samples[i - 1].EffectiveTimestampUtc).TotalMilliseconds);
        if (steps.Count == 0) return 25;
        steps.Sort();
        return steps[steps.Count / 2];
    }

    /// <summary>
    /// Finder den forskydning mellem rå og filtreret signal, der giver den største
    /// krydskorrelation. Det er den forsinkelse, filteret har indført, og det er den,
    /// FR-12's grænse på 50 ms gælder.
    /// </summary>
    /// <summary>
    /// Mindste andel af signalets spredning, der skal overleve filtreringen, før en
    /// forsinkelse overhovedet kan måles. Ligger den under, bestod signalet i al væsentlighed
    /// af støj, og krydskorrelationens top ligger tilfældigt.
    /// </summary>
    public const double MinStructureRatio = 0.80;

    public static bool IsDelayMeasurable(List<double> raw, List<double> filtered)
    {
        double before = StdDev(raw);
        if (before <= 1e-9 || raw.Count < 8) return false;
        return StdDev(filtered) / before >= MinStructureRatio;
    }

    public static double EstimateDelayMs(List<double> raw, List<double> filtered, double stepMs, int maxLagSamples = 20)
    {
        if (raw.Count < 8 || filtered.Count != raw.Count) return 0;

        // En faseforskydning kan kun måles på et signal med form. Er der kun støj,
        // ville et tal her foregøgle en præcision, der ikke findes.
        if (!IsDelayMeasurable(raw, filtered)) return 0;

        double rawMean = raw.Average(), filteredMean = filtered.Average();
        double best = double.NegativeInfinity;
        int bestLag = 0;

        for (int lag = 0; lag <= maxLagSamples && lag < raw.Count / 2; lag++)
        {
            double sum = 0;
            int count = 0;
            // Det filtrerede signal skubbes lag trin tilbage og holdes op mod det rå.
            for (int i = lag; i < raw.Count; i++)
            {
                sum += (raw[i - lag] - rawMean) * (filtered[i] - filteredMean);
                count++;
            }
            if (count == 0) continue;
            double value = sum / count;
            if (value > best)
            {
                best = value;
                bestLag = lag;
            }
        }

        return bestLag * stepMs;
    }

    private static SensorMessage Clone(SensorMessage m) => new()
    {
        Type = m.Type,
        X = m.X,
        Y = m.Y,
        Z = m.Z,
        T = m.T,
        TimestampUtc = m.TimestampUtc,
        SessionId = m.SessionId,
        SequenceNumber = m.SequenceNumber,
        Qx = m.Qx,
        Qy = m.Qy,
        Qz = m.Qz,
        Qw = m.Qw,
        AngleX = m.AngleX,
        AngleY = m.AngleY,
        AngleZ = m.AngleZ,
        Pressure = m.Pressure,
        PressureUnit = m.PressureUnit,
        // Felterne fra Pi-formatet skal med. Uden pos_age_ms mister den filtrerede
        // besked sit kameratidsstempel, og gentagne positioner bliver talt som nye
        // målinger. Uden track_status forsvinder forklaringen på et datagab.
        Seq = m.Seq,
        ForceN = m.ForceN,
        ForceCounts = m.ForceCounts,
        TrackStatus = m.TrackStatus,
        PositionAgeMs = m.PositionAgeMs,
        PositionInside = m.PositionInside
    };
}
