# TrainingSystem.PC — feedback UI prototype

This WPF/.NET 8 Windows prototype receives UDP JSON measurements, records a training session, shows descriptive feedback, displays a 3D probe model and exports raw measurements plus analysis to JSON.

## Run the PC feedback UI
1. Open `TrainingSystem.PC.csproj` in Visual Studio on Windows with the .NET desktop development workload installed.
2. Build and run the application.
3. Click **Start listening** (UDP port `5005` by default).
4. Click **Start session**, run the sender(s), and then click **End session & analyze**.
5. Session JSON exports are saved under `%LOCALAPPDATA%\TrainingSystem\Sessions`.

## Test the UI without hardware
With the PC app listening and a session started, run:

```bash
python tools/send_test_packets.py --host 127.0.0.1 --port 5005
```

This sends simulated XY position plus quaternion orientation. The values are only for testing the interface, not project results.

## BNO085 orientation sender (Raspberry Pi)
The updated `UL_RaspPi-master/Program.cs` sends quaternion orientation to the PC while keeping the existing local 3D viewer. Find the PC's LAN IPv4 address and run on the Pi:

```bash
dotnet run -- --host 192.168.1.50 --port 5005
```

Replace `192.168.1.50` with the PC's actual LAN IP. Use `--sim` to test without a connected BNO085:

```bash
dotnet run -- --sim --host 192.168.1.50 --port 5005
```

The PC firewall must permit inbound UDP on port 5005. The sender emits `qx`, `qy`, `qz`, `qw`, `timestampUtc`, `sequenceNumber` and `type: orientation`. It does **not** send position: BNO085 orientation is not absolute XYZ position.

## Data fields
The PC receiver supports these JSON fields:

- `timestampUtc`: ISO 8601 UTC timestamp. Preferred shared time basis.
- `t`: legacy Unix timestamp in seconds, still supported by the current ArUco Python sender.
- `x`, `y`: tracked position coordinates. The current ArUco script provides a calibrated plane/XY only; check its calibration before treating coordinates as millimetres.
- `z`: optional measured position Z. The UI displays `Not measured` when absent; it does not invent a value.
- `qx`, `qy`, `qz`, `qw`: BNO085 quaternion orientation.
- `angleX`, `angleY`, `angleZ`: optional Euler angles in degrees if a sender provides them directly. Otherwise the UI derives display angles from the quaternion using a standard roll/pitch/yaw convention.
- `pressure`, `pressureUnit`: optional future pressure-sensor fields.

Position and orientation may arrive in separate UDP packets. The UI keeps the latest known position and latest known orientation and combines them for the visualization. To compare measurements from separate devices accurately, keep the Pi and PC clocks synchronized (e.g. network time synchronization) and use timestamps from the senders. If no timestamp is supplied, the PC records packet-receive time; this is not a guarantee of synchronized sensor sampling.

## 3D view and interpretation
The 3D view shows a simplified probe model, orientation, and a trail of the last 100 XY position samples. XY values are mapped to a relative display plane and scaled to fit the viewport. This is a visualization aid, **not a calibrated 3D reconstruction**. The vertical/Z-position dimension is used only if a sender supplies a measured `z` value; otherwise no Z-position is fabricated. Confirm the axis directions, sign convention, Euler conversion, sensor mounting, coordinate-frame alignment and units against the actual hardware before interpreting the view quantitatively.

## Analysis limitations
- XY path length, displacement and speed use only position samples with both X and Y.
- The code labels distances as tracking units unless calibration establishes physical units.
- The speed-variation coefficient is exploratory and is not a validated clinical smoothness score.
- Feedback is descriptive. No universal good/bad competence thresholds are implemented.
- UDP can drop packets. Sequence numbers can help detect missing packets; the prototype does not yet reconstruct lost samples.
- Pressure fields are supported in the model, but the uploaded hardware sender does not yet implement a pressure sensor.
