using System.Text.Json.Serialization;

namespace TrainingSystem.PC.Models;

/// <summary>
/// Et punkt på en referencerute. Koordinaterne er i samme enhed som de målte
/// positioner (mm i bordets plan efter kalibrering af kameraopstillingen).
/// </summary>
public sealed class RoutePoint
{
    public int Index { get; set; }
    public double XMm { get; set; }
    public double YMm { get; set; }

    /// <summary>Sekunder fra sessionens start, da punktet blev registreret.</summary>
    public double OffsetSeconds { get; set; }
}

/// <summary>
/// Referencerute (FR-06). Ruten vælges af supervisor inden sessionen starter og er
/// det, den udførte rute sammenlignes med efter sessionen.
/// Korridoren hører til ruten, fordi den er en egenskab ved øvelsen og ikke ved systemet.
/// </summary>
public sealed class ReferenceRoute
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public string Name { get; set; } = "Uden navn";
    public DateTimeOffset CreatedUtc { get; set; } = DateTimeOffset.UtcNow;

    /// <summary>Tilladt afstand fra ruten i mm. Se NFR-R-03 for sammenhængen med måleusikkerheden.</summary>
    public double CorridorMm { get; set; } = DefaultCorridorMm;

    public List<RoutePoint> Points { get; set; } = new();

    /// <summary>
    /// Startværdi, ikke et fastlagt krav. Korridoren skal være mindst to til tre gange
    /// måleusikkerheden i NFR-R-03 (5 mm i billedplanet), ellers måler systemet sit eget
    /// støjgulv frem for brugerens præstation. Justér efter systemtest SyT-02.
    /// </summary>
    public const double DefaultCorridorMm = 15.0;

    [JsonIgnore]
    public bool IsUsable => Points.Count >= 2;

    public override string ToString() => $"{Name} ({Points.Count} punkter, korridor {CorridorMm:F0} mm)";

    /// <summary>
    /// Opretter en referencerute ud fra positionsmålingerne i en gennemført session (FR-07).
    /// Punkterne tyndes ud, så en rute ikke fylder mere end nødvendigt; formen bevares,
    /// fordi der kun fjernes punkter, der ligger tæt på det foregående.
    /// </summary>
    public static ReferenceRoute FromMeasurements(
        string name,
        IReadOnlyList<SensorMessage> measurements,
        DateTimeOffset startedAt,
        double corridorMm,
        double minimumSpacingMm = 2.0)
    {
        var route = new ReferenceRoute { Name = name, CorridorMm = corridorMm };

        var positions = measurements
            .Where(m => m.HasPositionXY)
            .OrderBy(m => m.EffectiveTimestampUtc)
            .ToList();

        double lastX = 0, lastY = 0;
        bool first = true;
        foreach (var m in positions)
        {
            double x = m.X!.Value, y = m.Y!.Value;
            if (!first)
            {
                double dx = x - lastX, dy = y - lastY;
                if (Math.Sqrt(dx * dx + dy * dy) < minimumSpacingMm)
                    continue;
            }

            route.Points.Add(new RoutePoint
            {
                Index = route.Points.Count,
                XMm = x,
                YMm = y,
                OffsetSeconds = (m.EffectiveTimestampUtc - startedAt).TotalSeconds
            });
            lastX = x;
            lastY = y;
            first = false;
        }

        return route;
    }
}
