using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Shapes;
using TrainingSystem.PC.Models;
using TrainingSystem.PC.Services;

namespace TrainingSystem.PC.Views;

/// <summary>Et punkt på den udførte rute, klar til tegning.</summary>
public readonly record struct PathPoint(double X, double Y, bool InsideCorridor);

/// <summary>
/// Tegner de to figurer, FR-09 kræver: ruteoverlayet og afvigelsen over tid.
/// Alt tegnes med almindelige WPF-figurer, så projektet ikke afhænger af et diagrambibliotek.
/// </summary>
public static class Plots
{
    private static readonly Brush RouteBrush = new SolidColorBrush(Color.FromRgb(0x2E, 0x54, 0x96));
    private static readonly Brush CorridorBrush = new SolidColorBrush(Color.FromArgb(0x33, 0x2E, 0x54, 0x96));
    private static readonly Brush InsideBrush = new SolidColorBrush(Color.FromRgb(0x2E, 0x7D, 0x32));
    private static readonly Brush OutsideBrush = new SolidColorBrush(Color.FromRgb(0xC6, 0x28, 0x28));
    private static readonly Brush AxisBrush = new SolidColorBrush(Color.FromRgb(0xB0, 0xBA, 0xC6));
    private static readonly Brush TextBrush = new SolidColorBrush(Color.FromRgb(0x65, 0x75, 0x8B));
    private static readonly Brush GapBrush = new SolidColorBrush(Color.FromArgb(0x30, 0xC6, 0x28, 0x28));
    private static readonly Brush RawBrush = new SolidColorBrush(Color.FromRgb(0xA8, 0xB4, 0xC2));

    // =================================================================
    // Ruteoverlay: udført rute oven på referenceruten, i samme koordinatsystem (FR-01)
    // =================================================================
    public static void DrawRouteOverlay(Canvas canvas, IReadOnlyList<PathPoint> recorded, ReferenceRoute? route)
    {
        canvas.Children.Clear();
        double w = canvas.ActualWidth, h = canvas.ActualHeight;
        if (w < 40 || h < 40) return;

        if (recorded.Count == 0 && route is null)
        {
            Message(canvas, w, h, "Ingen rutedata.");
            return;
        }

        // Fælles udstrækning, så de to ruter vises i samme koordinatsystem.
        double minX = double.MaxValue, maxX = double.MinValue;
        double minY = double.MaxValue, maxY = double.MinValue;

        void Extend(double x, double y)
        {
            if (x < minX) minX = x;
            if (x > maxX) maxX = x;
            if (y < minY) minY = y;
            if (y > maxY) maxY = y;
        }

        foreach (var p in recorded) Extend(p.X, p.Y);
        if (route is not null) foreach (var p in route.Points) Extend(p.XMm, p.YMm);
        if (minX > maxX) { Message(canvas, w, h, "Ingen rutedata."); return; }

        double corridor = route?.CorridorMm ?? 0;
        minX -= corridor; maxX += corridor; minY -= corridor; maxY += corridor;

        const double pad = 26;
        double spanX = Math.Max(maxX - minX, 1e-6);
        double spanY = Math.Max(maxY - minY, 1e-6);
        // Samme skala på begge akser, ellers bliver en afvigelse i x set anderledes ud end i y.
        double scale = Math.Min((w - 2 * pad) / spanX, (h - 2 * pad) / spanY);
        double offsetX = (w - spanX * scale) / 2 - minX * scale;
        double offsetY = (h - spanY * scale) / 2 - minY * scale;

        // y vendes, så den peger opad på skærmen.
        Point Map(double x, double y) => new(x * scale + offsetX, h - (y * scale + offsetY));

        // Korridoren som et bredt, gennemsigtigt bånd under referenceruten.
        if (route is { IsUsable: true } && corridor > 0)
        {
            var band = new Polyline
            {
                Stroke = CorridorBrush,
                StrokeThickness = Math.Max(2, corridor * 2 * scale),
                StrokeLineJoin = PenLineJoin.Round,
                StrokeStartLineCap = PenLineCap.Round,
                StrokeEndLineCap = PenLineCap.Round
            };
            foreach (var p in route.Points) band.Points.Add(Map(p.XMm, p.YMm));
            canvas.Children.Add(band);
        }

        // Referenceruten.
        if (route is { IsUsable: true })
        {
            var line = new Polyline
            {
                Stroke = RouteBrush,
                StrokeThickness = 2.5,
                StrokeDashArray = new DoubleCollection { 5, 3 }
            };
            foreach (var p in route.Points) line.Points.Add(Map(p.XMm, p.YMm));
            canvas.Children.Add(line);
        }

        // Den udførte rute, farvet efter om punktet lå i korridoren.
        for (int i = 1; i < recorded.Count; i++)
        {
            var a = Map(recorded[i - 1].X, recorded[i - 1].Y);
            var b = Map(recorded[i].X, recorded[i].Y);
            canvas.Children.Add(new Line
            {
                X1 = a.X, Y1 = a.Y, X2 = b.X, Y2 = b.Y,
                Stroke = recorded[i].InsideCorridor ? InsideBrush : OutsideBrush,
                StrokeThickness = 2.0
            });
        }

        if (recorded.Count > 0)
        {
            Dot(canvas, Map(recorded[0].X, recorded[0].Y), InsideBrush, "start");
            Dot(canvas, Map(recorded[^1].X, recorded[^1].Y), RouteBrush, "slut");
        }

        Label(canvas, 8, 6, $"Skala: 1 px ≈ {1 / scale:F2} mm");
        Legend(canvas, w, route is { IsUsable: true });
    }

    // =================================================================
    // Afvigelse over tid (FR-09) med korridorgrænse og markerede datagab (FR-10)
    // =================================================================
    public static void DrawDeviationChart(Canvas canvas, AnalysisResult result, DateTimeOffset startedAt)
    {
        canvas.Children.Clear();
        double w = canvas.ActualWidth, h = canvas.ActualHeight;
        if (w < 40 || h < 40) return;

        if (result.Deviations.Count == 0)
        {
            Message(canvas, w, h, "Ingen afvigelser beregnet — vælg en referencerute før sessionen.");
            return;
        }

        const double left = 46, right = 12, top = 14, bottom = 24;
        double plotW = w - left - right, plotH = h - top - bottom;
        if (plotW < 20 || plotH < 20) return;

        double maxSeconds = Math.Max(result.Duration.TotalSeconds,
                                     result.Deviations[^1].OffsetSeconds);
        if (maxSeconds <= 0) maxSeconds = 1;

        double maxValue = Math.Max(result.Deviations.Max(d => d.DistanceMm), result.CorridorMm) * 1.15;
        if (maxValue <= 0) maxValue = 1;

        double MapX(double seconds) => left + seconds / maxSeconds * plotW;
        double MapY(double mm) => top + plotH - mm / maxValue * plotH;

        // Datagab som lodrette bånd, så det er synligt, hvor der ikke er målt.
        foreach (var gap in result.Gaps)
        {
            double a = (gap.StartUtc - startedAt).TotalSeconds;
            double b = (gap.EndUtc - startedAt).TotalSeconds;
            double x1 = MapX(Math.Max(0, a)), x2 = MapX(Math.Min(maxSeconds, b));
            if (x2 - x1 < 1) x2 = x1 + 1;
            var band = new Rectangle { Width = x2 - x1, Height = plotH, Fill = GapBrush };
            Canvas.SetLeft(band, x1);
            Canvas.SetTop(band, top);
            canvas.Children.Add(band);
        }

        // Akser.
        canvas.Children.Add(new Line { X1 = left, Y1 = top, X2 = left, Y2 = top + plotH, Stroke = AxisBrush, StrokeThickness = 1 });
        canvas.Children.Add(new Line { X1 = left, Y1 = top + plotH, X2 = left + plotW, Y2 = top + plotH, Stroke = AxisBrush, StrokeThickness = 1 });

        for (int i = 0; i <= 4; i++)
        {
            double value = maxValue * i / 4.0;
            double y = MapY(value);
            canvas.Children.Add(new Line { X1 = left - 4, Y1 = y, X2 = left, Y2 = y, Stroke = AxisBrush, StrokeThickness = 1 });
            Label(canvas, 4, y - 9, value.ToString("F0", CultureInfo.CurrentCulture) + " mm");
        }
        Label(canvas, left, top + plotH + 5, "0 s");
        Label(canvas, left + plotW - 44, top + plotH + 5, maxSeconds.ToString("F0") + " s");

        // Korridorgrænsen.
        double corridorY = MapY(result.CorridorMm);
        canvas.Children.Add(new Line
        {
            X1 = left, Y1 = corridorY, X2 = left + plotW, Y2 = corridorY,
            Stroke = OutsideBrush, StrokeThickness = 1.4,
            StrokeDashArray = new DoubleCollection { 4, 3 }
        });
        Label(canvas, left + 6, corridorY - 16, $"korridor {result.CorridorMm:F0} mm");

        // Selve afvigelseskurven.
        var curve = new Polyline { Stroke = RouteBrush, StrokeThickness = 1.6 };
        foreach (var d in result.Deviations)
            curve.Points.Add(new Point(MapX(d.OffsetSeconds), MapY(d.DistanceMm)));
        canvas.Children.Add(curve);

        // Middelværdien.
        if (result.MeanDeviationMm.HasValue)
        {
            double meanY = MapY(result.MeanDeviationMm.Value);
            canvas.Children.Add(new Line
            {
                X1 = left, Y1 = meanY, X2 = left + plotW, Y2 = meanY,
                Stroke = InsideBrush, StrokeThickness = 1.2,
                StrokeDashArray = new DoubleCollection { 2, 3 }
            });
            Label(canvas, left + plotW - 110, meanY - 16, $"middel {result.MeanDeviationMm.Value:F1} mm");
        }
    }

    // =================================================================
    // Før og efter filtrering (FR-12)
    // =================================================================
    public static void DrawFilterComparison(Canvas canvas, FilterReport? report)
    {
        canvas.Children.Clear();
        double w = canvas.ActualWidth, h = canvas.ActualHeight;
        if (w < 40 || h < 40) return;

        if (report is null || !report.Applied || report.Comparison.Count < 2)
        {
            Message(canvas, w, h, report is { Applied: false }
                ? "Filtrering er slået fra. Analysen bruger rådata."
                : "Ingen data at sammenligne.");
            return;
        }

        const double left = 52, right = 12, top = 16, bottom = 22;
        double plotW = w - left - right, plotH = h - top - bottom;
        if (plotW < 20 || plotH < 20) return;

        var samples = report.Comparison;
        double maxSeconds = Math.Max(samples[^1].OffsetSeconds, 1e-6);
        double min = Math.Min(samples.Min(s => s.Raw), samples.Min(s => s.Filtered));
        double max = Math.Max(samples.Max(s => s.Raw), samples.Max(s => s.Filtered));
        if (max - min < 1e-9) { min -= 1; max += 1; }
        double margin = (max - min) * 0.1;
        min -= margin; max += margin;

        double MapX(double s) => left + s / maxSeconds * plotW;
        double MapY(double v) => top + plotH - (v - min) / (max - min) * plotH;

        canvas.Children.Add(new Line { X1 = left, Y1 = top, X2 = left, Y2 = top + plotH, Stroke = AxisBrush, StrokeThickness = 1 });
        canvas.Children.Add(new Line { X1 = left, Y1 = top + plotH, X2 = left + plotW, Y2 = top + plotH, Stroke = AxisBrush, StrokeThickness = 1 });
        Label(canvas, 4, MapY(max) - 4, max.ToString("F1", CultureInfo.CurrentCulture));
        Label(canvas, 4, MapY(min) - 12, min.ToString("F1", CultureInfo.CurrentCulture));
        Label(canvas, left, top + plotH + 4, "0 s");
        Label(canvas, left + plotW - 40, top + plotH + 4, maxSeconds.ToString("F0") + " s");

        var raw = new Polyline { Stroke = RawBrush, StrokeThickness = 1.0 };
        var filtered = new Polyline { Stroke = RouteBrush, StrokeThickness = 1.8 };
        foreach (var s in samples)
        {
            raw.Points.Add(new Point(MapX(s.OffsetSeconds), MapY(s.Raw)));
            filtered.Points.Add(new Point(MapX(s.OffsetSeconds), MapY(s.Filtered)));
        }
        canvas.Children.Add(raw);
        canvas.Children.Add(filtered);

        Label(canvas, left + 6, 2, report.ComparisonSignalName);
        double legendX = Math.Max(left + 6, w - 140);
        Swatch(canvas, legendX, 2, RawBrush, "rådata");
        Swatch(canvas, legendX, 17, RouteBrush, "filtreret");
    }

    // ---------------- små hjælpere ----------------

    private static void Dot(Canvas canvas, Point p, Brush brush, string text)
    {
        var dot = new Ellipse { Width = 8, Height = 8, Fill = brush };
        Canvas.SetLeft(dot, p.X - 4);
        Canvas.SetTop(dot, p.Y - 4);
        canvas.Children.Add(dot);
        Label(canvas, p.X + 7, p.Y - 9, text);
    }

    private static void Label(Canvas canvas, double x, double y, string text)
    {
        var block = new TextBlock { Text = text, Foreground = TextBrush, FontSize = 10 };
        Canvas.SetLeft(block, x);
        Canvas.SetTop(block, y);
        canvas.Children.Add(block);
    }

    private static void Message(Canvas canvas, double w, double h, string text)
    {
        var block = new TextBlock
        {
            Text = text, Foreground = TextBrush, FontSize = 12,
            TextWrapping = TextWrapping.Wrap, Width = Math.Max(80, w - 40),
            TextAlignment = TextAlignment.Center
        };
        Canvas.SetLeft(block, 20);
        Canvas.SetTop(block, h / 2 - 14);
        canvas.Children.Add(block);
    }

    private static void Legend(Canvas canvas, double w, bool hasRoute)
    {
        double x = w - 150, y = 6;
        if (hasRoute)
        {
            Swatch(canvas, x, y, RouteBrush, "referencerute"); y += 15;
        }
        Swatch(canvas, x, y, InsideBrush, "i korridor"); y += 15;
        Swatch(canvas, x, y, OutsideBrush, "uden for");
    }

    private static void Swatch(Canvas canvas, double x, double y, Brush brush, string text)
    {
        var line = new Line { X1 = x, Y1 = y + 6, X2 = x + 16, Y2 = y + 6, Stroke = brush, StrokeThickness = 2.5 };
        canvas.Children.Add(line);
        Label(canvas, x + 21, y, text);
    }
}
