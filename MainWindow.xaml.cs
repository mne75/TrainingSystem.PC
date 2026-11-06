using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO;
using System.Net.Sockets;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Media3D;
using TrainingSystem.PC.Models;
using TrainingSystem.PC.Services;
using TrainingSystem.PC.Views;

namespace TrainingSystem.PC;

/// <summary>
/// Præsentationslaget. Under en session vises probens position, stilling og bevægelse i
/// 3D — og intet andet. Ingen måleværdier, ingen afvigelse, ingen korridor, ingen graf og
/// ingen vurdering (FR-08). Al analyse og al feedback sker efter sessionen (FR-09).
/// </summary>
public partial class MainWindow : Window
{
    private readonly UdpMeasurementReceiver _receiver = new();
    private readonly ObservableCollection<MeasurementRow> _rows = new();
    private readonly List<SensorMessage> _sessionMessages = new();

    private DateTimeOffset? _sessionStartedAt;
    private bool _isSessionActive;

    private SensorMessage? _latestPositionMessage;
    private SensorMessage? _latestOrientationMessage;
    private (double X, double Y)? _positionOrigin;

    // Sporet viser hele den udfoerte rute for sessionen. Nye punkter tegnes paa med det
    // samme i stedet for at hele sporet tegnes om, og naar antallet vokser for stort,
    // fordobles afstanden mellem punkterne. Ruten bliver dermed lidt grovere undervejs,
    // men den bliver aldrig kortere, og visningen bliver ikke tung ved lange sessioner.
    private readonly List<(double X, double Y)> _trajectoryPoints = new();
    private Model3DGroup _trajectoryGroup = new();
    private double _trajectoryStepMm = TrajectoryInitialStepMm;
    private const double TrajectoryInitialStepMm = 1.5;
    private const int TrajectoryMaxPoints = 1500;

    private ReferenceRoute? _selectedRoute;
    private ReferenceRoute? _routeUsedInSession;
    private AnalysisResult? _lastResult;
    private SessionRecord? _lastSession;

    private const int MaxVisibleRows = 400;
    private const int MaxStoredSessionMessages = 200_000;

    public MainWindow()
    {
        InitializeComponent();
        MeasurementsGrid.ItemsSource = _rows;
        Build3DScene();
        LoadRoutes();
        Closing += async (_, _) => await _receiver.DisposeAsync();
    }

    // =====================================================================
    // Modtagelse
    // =====================================================================

    private async void ListenButton_Click(object sender, RoutedEventArgs e)
    {
        if (!int.TryParse(PortTextBox.Text, out int port) || port is < 1 or > 65535)
        {
            MessageBox.Show("Angiv en gyldig UDP-port mellem 1 og 65535.");
            return;
        }

        try
        {
            await _receiver.StartAsync(port, HandleMessageAsync);
            ListenButton.IsEnabled = false;
            StopListenButton.IsEnabled = true;
            PortTextBox.IsEnabled = false;
            StatusText.Text = $"Modtager på UDP-port {port}";
            ConnectionIndicator.Fill = Brushes.DarkOrange;
        }
        catch (SocketException ex)
        {
            MessageBox.Show($"Kunne ikke lytte på port {port}: {ex.Message}");
        }
        catch (InvalidOperationException ex)
        {
            MessageBox.Show(ex.Message);
        }
    }

    private async void StopListenButton_Click(object sender, RoutedEventArgs e)
    {
        await _receiver.StopAsync();
        ListenButton.IsEnabled = true;
        StopListenButton.IsEnabled = false;
        PortTextBox.IsEnabled = true;
        StatusText.Text = "Modtager ikke";
        ConnectionIndicator.Fill = Brushes.Gray;
    }

    /// <summary>
    /// Kaldes for hver modtaget pakke. Under en session gemmes målingen, og kun probens
    /// stilling i 3D opdateres. Der skrives ingen værdier, ingen graf og ingen vurdering
    /// til skærmen før sessionen er afsluttet (FR-08).
    /// </summary>
    private Task HandleMessageAsync(SensorMessage message)
    {
        // Mangler afsenderen et tidsstempel, sættes pc'ens modtagetid én gang. Det er en
        // fælles tidsbase, men ikke det samme som synkroniserede sensorure (FR-04).
        if (!message.TimestampUtc.HasValue && !message.T.HasValue)
            message.TimestampUtc = DateTimeOffset.UtcNow;

        return Dispatcher.InvokeAsync(() =>
        {
            ConnectionIndicator.Fill = Brushes.LimeGreen;

            if (message.HasPositionXY)
            {
                _latestPositionMessage = message;
                TrackMovement(message.X!.Value, message.Y!.Value);
            }
            if (message.HasOrientation) _latestOrientationMessage = message;
            UpdateProbeVisual();

            if (!_isSessionActive) return;

            if (_sessionMessages.Count >= MaxStoredSessionMessages)
            {
                SessionStatusText.Text = "Grænsen for antal målinger er nået — afslut sessionen.";
                return;
            }
            _sessionMessages.Add(message);
        }).Task;
    }

    // =====================================================================
    // Referenceruter
    // =====================================================================

    private void LoadRoutes()
    {
        var routes = SessionStore.LoadRoutes();
        RouteComboBox.Items.Clear();
        RouteComboBox.Items.Add("(ingen referencerute)");
        foreach (var route in routes) RouteComboBox.Items.Add(route);
        RouteComboBox.SelectedIndex = 0;
    }

    private void RefreshRoutesButton_Click(object sender, RoutedEventArgs e) => LoadRoutes();

    private void RouteComboBox_SelectionChanged(object sender, System.Windows.Controls.SelectionChangedEventArgs e)
    {
        _selectedRoute = RouteComboBox.SelectedItem as ReferenceRoute;
        if (_selectedRoute is not null)
            CorridorTextBox.Text = _selectedRoute.CorridorMm.ToString("F0");
    }

    private double ReadCorridorMm()
    {
        if (double.TryParse(CorridorTextBox.Text, out double mm) && mm > 0) return mm;
        CorridorTextBox.Text = ReferenceRoute.DefaultCorridorMm.ToString("F0");
        return ReferenceRoute.DefaultCorridorMm;
    }

    private void SaveAsRouteButton_Click(object sender, RoutedEventArgs e)
    {
        if (_lastSession is null || _lastSession.Measurements.Count == 0)
        {
            MessageBox.Show("Der er ingen gennemført session at gemme som referencerute.");
            return;
        }

        Directory.CreateDirectory(SessionStore.RoutesFolder);
        var dialog = new Microsoft.Win32.SaveFileDialog
        {
            Title = "Gem sessionen som referencerute",
            InitialDirectory = SessionStore.RoutesFolder,
            FileName = $"rute_{DateTime.Now:yyyyMMdd_HHmm}",
            DefaultExt = ".json",
            Filter = "Referencerute (*.json)|*.json"
        };
        if (dialog.ShowDialog() != true) return;

        string name = Path.GetFileNameWithoutExtension(dialog.FileName);
        var route = ReferenceRoute.FromMeasurements(
            name, _lastSession.Measurements, _lastSession.StartedAtUtc, ReadCorridorMm());

        if (!route.IsUsable)
        {
            MessageBox.Show("Sessionen indeholder for få positionsmålinger til at danne en referencerute.");
            return;
        }

        File.WriteAllText(dialog.FileName,
            System.Text.Json.JsonSerializer.Serialize(route, new System.Text.Json.JsonSerializerOptions { WriteIndented = true }));

        LoadRoutes();
        SelectRouteById(route.Id);
        MessageBox.Show($"Referenceruten \"{route.Name}\" er gemt med {route.Points.Count} punkter.");
    }

    private void SelectRouteById(string id)
    {
        foreach (var item in RouteComboBox.Items)
            if (item is ReferenceRoute r && r.Id == id)
            {
                RouteComboBox.SelectedItem = item;
                return;
            }
    }

    // =====================================================================
    // Støjfiltrering (FR-12)
    // =====================================================================

    private FilterSettings ReadFilterSettings()
    {
        if (FilterCheckBox.IsChecked != true) return FilterSettings.Off;

        if (!double.TryParse(FilterWindowTextBox.Text, out double windowMs) || windowMs <= 0)
        {
            windowMs = FilterSettings.DefaultWindowMs;
            FilterWindowTextBox.Text = windowMs.ToString("F0");
        }

        return new FilterSettings
        {
            Enabled = true,
            WindowMs = windowMs,
            Mode = FilterModeComboBox.SelectedIndex == 1 ? FilterMode.Causal : FilterMode.Centred,
            FilterPosition = true,
            FilterOrientation = FilterOrientationCheckBox.IsChecked == true
        };
    }

    private void ShowFilterSummary(FilterReport? report)
    {
        if (report is null || !report.Applied)
        {
            FilterSummaryText.Text = "Filtrering er slået fra. Afvigelse og bevægelsesmål er beregnet på rådata.";
            return;
        }

        var parts = new List<string>
        {
            $"Glidende middelværdi over {report.WindowMs:F0} ms, " +
            $"{(report.Mode == FilterMode.Centred ? "centreret vindue (nulfase)" : "kausalt vindue")}.",
            report.DelayMeasurable
                ? $"Målt forsinkelse: {report.MeasuredDelayMs:F0} ms " +
                  $"({(report.DelayWithinLimit ? "inden for" : "OVER")} grænsen på {FilterReport.MaxAllowedDelayMs:F0} ms i FR-12)."
                : "Forsinkelsen kan ikke måles her: datasættet indeholder ingen bevægelse. Optag en kendt bevægelse for at eftervise grænsen på 50 ms."
        };

        foreach (var s in report.Signals)
            parts.Add($"{s.Name}: spredning {s.StdDevBefore:F2} → {s.StdDevAfter:F2} " +
                      $"({s.ReductionRatio * 100:F0} %), måling-til-måling {s.StepStdDevBefore:F2} → " +
                      $"{s.StepStdDevAfter:F2} ({s.StepReductionRatio * 100:F0} %).");

        parts.Add($"Filteret er kørt på {report.SegmentCount} sammenhængende stykker og aldrig hen over et datagab.");
        FilterSummaryText.Text = string.Join(" ", parts);
    }

    // =====================================================================
    // Session
    // =====================================================================

    private void StartSessionButton_Click(object sender, RoutedEventArgs e)
    {
        if (_isSessionActive)
        {
            MessageBox.Show("Der kører allerede en session.");
            return;
        }

        if (!_receiver.IsListening)
        {
            MessageBox.Show("Start modtagelsen, før du starter en session.");
            return;
        }

        _sessionMessages.Clear();
        _rows.Clear();
        _positionOrigin = null;
        _latestPositionMessage = null;
        _latestOrientationMessage = null;
        _lastResult = null;
        _lastSession = null;
        _trajectoryPoints.Clear();
        _trajectoryStepMm = TrajectoryInitialStepMm;
        _trajectoryGroup = new Model3DGroup();
        TrajectoryVisual.Content = _trajectoryGroup;
        UpdateProbeVisual();

        // Korridoren hører til ruten. Ændrer brugeren feltet, gælder ændringen for denne session.
        double corridor = ReadCorridorMm();
        _routeUsedInSession = _selectedRoute is null ? null : CloneWithCorridor(_selectedRoute, corridor);

        _sessionStartedAt = DateTimeOffset.UtcNow;
        _isSessionActive = true;

        IdlePanel.Visibility = Visibility.Collapsed;
        ResultsPanel.Visibility = Visibility.Collapsed;
        RunningPanel.Visibility = Visibility.Visible;
        GapsText.Text = "—";
        AnalysisTextBox.Text = "Sessionen kører. Analysen beregnes, når sessionen afsluttes.";

        StartSessionButton.IsEnabled = false;
        EndSessionButton.IsEnabled = true;
        SaveAsRouteButton.IsEnabled = false;
        RouteComboBox.IsEnabled = false;
        CorridorTextBox.IsEnabled = false;
        SessionStatusText.Text = _routeUsedInSession is null
            ? "Session i gang — ingen referencerute valgt"
            : $"Session i gang — måles mod \"{_routeUsedInSession.Name}\"";
    }

    private void EndSessionButton_Click(object sender, RoutedEventArgs e)
    {
        if (!_isSessionActive || !_sessionStartedAt.HasValue) return;

        var endedAt = DateTimeOffset.UtcNow;
        _isSessionActive = false;
        StartSessionButton.IsEnabled = true;
        EndSessionButton.IsEnabled = false;
        RouteComboBox.IsEnabled = true;
        CorridorTextBox.IsEnabled = true;
        SessionStatusText.Text = "Session afsluttet";

        bool connectionLost = !_receiver.IsListening;

        var result = SessionAnalyzer.Analyze(
            _sessionStartedAt.Value, endedAt, _sessionMessages, _routeUsedInSession,
            connectionLost, ReadFilterSettings());

        var session = new SessionRecord
        {
            StartedAtUtc = _sessionStartedAt.Value,
            EndedAtUtc = endedAt,
            ReferenceRouteId = _routeUsedInSession?.Id,
            ReferenceRouteName = _routeUsedInSession?.Name,
            ReferenceRoute = _routeUsedInSession,
            CorridorMm = result.CorridorMm,
            Measurements = new List<SensorMessage>(_sessionMessages),
            Analysis = result
        };

        ShowResult(session, result);
        SaveAsRouteButton.IsEnabled = result.PositionSamples >= 2;

        try
        {
            string path = SessionStore.SaveSession(session);
            AnalysisTextBox.Text += Environment.NewLine + Environment.NewLine +
                                    "Rådata og analyse er gemt i:" + Environment.NewLine + path;
        }
        catch (Exception ex)
        {
            AnalysisTextBox.Text += Environment.NewLine + Environment.NewLine +
                                    "Sessionen kunne ikke gemmes: " + ex.Message;
        }
    }

    private void OpenSessionButton_Click(object sender, RoutedEventArgs e)
    {
        if (_isSessionActive)
        {
            MessageBox.Show("Afslut den igangværende session, før du åbner en gemt session.");
            return;
        }

        Directory.CreateDirectory(SessionStore.SessionsFolder);
        var dialog = new Microsoft.Win32.OpenFileDialog
        {
            Title = "Åbn gemt session",
            InitialDirectory = SessionStore.SessionsFolder,
            Filter = "Session (*.json)|*.json"
        };
        if (dialog.ShowDialog() != true) return;

        try
        {
            var session = SessionStore.LoadSession(dialog.FileName);

            // Analysen beregnes forfra ud fra rådata. Giver den samme værdier som ved
            // optagelsen, er FR-05 eftervist og ikke blot påstået.
            var result = SessionAnalyzer.Analyze(
                session.StartedAtUtc, session.EndedAtUtc, session.Measurements,
                session.ReferenceRoute, false, ReadFilterSettings());

            SaveAsRouteButton.IsEnabled = result.PositionSamples >= 2;
            SessionStatusText.Text = $"Indlæst: {Path.GetFileName(dialog.FileName)}";
            ShowResult(session, result);
        }
        catch (Exception ex)
        {
            MessageBox.Show($"Kunne ikke indlæse sessionen: {ex.Message}");
        }
    }

    private void OpenFolderButton_Click(object sender, RoutedEventArgs e)
    {
        Directory.CreateDirectory(SessionStore.RootFolder);
        Process.Start(new ProcessStartInfo
        {
            FileName = SessionStore.RootFolder,
            UseShellExecute = true
        });
    }

    // =====================================================================
    // Visning af resultatet
    // =====================================================================

    private void ShowResult(SessionRecord session, AnalysisResult result)
    {
        _lastSession = session;
        _lastResult = result;

        RunningPanel.Visibility = Visibility.Collapsed;
        IdlePanel.Visibility = Visibility.Collapsed;
        ResultsPanel.Visibility = Visibility.Visible;

        // Statusbanner
        StatusBannerTitle.Text = SessionAnalyzer.Status(result.Status);
        StatusBannerText.Text = result.StatusReason;
        (StatusBanner.Background, StatusBanner.BorderBrush) = result.Status switch
        {
            SessionStatus.Completed => (Brush("#E6F4EA"), Brush("#82B366")),
            SessionStatus.Partial => (Brush("#FFF4D6"), Brush("#D6B656")),
            _ => (Brush("#F8CECC"), Brush("#B85450"))
        };

        // De fem værdier i FR-09. En session, der ikke kan vurderes, får ingen feedback (NFR-R-02).
        bool show = result.MayShowFeedback;
        MeanDeviationValue.Text = show ? Fmt(result.MeanDeviationMm) : "—";
        MaxDeviationValue.Text = show ? Fmt(result.MaxDeviationMm) : "—";
        InCorridorValue.Text = show && result.TimeInCorridorRatio.HasValue
            ? $"{result.TimeInCorridorRatio.Value * 100:F0}"
            : "—";
        CorridorNote.Text = $"% · korridor {result.CorridorMm:F0} mm";
        DurationValue.Text = $"{result.Duration.TotalSeconds:F1}";
        SmoothnessValue.Text = show ? Fmt(result.SmoothnessSpeedCv) : "—";

        // Figurerne og dokumentationen af filtreringen
        DrawPlots();
        ShowFilterSummary(result.Filter);

        // Den udførte rute i 3D tegnes først nu.
        RebuildTrajectory(session.Measurements);

        // Datagab
        GapsText.Text = result.Gaps.Count == 0
            ? "Ingen datagab registreret."
            : string.Join(Environment.NewLine,
                result.Gaps.Take(12).Select(g =>
                    $"{g.StartUtc:HH:mm:ss} → {g.EndUtc:HH:mm:ss}  ({g.DurationSeconds:F2} s)  {g.Reason}"))
              + (result.Gaps.Count > 12 ? Environment.NewLine + $"… og {result.Gaps.Count - 12} flere." : "");

        // Målingstabellen
        _rows.Clear();
        foreach (var m in session.Measurements.OrderByDescending(m => m.EffectiveTimestampUtc).Take(MaxVisibleRows))
            _rows.Add(MeasurementRow.From(m));

        AnalysisTextBox.Text = SessionAnalyzer.Format(result);
    }

    private void DrawPlots()
    {
        if (_lastResult is null || _lastSession is null) return;

        var recorded = BuildPath(_lastSession, _lastResult);
        Plots.DrawRouteOverlay(RouteCanvas, recorded, _lastSession.ReferenceRoute);
        Plots.DrawDeviationChart(DeviationCanvas, _lastResult, _lastSession.StartedAtUtc);
        Plots.DrawFilterComparison(FilterCanvas, _lastResult.Filter);
    }

    private static List<PathPoint> BuildPath(SessionRecord session, AnalysisResult result)
    {
        if (result.Deviations.Count > 0)
            return result.Deviations
                .Select(d => new PathPoint(d.XMm, d.YMm, d.InsideCorridor))
                .ToList();

        // Uden referencerute er der ingen korridor at farve efter; ruten vises alligevel.
        return session.Measurements
            .Where(m => m.HasPositionXY)
            .OrderBy(m => m.EffectiveTimestampUtc)
            .Select(m => new PathPoint(m.X!.Value, m.Y!.Value, true))
            .ToList();
    }

    private void RouteCanvas_SizeChanged(object sender, SizeChangedEventArgs e) => DrawPlots();

    private void DeviationCanvas_SizeChanged(object sender, SizeChangedEventArgs e) => DrawPlots();

    private void FilterCanvas_SizeChanged(object sender, SizeChangedEventArgs e) => DrawPlots();

    private static string Fmt(double? value) => value.HasValue ? value.Value.ToString("F1") : "—";

    private static SolidColorBrush Brush(string hex) =>
        (SolidColorBrush)new BrushConverter().ConvertFromString(hex)!;

    private static ReferenceRoute CloneWithCorridor(ReferenceRoute route, double corridorMm) => new()
    {
        Id = route.Id,
        Name = route.Name,
        CreatedUtc = route.CreatedUtc,
        CorridorMm = corridorMm,
        Points = route.Points
    };

    // =====================================================================
    // 3D-visning
    // =====================================================================

    private void Build3DScene()
    {
        var scene = new Model3DGroup();
        scene.Children.Add(Box(new Point3D(0, -0.025, 0), new Size3D(4, 0.04, 4), Color.FromRgb(226, 232, 240)));
        // Referenceakser: X rød, Y grøn, Z blå. De er visuelle holdepunkter, ikke kalibrerede verdensakser.
        scene.Children.Add(Box(new Point3D(1.8, 0.01, 0), new Size3D(3.6, 0.025, 0.025), Colors.IndianRed));
        scene.Children.Add(Box(new Point3D(0, 1.8, 0), new Size3D(0.025, 3.6, 0.025), Colors.SeaGreen));
        scene.Children.Add(Box(new Point3D(0, 0.01, 1.8), new Size3D(0.025, 0.025, 3.6), Colors.SteelBlue));
        for (int i = -4; i <= 4; i++)
        {
            scene.Children.Add(Box(new Point3D(i * 0.4, 0.002, 0), new Size3D(0.008, 0.008, 3.2), Color.FromRgb(205, 213, 224)));
            scene.Children.Add(Box(new Point3D(0, 0.002, i * 0.4), new Size3D(3.2, 0.008, 0.008), Color.FromRgb(205, 213, 224)));
        }
        SceneVisual.Content = scene;

        var probe = new Model3DGroup();
        probe.Children.Add(Box(new Point3D(0, 0.45, 0), new Size3D(0.18, 0.9, 0.16), Color.FromRgb(40, 105, 180)));
        probe.Children.Add(Box(new Point3D(0, 0.98, 0), new Size3D(0.11, 0.18, 0.11), Color.FromRgb(80, 170, 220)));
        probe.Children.Add(Box(new Point3D(0, -0.12, 0), new Size3D(0.28, 0.12, 0.22), Color.FromRgb(30, 70, 120)));
        ProbeModelVisual.Content = probe;
    }

    private static GeometryModel3D Box(Point3D center, Size3D size, Color color)
    {
        double x0 = center.X - size.X / 2, x1 = center.X + size.X / 2;
        double y0 = center.Y - size.Y / 2, y1 = center.Y + size.Y / 2;
        double z0 = center.Z - size.Z / 2, z1 = center.Z + size.Z / 2;
        var points = new Point3DCollection
        {
            new(x0,y0,z0), new(x1,y0,z0), new(x1,y1,z0), new(x0,y1,z0),
            new(x0,y0,z1), new(x1,y0,z1), new(x1,y1,z1), new(x0,y1,z1)
        };
        int[] indices = { 0,2,1, 0,3,2, 4,5,6, 4,6,7, 0,1,5, 0,5,4,
                          3,7,6, 3,6,2, 1,2,6, 1,6,5, 0,4,7, 0,7,3 };
        var mesh = new MeshGeometry3D { Positions = points, TriangleIndices = new Int32Collection(indices) };
        var material = new DiffuseMaterial(new SolidColorBrush(color));
        return new GeometryModel3D(mesh, material) { BackMaterial = material };
    }

    /// <summary>
    /// Erstatter det levende spor med hele den registrerede rute, naar sessionen er slut.
    /// Under sessionen vises kun de seneste punkter; her vises det hele.
    /// </summary>
    private void RebuildTrajectory(IReadOnlyList<SensorMessage> measurements)
    {
        var positions = measurements.Where(m => m.HasPositionXY)
                                    .OrderBy(m => m.EffectiveTimestampUtc)
                                    .Select(m => (m.X!.Value, m.Y!.Value))
                                    .ToList();
        if (positions.Count > 0) _positionOrigin = positions[0];

        // Efter sessionen tegnes hele ruten med alle maalte punkter, uden udtynding.
        var points = positions.Count > TrajectoryMaxPoints * 2
            ? Thin(positions, positions.Count / TrajectoryMaxPoints)
            : positions;
        DrawTrajectory(points);
    }

    /// <summary>
    /// Foejer et punkt til sporet over den udfoerte rute. Det er probens position og
    /// bevaegelse - ikke en vurdering af dem - og det er netop det, FR-08 tillader at
    /// vise undervejs.
    /// </summary>
    private void TrackMovement(double x, double y)
    {
        if (_trajectoryPoints.Count > 0)
        {
            var last = _trajectoryPoints[^1];
            double dx = x - last.X, dy = y - last.Y;
            if (Math.Sqrt(dx * dx + dy * dy) < _trajectoryStepMm) return;
        }

        _positionOrigin ??= (x, y);
        _trajectoryPoints.Add((x, y));

        if (_trajectoryPoints.Count > TrajectoryMaxPoints)
        {
            // Ruten bevares i hele sin laengde; den tegnes bare med halvt saa mange punkter.
            _trajectoryStepMm *= 2;
            var thinned = new List<(double X, double Y)>(_trajectoryPoints.Count / 2 + 1);
            for (int i = 0; i < _trajectoryPoints.Count; i += 2) thinned.Add(_trajectoryPoints[i]);
            _trajectoryPoints.Clear();
            _trajectoryPoints.AddRange(thinned);
            DrawTrajectory(_trajectoryPoints);
            return;
        }

        _trajectoryGroup.Children.Add(Marker(x, y));
    }

    /// <summary>Tegner hele sporet forfra.</summary>
    private void DrawTrajectory(IReadOnlyList<(double X, double Y)> points)
    {
        _trajectoryGroup = new Model3DGroup();
        if (points.Count > 0)
        {
            _positionOrigin ??= points[0];
            foreach (var point in points) _trajectoryGroup.Children.Add(Marker(point.X, point.Y));
        }
        TrajectoryVisual.Content = _trajectoryGroup;
    }

    /// <summary>Et enkelt punkt paa sporet, placeret relativt til sessionens foerste position.</summary>
    private GeometryModel3D Marker(double xMm, double yMm)
    {
        double originX = _positionOrigin?.X ?? 0, originY = _positionOrigin?.Y ?? 0;
        double x = Math.Clamp((xMm - originX) * 0.01, -1.5, 1.5);
        double z = Math.Clamp((yMm - originY) * 0.01, -1.5, 1.5);
        return Box(new Point3D(x, 0.025, z), new Size3D(0.045, 0.035, 0.045),
                   Color.FromRgb(80, 170, 220));
    }

    private static List<(double X, double Y)> Thin(List<(double X, double Y)> points, int step)
    {
        var result = new List<(double X, double Y)>(points.Count / step + 2);
        for (int i = 0; i < points.Count; i += step) result.Add(points[i]);
        if (points.Count > 0) result.Add(points[^1]);
        return result;
    }

    private void UpdateProbeVisual()
    {
        var angles = _latestOrientationMessage?.GetEulerDegrees();
        double ax = angles?.X ?? 0, ay = angles?.Y ?? 0, az = angles?.Z ?? 0;
        double px = 0, py = 0, pz = 0;

        var position = _latestPositionMessage;
        if (position?.HasPositionXY == true)
        {
            _positionOrigin ??= (position.X!.Value, position.Y!.Value);
            // Visningen er bevidst relativ og skaleret til vinduet. Den er ikke en kalibreret 3D-position.
            px = Math.Clamp((position.X!.Value - _positionOrigin.Value.X) * 0.01, -1.5, 1.5);
            pz = Math.Clamp((position.Y!.Value - _positionOrigin.Value.Y) * 0.01, -1.5, 1.5);
            if (position.Z.HasValue) py = Math.Clamp(position.Z.Value * 0.01, -1.0, 1.0);
        }

        var transforms = new Transform3DGroup();
        transforms.Children.Add(new RotateTransform3D(new AxisAngleRotation3D(new Vector3D(1, 0, 0), ax)));
        transforms.Children.Add(new RotateTransform3D(new AxisAngleRotation3D(new Vector3D(0, 1, 0), ay)));
        transforms.Children.Add(new RotateTransform3D(new AxisAngleRotation3D(new Vector3D(0, 0, 1), az)));
        transforms.Children.Add(new TranslateTransform3D(px, py, pz));
        if (ProbeModelVisual.Content is Model3D model) model.Transform = transforms;
    }

    // =====================================================================

    private sealed class MeasurementRow
    {
        public DateTimeOffset TimestampUtc { get; init; }
        public string Source { get; init; } = "ukendt";
        public long? SequenceNumber { get; init; }
        public double? X { get; init; }
        public double? Y { get; init; }
        public string Angles { get; init; } = "—";
        public string Force { get; init; } = "—";

        public static MeasurementRow From(SensorMessage m)
        {
            var angles = m.GetEulerDegrees();
            return new MeasurementRow
            {
                TimestampUtc = m.EffectiveTimestampUtc,
                // En samlet besked fra Pi'en bærer flere signaler på én gang.
                Source = GapDetector.Describe(m),
                SequenceNumber = m.SequenceNumber,
                X = m.X,
                Y = m.Y,
                Angles = angles.HasValue
                    ? $"{angles.Value.X:F1} / {angles.Value.Y:F1} / {angles.Value.Z:F1}°"
                    : "—",
                Force = m.Pressure.HasValue ? $"{m.Pressure.Value:F2} N" : "—"
            };
        }
    }
}
