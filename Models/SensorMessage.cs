using System.Text.Json.Serialization;

namespace TrainingSystem.PC.Models;

/// Incoming measurement packet. Position and orientation are deliberately separate:
/// XY tracking does not imply a measured Z position.
public sealed class SensorMessage
{
    [JsonPropertyName("type")] public string? Type { get; set; }
    [JsonPropertyName("x")] public double? X { get; set; }
    [JsonPropertyName("y")] public double? Y { get; set; }
    [JsonPropertyName("z")] public double? Z { get; set; }
    [JsonPropertyName("t")] public double? T { get; set; }
    [JsonPropertyName("timestampUtc")] public DateTimeOffset? TimestampUtc { get; set; }
    [JsonPropertyName("sessionId")] public string? SessionId { get; set; }
    [JsonPropertyName("sequenceNumber")] public long? SequenceNumber { get; set; }

    // BNO085 quaternion, when the sender transmits quaternion orientation.
    [JsonPropertyName("qx")] public double? Qx { get; set; }
    [JsonPropertyName("qy")] public double? Qy { get; set; }
    [JsonPropertyName("qz")] public double? Qz { get; set; }
    [JsonPropertyName("qw")] public double? Qw { get; set; }

    // Optional Euler angles in degrees. If omitted, they are derived from the quaternion for display.
    [JsonPropertyName("angleX")] public double? AngleX { get; set; }
    [JsonPropertyName("angleY")] public double? AngleY { get; set; }
    [JsonPropertyName("angleZ")] public double? AngleZ { get; set; }

    [JsonPropertyName("pressure")] public double? Pressure { get; set; }
    [JsonPropertyName("pressureUnit")] public string? PressureUnit { get; set; }

    // ---------------------------------------------------------------------
    // Formatet fra Raspberry Pi (sensor_stream_tracking.py)
    //
    // Pi'en sender én samlet besked pr. måling med rotation, kraft og position
    // i hver sin undergruppe, og med ét fælles løbenummer. Felterne herunder
    // læser det format; Normalize() kopierer dem over i felterne ovenfor, så
    // resten af programmet kun kender én form.
    // ---------------------------------------------------------------------

    [JsonPropertyName("seq")] public long? Seq { get; set; }
    [JsonPropertyName("quat")] public QuaternionPayload? Quat { get; set; }
    [JsonPropertyName("pos")] public PositionPayload? Pos { get; set; }
    [JsonPropertyName("force_n")] public double? ForceN { get; set; }
    [JsonPropertyName("force_counts")] public int? ForceCounts { get; set; }
    [JsonPropertyName("track_status")] public string? TrackStatus { get; set; }

    /// <summary>
    /// Hvor gammelt kamerabilledet bag positionen er, i millisekunder. Pi'en sender
    /// hurtigere, end kameraet leverer, så den samme position gentages i flere beskeder.
    /// Feltet er det, der gør det muligt at kende forskel på en ny og en gentaget måling.
    /// </summary>
    [JsonPropertyName("pos_age_ms")] public int? PositionAgeMs { get; set; }

    /// <summary>Om positionen lå inden for det kalibrerede kvadrat.</summary>
    [JsonIgnore] public bool? PositionInside { get; set; }

    public sealed class QuaternionPayload
    {
        [JsonPropertyName("w")] public double W { get; set; }
        [JsonPropertyName("x")] public double X { get; set; }
        [JsonPropertyName("y")] public double Y { get; set; }
        [JsonPropertyName("z")] public double Z { get; set; }
    }

    public sealed class PositionPayload
    {
        [JsonPropertyName("x")] public double X { get; set; }
        [JsonPropertyName("y")] public double Y { get; set; }
        [JsonPropertyName("inside")] public bool? Inside { get; set; }
    }

    /// <summary>
    /// Oversætter Pi'ens format til de felter, resten af programmet bruger.
    /// Kaldes af modtageren, lige efter at pakken er læst, og er uskadelig at kalde
    /// på en besked, der allerede er på den flade form.
    /// </summary>
    public SensorMessage Normalize()
    {
        if (!SequenceNumber.HasValue && Seq.HasValue) SequenceNumber = Seq;

        if (Quat is not null && !HasQuaternion)
        {
            Qw = Quat.W;
            Qx = Quat.X;
            Qy = Quat.Y;
            Qz = Quat.Z;
        }

        if (Pos is not null && !HasPositionXY)
        {
            X = Pos.X;
            Y = Pos.Y;
            PositionInside = Pos.Inside;
        }

        if (ForceN.HasValue && !Pressure.HasValue)
        {
            Pressure = ForceN;
            PressureUnit ??= "N";
        }

        return this;
    }

    /// <summary>
    /// Tidspunktet for den kameramåling, positionen stammer fra. Uden pos_age_ms
    /// antages positionen at være målt samtidig med beskeden.
    /// </summary>
    [JsonIgnore]
    public DateTimeOffset PositionTimestampUtc =>
        PositionAgeMs.HasValue
            ? EffectiveTimestampUtc.AddMilliseconds(-PositionAgeMs.Value)
            : EffectiveTimestampUtc;

    /// <summary>
    /// Nøgle for den datastrøm, beskeden kom i. Pi'en sender alt i én strøm med ét
    /// løbenummer; ældre afsendere brugte en strøm pr. signal. Pakketab skal opgøres
    /// pr. strøm, for løbenumrene tælles op hver for sig.
    /// </summary>
    [JsonIgnore]
    public string StreamKey => string.IsNullOrWhiteSpace(Type) ? "samlet" : Type!;

    [JsonIgnore]
    public DateTimeOffset EffectiveTimestampUtc => TimestampUtc ?? (T.HasValue
        ? DateTimeOffset.FromUnixTimeMilliseconds((long)(T.Value * 1000))
        : DateTimeOffset.MinValue);

    [JsonIgnore] public bool HasPositionXY => X.HasValue && Y.HasValue;
    [JsonIgnore] public bool HasPositionXYZ => X.HasValue && Y.HasValue && Z.HasValue;
    [JsonIgnore] public bool HasQuaternion => Qx.HasValue && Qy.HasValue && Qz.HasValue && Qw.HasValue;
    [JsonIgnore] public bool HasOrientation => (AngleX.HasValue && AngleY.HasValue && AngleZ.HasValue) || HasQuaternion;

    /// <summary>Returns Euler angles in degrees. Quaternion conversion is for UI/inspection only.</summary>
    public (double X, double Y, double Z)? GetEulerDegrees()
    {
        if (AngleX.HasValue && AngleY.HasValue && AngleZ.HasValue)
            return (AngleX.Value, AngleY.Value, AngleZ.Value);
        if (!HasQuaternion) return null;

        double x = Qx!.Value, y = Qy!.Value, z = Qz!.Value, w = Qw!.Value;
        double norm = Math.Sqrt(x*x + y*y + z*z + w*w);
        if (norm <= 1e-12) return null;
        x /= norm; y /= norm; z /= norm; w /= norm;

        // Standard roll (X), pitch (Y), yaw (Z) convention; confirm axes/signs against the sensor mounting.
        double sinrCosp = 2 * (w * x + y * z);
        double cosrCosp = 1 - 2 * (x * x + y * y);
        double rollX = Math.Atan2(sinrCosp, cosrCosp);
        double sinp = 2 * (w * y - z * x);
        double pitchY = Math.Abs(sinp) >= 1 ? Math.CopySign(Math.PI / 2, sinp) : Math.Asin(sinp);
        double sinyCosp = 2 * (w * z + x * y);
        double cosyCosp = 1 - 2 * (y * y + z * z);
        double yawZ = Math.Atan2(sinyCosp, cosyCosp);
        const double toDegrees = 180.0 / Math.PI;
        return (rollX * toDegrees, pitchY * toDegrees, yawZ * toDegrees);
    }
}
