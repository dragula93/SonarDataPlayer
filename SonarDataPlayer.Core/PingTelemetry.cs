namespace SonarDataPlayer.Core;

public sealed record PingTelemetry(
    long RecordNumber,
    int ChannelId,
    double TimeSeconds,
    DateTime? TimestampUtc,
    int SampleCount,
    double? MinimumRangeMeters,
    double? MaximumRangeMeters,
    double? DepthMeters,
    double? Latitude,
    double? Longitude,
    double? SpeedMetersPerSecond,
    double? HeadingDegrees,
    double? TemperatureCelsius,
    double? TrackDistanceMeters,
    double? Gain,
    string? Survey,
    string? Frequency);
