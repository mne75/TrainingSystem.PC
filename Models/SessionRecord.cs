namespace TrainingSystem.PC.Models;

/// <summary>
/// En gemt træningssession (FR-05). Indeholder rådata, den referencerute der blev målt
/// op imod, de registrerede datagab og analysen, så en indlæst session giver præcis
/// samme visning og samme beregnede værdier som ved optagelsen.
/// </summary>
public sealed class SessionRecord
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public DateTimeOffset StartedAtUtc { get; set; }
    public DateTimeOffset EndedAtUtc { get; set; }

    public string? ReferenceRouteId { get; set; }
    public string? ReferenceRouteName { get; set; }

    /// <summary>Ruten gemmes med sessionen, så analysen kan gentages, selv om ruten senere ændres.</summary>
    public ReferenceRoute? ReferenceRoute { get; set; }

    public double CorridorMm { get; set; } = Models.ReferenceRoute.DefaultCorridorMm;

    public List<SensorMessage> Measurements { get; set; } = new();
    public AnalysisResult? Analysis { get; set; }

    /// <summary>
    /// Filterindstillingen, analysen blev beregnet med. Gemmes, så en indlæst session
    /// beregnes med samme filter og giver samme værdier (FR-05), uanset hvad der står i
    /// vinduet, når den åbnes. Null i sessioner gemt før denne version.
    /// </summary>
    public Services.FilterSettings? Filter { get; set; }

    /// <summary>Sand, hvis modtagelsen blev stoppet, mens sessionen kørte (NFR-R-02).</summary>
    public bool ReceiverStopped { get; set; }

    public string FileName =>
        $"session_{StartedAtUtc.UtcDateTime:yyyyMMdd_HHmmss}.json";
}
