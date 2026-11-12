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
    // Lag 2. Vinduet taler kun med sessionstjenesten, ikke med modtager eller lagring.
    private readonly SessionService _session = new();
    private readonly ObservableCollection<MeasurementRow> _rows = new();

    private SensorMessage? _latestPositionMessage;
    private SensorMessage? _latestOrientationMessage;

    /// <summary>
    /// Afstanden mellem hjørnemarkørernes centre i mm. Skal være den samme som SIDE_MM i
    /// sensor_stream_tracking.py på Pi'en (260 mm efter rettelsen i MT3.4). Pi'en giver
    /// positionen i mm med markør ID 0 i (0, 0) og ID 2 i (side, side).
    /// </summary>
    private const double BoardSideMm = 260.0;

    /// <summary>3D-enheder pr. mm. Brættet i scenen er 4 × 4 enheder, kvadratet 2,6 × 2,6.</summary>
    private const double SceneScale = 0.01;

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
    private AnalysisResult? _lastResult;
    private SessionRecord? _lastSession;

    private const int MaxVisibleRows = 400;

    public MainWindow()
    {
        InitializeComponent();
        MeasurementsGrid.ItemsSource = _rows;
        Build3DScene();
        LoadRoutes();
        Closing += async (_, _) => await _session.DisposeAsync();
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
            await _session.StartListeningAsync(port, HandleMessageAsync);
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
        await _session.StopListeningAsync();
        ListenButton.IsEnabled = true;
        StopListenButton.IsEnabled = false;
        PortTextBox.IsEnabled = true;
        StatusText.Text = "Modtager ikke";
        ConnectionIndicator.Fill = Brushes.Gray;
    }

    /// <summary>
    /// Kaldes af sessionstjenesten for hver modtaget pakke. SessionService gemmer selv
    /// målingen under en session; her opdateres kun probens stilling i 3D. Der skrives
    /// ingen værdier, ingen graf og ingen vurdering til skærmen før sessionen er afsluttet (FR-08).
    /// </summary>
    private Task HandleMessageAsync(SensorMessage message)
    {
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

            if (_session.IsSessionActive && _session.IsBufferFull)
                SessionStatusText.Text = "Grænsen for antal målinger er nået — afslut sessionen.";
        }).Task;
    }

    // =====================================================================
    // Referenceruter
    // =====================================================================

    private void LoadRoutes()
    {
        var routes = _session.LoadRoutes();
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

        Directory.CreateDirectory(SessionService.RoutesFolder);
        var dialog = new Microsoft.Win32.SaveFileDialog
        {
            Title = "Gem sessionen som referencerute",
            InitialDirectory = SessionService.RoutesFolder,
            FileName = $"rute_{DateTime.Now:yyyyMMdd_HHmm}",
            DefaultExt = ".json",
            Filter = "Referencerute (*.json)|*.json"
        };
        if (dialog.ShowDialog() != true) return;

        string name = Path.GetFileNameWithoutExtension(dialog.FileName);
        var route = _session.SaveSessionAsRoute(_lastSession, name, ReadCorridorMm(), dialog.FileName);

        if (route is null)
        {
            MessageBox.Show("Sessionen indeholder for få positionsmålinger til at danne en referencerute.");
            return;
        }

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
        try
        {
            // Korridoren hører til ruten. Ændrer brugeren feltet, gælder ændringen for denne session.
            _session.StartSession(_selectedRoute, ReadCorridorMm());
        }
        catch (InvalidOperationException ex)
        {
            MessageBox.Show(ex.Message);
            return;
        }

        _rows.Clear();
        _latestPositionMessage = null;
        _latestOrientationMessage = null;
        _lastResult = null;
        _lastSession = null;
        _trajectoryPoints.Clear();
        _trajectoryStepMm = TrajectoryInitialStepMm;
        _trajectoryGroup = new Model3DGroup();
        TrajectoryVisual.Content = _trajectoryGroup;
        UpdateProbeVisual();

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
        var activeRoute = _session.ActiveRoute;
        SessionStatusText.Text = activeRoute is null
            ? "Session i gang — ingen referencerute valgt"
            : $"Session i gang — måles mod \"{activeRoute.Name}\"";
    }

    private void EndSessionButton_Click(object sender, RoutedEventArgs e)
    {
        if (!_session.IsSessionActive) return;

        // Tiden fra afslutning til visning af feedback måles, så NFR-P-01 (SyT-05) kan
        // aflæses direkte i stedet for med stopur.
        var clock = Stopwatch.StartNew();

        // Sessionstjenesten analyserer og gemmer; vinduet viser kun resultatet.
        var outcome = _session.EndSession(ReadFilterSettings());

        StartSessionButton.IsEnabled = true;
        EndSessionButton.IsEnabled = false;
        RouteComboBox.IsEnabled = true;
        CorridorTextBox.IsEnabled = true;
        SessionStatusText.Text = "Session afsluttet";

        ShowResult(outcome.Session, outcome.Result);
        SaveAsRouteButton.IsEnabled = outcome.Result.PositionSamples >= 2;

        AnalysisTextBox.Text += Environment.NewLine + Environment.NewLine +
            (outcome.SavedPath is not null
                ? "Rådata og analyse er gemt i:" + Environment.NewLine + outcome.SavedPath
                : "Sessionen kunne ikke gemmes: " + outcome.SaveError);

        // Måles først, når vinduet har tegnet resultatet færdigt.
        Dispatcher.InvokeAsync(() =>
        {
            clock.Stop();
            string timing = $"Feedback vist {clock.Elapsed.TotalSeconds:F2} s efter, at sessionen blev afsluttet " +
                            $"(NFR-P-01, grænse {SessionAnalyzer.MaxFeedbackSeconds:F0} s).";
            SessionStatusText.Text = "Session afsluttet. " + timing;
            AnalysisTextBox.Text += Environment.NewLine + Environment.NewLine + timing;
        }, System.Windows.Threading.DispatcherPriority.ContextIdle);
    }

    private void OpenSessionButton_Click(object sender, RoutedEventArgs e)
    {
        if (_session.IsSessionActive)
        {
            MessageBox.Show("Afslut den igangværende session, før du åbner en gemt session.");
            return;
        }

        Directory.CreateDirectory(SessionService.SessionsFolder);
        var dialog = new Microsoft.Win32.OpenFileDialog
        {
            Title = "Åbn gemt session",
            InitialDirectory = SessionService.SessionsFolder,
            Filter = "Session (*.json)|*.json"
        };
        if (dialog.ShowDialog() != true) return;

        try
        {
            // Analysen beregnes forfra ud fra rådata (FR-05).
            var (session, result) = _session.OpenSession(dialog.FileName, ReadFilterSettings());

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
        Directory.CreateDirectory(SessionService.RootFolder);
        Process.Start(new ProcessStartInfo
        {
            FileName = SessionService.RootFolder,
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

        // Datagab. Enkelte tabte pakker kan være mange og korte; de opsummeres på én linje,
        // så de længere gab (dækket markør, afbrudt forbindelse) ikke forsvinder i listen.
        // Hvert enkelt står med løbenummer i den gemte session og i analyseteksten.
        var longGaps = result.Gaps.Where(g => g.Source != MeasurementSource.Transfer).ToList();
        var lines = longGaps.Take(12).Select(g =>
            $"{g.StartUtc:HH:mm:ss} → {g.EndUtc:HH:mm:ss}  ({g.DurationSeconds:F2} s)  {g.Reason}").ToList();
        if (longGaps.Count > 12) lines.Add($"… og {longGaps.Count - 12} flere.");
        if (result.PacketLossGapCount > 0)
            lines.Add($"Pakketab: {result.PacketLossGapCount} huller i løbenumrene, " +
                      $"{result.MaxPacketLossRatio * 100:F2} % af beskederne (NFR-R-01).");
        GapsText.Text = lines.Count == 0 ? "Ingen datagab registreret." : string.Join(Environment.NewLine, lines);

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
        Plots.DrawFilterComparison(FilterCanvas, _lastResult.Filter);

        // En session, der ikke kan vurderes, får hverken tal eller grafer for afvigelse
        // og jævnhed (NFR-R-02) — de samme værdier, som kortene ovenfor skjuler.
        if (_lastResult.MayShowFeedback)
        {
            Plots.DrawDeviationChart(DeviationCanvas, _lastResult, _lastSession.StartedAtUtc);
            Plots.DrawSpeedChart(SpeedCanvas, _lastResult, _lastSession.StartedAtUtc);
        }
        else
        {
            string reason = $"Ingen vurdering: sessionen er {SessionAnalyzer.Status(_lastResult.Status).ToLower()} (NFR-R-02).";
            Plots.ShowMessage(DeviationCanvas, reason);
            Plots.ShowMessage(SpeedCanvas, reason);
        }
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

    private void SpeedCanvas_SizeChanged(object sender, SizeChangedEventArgs e) => DrawPlots();

    private static string Fmt(double? value) => value.HasValue ? value.Value.ToString("F1") : "—";

    private static SolidColorBrush Brush(string hex) =>
        (SolidColorBrush)new BrushConverter().ConvertFromString(hex)!;

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

        // Det kalibrerede kvadrat og de fire hjørnemarkører, så probens placering kan ses i
        // forhold til dem. ID 0 er rød: det er (0, 0) i Pi'ens koordinater.
        // Hjørnerne sorteres, så bredden altid er positiv, uanset hvilken vej akserne vender.
        var (ax, az) = ToScene(0, 0);
        var (bx, bz) = ToScene(BoardSideMm, BoardSideMm);
        double minX = Math.Min(ax, bx), maxX = Math.Max(ax, bx);
        double minZ = Math.Min(az, bz), maxZ = Math.Max(az, bz);
        var edge = Color.FromRgb(120, 132, 150);
        scene.Children.Add(Box(new Point3D((minX + maxX) / 2, 0.006, minZ), new Size3D(maxX - minX, 0.01, 0.015), edge));
        scene.Children.Add(Box(new Point3D((minX + maxX) / 2, 0.006, maxZ), new Size3D(maxX - minX, 0.01, 0.015), edge));
        scene.Children.Add(Box(new Point3D(minX, 0.006, (minZ + maxZ) / 2), new Size3D(0.015, 0.01, maxZ - minZ), edge));
        scene.Children.Add(Box(new Point3D(maxX, 0.006, (minZ + maxZ) / 2), new Size3D(0.015, 0.01, maxZ - minZ), edge));
        foreach (var (mx, my, color) in new[]
                 {
                     (0.0, 0.0, Color.FromRgb(200, 40, 40)),
                     (BoardSideMm, 0.0, Color.FromRgb(30, 30, 30)),
                     (BoardSideMm, BoardSideMm, Color.FromRgb(30, 30, 30)),
                     (0.0, BoardSideMm, Color.FromRgb(30, 30, 30))
                 })
        {
            var (sx, sz) = ToScene(mx, my);
            scene.Children.Add(Box(new Point3D(sx, 0.012, sz), new Size3D(0.16, 0.02, 0.16), color));
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
        foreach (var point in points) _trajectoryGroup.Children.Add(Marker(point.X, point.Y));
        TrajectoryVisual.Content = _trajectoryGroup;
    }

    /// <summary>Et enkelt punkt paa sporet, placeret der hvor det blev maalt paa braettet.</summary>
    private static GeometryModel3D Marker(double xMm, double yMm)
    {
        var (x, z) = ToScene(xMm, yMm);
        return Box(new Point3D(x, 0.025, z), new Size3D(0.045, 0.035, 0.045),
                   Color.FromRgb(80, 170, 220));
    }

    /// <summary>
    /// Omregner en position i mm fra Pi'en til scenens koordinater. Midten af det kalibrerede
    /// kvadrat ligger i midten af braettet, saa proben vises der, hvor den faktisk er i
    /// forhold til hjoernemarkoererne. Positioner uden for braettet holdes ved kanten.
    /// x er vendt, fordi Pi'ens x-akse peger den modsatte vej af scenens, set fra brugeren
    /// (afproevet med den rigtige opstilling 12/11).
    /// </summary>
    private static (double X, double Z) ToScene(double xMm, double yMm) =>
        (Math.Clamp(-(xMm - BoardSideMm / 2) * SceneScale, -1.9, 1.9),
         Math.Clamp((yMm - BoardSideMm / 2) * SceneScale, -1.9, 1.9));

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
            // Placeret i forhold til hjoernemarkoererne. Hoejden (y) maales ikke; proben staar paa braettet.
            (px, pz) = ToScene(position.X!.Value, position.Y!.Value);
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
