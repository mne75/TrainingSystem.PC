using TrainingSystem.PC.Models;

namespace TrainingSystem.PC.Services;

/// <summary>
/// Sammenligner den udførte rute med referenceruten (FR-01 og FR-09).
///
/// Afvigelsen er den korteste afstand fra et målt punkt til referenceruten som kurve,
/// ikke til det nærmeste rutepunkt. Forskellen betyder noget: måler man til nærmeste
/// punkt, får man en afvigelse, der svinger med, hvor tæt rutens punkter ligger,
/// og ikke med, hvor langt brugeren reelt var fra ruten.
/// </summary>
public static class RouteComparer
{
    public static List<DeviationSample> Compare(
        IReadOnlyList<SensorMessage> measurements,
        ReferenceRoute route,
        DateTimeOffset startedAt)
    {
        var result = new List<DeviationSample>();
        if (!route.IsUsable) return result;

        var positions = measurements
            .Where(m => m.HasPositionXY)
            .OrderBy(m => m.EffectiveTimestampUtc);

        foreach (var m in positions)
        {
            double x = m.X!.Value, y = m.Y!.Value;
            double distance = DistanceToRoute(x, y, route.Points);
            result.Add(new DeviationSample
            {
                OffsetSeconds = (m.EffectiveTimestampUtc - startedAt).TotalSeconds,
                XMm = x,
                YMm = y,
                DistanceMm = distance,
                InsideCorridor = distance <= route.CorridorMm
            });
        }

        return result;
    }

    /// <summary>Korteste afstand fra et punkt til den polylinje, rutepunkterne danner.</summary>
    public static double DistanceToRoute(double x, double y, IReadOnlyList<RoutePoint> points)
    {
        if (points.Count == 0) return double.NaN;
        if (points.Count == 1) return Distance(x, y, points[0].XMm, points[0].YMm);

        double best = double.MaxValue;
        for (int i = 1; i < points.Count; i++)
        {
            double d = DistanceToSegment(x, y,
                points[i - 1].XMm, points[i - 1].YMm,
                points[i].XMm, points[i].YMm);
            if (d < best) best = d;
        }
        return best;
    }

    private static double DistanceToSegment(double px, double py,
        double ax, double ay, double bx, double by)
    {
        double abx = bx - ax, aby = by - ay;
        double lengthSquared = abx * abx + aby * aby;
        if (lengthSquared <= 1e-12) return Distance(px, py, ax, ay);

        // Projicér punktet ned på linjestykket og lås til [0,1], så vi ikke måler
        // til en forlængelse af segmentet.
        double t = ((px - ax) * abx + (py - ay) * aby) / lengthSquared;
        t = Math.Clamp(t, 0.0, 1.0);
        return Distance(px, py, ax + t * abx, ay + t * aby);
    }

    private static double Distance(double x1, double y1, double x2, double y2)
    {
        double dx = x1 - x2, dy = y1 - y2;
        return Math.Sqrt(dx * dx + dy * dy);
    }
}
