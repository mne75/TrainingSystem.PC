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

    public string FileName =>
        $"session_{StartedAtUtc.UtcDateTime:yyyyMMdd_HHmmss}.json";
}
