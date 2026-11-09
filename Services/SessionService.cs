using TrainingSystem.PC.Models;

namespace TrainingSystem.PC.Services;

/// <summary>Det, der kommer ud af en afsluttet session.</summary>
public sealed class SessionOutcome
{
    public required SessionRecord Session { get; init; }
    public required AnalysisResult Result { get; init; }

    /// <summary>Hvor sessionen blev gemt. Null, hvis den ikke kunne gemmes.</summary>
    public string? SavedPath { get; init; }

    /// <summary>Fejlbesked, hvis sessionen ikke kunne gemmes.</summary>
    public string? SaveError { get; init; }
}

/// <summary>
/// Lag 2: sessionens livscyklus (NFR-S-01). Styrer modtagelsen, samler målingerne under
/// sessionen, kalder analysen, når den afsluttes, og gemmer resultatet.
///
/// Præsentationslaget (MainWindow) taler kun med denne klasse og kender hverken
/// UdpMeasurementReceiver eller SessionStore. Klassen kender ikke WPF og kan derfor
/// testes uden brugergrænseflade.
/// </summary>
public sealed class SessionService : IAsyncDisposable
{
    public const int MaxStoredMessages = 200_000;

    private readonly UdpMeasurementReceiver _receiver;
    private readonly Func<SessionRecord, string> _saveSession;
    private readonly object _lock = new();
    private readonly List<SensorMessage> _messages = new();

    private Func<SensorMessage, Task>? _onMeasurement;
    private DateTimeOffset? _startedAt;
    private ReferenceRoute? _route;

    /// <param name="receiver">Målingskilden. Udelades den, oprettes en ny.</param>
    /// <param name="saveSession">
    /// Hvordan en afsluttet session gemmes. Udelades den, bruges SessionStore.
    /// Testene sender deres egen ind, så de ikke skriver i brugerens sessionsmappe.
    /// </param>
    public SessionService(UdpMeasurementReceiver? receiver = null,
                          Func<SessionRecord, string>? saveSession = null)
    {
        _receiver = receiver ?? new UdpMeasurementReceiver();
        _saveSession = saveSession ?? SessionStore.SaveSession;
    }

    public bool IsListening => _receiver.IsListening;

    public bool IsSessionActive
    {
        get { lock (_lock) return _startedAt.HasValue; }
    }

    /// <summary>Den referencerute, den igangværende session måles mod.</summary>
    public ReferenceRoute? ActiveRoute
    {
        get { lock (_lock) return _route; }
    }

    /// <summary>Sand, når grænsen for antal målinger i én session er nået.</summary>
    public bool IsBufferFull
    {
        get { lock (_lock) return _messages.Count >= MaxStoredMessages; }
    }

    public static string RootFolder => SessionStore.RootFolder;
    public static string SessionsFolder => SessionStore.SessionsFolder;
    public static string RoutesFolder => SessionStore.RoutesFolder;

    // =====================================================================
    // Modtagelse
    // =====================================================================

    /// <summary>
    /// Begynder at lytte efter målinger. <paramref name="onMeasurement"/> kaldes for hver
    /// måling, også uden for en session, så præsentationslaget kan vise probens bevægelse.
    /// </summary>
    public Task StartListeningAsync(int port, Func<SensorMessage, Task> onMeasurement)
    {
        _onMeasurement = onMeasurement;
        return _receiver.StartAsync(port, HandleMeasurementAsync);
    }

    public Task StopListeningAsync() => _receiver.StopAsync();

    /// <summary>Kaldes af modtageren for hver pakke. Internal, så testene kan kalde den direkte.</summary>
    internal Task HandleMeasurementAsync(SensorMessage message)
    {
        // Mangler afsenderen et tidsstempel, sættes pc'ens modtagetid én gang. Det er en
        // fælles tidsbase, men ikke det samme som synkroniserede sensorure (FR-04).
        if (!message.TimestampUtc.HasValue && !message.T.HasValue)
            message.TimestampUtc = DateTimeOffset.UtcNow;

        lock (_lock)
        {
            if (_startedAt.HasValue && _messages.Count < MaxStoredMessages)
                _messages.Add(message);
        }

        return _onMeasurement?.Invoke(message) ?? Task.CompletedTask;
    }

    // =====================================================================
    // Session
    // =====================================================================

    /// <summary>
    /// Starter en session. Korridoren hører til ruten; angives en anden bredde, gælder den
    /// kun for denne session, og den gemte rute ændres ikke.
    /// </summary>
    public void StartSession(ReferenceRoute? route, double corridorMm)
    {
        if (!IsListening)
            throw new InvalidOperationException("Start modtagelsen, før du starter en session.");

        lock (_lock)
        {
            if (_startedAt.HasValue)
                throw new InvalidOperationException("Der kører allerede en session.");

            _messages.Clear();
            _route = route is null ? null : WithCorridor(route, corridorMm);
            _startedAt = DateTimeOffset.UtcNow;
        }
    }

    /// <summary>Afslutter sessionen, analyserer den og gemmer rådata og analyse.</summary>
    public SessionOutcome EndSession(FilterSettings filter)
    {
        DateTimeOffset startedAt, endedAt;
        ReferenceRoute? route;
        List<SensorMessage> messages;

        lock (_lock)
        {
            if (!_startedAt.HasValue)
                throw new InvalidOperationException("Der kører ingen session.");

            startedAt = _startedAt.Value;
            endedAt = DateTimeOffset.UtcNow;
            route = _route;
            messages = new List<SensorMessage>(_messages);
            _startedAt = null;
        }

        bool connectionLost = !IsListening;
        var result = SessionAnalyzer.Analyze(startedAt, endedAt, messages, route, connectionLost, filter);

        var session = new SessionRecord
        {
            StartedAtUtc = startedAt,
            EndedAtUtc = endedAt,
            ReferenceRouteId = route?.Id,
            ReferenceRouteName = route?.Name,
            ReferenceRoute = route,
            CorridorMm = result.CorridorMm,
            Measurements = messages,
            Analysis = result
        };

        try
        {
            return new SessionOutcome { Session = session, Result = result, SavedPath = _saveSession(session) };
        }
        catch (Exception ex)
        {
            return new SessionOutcome { Session = session, Result = result, SaveError = ex.Message };
        }
    }

    /// <summary>
    /// Indlæser en gemt session. Analysen beregnes forfra ud fra rådata; giver den samme
    /// værdier som ved optagelsen, er FR-05 eftervist og ikke blot påstået.
    /// </summary>
    public (SessionRecord Session, AnalysisResult Result) OpenSession(string path, FilterSettings filter)
    {
        if (IsSessionActive)
            throw new InvalidOperationException("Afslut den igangværende session, før du åbner en gemt session.");

        var session = SessionStore.LoadSession(path);
        var result = SessionAnalyzer.Analyze(
            session.StartedAtUtc, session.EndedAtUtc, session.Measurements,
            session.ReferenceRoute, false, filter);
        return (session, result);
    }

    // =====================================================================
    // Referenceruter
    // =====================================================================

    public List<ReferenceRoute> LoadRoutes() => SessionStore.LoadRoutes();

    /// <summary>
    /// Danner en referencerute ud fra en gennemført session (FR-07) og gemmer den.
    /// Returnerer null, hvis sessionen har for få positionsmålinger til en rute.
    /// </summary>
    public ReferenceRoute? SaveSessionAsRoute(SessionRecord session, string name, double corridorMm, string path)
    {
        var route = ReferenceRoute.FromMeasurements(name, session.Measurements, session.StartedAtUtc, corridorMm);
        if (!route.IsUsable) return null;

        SessionStore.SaveRoute(route, path);
        return route;
    }

    private static ReferenceRoute WithCorridor(ReferenceRoute route, double corridorMm) => new()
    {
        Id = route.Id,
        Name = route.Name,
        CreatedUtc = route.CreatedUtc,
        CorridorMm = corridorMm,
        Points = route.Points
    };

    public ValueTask DisposeAsync() => _receiver.DisposeAsync();
}
