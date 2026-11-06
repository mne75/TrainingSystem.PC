namespace TrainingSystem.PC.Models;

/// <summary>Hvilken datakilde en måling eller et datagab hører til.</summary>
public enum MeasurementSource
{
    Position,
    Orientation,
    ContactForce,
    Unknown
}

/// <summary>
/// En periode uden gyldige målinger (FR-10). Et datagab opstår enten, fordi
/// trackingmarkøren var dækket, eller fordi UDP-pakker er gået tabt.
/// Systemet må ikke erstatte et gab med beregnede positioner.
/// </summary>
public sealed class DataGap
{
    public MeasurementSource Source { get; set; }
    public DateTimeOffset StartUtc { get; set; }
    public DateTimeOffset EndUtc { get; set; }
    public double DurationSeconds { get; set; }
    public int MissingSequenceNumbers { get; set; }
    public string Reason { get; set; } = "";
}

/// <summary>Status for en session (NFR-R-02).</summary>
public enum SessionStatus
{
    /// <summary>Sessionen kan vurderes.</summary>
    Completed,

    /// <summary>Data blev registreret, men sessionen blev afbrudt eller mangler målinger.</summary>
    Partial,

    /// <summary>Datagabene er så store, at et resultat ville være misvisende frem for upræcist.</summary>
    NotAssessable
}

/// <summary>Én positionsmåling holdt op mod referenceruten.</summary>
public sealed class DeviationSample
{
    public double OffsetSeconds { get; set; }
    public double XMm { get; set; }
    public double YMm { get; set; }
    public double DistanceMm { get; set; }
    public bool InsideCorridor { get; set; }
}

/// <summary>
/// Resultatet af analysen efter en session. Indeholder de fem værdier, FR-09 kræver,
/// plus det datagrundlag, de hviler på.
/// </summary>
public sealed class AnalysisResult
{
    public SessionStatus Status { get; set; } = SessionStatus.Completed;
    public string StatusReason { get; set; } = "";

    public TimeSpan Duration { get; set; }
    public TimeSpan ValidDuration { get; set; }

    public int TotalMessages { get; set; }
    public int PositionSamples { get; set; }
    public int OrientationSamples { get; set; }
    public int ContactForceSamples { get; set; }

    // --- de fem værdier i FR-09 ---
    public double? MeanDeviationMm { get; set; }
    public double? MaxDeviationMm { get; set; }
    public double? TimeInCorridorRatio { get; set; }
    public double? SmoothnessSpeedCv { get; set; }
    // Varigheden er Duration ovenfor.

    // --- understøttende mål ---
    public double? PathLengthMm { get; set; }
    public double? StraightLineDisplacementMm { get; set; }
    public double? MeanSpeedMmPerSecond { get; set; }
    public double? MeanContactForceN { get; set; }
    public double? MaxContactForceN { get; set; }

    // --- datagrundlagets kvalitet ---
    public double GapSeconds { get; set; }
    public double GapRatio { get; set; }
    public int GapCount { get; set; }
    public double PositionLossRatio { get; set; }
    public double OrientationLossRatio { get; set; }

    public double CorridorMm { get; set; }
    public string? ReferenceRouteName { get; set; }

    /// <summary>Dokumentation af støjfiltreringen (FR-12). Null, hvis der ikke blev filtreret.</summary>
    public Services.FilterReport? Filter { get; set; }

    public List<DataGap> Gaps { get; set; } = new();
    public List<DeviationSample> Deviations { get; set; } = new();
    public List<string> Notes { get; set; } = new();

    /// <summary>Feedback må kun vises, når sessionen kan vurderes (NFR-R-02).</summary>
    public bool MayShowFeedback => Status == SessionStatus.Completed;
}
