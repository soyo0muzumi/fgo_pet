using System.Globalization;
using System.IO;
using FgoPet.RenderingProbe.Rendering;
using FgoPet.RenderingProbe.Windowing;

namespace FgoPet.RenderingProbe;

public sealed record ProbeOptions(
    string BundlePath,
    RenderBackend Backend,
    TransparencyMode Transparency,
    double Scale,
    string OutputDirectory)
{
    private static readonly double[] SupportedScales = [0.5, 0.6, 0.75];

    public static ProbeOptions Parse(string[] args)
    {
        if (args.Length % 2 != 0)
        {
            throw new ArgumentException("Arguments must be supplied as --name value pairs.");
        }

        var values = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        for (var index = 0; index < args.Length; index += 2)
        {
            if (!args[index].StartsWith("--", StringComparison.Ordinal))
            {
                throw new ArgumentException($"Invalid option name: {args[index]}");
            }
            values[args[index][2..]] = args[index + 1];
        }

        var bundlePath = Required(values, "bundle");
        if (!Path.IsPathFullyQualified(bundlePath))
        {
            throw new ArgumentException("--bundle must be an absolute manifest path.");
        }

        if (!Enum.TryParse<RenderBackend>(Required(values, "renderer"), true, out var backend))
        {
            throw new ArgumentException("--renderer must be wpf or skia.");
        }
        if (!Enum.TryParse<TransparencyMode>(Required(values, "transparency"), true, out var transparency))
        {
            throw new ArgumentException("--transparency must be conventional or dwm.");
        }
        if (!double.TryParse(Required(values, "scale"), NumberStyles.Float, CultureInfo.InvariantCulture, out var scale)
            || !SupportedScales.Contains(scale))
        {
            throw new ArgumentException("--scale must be 0.5, 0.6, or 0.75.");
        }

        return new ProbeOptions(
            Path.GetFullPath(bundlePath),
            backend,
            transparency,
            scale,
            Path.GetFullPath(Required(values, "output")));
    }

    private static string Required(IReadOnlyDictionary<string, string> values, string name) =>
        values.TryGetValue(name, out var value) && !string.IsNullOrWhiteSpace(value)
            ? value
            : throw new ArgumentException($"Missing required option --{name}.");
}
