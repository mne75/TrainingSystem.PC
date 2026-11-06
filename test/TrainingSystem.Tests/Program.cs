using TrainingSystem.PC.Models;
using TrainingSystem.PC.Services;
using TrainingSystem.Tests;

// Uden argumenter koeres systemtestene. Med --listen lyttes der i stedet paa UDP-porten,
// saa opsaetningen af hardwaren kan fejlsoeges uden brugergraensefladen.
if (args.Contains("--listen"))
    return await Listen.RunAsync(args);

int failures = 0;

void Check(string name, bool ok, string detail = "")
{
    Console.WriteLine($"{(ok ? "OK  " : "FEJL")}  {name}{(detail.Length > 0 ? "  -> " + detail : "")}");
    if (!ok) failures++;
}

var start = new DateTimeOffset(2026, 11, 6, 10, 0, 0, TimeSpan.Zero);

// En lige referencerute fra (0,0) til (200,0), korridor 15 mm.
var route = new ReferenceRoute
{
    Name = "Testrute",
    CorridorMm = 15,
    Points =
    {
        new RoutePoint { Index = 0, XMm = 0,   YMm = 0 },
        new RoutePoint { Index = 1, XMm = 200, YMm = 0 }
    }
};

// --- Test 1: kendt afvigelse genfindes (svarer til systemtest T-06) ---------
// 101 positionsmålinger ved 40 Hz. Første halvdel ligger på ruten, anden halvdel
// er forskudt 20 mm - altså uden for korridoren på 15 mm.
var m1 = new List<SensorMessage>();
for (int i = 0; i <= 100; i++)
{
    m1.Add(new SensorMessage
    {
        Type = "position",
        X = i * 2.0,
        Y = i < 50 ? 0.0 : 20.0,
        SequenceNumber = i + 1,
        TimestampUtc = start.AddMilliseconds(i * 25)
    });
}

var r1 = SessionAnalyzer.Analyze(start, start.AddMilliseconds(2500), m1, route);

Check("Status er Gennemfoert", r1.Status == SessionStatus.Completed, r1.StatusReason);
Check("Stoerste afvigelse = 20 mm", Math.Abs(r1.MaxDeviationMm!.Value - 20.0) < 0.001,
      $"{r1.MaxDeviationMm:F3}");
Check("Middelafvigelse ca. 10,1 mm", Math.Abs(r1.MeanDeviationMm!.Value - 10.099) < 0.01,
      $"{r1.MeanDeviationMm:F3}");
Check("Andel i korridor ca. 50 %", Math.Abs(r1.TimeInCorridorRatio!.Value - 50.0 / 101.0) < 0.001,
      $"{r1.TimeInCorridorRatio * 100:F1} %");
Check("Positionsmaalinger = 101", r1.PositionSamples == 101, r1.PositionSamples.ToString());
Check("Ingen huller i positionsstroemmen",
      !r1.Gaps.Any(g => g.Source == MeasurementSource.Position),
      $"{r1.Gaps.Count(g => g.Source == MeasurementSource.Position)} gab");
// Orienteringskilden leverede intet. Det skal staa som et datagab, men maa ikke
// taelle med i gabandelen, der afgoer om sessionen kan vurderes.
Check("Manglende kilde noteret som gab",
      r1.Gaps.Any(g => g.Source == MeasurementSource.Orientation));
Check("Manglende kilde taeller ikke i gabandelen", Math.Abs(r1.GapRatio) < 1e-9,
      $"{r1.GapRatio * 100:F1} %");

// --- Test 2: afstand maales til ruten som kurve, ikke til naermeste punkt ----
// Et punkt midt mellem to rutepunkter, 5 mm ved siden af. Maales der til naermeste
// RUTEPUNKT, bliver svaret ~100 mm. Maales der til linjestykket, bliver det 5 mm.
double d = RouteComparer.DistanceToRoute(100, 5, route.Points);
Check("Afstand til linjestykke = 5 mm", Math.Abs(d - 5.0) < 1e-9, $"{d:F3} mm");

// --- Test 3: datagab over 25 % goer sessionen ikke-vurderbar (NFR-R-02) -----
// 10 sekunders session. Maalinger i de foerste 4 s, derefter intet.
var m3 = new List<SensorMessage>();
for (int i = 0; i < 160; i++)
    m3.Add(new SensorMessage
    {
        Type = "position", X = i * 0.5, Y = 0,
        SequenceNumber = i + 1,
        TimestampUtc = start.AddMilliseconds(i * 25)
    });
for (int i = 0; i < 400; i++)
    m3.Add(new SensorMessage
    {
        Type = "orientation", Qx = 0, Qy = 0, Qz = 0, Qw = 1,
        SequenceNumber = i + 1,
        TimestampUtc = start.AddMilliseconds(i * 25)
    });

var r3 = SessionAnalyzer.Analyze(start, start.AddSeconds(10), m3, route);
Check("Stort datagab -> ikke vurderbar", r3.Status == SessionStatus.NotAssessable,
      $"{r3.Status}, gab {r3.GapRatio * 100:F0} %");
Check("Feedback maa ikke vises", !r3.MayShowFeedback);
Check("Gab registreret", r3.GapCount > 0, $"{r3.GapCount} gab paa {r3.GapSeconds:F1} s");

// --- Test 4: pakketab opdages via loebenumre (NFR-R-01) ---------------------
var m4 = new List<SensorMessage>();
long seq = 0;
for (int i = 0; i < 200; i++)
{
    seq++;
    if (i % 50 == 0 && i > 0) seq += 3;   // tre tabte pakker
    m4.Add(new SensorMessage
    {
        Type = "position", X = i * 1.0, Y = 0,
        SequenceNumber = seq,
        TimestampUtc = start.AddMilliseconds(i * 25)
    });
}
var r4 = SessionAnalyzer.Analyze(start, start.AddSeconds(5), m4, route);
Check("Pakketab opdaget", r4.PositionLossRatio > 0, $"{r4.PositionLossRatio * 100:F2} %");

// --- Test 5: referencerute fra en session (FR-07) ---------------------------
var created = ReferenceRoute.FromMeasurements("Fra session", m1, start, 15);
Check("Rute dannet fra session", created.IsUsable, $"{created.Points.Count} punkter");

// Taet maalte punkter skal tyndes ud, saa en rute ikke fylder mere end noedvendigt.
var dense = new List<SensorMessage>();
for (int i = 0; i < 400; i++)
    dense.Add(new SensorMessage
    {
        Type = "position", X = i * 0.5, Y = 0,
        SequenceNumber = i + 1,
        TimestampUtc = start.AddMilliseconds(i * 25)
    });
var thinned = ReferenceRoute.FromMeasurements("Taet", dense, start, 15);
Check("Rutepunkter tyndet ud", thinned.Points.Count < dense.Count / 2,
      $"{thinned.Points.Count} af {dense.Count}");
Check("Ruten daekker stadig hele straekningen",
      Math.Abs(thinned.Points[^1].XMm - dense[^1].X!.Value) < 2.1,
      $"slutter i x={thinned.Points[^1].XMm:F1}, data slutter i x={dense[^1].X:F1}");

// --- Test 6: ingen referencerute -> ingen afvigelse, men stadig gennemfoert -
var r6 = SessionAnalyzer.Analyze(start, start.AddMilliseconds(2500), m1, null);
Check("Uden rute: ingen middelafvigelse", r6.MeanDeviationMm is null);
Check("Uden rute: stadig vejlaengde", r6.PathLengthMm is > 0, $"{r6.PathLengthMm:F1} mm");
Check("Uden rute: status gennemfoert", r6.Status == SessionStatus.Completed);

// --- Test 7: samme raadata giver samme resultat (FR-05) ---------------------
var again = SessionAnalyzer.Analyze(start, start.AddMilliseconds(2500), m1, route);
Check("Genberegning giver samme tal",
      again.MeanDeviationMm == r1.MeanDeviationMm &&
      again.MaxDeviationMm == r1.MaxDeviationMm &&
      again.TimeInCorridorRatio == r1.TimeInCorridorRatio);


// =====================================================================
// FR-12: stoejfiltrering
// =====================================================================

// Et datasaet "optaget med proben i ro": konstant position plus stoej.
// Det er praecis acceptkriteriets opstilling.
var rng = new Random(42);
var still = new List<SensorMessage>();
for (int i = 0; i < 400; i++)
    still.Add(new SensorMessage
    {
        Type = "position",
        X = 100.0 + (rng.NextDouble() - 0.5) * 4.0,   // +/- 2 mm stoej
        Y = 50.0 + (rng.NextDouble() - 0.5) * 4.0,
        SequenceNumber = i + 1,
        TimestampUtc = start.AddMilliseconds(i * 25)   // 40 Hz
    });

var centred = new FilterSettings { Enabled = true, WindowMs = 100, Mode = FilterMode.Centred };
var (fStill, rStill) = NoiseFilter.Apply(still, centred);

var sx = rStill.Signals.First(s => s.Name == "x");
Check("Filter: spredning reduceret", sx.ReductionRatio > 0.4,
      $"{sx.StdDevBefore:F3} -> {sx.StdDevAfter:F3} ({sx.ReductionRatio * 100:F0} %)");
Check("Filter: antal maalinger uaendret", fStill.Count == still.Count,
      $"{fStill.Count} af {still.Count}");
Check("Filter: ingen forsinkelse paastaas paa ren stoej",
      Math.Abs(rStill.MeasuredDelayMs) < 1e-9, $"{rStill.MeasuredDelayMs:F0} ms");
Check("Filter: hvilesaet markeres som ikke-maalbart", !rStill.DelayMeasurable);
Check("Filter: forsinkelse inden for FR-12", rStill.DelayWithinLimit);

// Kausalt vindue skal forsinke - ellers maaler testen ingenting.
// En langsom sinus giver et signal, krydskorrelationen kan arbejde paa.
var moving = new List<SensorMessage>();
for (int i = 0; i < 400; i++)
{
    double t = i * 0.025;
    moving.Add(new SensorMessage
    {
        Type = "position",
        X = 100.0 + 30.0 * Math.Sin(2 * Math.PI * 0.5 * t) + (rng.NextDouble() - 0.5) * 2.0,
        Y = 50.0,
        SequenceNumber = i + 1,
        TimestampUtc = start.AddMilliseconds(i * 25)
    });
}

// Nulfase maales paa et signal med form. Samme data, centreret vindue.
var (_, rCentredMoving) = NoiseFilter.Apply(moving, centred);
Check("Filter: centreret vindue giver nul forsinkelse",
      Math.Abs(rCentredMoving.MeasuredDelayMs) < 1e-9, $"{rCentredMoving.MeasuredDelayMs:F0} ms");
Check("Filter: bevaegelse er maalbar", rCentredMoving.DelayMeasurable);

var causal = new FilterSettings { Enabled = true, WindowMs = 100, Mode = FilterMode.Causal };
var (_, rCausal) = NoiseFilter.Apply(moving, causal);
Check("Filter: kausalt vindue forsinker", rCausal.MeasuredDelayMs > 0,
      $"{rCausal.MeasuredDelayMs:F0} ms ved {causal.WindowMs:F0} ms vindue");
Check("Filter: kausal forsinkelse ca. halvt vindue",
      Math.Abs(rCausal.MeasuredDelayMs - causal.WindowMs / 2) <= 25.0,
      $"{rCausal.MeasuredDelayMs:F0} ms, forventet ca. {causal.WindowMs / 2:F0} ms");
Check("Filter: kausal forsinkelse inden for FR-12", rCausal.DelayWithinLimit,
      $"{rCausal.MeasuredDelayMs:F0} ms");

// Det vigtigste: filteret maa ikke regne hen over et datagab (FR-10).
// To stykker med 2 sekunders hul imellem, og vidt forskellige niveauer.
var split = new List<SensorMessage>();
for (int i = 0; i < 40; i++)
    split.Add(new SensorMessage { Type = "position", X = 0, Y = 0, SequenceNumber = i + 1,
                                  TimestampUtc = start.AddMilliseconds(i * 25) });
for (int i = 0; i < 40; i++)
    split.Add(new SensorMessage { Type = "position", X = 1000, Y = 0, SequenceNumber = 100 + i,
                                  TimestampUtc = start.AddSeconds(3).AddMilliseconds(i * 25) });

var (fSplit, rSplit) = NoiseFilter.Apply(split, centred);
var ordered = fSplit.OrderBy(m => m.EffectiveTimestampUtc).ToList();
Check("Filter: to stykker fundet", rSplit.SegmentCount == 2, $"{rSplit.SegmentCount} stykker");
Check("Filter: glatter ikke hen over gab",
      Math.Abs(ordered[^1].X!.Value - 1000.0) < 1e-9 && Math.Abs(ordered[0].X!.Value) < 1e-9,
      $"foerste x={ordered[0].X:F1}, sidste x={ordered[^1].X:F1}");

// Filtrering slaaet fra skal give raadata uaendret igennem.
var (fOff, rOff) = NoiseFilter.Apply(still, FilterSettings.Off);
Check("Filter: slaaet fra giver raadata", !rOff.Applied &&
      Math.Abs(fOff[0].X!.Value - still[0].X!.Value) < 1e-9);

// Analysen skal bruge de filtrerede maalinger.
var rFiltered = SessionAnalyzer.Analyze(start, start.AddMilliseconds(2500), m1, route, false, centred);
Check("Analyse: filterrapport med i resultatet", rFiltered.Filter is { Applied: true });

// Proben foeres langs ruten med maalestoej paa. Afstanden er en absolutvaerdi, saa stoej
// goer middelafvigelsen stoerre end den sande. Filtrering skal traekke den ned mod sandheden.
var noisyOnRoute = new List<SensorMessage>();
for (int i = 0; i < 200; i++)
    noisyOnRoute.Add(new SensorMessage
    {
        Type = "position",
        X = i * 1.0,
        Y = (rng.NextDouble() - 0.5) * 8.0,          // sand rute er y = 0
        SequenceNumber = i + 1,
        TimestampUtc = start.AddMilliseconds(i * 25)
    });

var noisyRaw = SessionAnalyzer.Analyze(start, start.AddSeconds(5), noisyOnRoute, route, false, FilterSettings.Off);
var noisyFiltered = SessionAnalyzer.Analyze(start, start.AddSeconds(5), noisyOnRoute, route, false, centred);
Check("Analyse: filtrering reducerer middelafvigelsen",
      noisyFiltered.MeanDeviationMm < noisyRaw.MeanDeviationMm,
      $"ufiltreret {noisyRaw.MeanDeviationMm:F2} mm -> filtreret {noisyFiltered.MeanDeviationMm:F2} mm");
Check("Analyse: filtrering reducerer stoerste afvigelse",
      noisyFiltered.MaxDeviationMm < noisyRaw.MaxDeviationMm,
      $"{noisyRaw.MaxDeviationMm:F2} mm -> {noisyFiltered.MaxDeviationMm:F2} mm");


Console.WriteLine();
Console.WriteLine(failures == 0 ? "ALLE TEST BESTAAET" : $"{failures} TEST FEJLEDE");
Console.WriteLine();
Console.WriteLine("--- eksempel paa analyseudskrift ---");
Console.WriteLine(SessionAnalyzer.Format(r1));
return failures;
