using TrainingSystem.PC.Models;
using TrainingSystem.PC.Services;

namespace TrainingSystem.Tests;

// Diagnoseværktøj til opsætning af hardwaren.
// Lytter på UDP-porten med nøjagtig de samme klasser, som programmet bruger, og skriver
// bagefter den samme analyse ud. Det gør det muligt at afgøre, om det er netværket,
// afsenderne eller brugergrænsefladen, der driller — uden at skulle gætte.
//   dotnet run -- --listen
//   dotnet run -- --listen --port 5005 --seconds 30 --rute minrute.json

public static class Listen
{
    public static async Task<int> RunAsync(string[] args)
    {
        int port = IntArg(args, "--port", 5005);
        double seconds = DoubleArg(args, "--seconds", 20);
        string? routePath = StringArg(args, "--rute");

        ReferenceRoute? route = null;
        if (routePath is not null)
        {
            try
            {
                route = System.Text.Json.JsonSerializer.Deserialize<ReferenceRoute>(
                    File.ReadAllText(routePath));
                Console.WriteLine($"Referencerute indlæst: {route}");
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Kunne ikke indlæse referenceruten: {ex.Message}");
            }
        }

        var messages = new List<SensorMessage>();
        var firstSeen = new Dictionary<string, DateTimeOffset>();
        var counts = new Dictionary<string, int>();
        object gate = new();

        await using var receiver = new UdpMeasurementReceiver();

        try
        {
            await receiver.StartAsync(port, message =>
            {
                if (!message.TimestampUtc.HasValue && !message.T.HasValue)
                    message.TimestampUtc = DateTimeOffset.UtcNow;

                string source = GapDetector.Name(GapDetector.SourceOf(message));
                lock (gate)
                {
                    messages.Add(message);
                    counts[source] = counts.GetValueOrDefault(source) + 1;
                    if (!firstSeen.ContainsKey(source))
                    {
                        firstSeen[source] = DateTimeOffset.UtcNow;
                        Console.WriteLine($"  [{DateTime.Now:HH:mm:ss}] første pakke fra {source}");
                    }
                }
                return Task.CompletedTask;
            });
        }
        catch (Exception ex)
        {
            Console.WriteLine($"Kunne ikke lytte på port {port}: {ex.Message}");
            return 1;
        }

        var startedAt = DateTimeOffset.UtcNow;
        Console.WriteLine($"Lytter på UDP-port {port} i {seconds:F0} sekunder…");
        Console.WriteLine("Start afsenderne nu. Tryk Ctrl+C for at stoppe før tid.");
        Console.WriteLine();

        var deadline = DateTime.UtcNow.AddSeconds(seconds);
        int lastTotal = 0;
        while (DateTime.UtcNow < deadline)
        {
            await Task.Delay(1000);
            int total;
            lock (gate) total = messages.Count;
            Console.WriteLine($"  {(DateTime.UtcNow - deadline.AddSeconds(-seconds)).TotalSeconds,4:F0} s   " +
                              $"i alt {total,6}   (+{total - lastTotal}/s)");
            lastTotal = total;
        }

        await receiver.StopAsync();
        var endedAt = DateTimeOffset.UtcNow;

        List<SensorMessage> captured;
        lock (gate) captured = new List<SensorMessage>(messages);

        Console.WriteLine();
        if (captured.Count == 0)
        {
            Console.WriteLine("INGEN PAKKER MODTAGET.");
            Console.WriteLine("  Tjek: samme netværk? rigtig IP på afsenderen? samme portnummer?");
            Console.WriteLine("  Windows Defender kan blokere indgående UDP — tillad porten for private netværk.");
            return 1;
        }

        Console.WriteLine("MODTAGET");
        foreach (var (source, count) in counts.OrderBy(k => k.Key))
            Console.WriteLine($"  {source,-24} {count,6} pakker   ({count / (endedAt - startedAt).TotalSeconds,6:F1} Hz)");

        Console.WriteLine();
        var result = SessionAnalyzer.Analyze(startedAt, endedAt, captured, route,
                                             false, new FilterSettings());
        Console.WriteLine(SessionAnalyzer.Format(result));

        try
        {
            var session = new SessionRecord
            {
                StartedAtUtc = startedAt,
                EndedAtUtc = endedAt,
                ReferenceRoute = route,
                ReferenceRouteName = route?.Name,
                CorridorMm = result.CorridorMm,
                Measurements = captured,
                Analysis = result
            };
            Console.WriteLine();
            Console.WriteLine("Gemt: " + SessionStore.SaveSession(session));
            Console.WriteLine("Sessionen kan åbnes i programmet med \"Åbn gemt session…\".");
        }
        catch (Exception ex)
        {
            Console.WriteLine($"Kunne ikke gemme sessionen: {ex.Message}");
        }

        return 0;
    }

    private static string? StringArg(string[] args, string name)
    {
        int i = Array.IndexOf(args, name);
        return i >= 0 && i + 1 < args.Length ? args[i + 1] : null;
    }

    private static int IntArg(string[] args, string name, int fallback) =>
        int.TryParse(StringArg(args, name), out int value) ? value : fallback;

    private static double DoubleArg(string[] args, string name, double fallback) =>
        double.TryParse(StringArg(args, name), System.Globalization.CultureInfo.InvariantCulture,
            out double value) ? value : fallback;
}
