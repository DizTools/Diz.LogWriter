using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;

namespace Diz.LogWriter.assets;

/// <summary>
/// The repo layout the generated build works in. Every path is relative to the build root
/// (where build.ninja lives) and uses forward slashes, because the file is checked in and
/// must not carry host-specific separators.
/// </summary>
public class BuildFileGeneratorSettings
{
    /// <summary>Main .asm entry point, relative to the build root.</summary>
    public string MainAsmPath { get; init; } = "generated/main.asm";

    /// <summary>Vendored tools dir, relative to build root.</summary>
    public string ToolsDir { get; init; } = "tools/vendor/dizpack";

    /// <summary>Hand-authored asset layer: the last, always-complete layer of the search path.</summary>
    public string AssetsDir { get; init; } = "assets";

    /// <summary>Where `extract` decodes ROM bytes into editable sources.</summary>
    public string ExtractedDir { get; init; } = "extracted";

    /// <summary>Where compiled artifacts land; assets go to &lt;BuildDir&gt;/assets.</summary>
    public string BuildDir { get; init; } = "build";

    /// <summary>Where Diz writes asset manifests; inside the assembly output tree.</summary>
    public string ManifestDir { get; init; } = "generated/assets";
}

/// <summary>
/// How to rebuild one family of asset (keyed by <see cref="AssetType"/> prefix) in the ninja
/// graph: which codec tool runs it and what its two ninja rules are called. Replaces the
/// hardcoded gfx-only wiring so a second asset type (e.g. "audio." / BRR) can be added by
/// registering another binding rather than editing the generator body.
///
/// Dispatch mirrors <see cref="BinaryAssetExporterBase"/>: an asset is built by the binding
/// whose <see cref="TypePrefix"/> its AssetType starts with.
/// </summary>
public sealed class BuildToolBinding
{
    /// <summary>AssetType prefix this binding claims, e.g. "gfx." or "audio.".</summary>
    public string TypePrefix { get; init; }

    /// <summary>
    /// Ninja variable name of the codec script, referenced as $ToolVar in commands/deps.
    /// The gfx binding reuses the shared "gfxpack" var (which also hosts romcheck), so it
    /// declares no extra var line; a binding on its own tool declares one.
    /// </summary>
    public string ToolVar { get; init; }

    /// <summary>Codec filename under the tools dir, e.g. "gfxpack.py". Null == reuse the shared tool var.</summary>
    public string ToolFile { get; init; }

    /// <summary>Extension of the editable source the codec compiles from, e.g. ".png" or ".brr".</summary>
    public string SourceExtension { get; init; }

    /// <summary>Extension of the compiled payload the assembler incbin's, e.g. ".bin".</summary>
    public string CompiledExtension { get; init; } = ".bin";

    /// <summary>Ninja rule name for the compile step, e.g. "gfx_compile".</summary>
    public string CompileRule { get; init; }

    /// <summary>Ninja rule name for the extract step, e.g. "gfx_extract".</summary>
    public string ExtractRule { get; init; }

    /// <summary>Full `command =` line body for the compile rule (references $ToolVar).</summary>
    public string CompileCommand { get; init; }

    /// <summary>Full `description =` line body for the compile rule.</summary>
    public string CompileDescription { get; init; }

    /// <summary>Full `command =` line body for the extract rule.</summary>
    public string ExtractCommand { get; init; }

    /// <summary>Full `description =` line body for the extract rule.</summary>
    public string ExtractDescription { get; init; }

    public bool Handles(string assetType) =>
        assetType?.StartsWith(TypePrefix, StringComparison.Ordinal) == true;
}

/// <summary>
/// Emits a `build.ninja` that rebuilds the ROM from the exported assembly plus the editable
/// assets, and verifies the result byte-for-byte against the original.
///
/// The generated file is throwaway-but-checked-in, exactly like the .asm: regenerating it
/// should produce a clean diff. It must never depend on Diz -- everything it invokes is
/// either vendored into the game repo or already on PATH.
///
/// Game-specific settings ($asar, $out_rom, $orig_rom, $mod_roots) are NOT defined in
/// build.ninja: they live in build-config.ninja, which is seeded once (only if missing)
/// and never overwritten, so user edits survive re-export.
/// </summary>
public class BuildFileGenerator
{
    /// <summary>The user-owned, game-specific half of the build. Seeded once, never overwritten.</summary>
    public const string ConfigFileName = "build-config.ninja";

    /// <summary>Ninja var name of the shared tool. Hosts romcheck (the oracle) and the gfx codec.</summary>
    private const string SharedToolVar = "gfxpack";

    /// <summary>Shared tool filename. Always vendored + declared, since romcheck (verify) needs it.</summary>
    public const string SharedToolFile = "gfxpack.py";

    /// <summary>Ninja var name of the generic verbatim codec (BRR audio, and later palette/tilemap).</summary>
    private const string BinpackToolVar = "binpack";

    /// <summary>The generic passthrough codec filename (vendored alongside gfxpack).</summary>
    public const string BinpackToolFile = "binpack.py";

    /// <summary>Ninja var name of the fixed-width text codec (item/tech/menu name tables).</summary>
    private const string TextpackToolVar = "textpack";

    /// <summary>The text codec filename (vendored alongside gfxpack).</summary>
    public const string TextpackToolFile = "textpack.py";

    /// <summary>
    /// The codec bindings, keyed by AssetType prefix. This is the one place a new asset type
    /// gets added -- register a binding, and the per-asset build edges pick it up by prefix.
    /// </summary>
    public static readonly IReadOnlyList<BuildToolBinding> DefaultToolBindings = new[]
    {
        new BuildToolBinding
        {
            TypePrefix = "gfx.",
            ToolVar = SharedToolVar,   // reuses the shared var; declares no extra `= ...` line
            ToolFile = null,
            SourceExtension = ".png",
            CompiledExtension = ".bin",
            CompileRule = "gfx_compile",
            ExtractRule = "gfx_extract",
            CompileCommand = $"python ${SharedToolVar} compile --name $name $search_roots --out $out",
            CompileDescription = "gfxpack compile $name",
            ExtractCommand = ExtractCommandFor(SharedToolVar),
            ExtractDescription = "gfxpack extract $out",
        },

        // audio.* -> BRR (and later any other verbatim binary asset) via binpack. Runs off its
        // OWN tool var (declares a `binpack = ...` line), unlike gfx which reuses the shared
        // gfxpack var. The editable source is `.brr`; binpack resolves that extension from the
        // manifest's `audio.ext` block (written by BrrRegionAssetExporter), so the commands pass
        // NO --ext -- the manifest is the single source of truth (no `--ext` on the commands).
        new BuildToolBinding
        {
            TypePrefix = "audio.",
            ToolVar = BinpackToolVar,
            ToolFile = BinpackToolFile,
            SourceExtension = ".brr",
            CompiledExtension = ".bin",
            CompileRule = "audio_compile",
            ExtractRule = "audio_extract",
            CompileCommand = $"python ${BinpackToolVar} compile --name $name $search_roots --out $out",
            CompileDescription = "binpack compile $name",
            ExtractCommand = ExtractCommandFor(BinpackToolVar),
            ExtractDescription = "binpack extract $out",
        },

        // text.* -> fixed-width name tables via textpack. Own tool var (declares a `textpack = ...`
        // line), like binpack. The editable source is `.yaml`; textpack reads the table/width/pad/
        // tokens from the manifest's `text` block (written by TextRegionAssetExporter), so the
        // commands pass no extra flags -- the manifest is the single source of truth. The one
        // input the manifest only NAMES rather than contains is the character table; the text
        // exporter reports it as a shared file, and it becomes an implicit dep of the extract edge.
        new BuildToolBinding
        {
            TypePrefix = "text.",
            ToolVar = TextpackToolVar,
            ToolFile = TextpackToolFile,
            SourceExtension = ".yaml",
            CompiledExtension = ".bin",
            CompileRule = "text_compile",
            ExtractRule = "text_extract",
            CompileCommand = $"python ${TextpackToolVar} compile --name $name $search_roots --out $out",
            CompileDescription = "textpack compile $name",
            ExtractCommand = ExtractCommandFor(TextpackToolVar),
            ExtractDescription = "textpack extract $out",
        },

        // raw.* -> verbatim byte ranges via binpack, the same codec BRR uses -- a region marked
        // for plain-binary export resolves to this type. The editable source IS the bytes, so
        // its extension matches the compiled one; the two live in different tiers, so they never
        // collide. Shares binpack's tool var with the audio binding (declared once).
        new BuildToolBinding
        {
            TypePrefix = "raw.",
            ToolVar = BinpackToolVar,
            ToolFile = BinpackToolFile,
            SourceExtension = ".bin",
            CompiledExtension = ".bin",
            CompileRule = "raw_compile",
            ExtractRule = "raw_extract",
            CompileCommand = $"python ${BinpackToolVar} compile --name $name $search_roots --out $out",
            CompileDescription = "binpack compile $name",
            ExtractCommand = ExtractCommandFor(BinpackToolVar),
            ExtractDescription = "binpack extract $out",
        },
    };

    /// <summary>
    /// The extract command every codec shares: slice the ROM per the asset's own manifest and
    /// decode it into the editable source. The manifest path is explicit (per-edge `$manifest`)
    /// rather than resolved through the search path, because extraction is ROM ground truth --
    /// a mod layer must never be able to redirect what gets extracted. $search_roots still
    /// rides along so shared files the codec needs (e.g. a .tbl) resolve with overrides.
    /// </summary>
    private static string ExtractCommandFor(string toolVar) =>
        $"python ${toolVar} extract --manifest $manifest --rom $orig_rom --out $out $search_roots";

    private readonly BuildFileGeneratorSettings settings;
    private readonly IReadOnlyList<BuildToolBinding> toolBindings;

    public BuildFileGenerator(
        BuildFileGeneratorSettings settings = null,
        IReadOnlyList<BuildToolBinding> toolBindings = null)
    {
        this.settings = settings ?? new BuildFileGeneratorSettings();
        this.toolBindings = toolBindings ?? DefaultToolBindings;
    }

    private BuildToolBinding BindingFor(string assetType) =>
        toolBindings.FirstOrDefault(b => b.Handles(assetType))
        ?? throw new InvalidOperationException(
            $"No build tool is registered for asset type '{assetType}'. " +
            $"Known prefixes: {string.Join(", ", toolBindings.Select(b => b.TypePrefix))}. " +
            "Register a BuildToolBinding for it (see BuildFileGenerator.DefaultToolBindings).");

    /// <summary>
    /// Seed build-config.ninja with generic defaults, ONLY if the file doesn't already
    /// exist. It is user-owned and game-specific; re-export must never overwrite it.
    /// </summary>
    public static void SeedConfigIfMissing(string exportRootDir)
    {
        var path = Path.Combine(exportRootDir, ConfigFileName);
        if (File.Exists(path))
            return;

        File.WriteAllText(path, Templates.Load(ConfigFileName), new UTF8Encoding(false));
    }

    /// <summary>
    /// Resolve a node's shared files to build-root-relative paths under the hand-authored asset
    /// layer -- that layer is where these live; a mod overriding one is resolved by the codec at
    /// run time, which ninja cannot know about. The node already names them: the exporter that
    /// wrote the manifest is what validated the authoring they came from.
    /// </summary>
    private IEnumerable<string> SharedFileDeps(AssetBuildNode asset) =>
        (asset.SharedFiles ?? [])
        .Where(reference => !string.IsNullOrWhiteSpace(reference))
        .Select(reference => $"{settings.AssetsDir}/{reference.Replace('\\', '/').TrimStart('/')}")
        .Distinct(StringComparer.Ordinal)
        .OrderBy(path => path, StringComparer.Ordinal); // deterministic output => clean diffs

    public string Generate(IReadOnlyList<AssetBuildNode> assets)
    {
        var sb = new StringBuilder();
        var s = settings;

        sb.AppendLine("# ============================================================================");
        sb.AppendLine("# GENERATED BY DiztinGUIsh -- DO NOT EDIT.");
        sb.AppendLine("# Regenerated on every assembly export; your edits will be overwritten.");
        sb.AppendLine("# Checked into git on purpose, so changes to the build are reviewable.");
        sb.AppendLine("#");
        sb.AppendLine($"# Game-specific settings ($asar, $out_rom, $orig_rom, $mod_roots) live in");
        sb.AppendLine($"# {ConfigFileName} -- seeded once, owned by you, never overwritten.");
        sb.AppendLine("#");
        sb.AppendLine("# Requires: ninja, python3, and the assembler set in the config. NOT Diz.");
        sb.AppendLine("#");
        sb.AppendLine("# Targets:");
        sb.AppendLine("#   ninja            build the ROM");
        sb.AppendLine("#   ninja verify     build it and assert it matches the original ROM (the oracle)");
        sb.AppendLine($"#   ninja extract    decode the original ROM into editable sources in {s.ExtractedDir}/");
        sb.AppendLine("# ============================================================================");
        sb.AppendLine();
        sb.AppendLine("ninja_required_version = 1.10");
        sb.AppendLine();
        sb.AppendLine($"include {ConfigFileName}");
        sb.AppendLine();
        // The shared tool is always declared: romcheck (the `verify` oracle) lives in it,
        // independent of which codec asset types the project uses.
        sb.AppendLine($"{SharedToolVar} = {s.ToolsDir}/{SharedToolFile}");
        // Any codec binding that runs off its OWN script (not the shared one) declares its var
        // here. Distinct, because several bindings may share one codec (binpack backs both the
        // verbatim audio and raw types) and ninja would otherwise get the same assignment twice.
        foreach (var (toolVar, toolFile) in toolBindings
                     .Where(b => b.ToolVar != SharedToolVar && b.ToolFile != null)
                     .Select(b => (b.ToolVar, b.ToolFile))
                     .Distinct())
            sb.AppendLine($"{toolVar} = {s.ToolsDir}/{toolFile}");
        sb.AppendLine($"main_asm = {s.MainAsmPath}");
        sb.AppendLine();
        sb.AppendLine("# Asset layer search path, highest priority first. Mod layers come from");
        sb.AppendLine($"# $mod_roots in {ConfigFileName}; the hand-authored layer is always last and must be");
        sb.AppendLine("# complete. A layer must carry BOTH an asset's manifest and its content to override it;");
        sb.AppendLine("# the base pair below is the fallback, split across the two generated tiers.");
        sb.AppendLine(
            $"search_roots = $mod_roots --search {s.AssetsDir} " +
            $"--base-manifests {s.ManifestDir} --base-content {s.ExtractedDir}");
        sb.AppendLine();

        sb.AppendLine("# ---- rules ----------------------------------------------------------------");
        sb.AppendLine();
        // One compile + one extract rule per registered codec binding. Emitted in registration
        // order so output stays deterministic (build.ninja is checked in).
        foreach (var binding in toolBindings)
        {
            sb.AppendLine($"rule {binding.CompileRule}");
            sb.AppendLine($"  command = {binding.CompileCommand}");
            sb.AppendLine($"  description = {binding.CompileDescription}");
            sb.AppendLine();
            sb.AppendLine($"rule {binding.ExtractRule}");
            sb.AppendLine($"  command = {binding.ExtractCommand}");
            sb.AppendLine($"  description = {binding.ExtractDescription}");
            sb.AppendLine();
        }
        sb.AppendLine("rule assemble");
        sb.AppendLine("  command = $asar -v $main_asm $out");
        sb.AppendLine("  description = asar $out");
        sb.AppendLine();
        sb.AppendLine("# The oracle. Exits non-zero on mismatch, which fails the build.");
        sb.AppendLine("rule sha_verify");
        sb.AppendLine("  command = python $gfxpack romcheck --rom $in --expect-file $orig_rom");
        sb.AppendLine("  description = verify $in matches $orig_rom");
        sb.AppendLine();

        sb.AppendLine("# ---- assets ---------------------------------------------------------------");
        sb.AppendLine();

        var compiledBins = new List<string>();
        var extractedSources = new List<string>();

        if (assets.Count == 0)
        {
            sb.AppendLine("# (no asset-typed regions in this project yet)");
            sb.AppendLine();
        }

        // sorted here, not by the caller: build.ninja is checked in, so the order must not
        // depend on which order the regions happened to be exported in.
        foreach (var asset in assets.OrderBy(a => a.Name, StringComparer.Ordinal))
        {
            var binding = BindingFor(asset.AssetType);
            var outBin = $"{s.BuildDir}/{RegionAssetExportService.AssetSubDir}/{asset.Name}{binding.CompiledExtension}";
            var extracted = $"{s.ExtractedDir}/{asset.Name}{binding.SourceExtension}";
            var manifest = $"{s.ManifestDir}/{asset.Name}.json";
            var toolRef = $"${binding.ToolVar}";

            // The extract edge's deps are the manifest (geometry + where in the ROM), the ROM
            // itself, the codec, and any shared file the codec reads. Editing any of them must
            // re-extract; the ROM is listed even though it should never change, because if it
            // does, everything decoded from it is stale.
            var extractDeps = string.Join(" ",
                new[] { manifest, toolRef, "$orig_rom" }.Concat(SharedFileDeps(asset)));

            sb.AppendLine($"# {asset.Name}  ({asset.AssetType})");
            sb.AppendLine($"build {extracted}: {binding.ExtractRule} | {extractDeps}");
            sb.AppendLine($"  manifest = {manifest}");
            sb.AppendLine();
            sb.AppendLine($"build {outBin}: {binding.CompileRule} {extracted} | {manifest} {toolRef}");
            sb.AppendLine($"  name = {asset.Name}");
            sb.AppendLine();

            compiledBins.Add(outBin);
            extractedSources.Add(extracted);
        }

        sb.AppendLine("# ---- rom ------------------------------------------------------------------");
        sb.AppendLine();

        // The assembled ROM depends on every compiled asset, because the .asm incbin's them.
        // Order-only would be wrong here: if an asset's bytes change, the ROM must rebuild.
        var deps = compiledBins.Count > 0 ? " | " + string.Join(" ", compiledBins) : "";
        sb.AppendLine($"build $out_rom: assemble{deps}");
        sb.AppendLine();
        sb.AppendLine("build verify: sha_verify $out_rom | $orig_rom");
        sb.AppendLine();

        // `extract` is a convenience alias for "decode everything out of the ROM". The ordinary
        // build already depends on these edges, so this is not a required first step -- it just
        // gives a name to "give me the editable sources without assembling a ROM".
        sb.AppendLine(extractedSources.Count > 0
            ? $"build extract: phony {string.Join(" ", extractedSources)}"
            : "build extract: phony");
        sb.AppendLine();

        sb.AppendLine("default $out_rom");

        return sb.ToString();
    }

    /// <summary>
    /// Write build.ninja (always regenerated) into the export root, and seed
    /// build-config.ninja alongside it (only if missing).
    /// </summary>
    public void WriteTo(string exportRootDir, IReadOnlyList<AssetBuildNode> assets)
    {
        Directory.CreateDirectory(exportRootDir);
        SeedConfigIfMissing(exportRootDir);
        var path = Path.Combine(exportRootDir, "build.ninja");
        File.WriteAllText(path, Generate(assets), new UTF8Encoding(false));
    }
}
