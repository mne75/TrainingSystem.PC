# Opsætning og hardwaretest

Denne vejledning gælder den nuværende brugergrænseflade. `README.md` beskriver den gamle
og er forældet på knapnavne og arbejdsgang.

**Pc'ens IPv4-adresse er lige nu `172.20.10.7`** (Wi-Fi). Den skifter, når I skifter
netværk — tjek med `ipconfig`, hver gang I sætter op.

Bemærk, at 172.20.10.x typisk er et telefon-hotspot. Pi'en og pc'en skal være på
**samme** netværk, og nogle gæstenet blokerer trafik mellem enheder.

---

## 1. Start pc-programmet

1. Åbn `TrainingSystem.PC.sln` i Visual Studio og kør projektet `TrainingSystem.PC`.
2. Tryk **Start modtagelse** (port 5005).
3. Siger Windows Defender god for indgående trafik, så **tillad** det for private netværk.
   Kommer spørgsmålet ikke, og der alligevel ikke kommer pakker ind, er det næsten
   altid firewallen. Den skal åbnes for indgående UDP på port 5005.

Indikatoren til venstre bliver orange ved "modtager" og grøn, når første pakke ankommer.

## 2. Start afsenderne på Raspberry Pi

**Orientering (BNO085):**

```bash
dotnet run -- --headless --host 172.20.10.7 --port 5005
```

**Position (kamera og ArUco):**

```bash
python3 aruco_track.py --size 300 --host 172.20.10.7 --udp 5005
```

`--size` er afstanden i **millimeter mellem centrene** af to nabohjørnemarkører. Mål den;
tallet går direkte ind i alle positionsmålinger, og er det forkert, er alle afstande forkerte.

Trackeren sender først, når alle fire hjørnemarkører har været synlige **og**
probemarkøren er i billedet.

## 3. Første gang: optag en referencerute

Der findes ingen referenceruter, før I laver én. Derfor er første gennemløb altid:

1. **Start session**, før proben ad den rute, øvelsen skal måles imod, **Afslut session**.
2. Tryk **Gem sessionen som referencerute** og giv den et navn.
3. Tryk **Opdatér** ved rullemenuen. Ruten kan nu vælges.

Referenceruten må gerne optages af jer selv — det blev aftalt på vejledermødet 30/10.

## 4. En almindelig træningssession

1. Vælg referencerute i rullemenuen.
2. Sæt korridoren. 15 mm er udgangspunktet; den skal være mindst to til tre gange
   måleusikkerheden, ellers måler I jeres eget støjgulv.
3. **Start session.** Nu vises kun probens bevægelse i 3D — ingen tal, ingen vurdering.
4. Udfør øvelsen.
5. **Afslut session og analysér.**

Resultatet gemmes automatisk i `%LOCALAPPDATA%\TrainingSystem\Sessions`.

---

## Når der ikke kommer noget ind

Brug lytteren i stedet for at gætte. Den bruger nøjagtig de samme klasser som programmet,
men skriver til konsollen, så I kan se, om problemet er netværket eller brugergrænsefladen:

```bash
cd test/TrainingSystem.Tests
dotnet run -- --listen --port 5005 --seconds 30
```

Den tæller pakker pr. sekund, viser hvornår første pakke fra hver kilde ankommer, kører
den rigtige analyse og gemmer sessionen, så den kan åbnes i programmet bagefter med
**Åbn gemt session…**.

Husk at lukke pc-programmet først — to programmer kan ikke lytte på samme port.

Tjekliste, hvis der ikke kommer noget:

- Samme netværk? Prøv `ping 172.20.10.7` fra Pi'en.
- Rigtig IP i `--host`? Ikke `127.0.0.1`, når afsenderen kører på Pi'en.
- Samme portnummer begge steder?
- Firewall på pc'en?
- Kører pc-programmet allerede og optager porten?

---

## Test uden hardware

```bash
cd tools
python send_test_packets.py --host 127.0.0.1
```

Scriptet efterligner de to rigtige strømme hver for sig: orientering ved 100 Hz og
position ved 30 Hz, med hver sin løbenummerserie.

Nyttige tilvalg:

| Tilvalg | Hvad det gør | Hvilket krav det afprøver |
|---|---|---|
| `--occlusion 4 --at 10` | Positionen holder op i 4 s efter 10 s | FR-10, NFR-R-02 |
| `--loss 0.03` | 3 % af positionspakkerne kastes væk | NFR-R-01 |
| `--still --noise 3` | Proben ligger i ro med 3 mm støj | FR-12, støjreduktion |
| `--shape linje` | Ret rute i stedet for cirkel | FR-01, FR-09 |
| `--duration 60` | Stopper af sig selv efter 60 s | |

## FR-12 kræver to optagelser

Acceptkriteriet beder om både en støjreduktion og en forsinkelse under 50 ms. De to ting
kan ikke måles på samme datasæt:

**Støjreduktionen** måles med proben i ro. Prøvet med `--still --noise 3` giver det
cirka 40 % reduktion på selve signalet og 66 % på udsvinget fra måling til måling.

**Forsinkelsen** kan ikke måles på et hvilesæt — der er ingen bevægelse at genkende
forskydningen på, og programmet skriver derfor "kan ikke måles på dette datasæt".
Den skal måles på en optagelse med en kendt bevægelse.

Med centreret vindue er forsinkelsen nul, fordi filtreringen sker efter sessionen.
Vil I vise, hvad kravet ville koste et filter, der kørte live, så vælg **Kausalt** i
rullemenuen: ved 100 ms vindue måler programmet 50 ms.
