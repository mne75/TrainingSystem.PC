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
