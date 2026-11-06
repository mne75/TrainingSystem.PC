using TrainingSystem.PC.Models;

namespace TrainingSystem.PC.Services;

/// <summary>
/// Beregner resultatet af en gennemført session. Analysen kører først, når sessionen er
/// afsluttet (FR-08 og FR-09), og den kender hverken UDP, JSON eller brugergrænsefladen.
/// </summary>
public static class SessionAnalyzer
{
    /// <summary>Over denne andel datagab er et resultat misvisende frem for upræcist (NFR-R-02).</summary>
    public const double MaxGapRatio = 0.25;

    /// <summary>Grænse for pakketab, hvor datagrundlaget skal markeres (NFR-R-01).</summary>
    public const double MaxLossRatio = 0.01;

    public static AnalysisResult Analyze(
        DateTimeOffset startedAt,
        DateTimeOffset endedAt,
        IReadOnlyList<SensorMessage> measurements,
        ReferenceRoute? route,
        bool connectionLost = false,
        FilterSettings? filterSettings = null)
    {
        var result = new AnalysisResult
        {
            Duration = endedAt - startedAt,
            TotalMessages = measurements.Count,
            CorridorMm = route?.CorridorMm ?? ReferenceRoute.DefaultCorridorMm,
            ReferenceRouteName = route?.Name
        };

        // Støjfiltrering (FR-12). Datagab findes på rådata: et hul er noget, der mangler i
        // det modtagne, og det ændrer filtrering ikke på. Alt andet regnes på de filtrerede
        // målinger, for det er dem, resultatet skal hvile på.
        var (working, filterReport) = NoiseFilter.Apply(measurements, filterSettings ?? FilterSettings.Off);
        result.Filter = filterReport;

        var positions = working
            .Where(m => m.HasPositionXY)
            .OrderBy(m => m.EffectiveTimestampUtc)
            .ToList();

        result.PositionSamples = positions.Count;
        result.OrientationSamples = working.Count(m => m.HasOrientation);
        result.ContactForceSamples = working.Count(m => m.Pressure.HasValue);

        // --- datagrundlagets kvalitet ---
        var gaps = GapDetector.Detect(measurements, startedAt, endedAt);
        result.Gaps = gaps.Gaps;
        result.GapCount = gaps.Gaps.Count;
        result.GapSeconds = gaps.TotalGapSeconds;
        result.GapRatio = gaps.GapRatio;
        result.PositionLossRatio = gaps.PositionLossRatio;
        result.OrientationLossRatio = gaps.OrientationLossRatio;
        result.ValidDuration = TimeSpan.FromSeconds(
            Math.Max(0, result.Duration.TotalSeconds - gaps.TotalGapSeconds));

        // --- bevægelsen i sig selv ---
        ComputeMovement(positions, result);

        // --- kontaktkraft (FR-11, hvis den er realiseret) ---
        var forces = working.Where(m => m.Pressure.HasValue)
                            .Select(m => m.Pressure!.Value).ToList();
        if (forces.Count > 0)
        {
            result.MeanContactForceN = forces.Average();
            result.MaxContactForceN = forces.Max();
        }

        // --- afvigelse fra referenceruten ---
        if (route is { IsUsable: true } && positions.Count > 0)
        {
            var deviations = RouteComparer.Compare(working, route, startedAt);
            result.Deviations = deviations;
            if (deviations.Count > 0)
            {
                result.MeanDeviationMm = deviations.Average(d => d.DistanceMm);
                result.MaxDeviationMm = deviations.Max(d => d.DistanceMm);
                result.TimeInCorridorRatio =
                    (double)deviations.Count(d => d.InsideCorridor) / deviations.Count;
            }
        }

        DetermineStatus(result, route, connectionLost);
        AddNotes(result, route);
        return result;
    }

    private static void ComputeMovement(List<SensorMessage> positions, AnalysisResult result)
    {
        if (positions.Count < 2) return;

        double path = 0;
        var speeds = new List<double>();

        for (int i = 1; i < positions.Count; i++)
        {
            var previous = positions[i - 1];
            var current = positions[i];
            double dx = current.X!.Value - previous.X!.Value;
            double dy = current.Y!.Value - previous.Y!.Value;
            double step = Math.Sqrt(dx * dx + dy * dy);
            path += step;

            double seconds = (current.EffectiveTimestampUtc - previous.EffectiveTimestampUtc).TotalSeconds;
            // Spring over et datagab: en hastighed beregnet hen over et hul beskriver
            // ikke en bevægelse, der har fundet sted.
            if (seconds > 0 && seconds <= GapDetector.MaxSampleIntervalSeconds)
                speeds.Add(step / seconds);
        }

        result.PathLengthMm = path;

        double totalDx = positions[^1].X!.Value - positions[0].X!.Value;
        double totalDy = positions[^1].Y!.Value - positions[0].Y!.Value;
        result.StraightLineDisplacementMm = Math.Sqrt(totalDx * totalDx + totalDy * totalDy);

        if (speeds.Count > 0)
        {
            double mean = speeds.Average();
            result.MeanSpeedMmPerSecond = mean;
            if (mean > 0)
            {
                double variance = speeds.Average(v => (v - mean) * (v - mean));
                // Variationskoefficienten for hastigheden: lav værdi = jævn bevægelse.
                result.SmoothnessSpeedCv = Math.Sqrt(variance) / mean;
            }
        }
    }

    private static void DetermineStatus(AnalysisResult result, ReferenceRoute? route, bool connectionLost)
    {
        if (result.GapRatio > MaxGapRatio)
        {
            result.Status = SessionStatus.NotAssessable;
            result.StatusReason =
                $"{result.GapRatio * 100:F0} % af sessionen er datagab. Grænsen er " +
                $"{MaxGapRatio * 100:F0} %, og over den kan systemet ikke skelne en god " +
                "udførelse fra en, hvor de vanskelige passager mangler (NFR-R-02).";
            return;
        }

        if (connectionLost)
        {
            result.Status = SessionStatus.Partial;
            result.StatusReason =
                "Forbindelsen blev afbrudt undervejs. Data er gemt, men sessionen indgår ikke " +
                "i sammenligning med referenceruten (NFR-R-02).";
            return;
        }

        if (result.PositionSamples < 2)
        {
            result.Status = SessionStatus.Partial;
            result.StatusReason =
                "Der er færre end to positionsmålinger. Der er ikke grundlag for at beregne " +
                "hverken rute eller afvigelse.";
            return;
        }

        if (route is null || !route.IsUsable)
        {
            result.Status = SessionStatus.Completed;
            result.StatusReason =
                "Sessionen er gennemført, men der var ikke valgt en referencerute, så " +
                "afvigelse og korridor er ikke beregnet (FR-06).";
            return;
        }

        result.Status = SessionStatus.Completed;
        result.StatusReason = "Sessionen er gennemført og kan vurderes.";
    }

    private static void AddNotes(AnalysisResult result, ReferenceRoute? route)
    {
        if (route is null || !route.IsUsable)
            result.Notes.Add("Ingen referencerute var valgt. Middelafvigelse, største afvigelse og tid i korridor kan ikke beregnes.");

        if (result.OrientationSamples == 0)
            result.Notes.Add("Der blev ikke modtaget orienteringsdata. Kontrollér afsenderen på proben.");

        if (result.PositionSamples == 0)
            result.Notes.Add("Der blev ikke modtaget positionsdata. Kontrollér kameraopstillingen og markørerne.");

        if (result.ContactForceSamples == 0)
            result.Notes.Add("Kontaktkraft blev ikke modtaget. FR-11 er ikke realiseret på probe-siden.");

        if (result.PositionLossRatio > MaxLossRatio)
            result.Notes.Add($"Pakketab i positionsstrømmen er {result.PositionLossRatio * 100:F1} %, over grænsen på {MaxLossRatio * 100:F0} % (NFR-R-01).");

        if (result.OrientationLossRatio > MaxLossRatio)
            result.Notes.Add($"Pakketab i orienteringsstrømmen er {result.OrientationLossRatio * 100:F1} %, over grænsen på {MaxLossRatio * 100:F0} % (NFR-R-01).");

        if (result.GapCount > 0)
            result.Notes.Add($"{result.GapCount} datagab på i alt {result.GapSeconds:F1} s er markeret og indgår ikke som målte positioner (FR-10).");

        if (result.Filter is { Applied: true } filter)
        {
            var position = filter.Signals.FirstOrDefault(s => s.Name is "x" or "y");
            if (position is not null)
                result.Notes.Add(
                    $"Støjfiltrering: glidende middelværdi over {filter.WindowMs:F0} ms. " +
                    $"Spredningen på ændringen fra måling til måling faldt {position.StepReductionRatio * 100:F0} % " +
                    $"for {position.Name}. Målt forsinkelse {filter.MeasuredDelayMs:F0} ms " +
                    $"(grænsen i FR-12 er {FilterReport.MaxAllowedDelayMs:F0} ms).");

            if (!filter.DelayWithinLimit)
                result.Notes.Add(
                    $"Forsinkelsen på {filter.MeasuredDelayMs:F0} ms overskrider grænsen i FR-12. " +
                    "Vælg et kortere vindue eller et centreret vindue.");
        }
        foreach (var note in result.Filter?.Notes ?? new List<string>())
            result.Notes.Add(note);

        result.Notes.Add("Variationskoefficienten for hastigheden er et foreløbigt mål for bevægelsesjævnhed og ikke en valideret score.");
        result.Notes.Add("Resultatet beskriver udførelsen. Det afgør ikke klinisk kompetence; der findes ingen beståelsesgrænse i denne version (W-01).");
    }

    /// <summary>Tekstopsummering til rapporten og til eksport.</summary>
    public static string Format(AnalysisResult r)
    {
        string Fmt(double? v, string unit = "") =>
            v.HasValue ? $"{v.Value:F2}{(unit.Length > 0 ? " " + unit : "")}" : "ikke tilgængelig";

        var lines = new List<string>
        {
            "SESSIONSANALYSE",
            $"Status: {Status(r.Status)} — {r.StatusReason}",
            "",
            $"Referencerute: {r.ReferenceRouteName ?? "ingen valgt"} (korridor {r.CorridorMm:F0} mm)",
            $"Varighed: {r.Duration.TotalSeconds:F1} s, heraf gyldig {r.ValidDuration.TotalSeconds:F1} s",
            "",
            "FEEDBACK (FR-09)",
            $"  Middelafvigelse:        {Fmt(r.MeanDeviationMm, "mm")}",
            $"  Største afvigelse:      {Fmt(r.MaxDeviationMm, "mm")}",
            $"  Andel af tid i korridor: {(r.TimeInCorridorRatio.HasValue ? $"{r.TimeInCorridorRatio.Value * 100:F1} %" : "ikke tilgængelig")}",
            $"  Varighed:               {r.Duration.TotalSeconds:F1} s",
            $"  Bevægelsesjævnhed (CV): {Fmt(r.SmoothnessSpeedCv)}",
            "",
            "DATAGRUNDLAG",
            $"  Modtagne pakker:        {r.TotalMessages}",
            $"  Positionsmålinger:      {r.PositionSamples}",
            $"  Orienteringsmålinger:   {r.OrientationSamples}",
            $"  Kraftmålinger:          {r.ContactForceSamples}",
            $"  Datagab:                {r.GapCount} stk., i alt {r.GapSeconds:F1} s ({r.GapRatio * 100:F1} %)",
            $"  Pakketab position:      {r.PositionLossRatio * 100:F2} %",
            $"  Pakketab orientering:   {r.OrientationLossRatio * 100:F2} %",
            "",
            "BEVÆGELSE",
            $"  Vejlængde:              {Fmt(r.PathLengthMm, "mm")}",
            $"  Forskydning start-slut: {Fmt(r.StraightLineDisplacementMm, "mm")}",
            $"  Middelhastighed:        {Fmt(r.MeanSpeedMmPerSecond, "mm/s")}",
            $"  Middel kontaktkraft:    {Fmt(r.MeanContactForceN, "N")}",
            $"  Maks. kontaktkraft:     {Fmt(r.MaxContactForceN, "N")}"
        };

        if (r.Filter is { Applied: true } f)
        {
            lines.Add("");
            lines.Add("STØJFILTRERING (FR-12)");
            lines.Add($"  Metode:                 glidende middelværdi, {f.WindowMs:F0} ms, " +
                      $"{(f.Mode == FilterMode.Centred ? "centreret (nulfase)" : "kausalt")}");
            lines.Add($"  Sammenhængende stykker: {f.SegmentCount} (filteret regner aldrig hen over et datagab)");
            lines.Add(f.DelayMeasurable
                ? $"  Målt forsinkelse:       {f.MeasuredDelayMs:F0} ms " +
                  $"(grænse {FilterReport.MaxAllowedDelayMs:F0} ms — {(f.DelayWithinLimit ? "overholdt" : "OVERSKREDET")})"
                : "  Målt forsinkelse:       kan ikke måles på dette datasæt (ingen bevægelse)");
            foreach (var s in f.Signals)
            {
                lines.Add($"  Signal {s.Name}:");
                lines.Add($"      spredning i signalet:   {s.StdDevBefore:F3} → {s.StdDevAfter:F3}  ({s.ReductionRatio * 100:F1} % reduktion)");
                lines.Add($"      spredning måling-til-måling: {s.StepStdDevBefore:F3} → {s.StepStdDevAfter:F3}  ({s.StepReductionRatio * 100:F1} % reduktion)");
            }
            lines.Add("  Spredningen i signalet er målet, når proben ligger i ro. Spredningen fra");
            lines.Add("  måling til måling er målet, der også gælder, når proben bevæger sig.");
        }

        if (r.Gaps.Count > 0)
        {
            lines.Add("");
            lines.Add("DATAGAB");
            foreach (var g in r.Gaps.Take(25))
                lines.Add($"  {g.StartUtc:HH:mm:ss.fff} → {g.EndUtc:HH:mm:ss.fff}  ({g.DurationSeconds:F2} s)  {g.Reason}");
            if (r.Gaps.Count > 25) lines.Add($"  … og {r.Gaps.Count - 25} flere.");
        }

        if (r.Notes.Count > 0)
        {
            lines.Add("");
            lines.Add("FORBEHOLD");
            foreach (var n in r.Notes) lines.Add("  • " + n);
        }

        return string.Join(Environment.NewLine, lines);
    }

    public static string Status(SessionStatus status) => status switch
    {
        SessionStatus.Completed => "Gennemført",
        SessionStatus.Partial => "Delvis",
        SessionStatus.NotAssessable => "Ikke vurderbar",
        _ => status.ToString()
    };
}
