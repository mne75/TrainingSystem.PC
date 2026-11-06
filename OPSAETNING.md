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

## 2. Start afsenderen på Raspberry Pi

Der er kun ét program at starte. `sensor_stream_tracking.py` læser rotation, kraft og
position og sender det hele i én besked:

```bash
python3 sensor_stream_tracking.py
```

Indstillingerne står i filerne, ikke på kommandolinjen:

| Hvad | Hvor |
|---|---|
| Pc'ens IP og port | `PC_IP` og `PC_PORT` øverst i `sensor_stream.py` |
| Sendefrekvens | `RATE_HZ` i `sensor_stream.py` (40 Hz) |
| Afstand mellem hjørnemarkører | `SIDE_MM` øverst i `sensor_stream_tracking.py` (320 mm) |
| Kamera og opløsning | `CAM_INDEX`, `CAM_WIDTH`, `CAM_HEIGHT` samme sted |

**`PC_IP` skal rettes til `172.20.10.7`**, hver gang pc'en skifter netværk.

`SIDE_MM` er afstanden i **millimeter mellem centrene** af to nabohjørnemarkører. Mål den;
tallet går direkte ind i alle positionsmålinger, og er det forkert, er alle afstande forkerte.

Programmet tarerer tryksensoren ved start — hold proben ubelastet de første sekunder.

Positionen kommer først med, når alle fire hjørnemarkører har været synlige **og**
probemarkøren er i billedet. Indtil da sender Pi'en `"pos": null` med en forklaring i
`track_status`, og pc-programmet viser det som et datagab.

### Modultest hver for sig

```bash
python3 imu_viewer.py --nogui          kun rotationssensoren
python3 fx29_test.py --log mt2.csv     kun kraftsensoren
python3 aruco_tracker.py --snapshot    kun kameraet
```

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

Husk at lukke pc-programmet først — to programmer kan ikke lytte på samme port. Det
gælder også `udp_receiver.py`: den og pc-programmet kan ikke køre samtidig.

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

Scriptet efterligner Pi'ens format: én samlet besked ved 40 Hz med rotation, kraft og
position, ét fælles løbenummer, og et kamera der kun leverer 20 Hz — så den samme position
gentages i to beskeder i træk, præcis som i virkeligheden.

Nyttige tilvalg:

| Tilvalg | Hvad det gør | Hvilket krav det afprøver |
|---|---|---|
| `--occlusion 4 --at 10` | Positionen holder op i 4 s efter 10 s | FR-10, NFR-R-02 |
| `--loss 0.03` | 3 % af positionspakkerne kastes væk | NFR-R-01 |
| `--still --noise 3` | Proben ligger i ro med 3 mm støj | FR-12, støjreduktion |
| `--shape linje` | Ret rute i stedet for cirkel | FR-01, FR-09 |
| `--cam 10` | Langsommere kamera, flere gentagne positioner | |
| `--no-force` | Ingen kraftmåling | FR-11 |
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


---

## Hvad pc-programmet gør ved Pi'ens beskeder

Pi'en sender én samlet besked pr. måling. Pc-programmet oversætter den ved modtagelsen,
og tre ting er værd at kende:

**`quat: null` og `pos: null` er ikke pakketab.** Beskeden kom frem; sensoren havde bare
ikke noget at give. Programmet skelner: pakketab tælles på huller i `seq`, mens datagab
tælles på perioder uden en gyldig måling af det enkelte signal. En session kan have nul
pakketab og alligevel være ikke-vurderbar, fordi markøren var dækket.

**Gentagne positioner tælles kun én gang.** Pi'en sender 40 gange i sekundet, men kameraet
leverer langsommere, så den samme position optræder i flere beskeder. `pos_age_ms` fortæller,
hvor gammelt billedet er, og programmet bruger det til at finde ud af, hvad der er den samme
måling. Uden det ville halvdelen af alle skridt være nul, og bevægelsesjævnheden ville blive
meningsløs.

**`track_status` bliver til forklaringen på et datagab.** Står der
`probe marker (ID 4) not visible`, er det den tekst, der vises ved hullet i resultatet.
