using System.Buffers;
using System.IO;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using SonarDataPlayer.Core;

namespace SonarDataPlayer.App;

public static class BinaryWaterfallRenderer
{
    private sealed record RenderMetadata(int[] ChannelIds, IReadOnlyDictionary<int, int> MaxSamplesByChannel, double AutoMaxRangeMeters);
    private sealed record ContrastRange(double LogMin, double LogMax);

    private static readonly Dictionary<string, RenderMetadata> MetadataCache = new(StringComparer.OrdinalIgnoreCase);
    private static readonly Dictionary<string, IReadOnlyDictionary<int, ContrastRange>> ContrastRangeCache = new(StringComparer.OrdinalIgnoreCase);
    private static readonly Dictionary<string, IReadOnlyDictionary<int, bool>> SideScanSampleOrderCache = new(StringComparer.OrdinalIgnoreCase);
    private static readonly object CacheLock = new();

    public static IReadOnlyDictionary<int, BitmapSource> Render(
        SonarRecording recording,
        double displayMinRangeMeters = 0,
        double? displayMaxRangeMeters = null,
        string? paletteName = null,
        double lowPercentile = 0.01,
        double highPercentile = 0.995,
        bool lockAcrossChannels = false,
        double sideScanContrastBoost = 0)
    {
        if (recording.SamplesPath is null || recording.Frames.Count == 0)
        {
            return new Dictionary<int, BitmapSource>();
        }

        var recordingKey = BuildRecordingCacheKey(recording);
        var metadata = GetRenderMetadata(recordingKey, recording);
        var channelIds = metadata.ChannelIds;
        var maxSamplesByChannel = metadata.MaxSamplesByChannel;
        var autoMaxRangeMeters = metadata.AutoMaxRangeMeters;
        var renderMaxRangeMeters = displayMaxRangeMeters ?? autoMaxRangeMeters;
        var renderMinRangeMeters = Math.Clamp(displayMinRangeMeters, 0, Math.Max(0, renderMaxRangeMeters));
        var contrastByChannel = GetCachedContrastRanges(
            recordingKey,
            recording,
            channelIds,
            lowPercentile,
            highPercentile,
            lockAcrossChannels);
        var sideScanGamma = 1.0 + Math.Clamp(sideScanContrastBoost, 0, 2.0);
        var reversedSampleByChannel = GetCachedSideScanSampleOrder(recordingKey, recording);
        var boostedChannelIds = new HashSet<int>(
            recording.Channels
                .Where(c =>
                    c.Label.Contains("SideScan", StringComparison.OrdinalIgnoreCase) ||
                    c.Label.Contains("Down Imaging", StringComparison.OrdinalIgnoreCase) ||
                    c.Label.Contains("DownImage", StringComparison.OrdinalIgnoreCase))
                .Select(c => c.ChannelId));

        var palette = SonarPaletteCatalog.Build(paletteName);
        var output = new Dictionary<int, BitmapSource>();

        foreach (var channelId in channelIds)
        {
            var width = recording.Frames.Count;
            var height = Math.Max(1, maxSamplesByChannel[channelId]);
            var contrastRange = contrastByChannel.TryGetValue(channelId, out var configuredRange)
                ? configuredRange
                : new ContrastRange(0, 1);
            var channelGamma = boostedChannelIds.Contains(channelId) ? sideScanGamma : 1.0;
            var reverseSampleOrder = reversedSampleByChannel.TryGetValue(channelId, out var reverse) && reverse;
            var pixels = new byte[width * height * 4];

            using var stream = File.OpenRead(recording.SamplesPath);
            var x = 0;
            foreach (var frame in recording.Frames)
            {
                var block = frame.Channels.FirstOrDefault(c => c.ChannelId == channelId);
                if (block is not null)
                {
                    FillColumn(
                        stream,
                        block,
                        pixels,
                        x,
                        width,
                        height,
                        renderMinRangeMeters,
                        renderMaxRangeMeters,
                        contrastRange,
                        channelGamma,
                        reverseSampleOrder,
                        palette);
                }

                x++;
            }

            var bitmap = BitmapSource.Create(
                width,
                height,
                96,
                96,
                PixelFormats.Bgra32,
                null,
                pixels,
                width * 4);
            bitmap.Freeze();
            output[channelId] = bitmap;
        }

        return output;
    }

    private static string BuildRecordingCacheKey(SonarRecording recording)
    {
        if (string.IsNullOrWhiteSpace(recording.SamplesPath) || !File.Exists(recording.SamplesPath))
        {
            return string.Empty;
        }

        var info = new FileInfo(recording.SamplesPath);
        return string.Create(
            recording.SamplesPath.Length + 40,
            (recording.SamplesPath, info.Length, info.LastWriteTimeUtc.Ticks),
            static (span, state) =>
            {
                state.SamplesPath.AsSpan().CopyTo(span);
                var index = state.SamplesPath.Length;
                span[index++] = '|';
                if (!state.Length.TryFormat(span[index..], out var writtenLength))
                {
                    return;
                }

                index += writtenLength;
                span[index++] = '|';
                state.Ticks.TryFormat(span[index..], out _);
            });
    }

    private static RenderMetadata GetRenderMetadata(string recordingKey, SonarRecording recording)
    {
        if (!string.IsNullOrWhiteSpace(recordingKey))
        {
            lock (CacheLock)
            {
                if (MetadataCache.TryGetValue(recordingKey, out var cached))
                {
                    return cached;
                }
            }
        }

        var channelIds = recording.Frames
            .SelectMany(f => f.Channels.Select(c => c.ChannelId))
            .Distinct()
            .Order()
            .ToArray();

        var maxSamplesByChannel = channelIds.ToDictionary(
            id => id,
            id => recording.Frames
                .SelectMany(f => f.Channels.Where(c => c.ChannelId == id))
                .Select(c => c.SampleCount)
                .DefaultIfEmpty(0)
                .Max());
        var autoMaxRangeMeters = recording.Frames
            .SelectMany(f => f.Channels)
            .Select(c => c.MaximumRangeMeters ?? 0)
            .DefaultIfEmpty(0)
            .Max();
        var computed = new RenderMetadata(channelIds, maxSamplesByChannel, autoMaxRangeMeters);

        if (!string.IsNullOrWhiteSpace(recordingKey))
        {
            lock (CacheLock)
            {
                MetadataCache[recordingKey] = computed;
            }
        }

        return computed;
    }

    private static IReadOnlyDictionary<int, ContrastRange> GetCachedContrastRanges(
        string recordingKey,
        SonarRecording recording,
        IReadOnlyList<int> channelIds,
        double lowPercentile,
        double highPercentile,
        bool lockAcrossChannels)
    {
        var normalizedLow = Math.Clamp(lowPercentile, 0, 0.999);
        var normalizedHigh = Math.Clamp(highPercentile, normalizedLow + 0.0001, 0.999999);
        var contrastCacheKey = string.IsNullOrWhiteSpace(recordingKey)
            ? string.Empty
            : $"{recordingKey}|{normalizedLow:0.######}|{normalizedHigh:0.######}|{(lockAcrossChannels ? 1 : 0)}";

        if (!string.IsNullOrWhiteSpace(contrastCacheKey))
        {
            lock (CacheLock)
            {
                if (ContrastRangeCache.TryGetValue(contrastCacheKey, out var cached))
                {
                    return cached;
                }
            }
        }

        var computed = ComputeContrastRanges(recording, channelIds, normalizedLow, normalizedHigh, lockAcrossChannels);
        if (!string.IsNullOrWhiteSpace(contrastCacheKey))
        {
            lock (CacheLock)
            {
                ContrastRangeCache[contrastCacheKey] = computed;
            }
        }

        return computed;
    }

    private static IReadOnlyDictionary<int, ContrastRange> ComputeContrastRanges(
        SonarRecording recording,
        IReadOnlyList<int> channelIds,
        double lowPercentile,
        double highPercentile,
        bool lockAcrossChannels)
    {
        if (recording.SamplesPath is null)
        {
            return channelIds.ToDictionary(
                id => id,
                _ => new ContrastRange(0, 1));
        }

        if (lockAcrossChannels)
        {
            var globalHistogram = new long[ushort.MaxValue + 1];
            long globalSampleCount = 0;

            var globalBuffer = new byte[8192];
            using var globalStream = File.OpenRead(recording.SamplesPath);
            foreach (var block in recording.Frames.SelectMany(f => f.Channels))
            {
                globalStream.Seek(block.SampleOffset, SeekOrigin.Begin);
                var remaining = block.ByteLength;
                while (remaining > 0)
                {
                    var read = globalStream.Read(globalBuffer, 0, Math.Min(globalBuffer.Length, remaining));
                    if (read <= 0)
                    {
                        break;
                    }

                    for (var i = 0; i + 1 < read; i += 2)
                    {
                        var value = (ushort)(globalBuffer[i] | (globalBuffer[i + 1] << 8));
                        globalHistogram[value]++;
                        globalSampleCount++;
                    }

                    remaining -= read;
                }
            }

            var globalRange = ComputeRangeFromHistogram(globalHistogram, globalSampleCount, lowPercentile, highPercentile);
            return channelIds.ToDictionary(
                id => id,
                _ => globalRange);
        }

        var histogramsByChannel = channelIds.ToDictionary(
            id => id,
            _ => new long[ushort.MaxValue + 1]);
        var sampleCountByChannel = channelIds.ToDictionary(
            id => id,
            _ => 0L);

        var histogram = new long[ushort.MaxValue + 1];
        var buffer = new byte[8192];
        using var stream = File.OpenRead(recording.SamplesPath);
        foreach (var block in recording.Frames.SelectMany(f => f.Channels))
        {
            if (!histogramsByChannel.TryGetValue(block.ChannelId, out histogram))
            {
                continue;
            }

            stream.Seek(block.SampleOffset, SeekOrigin.Begin);
            var remaining = block.ByteLength;
            while (remaining > 0)
            {
                var read = stream.Read(buffer, 0, Math.Min(buffer.Length, remaining));
                if (read <= 0)
                {
                    break;
                }

                for (var i = 0; i + 1 < read; i += 2)
                {
                    var value = (ushort)(buffer[i] | (buffer[i + 1] << 8));
                    histogram[value]++;
                    sampleCountByChannel[block.ChannelId]++;
                }

                remaining -= read;
            }
        }

        var contrastByChannel = new Dictionary<int, ContrastRange>(channelIds.Count);
        foreach (var channelId in channelIds)
        {
            var channelHistogram = histogramsByChannel[channelId];
            var channelSampleCount = sampleCountByChannel[channelId];
            contrastByChannel[channelId] = ComputeRangeFromHistogram(channelHistogram, channelSampleCount, lowPercentile, highPercentile);
        }

        return contrastByChannel;
    }

    private static IReadOnlyDictionary<int, bool> GetCachedSideScanSampleOrder(string recordingKey, SonarRecording recording)
    {
        if (!string.IsNullOrWhiteSpace(recordingKey))
        {
            lock (CacheLock)
            {
                if (SideScanSampleOrderCache.TryGetValue(recordingKey, out var cached))
                {
                    return cached;
                }
            }
        }

        var computed = ComputeSideScanSampleOrder(recording);
        if (!string.IsNullOrWhiteSpace(recordingKey))
        {
            lock (CacheLock)
            {
                SideScanSampleOrderCache[recordingKey] = computed;
            }
        }

        return computed;
    }

    private static IReadOnlyDictionary<int, bool> ComputeSideScanSampleOrder(SonarRecording recording)
    {
        if (recording.SamplesPath is null)
        {
            return new Dictionary<int, bool>();
        }

        var sideScanIds = new HashSet<int>(
            recording.Channels
                .Where(c => c.Label.Contains("SideScan", StringComparison.OrdinalIgnoreCase))
                .Select(c => c.ChannelId));
        if (sideScanIds.Count == 0)
        {
            return new Dictionary<int, bool>();
        }

        var firstSum = new Dictionary<int, double>();
        var lastSum = new Dictionary<int, double>();
        var sampleWindows = new Dictionary<int, int>();
        var maxFramesToScan = Math.Min(recording.Frames.Count, 180);

        using var stream = File.OpenRead(recording.SamplesPath);
        foreach (var frame in recording.Frames.Take(maxFramesToScan))
        {
            foreach (var block in frame.Channels)
            {
                if (!sideScanIds.Contains(block.ChannelId) || block.SampleCount < 8)
                {
                    continue;
                }

                var bytesToRead = block.SampleCount * 2;
                var raw = ArrayPool<byte>.Shared.Rent(bytesToRead);
                try
                {
                    stream.Seek(block.SampleOffset, SeekOrigin.Begin);
                    if (!ReadExactly(stream, raw, bytesToRead))
                    {
                        continue;
                    }

                    var window = Math.Max(1, block.SampleCount / 8);
                    double sumFirst = 0;
                    double sumLast = 0;
                    for (var i = 0; i < window; i++)
                    {
                        var firstRawIndex = i * 2;
                        var firstValue = (ushort)(raw[firstRawIndex] | (raw[firstRawIndex + 1] << 8));
                        sumFirst += firstValue;

                        var lastSample = block.SampleCount - window + i;
                        var lastRawIndex = lastSample * 2;
                        var lastValue = (ushort)(raw[lastRawIndex] | (raw[lastRawIndex + 1] << 8));
                        sumLast += lastValue;
                    }

                    firstSum[block.ChannelId] = firstSum.GetValueOrDefault(block.ChannelId, 0) + sumFirst;
                    lastSum[block.ChannelId] = lastSum.GetValueOrDefault(block.ChannelId, 0) + sumLast;
                    sampleWindows[block.ChannelId] = sampleWindows.GetValueOrDefault(block.ChannelId, 0) + window;
                }
                finally
                {
                    ArrayPool<byte>.Shared.Return(raw);
                }
            }
        }

        var output = new Dictionary<int, bool>();
        foreach (var id in sideScanIds)
        {
            if (!sampleWindows.TryGetValue(id, out var windows) || windows <= 0)
            {
                continue;
            }

            var meanFirst = firstSum.GetValueOrDefault(id, 0) / windows;
            var meanLast = lastSum.GetValueOrDefault(id, 0) / windows;
            // If the first samples are much darker than the last samples, order is likely far->near.
            output[id] = meanFirst < (meanLast * 0.85);
        }

        return output;
    }

    private static ContrastRange ComputeRangeFromHistogram(
        long[] histogram,
        long sampleCount,
        double lowPercentile,
        double highPercentile)
    {
        if (sampleCount <= 0)
        {
            return new ContrastRange(0, 1);
        }

        // Exclude zero-valued samples ("no data" / above-waterline returns) from the percentile
        // computation. Without this, channels with many zeros (e.g. Down Imaging) have their
        // logMin pinned to zero, compressing all real returns into the top of the brightness range.
        var zeroCount = histogram[0];
        var nonZeroCount = sampleCount - zeroCount;
        if (nonZeroCount <= 0)
        {
            return new ContrastRange(0, 1);
        }

        var lowTarget = (long)Math.Floor(nonZeroCount * lowPercentile);
        var highTarget = (long)Math.Floor(nonZeroCount * highPercentile);
        if (highTarget <= lowTarget)
        {
            highTarget = Math.Min(nonZeroCount - 1, lowTarget + 1);
        }

        // Offset each target by zeroCount so FindValueAtCumulativeCount skips the zero bin.
        var lowValue = FindValueAtCumulativeCount(histogram, zeroCount + lowTarget);
        var highValue = FindValueAtCumulativeCount(histogram, zeroCount + highTarget);
        if (highValue <= lowValue)
        {
            highValue = Math.Min(ushort.MaxValue, lowValue + 1);
        }

        var logMin = Math.Log(1 + lowValue);
        var logMax = Math.Log(1 + highValue);
        if (logMax <= logMin)
        {
            logMax = logMin + 1;
        }

        return new ContrastRange(logMin, logMax);
    }

    private static int FindValueAtCumulativeCount(long[] histogram, long targetCount)
    {
        long cumulative = 0;
        for (var value = 0; value < histogram.Length; value++)
        {
            cumulative += histogram[value];
            if (cumulative > targetCount)
            {
                return value;
            }
        }

        return histogram.Length - 1;
    }

    private static void FillColumn(
        FileStream stream,
        ChannelSampleBlock block,
        byte[] pixels,
        int x,
        int width,
        int height,
        double displayMinRangeMeters,
        double displayMaxRangeMeters,
        ContrastRange contrastRange,
        double gamma,
        bool reverseSampleOrder,
        IReadOnlyList<RgbColor> palette)
    {
        var raw = ArrayPool<byte>.Shared.Rent(block.ByteLength);
        try
        {
            stream.Seek(block.SampleOffset, SeekOrigin.Begin);
            if (!ReadExactly(stream, raw, block.ByteLength))
            {
                return;
            }

            var minRange = block.MinimumRangeMeters ?? 0;
            var maxRange = block.MaximumRangeMeters ?? displayMaxRangeMeters;
            var visibleSpan = displayMaxRangeMeters - displayMinRangeMeters;
            if (visibleSpan <= 0 || maxRange <= minRange || block.SampleCount <= 1)
            {
                return;
            }

            var logSpan = contrastRange.LogMax - contrastRange.LogMin;
            if (logSpan <= 0)
            {
                logSpan = 1;
            }

            for (var y = 0; y < height; y++)
            {
                var depthMeters = height <= 1
                    ? displayMinRangeMeters
                    : displayMinRangeMeters + ((y / (double)(height - 1)) * visibleSpan);
                if (depthMeters < minRange || depthMeters > maxRange)
                {
                    continue;
                }

                var samplePosition = ((depthMeters - minRange) / (maxRange - minRange)) * (block.SampleCount - 1);
                var sample = Math.Clamp((int)Math.Round(samplePosition), 0, block.SampleCount - 1);
                if (reverseSampleOrder)
                {
                    sample = (block.SampleCount - 1) - sample;
                }
                var rawIndex = sample * 2;
                var value = (ushort)(raw[rawIndex] | (raw[rawIndex + 1] << 8));
                var normalizedLogValue = (Math.Log(1 + value) - contrastRange.LogMin) / logSpan;
                var gammaAdjusted = Math.Pow(Math.Clamp(normalizedLogValue, 0, 1), Math.Max(0.01, gamma));
                var paletteIndex = Math.Clamp((int)Math.Round(gammaAdjusted * 255), 0, 255);
                var color = palette[paletteIndex];

                var pixelIndex = ((y * width) + x) * 4;
                pixels[pixelIndex] = color.B;
                pixels[pixelIndex + 1] = color.G;
                pixels[pixelIndex + 2] = color.R;
                pixels[pixelIndex + 3] = 255;
            }
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(raw);
        }
    }

    /// <summary>
    /// Returns the maximum cross-track range (meters from nadir) stored in the side-scan channels,
    /// or 0 if no port/starboard pair can be found.  Use this to drive the range controls.
    /// </summary>
    public static double GetSideScanMaxRangeMeters(SonarRecording recording)
    {
        var portChannel = recording.Channels
            .FirstOrDefault(c =>
                c.Label.Contains("Port", StringComparison.OrdinalIgnoreCase) &&
                c.Label.Contains("SideScan", StringComparison.OrdinalIgnoreCase))
            ?? recording.Channels.FirstOrDefault(c =>
                c.Label.Contains("Port", StringComparison.OrdinalIgnoreCase));
        var starChannel = recording.Channels
            .FirstOrDefault(c =>
                c.Label.Contains("Starboard", StringComparison.OrdinalIgnoreCase) &&
                c.Label.Contains("SideScan", StringComparison.OrdinalIgnoreCase))
            ?? recording.Channels.FirstOrDefault(c =>
                c.Label.Contains("Starboard", StringComparison.OrdinalIgnoreCase));
        if (portChannel is null || starChannel is null)
        {
            return 0;
        }
        var ids = new HashSet<int> { portChannel.ChannelId, starChannel.ChannelId };
        return recording.Frames
            .SelectMany(f => f.Channels.Where(c => ids.Contains(c.ChannelId)))
            .Select(c => c.MaximumRangeMeters ?? 0)
            .DefaultIfEmpty(0)
            .Max();
    }

    /// <summary>
    /// Renders a traditional side-scan sonar waterfall: port on the left, nadir at centre,
    /// starboard on the right.  The output bitmap has
    ///   width  = portMaxSamples + starboardMaxSamples  (cross-track pixels)
    ///   height = frameCount                            (ping / time axis, top = earliest)
    /// Returns null when no port + starboard channel pair can be found.
    /// </summary>
    public static BitmapSource? RenderSideScan(
        SonarRecording recording,
        string? paletteName = null,
        double lowPercentile = 0.01,
        double highPercentile = 0.995,
        double sideScanBoost = 0)
    {
        if (recording.SamplesPath is null || recording.Frames.Count == 0)
        {
            return null;
        }

        // Locate port and starboard channels.
        var portChannel = recording.Channels
            .FirstOrDefault(c =>
                c.Label.Contains("Port", StringComparison.OrdinalIgnoreCase) &&
                c.Label.Contains("SideScan", StringComparison.OrdinalIgnoreCase))
            ?? recording.Channels.FirstOrDefault(c =>
                c.Label.Contains("Port", StringComparison.OrdinalIgnoreCase));

        var starChannel = recording.Channels
            .FirstOrDefault(c =>
                c.Label.Contains("Starboard", StringComparison.OrdinalIgnoreCase) &&
                c.Label.Contains("SideScan", StringComparison.OrdinalIgnoreCase))
            ?? recording.Channels.FirstOrDefault(c =>
                c.Label.Contains("Starboard", StringComparison.OrdinalIgnoreCase));

        if (portChannel is null || starChannel is null)
        {
            return null;
        }

        var recordingKey = BuildRecordingCacheKey(recording);
        var reversedSampleByChannel = GetCachedSideScanSampleOrder(recordingKey, recording);
        var reversePort = reversedSampleByChannel.TryGetValue(portChannel.ChannelId, out var reversePortFlag) && reversePortFlag;
        var reverseStar = reversedSampleByChannel.TryGetValue(starChannel.ChannelId, out var reverseStarFlag) && reverseStarFlag;

        var metadata = GetRenderMetadata(recordingKey, recording);
        var portMaxSamples = metadata.MaxSamplesByChannel.GetValueOrDefault(portChannel.ChannelId, 0);
        var starMaxSamples = metadata.MaxSamplesByChannel.GetValueOrDefault(starChannel.ChannelId, 0);
        if (portMaxSamples == 0 || starMaxSamples == 0)
        {
            return null;
        }

        var channelIds = new[] { portChannel.ChannelId, starChannel.ChannelId };
        var contrastByChannel = GetCachedContrastRanges(
            recordingKey, recording, channelIds, lowPercentile, highPercentile, lockAcrossChannels: false);

        var gamma = 1.0 + Math.Clamp(sideScanBoost, 0, 2.0);
        var palette = SonarPaletteCatalog.Build(paletteName);

        // Image layout:
        //   x in [0, portMaxSamples)        → port
        //   x in [portMaxSamples, width)    → starboard
        // Sample order can vary by channel and source. We normalize per-channel so the
        // composed view is always nadir-centred with near->far outwards on both sides.
        var imgWidth = portMaxSamples + starMaxSamples;
        var imgHeight = recording.Frames.Count;
        var pixels = new byte[imgWidth * imgHeight * 4];

        var portContrast = contrastByChannel.TryGetValue(portChannel.ChannelId, out var pc) ? pc : new ContrastRange(0, 1);
        var starContrast = contrastByChannel.TryGetValue(starChannel.ChannelId, out var sc) ? sc : new ContrastRange(0, 1);

        var bufSize = Math.Max(portMaxSamples, starMaxSamples) * 2 + 64;
        var raw = ArrayPool<byte>.Shared.Rent(bufSize);
        try
        {
            using var stream = File.OpenRead(recording.SamplesPath);
            for (var y = 0; y < recording.Frames.Count; y++)
            {
                var frame = recording.Frames[y];
                var drawY = imgHeight - 1 - y;

                // Port channel: map sample order to the left half.
                var portBlock = frame.Channels.FirstOrDefault(c => c.ChannelId == portChannel.ChannelId);
                if (portBlock is not null)
                {
                    stream.Seek(portBlock.SampleOffset, SeekOrigin.Begin);
                    if (ReadExactly(stream, raw, portBlock.ByteLength))
                    {
                        var count = Math.Min(portBlock.SampleCount, portMaxSamples);
                        for (var s = 0; s < count; s++)
                        {
                            var ri = s * 2;
                            var value = (ushort)(raw[ri] | (raw[ri + 1] << 8));
                            var sourceSample = reversePort ? (count - 1 - s) : s;
                            var sourceRawIndex = sourceSample * 2;
                            var sourceValue = (ushort)(raw[sourceRawIndex] | (raw[sourceRawIndex + 1] << 8));
                            var x = portMaxSamples - 1 - s;
                            PaintPixel(pixels, x, drawY, imgWidth, sourceValue, portContrast, gamma, palette);
                        }
                    }
                }

                // Starboard channel: map sample order to the right half.
                var starBlock = frame.Channels.FirstOrDefault(c => c.ChannelId == starChannel.ChannelId);
                if (starBlock is not null)
                {
                    stream.Seek(starBlock.SampleOffset, SeekOrigin.Begin);
                    if (ReadExactly(stream, raw, starBlock.ByteLength))
                    {
                        var count = Math.Min(starBlock.SampleCount, starMaxSamples);
                        for (var s = 0; s < count; s++)
                        {
                            var ri = s * 2;
                            var value = (ushort)(raw[ri] | (raw[ri + 1] << 8));
                            var sourceSample = reverseStar ? (count - 1 - s) : s;
                            var sourceRawIndex = sourceSample * 2;
                            var sourceValue = (ushort)(raw[sourceRawIndex] | (raw[sourceRawIndex + 1] << 8));
                            var x = portMaxSamples + s;
                            PaintPixel(pixels, x, drawY, imgWidth, sourceValue, starContrast, gamma, palette);
                        }
                    }
                }
            }
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(raw);
        }

        var bitmap = BitmapSource.Create(
            imgWidth, imgHeight, 96, 96,
            PixelFormats.Bgra32, null,
            pixels, imgWidth * 4);
        bitmap.Freeze();
        return bitmap;
    }

    private static void PaintPixel(
        byte[] pixels,
        int x,
        int y,
        int imgWidth,
        ushort value,
        ContrastRange contrast,
        double gamma,
        IReadOnlyList<RgbColor> palette)
    {
        var logSpan = contrast.LogMax - contrast.LogMin;
        if (logSpan <= 0)
        {
            logSpan = 1;
        }

        var norm = (Math.Log(1 + value) - contrast.LogMin) / logSpan;
        var boosted = Math.Pow(Math.Clamp(norm, 0, 1), Math.Max(0.01, gamma));
        var idx = Math.Clamp((int)Math.Round(boosted * 255), 0, 255);
        var color = palette[idx];
        var pi = ((y * imgWidth) + x) * 4;
        pixels[pi] = color.B;
        pixels[pi + 1] = color.G;
        pixels[pi + 2] = color.R;
        pixels[pi + 3] = 255;
    }

    private static bool ReadExactly(Stream stream, byte[] buffer, int count)
    {
        var offset = 0;
        while (offset < count)
        {
            var read = stream.Read(buffer, offset, count - offset);
            if (read <= 0)
            {
                return false;
            }

            offset += read;
        }

        return true;
    }
}
