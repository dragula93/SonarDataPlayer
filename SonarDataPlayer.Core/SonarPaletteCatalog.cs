using System.Text.Json;

namespace SonarDataPlayer.Core;

public static class SonarPaletteCatalog
{
    public const string Garmin = "Garmin";
    public const string DefaultName = Garmin;

    private const string PaletteDataFileName = "MatplotlibPalettes.json";

    private static readonly string[] RecommendedMatplotlibNames =
    [
        "copper",
        "grey",
        "bone",
        "cividis",
        "afmhot",
        "pink",
        "Blues_r",
        "YlGnBu_r",
        "gist_heat",
        "plasma",
        "magma",
        "inferno"
    ];

    private static readonly Lazy<PaletteStore> PaletteStoreLazy = new(LoadPaletteStore);

    public static IReadOnlyList<string> RecommendedNames { get; } =
        [Garmin, .. RecommendedMatplotlibNames];

    public static IReadOnlyList<string> AllNames => PaletteStoreLazy.Value.AllNames;

    public static RgbColor[] Build(string? paletteName)
    {
        var normalized = NormalizeName(paletteName);
        if (normalized.Equals(Garmin, StringComparison.OrdinalIgnoreCase))
        {
            return GarminPalette.Build();
        }

        return PaletteStoreLazy.Value.Palettes.TryGetValue(normalized, out var stops)
            ? BuildInterpolatedPalette(stops)
            : GarminPalette.Build();
    }

    public static string NormalizeName(string? paletteName)
    {
        if (string.IsNullOrWhiteSpace(paletteName))
        {
            return DefaultName;
        }

        var trimmed = paletteName.Trim();
        if (trimmed.Equals(Garmin, StringComparison.OrdinalIgnoreCase))
        {
            return Garmin;
        }

        return PaletteStoreLazy.Value.Palettes.Keys.FirstOrDefault(name => name.Equals(trimmed, StringComparison.OrdinalIgnoreCase))
            ?? DefaultName;
    }

    public static IReadOnlyList<string> GetSelectableNames(bool includeFullList, string? selectedName = null)
    {
        var selected = NormalizeName(selectedName);
        var names = includeFullList ? AllNames.ToList() : RecommendedNames.ToList();
        if (!names.Contains(selected, StringComparer.OrdinalIgnoreCase))
        {
            names.Add(selected);
        }

        return names;
    }

    private static PaletteStore LoadPaletteStore()
    {
        var palettePath = ResolvePaletteDataPath();
        if (palettePath is null || !File.Exists(palettePath))
        {
            return new PaletteStore(
                new Dictionary<string, (int Index, RgbColor Color)[]>(StringComparer.OrdinalIgnoreCase),
                RecommendedNames);
        }

        using var document = JsonDocument.Parse(File.ReadAllText(palettePath));
        var palettes = new Dictionary<string, (int Index, RgbColor Color)[]>(StringComparer.OrdinalIgnoreCase);
        var names = new List<string> { Garmin };

        foreach (var property in document.RootElement.EnumerateObject())
        {
            var stops = property.Value.EnumerateArray()
                .Select(stop =>
                {
                    var index = stop[0].GetInt32();
                    var rgb = stop[1];
                    return (index, new RgbColor(
                        (byte)rgb[0].GetInt32(),
                        (byte)rgb[1].GetInt32(),
                        (byte)rgb[2].GetInt32()));
                })
                .ToArray();

            palettes[property.Name] = stops;

            if (!names.Contains(property.Name, StringComparer.OrdinalIgnoreCase))
            {
                names.Add(property.Name);
            }
        }

        var orderedNames = new List<string> { Garmin };
        orderedNames.AddRange(RecommendedMatplotlibNames.Where(name => palettes.ContainsKey(name)));
        orderedNames.AddRange(names.Where(name =>
            !name.Equals(Garmin, StringComparison.OrdinalIgnoreCase) &&
            !RecommendedMatplotlibNames.Contains(name, StringComparer.OrdinalIgnoreCase)));

        return new PaletteStore(palettes, orderedNames);
    }

    private static string? ResolvePaletteDataPath()
    {
        var candidates = new[]
        {
            Path.Combine(AppContext.BaseDirectory, PaletteDataFileName),
            Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "SonarDataPlayer.Core", PaletteDataFileName))
        };

        return candidates.FirstOrDefault(File.Exists);
    }

    private static RgbColor[] BuildInterpolatedPalette((int Index, RgbColor Color)[] stops)
    {
        var palette = new RgbColor[256];
        for (var i = 0; i < palette.Length; i++)
        {
            for (var stop = 0; stop < stops.Length - 1; stop++)
            {
                var a = stops[stop];
                var b = stops[stop + 1];
                if (i < a.Index || i > b.Index)
                {
                    continue;
                }

                var t = a.Index == b.Index ? 0 : (double)(i - a.Index) / (b.Index - a.Index);
                palette[i] = new RgbColor(
                    Lerp(a.Color.R, b.Color.R, t),
                    Lerp(a.Color.G, b.Color.G, t),
                    Lerp(a.Color.B, b.Color.B, t));
                break;
            }
        }

        return palette;
    }

    private static byte Lerp(byte a, byte b, double t)
    {
        return (byte)Math.Round(a + ((b - a) * t));
    }

    private sealed record PaletteStore(
        IReadOnlyDictionary<string, (int Index, RgbColor Color)[]> Palettes,
        IReadOnlyList<string> AllNames);
}