using System.Reflection;
using System.Text;
using System.Text.RegularExpressions;

namespace CodexWallpaperSkin;

internal static partial class SceneRuntimeSource
{
    // This fingerprint covers both the upstream snapshot and our local renderer
    // patches.  It must change whenever any bundled JavaScript changes; otherwise
    // an already-open Codex page can keep the previous renderer even after the
    // native controller itself has been upgraded.
    internal const string UpstreamRevision = "we-scene@6b503a36b952f91dbab5e6f378f632f87baf05cc+cws.11";

    private static readonly string[] LibraryFiles =
    [
        "ThirdParty.we_scene.src.pkg.container.js",
        "ThirdParty.we_scene.src.pkg.texture.js",
        "ThirdParty.we_scene.src.scene.parse.js",
        "ThirdParty.we_scene.src.scene.effects-parse.js",
        "ThirdParty.we_scene.src.render.math.js",
        "ThirdParty.we_scene.src.render.noise.js",
        "ThirdParty.we_scene.src.render.hlsl2glsl.js",
        "ThirdParty.we_scene.src.render.effects.js",
        "ThirdParty.we_scene.src.render.renderer.js"
    ];

    internal static string Script { get; } = Build();

    private static string Build()
    {
        var builder = new StringBuilder(128 * 1024);
        builder.AppendLine("(() => {");
        builder.Append("  if (window.__cwsWeSceneLibrary?.version === ")
            .Append(System.Text.Json.JsonSerializer.Serialize(UpstreamRevision))
            .AppendLine(") return;");
        foreach (var file in LibraryFiles)
        {
            builder.AppendLine("// bundled from " + file);
            builder.AppendLine(StripModuleSyntax(ReadResource(file)));
        }
        builder.Append("window.__cwsWeSceneLibrary = Object.freeze({ version: ")
            .Append(System.Text.Json.JsonSerializer.Serialize(UpstreamRevision))
            .AppendLine(", parsePkg, getEntry, parseTex, decodeMip0, decodeMips, FIF, parseScene, resolveMaterial, resolveEffectChain, BUILTIN_MODELS, BUILTIN_MATERIALS, createRenderer, makeTexture, makeTextureMip, generateNoiseTexture });");
        builder.AppendLine("})();");
        builder.AppendLine(ReadResource("Runtime.scene-host.js"));
        return builder.ToString();
    }

    private static string ReadResource(string suffix)
    {
        var assembly = Assembly.GetExecutingAssembly();
        var resourceName = assembly.GetManifestResourceNames()
            .SingleOrDefault(name => name.EndsWith(suffix, StringComparison.OrdinalIgnoreCase))
            ?? throw new InvalidOperationException($"Embedded scene runtime resource is missing: {suffix}");
        using var stream = assembly.GetManifestResourceStream(resourceName)
            ?? throw new InvalidOperationException($"Embedded scene runtime resource could not be opened: {suffix}");
        using var reader = new StreamReader(stream, Encoding.UTF8, detectEncodingFromByteOrderMarks: true);
        return reader.ReadToEnd().Replace("\r\n", "\n", StringComparison.Ordinal);
    }

    private static string StripModuleSyntax(string source)
    {
        var lines = source.Split('\n');
        var builder = new StringBuilder(source.Length);
        foreach (var line in lines)
        {
            if (ImportLinePattern().IsMatch(line) || ExportListPattern().IsMatch(line))
            {
                continue;
            }
            builder.AppendLine(ExportDeclarationPattern().Replace(line, "$1"));
        }
        return builder.ToString();
    }

    [GeneratedRegex("^\\s*import\\s+.*\\bfrom\\s+['\"][^'\"]+['\"]\\s*;?\\s*$", RegexOptions.CultureInvariant)]
    private static partial Regex ImportLinePattern();

    [GeneratedRegex("^\\s*export\\s*\\{.*\\}\\s*;?\\s*$", RegexOptions.CultureInvariant)]
    private static partial Regex ExportListPattern();

    [GeneratedRegex("^(\\s*)export\\s+(?=(?:async\\s+)?(?:function|const|let|class)\\b)", RegexOptions.CultureInvariant)]
    private static partial Regex ExportDeclarationPattern();
}
