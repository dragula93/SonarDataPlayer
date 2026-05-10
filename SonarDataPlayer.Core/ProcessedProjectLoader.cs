using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace SonarDataPlayer.Core;

public static class ProcessedProjectLoader
{
    public static SonarRecording Load(string manifestPath)
    {
        var projectRoot = Path.GetDirectoryName(Path.GetFullPath(manifestPath))
            ?? throw new InvalidOperationException("Manifest path has no parent directory.");

        using var stream = File.OpenRead(manifestPath);
        var manifest = JsonSerializer.Deserialize<ProjectManifest>(stream, JsonOptions)
            ?? throw new InvalidOperationException("Manifest could not be read.");

        var telemetryPath = Resolve(projectRoot, manifest.Telemetry);
        var telemetry = File.Exists(telemetryPath)
            ? LoadTelemetryCsv(telemetryPath)
            : Array.Empty<PingTelemetry>();

        var channels = manifest.Channels
            .Select(c => new ChannelTrack(
                c.ChannelId,
                c.Label ?? $"Channel {c.ChannelId}",
                c.Mode ?? "Unknown",
                c.Orientation,
                c.Beam,
                c.StartFrequencyHz,
                c.EndFrequencyHz,
                Resolve(projectRoot, c.Waterfall),
                c.Rows,
                c.MaxSamples,
                c.TimeStart,
                c.TimeEnd))
            .ToArray();

        var samplesPath = Resolve(projectRoot, manifest.Samples?.Path);
        var framesPath = Resolve(projectRoot, manifest.Frames);
        var frames = File.Exists(framesPath)
            ? LoadFrames(framesPath)
            : Array.Empty<SonarFrame>();

        return new SonarRecording(
            manifest.Source ?? string.Empty,
            channels,
            telemetry,
            frames,
            File.Exists(samplesPath) ? samplesPath : null);
    }

    private static IReadOnlyList<SonarFrame> LoadFrames(string path)
    {
        var frames = new List<SonarFrame>();
        foreach (var line in File.ReadLines(path))
        {
            if (string.IsNullOrWhiteSpace(line))
            {
                continue;
            }

            var frame = JsonSerializer.Deserialize<FrameDto>(line, JsonOptions);
            if (frame is null)
            {
                continue;
            }

            frames.Add(new SonarFrame(
                frame.FrameIndex,
                frame.SequenceCount,
                frame.TimeSeconds,
                frame.Lat,
                frame.Lon,
                frame.SpeedMetersPerSecond,
                frame.TrackDistanceMeters,
                frame.HeadingDegrees,
                frame.TemperatureCelsius,
                frame.Channels.Select(c => new ChannelSampleBlock(
                    c.ChannelId,
                    c.SampleOffset,
                    c.SampleCount,
                    c.ByteLength,
                    c.MinRangeMeters,
                    c.MaxRangeMeters,
                    c.BottomDepthMeters)).ToArray()));
        }

        return frames.OrderBy(f => f.FrameIndex).ToArray();
    }

    private static IReadOnlyList<PingTelemetry> LoadTelemetryCsv(string path)
    {
        var lines = File.ReadLines(path).ToArray();
        if (lines.Length < 2)
        {
            return Array.Empty<PingTelemetry>();
        }

        var headers = SplitCsvLine(lines[0]);
        var index = headers
            .Select((name, i) => (name, i))
            .ToDictionary(x => x.name, x => x.i, StringComparer.OrdinalIgnoreCase);

        var rows = new List<PingTelemetry>(lines.Length - 1);
        for (var i = 1; i < lines.Length; i++)
        {
            var cells = SplitCsvLine(lines[i]);
            rows.Add(new PingTelemetry(
                Long(cells, index, "record_num"),
                Int(cells, index, "channel_id"),
                Double(cells, index, "time_s"),
                DateTimeUtc(cells, index),
                Int(cells, index, "ping_cnt", "sample_cnt"),
                NullableDouble(cells, index, "min_range", "first_sample_depth"),
                NullableDouble(cells, index, "max_range", "last_sample_depth"),
                NullableDouble(cells, index, "inst_dep_m", "bottom_depth"),
                NullableDouble(cells, index, "lat"),
                NullableDouble(cells, index, "lon"),
                NullableDouble(cells, index, "speed_ms"),
                NullableDouble(cells, index, "instr_heading"),
                NullableDouble(cells, index, "tempC", "water_temp", "water_temperature"),
                NullableDouble(cells, index, "trk_dist"),
                NullableDouble(cells, index, "gain"),
                Text(cells, index, "survey"),
                Text(cells, index, "frequency")));
        }

        return rows.OrderBy(r => r.TimeSeconds).ToArray();
    }

    private static string Resolve(string root, string? path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return string.Empty;
        }

        return Path.IsPathRooted(path) ? path : Path.GetFullPath(Path.Combine(root, path));
    }

    private static string[] SplitCsvLine(string line)
    {
        return line.Split(',');
    }

    private static int Int(string[] cells, IReadOnlyDictionary<string, int> index, params string[] names)
    {
        return (int)Long(cells, index, names);
    }

    private static long Long(string[] cells, IReadOnlyDictionary<string, int> index, params string[] names)
    {
        foreach (var name in names)
        {
            if (index.TryGetValue(name, out var i) &&
                i < cells.Length &&
                long.TryParse(cells[i], NumberStyles.Any, CultureInfo.InvariantCulture, out var value))
            {
                return value;
            }
        }

        return 0;
    }

    private static double Double(string[] cells, IReadOnlyDictionary<string, int> index, params string[] names)
    {
        return NullableDouble(cells, index, names) ?? 0;
    }

    private static double? NullableDouble(string[] cells, IReadOnlyDictionary<string, int> index, params string[] names)
    {
        foreach (var name in names)
        {
            if (index.TryGetValue(name, out var i) &&
                i < cells.Length &&
                double.TryParse(cells[i], NumberStyles.Any, CultureInfo.InvariantCulture, out var value))
            {
                return value;
            }
        }

        return null;
    }

    private static string? Text(string[] cells, IReadOnlyDictionary<string, int> index, params string[] names)
    {
        foreach (var name in names)
        {
            if (index.TryGetValue(name, out var i) &&
                i < cells.Length &&
                !string.IsNullOrWhiteSpace(cells[i]))
            {
                return cells[i];
            }
        }

        return null;
    }

    private static DateTime? DateTimeUtc(string[] cells, IReadOnlyDictionary<string, int> index)
    {
        if (!index.TryGetValue("date", out var dateIndex) ||
            !index.TryGetValue("time", out var timeIndex) ||
            dateIndex >= cells.Length ||
            timeIndex >= cells.Length)
        {
            return null;
        }

        var text = $"{cells[dateIndex]} {cells[timeIndex]}";
        return DateTime.TryParse(
            text,
            CultureInfo.InvariantCulture,
            DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal,
            out var value)
            ? DateTime.SpecifyKind(value, DateTimeKind.Utc)
            : null;
    }

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true
    };

    private sealed record ProjectManifest(
        int FormatVersion,
        string? Source,
        string? Telemetry,
        string? Frames,
        SamplesManifest? Samples,
        ChannelManifest[] Channels);

    private sealed record SamplesManifest(string Path, string Encoding);

    private sealed record ChannelManifest(
        int ChannelId,
        string? Label,
        string? Mode,
        string? Orientation,
        int? Beam,
        int? StartFrequencyHz,
        int? EndFrequencyHz,
        string Waterfall,
        int Rows,
        int MaxSamples,
        double TimeStart,
        double TimeEnd);

    private sealed record FrameDto(
        int FrameIndex,
        int SequenceCount,
        double TimeSeconds,
        double? Lat,
        double? Lon,
        double? SpeedMetersPerSecond,
        double? TrackDistanceMeters,
        double? HeadingDegrees,
        double? TemperatureCelsius,
        ChannelBlockDto[] Channels);

    private sealed record ChannelBlockDto(
        int ChannelId,
        long SampleOffset,
        int SampleCount,
        int ByteLength,
        double? MinRangeMeters,
        double? MaxRangeMeters,
        double? BottomDepthMeters);
}
