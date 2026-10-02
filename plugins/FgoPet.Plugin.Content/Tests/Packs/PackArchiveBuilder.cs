using System.IO.Compression;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using FgoPet.Core.Packs;
using FgoPet.Core.Portraits;

namespace FgoPet.Infrastructure.Tests.Packs;

/// <summary>Builds in-memory-then-disk <c>.fgopetpack</c> archives for installer tests.</summary>
internal static class PackArchiveBuilder
{
    public static void Raw(string archivePath, Action<ZipArchive> fill)
    {
        using var file = File.Create(archivePath);
        using var archive = new ZipArchive(file, ZipArchiveMode.Create);
        fill(archive);
    }

    public static void AddText(ZipArchive archive, string name, string text) =>
        AddContent(archive, name, new UTF8Encoding(false).GetBytes(text));

    public static void AddContent(ZipArchive archive, string name, byte[] content)
    {
        var entry = archive.CreateEntry(name);
        using var stream = entry.Open();
        stream.Write(content);
    }

    public static void WriteFullPack(
        string archivePath,
        string packageId = "official.mash",
        string packageVersion = "1.0.0",
        string? minAppVersion = "1.0.0",
        string? packageJson = null)
    {
        Raw(archivePath, archive =>
        {
            AddText(archive, "package.json", packageJson ?? PackManifestJson(packageId, packageVersion, minAppVersion));
            AddContent(archive, "previews/library.png", new byte[] { 1, 2, 3, 4, 5, 6, 7, 8 });

            var body = new byte[] { 10, 20, 30, 40 };
            var expression = new byte[] { 11, 21, 31, 41 };
            AddContent(archive, "appearances/casual/runtime/full_body.png", body);
            AddContent(archive, "appearances/casual/runtime/expressions/r01c01.png", expression);
            AddText(archive, "appearances/casual/manifest.json", PackFixture.V3Json([
                ("body", "full_body", "runtime/full_body.png", PackFixture.Sha256(body)),
                ("expression", "r01c01", "runtime/expressions/r01c01.png", PackFixture.Sha256(expression)),
            ]));
        });
    }

    public static string PackManifestJson(
        string packageId = "official.mash",
        string packageVersion = "1.0.0",
        string? minAppVersion = "1.0.0",
        string? servantId = null,
        string? displayName = null,
        bool includeCapabilities = false,
        bool includeFiles = false)
    {
        var root = new JsonObject
        {
            ["schema_version"] = 1,
            ["package_id"] = packageId,
            ["package_version"] = packageVersion,
            ["servant_id"] = servantId ?? "mash_kyrielight",
            ["display_name"] = displayName ?? "玛修·基列莱特",
            ["publisher"] = "community",
            ["min_app_version"] = minAppVersion ?? string.Empty,
            ["preview_path"] = "previews/library.png",
            ["appearances"] = new JsonArray
            {
                new JsonObject
                {
                    ["appearance_id"] = "casual",
                    ["manifest_path"] = "appearances/casual/manifest.json",
                },
            },
        };
        if (includeCapabilities)
        {
            root["capabilities"] = new JsonArray("art.v3", "persona.v1");
        }
        if (includeFiles)
        {
            root["files"] = new JsonArray(
                "previews/library.png",
                "appearances/casual/manifest.json",
                "appearances/casual/runtime/full_body.png",
                "appearances/casual/runtime/expressions/r01c01.png");
        }
        return root.ToJsonString(new JsonSerializerOptions { WriteIndented = true });
    }
}
