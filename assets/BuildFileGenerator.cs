using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using Diz.Core.Interfaces;

namespace Diz.LogWriter.assets;

/// <summary>
/// Describes one asset the build has to rebuild. Deliberately a flat record rather than an
/// IRegion, so the generator can be tested without a project and so a future non-region
/// asset source can feed it too.
/// </summary>
public class BuildAssetEntry
{
    /// <summary>Logical name, e.g. "gfx/font".</summary>
    public string Name { get; init; }

    /// <summary>Codec contract, e.g. "gfx.snes.2bpp".</summary>
    public string AssetType { get; init; }
}

public class BuildFileGeneratorSettings
{
    /// <summary>Main .asm entry point, relative to the build root.</summary>
    public string MainAsmPath { get; init; } = "generated/main.asm";

    /// <summary>Vendored tools dir, relative to build root.</summary>
    public string ToolsDir { get; init; } = "tools/vendor/dizpack";
}

/// <summary>
/// How to rebuild one family of asset (keyed by <see cref="AssetType"/> prefix) in the ninja
/// graph: which codec tool runs it and what its two ninja rules are called. Replaces the
/// hardcoded gfx-only wiring so a second asset type (e.g. "audio." / BRR) can be added by
/// registering another binding rather than editing the generator body.
///
/// Dispatch mirrors <see cref="BinaryAssetExporterBase"/>: an asset is built by the binding
/// whose <see cref="TypePrefix"/> its AssetType starts with. See regions-as-partition-plan.md
/// §B.2/§B.3.
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

    /// <summary>Ninja rule name for the seed step, e.g. "gfx_seed".</summary>
    public string SeedRule { get; init; }

    /// <summary>Full `command =` line body for the compile rule (references $ToolVar).</summary>
    public string CompileCommand { get; init; }

    /// <summary>Full `description =` line body for the compile rule.</summary>
    public string CompileDescription { get; init; }

    /// <summary>Full `command =` line body for the seed rule.</summary>
    public string SeedCommand { get; init; }

    /// <summary>Full `description =` line body for the seed rule.</summary>
    public string SeedDescription { get; init; }

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

    /// <summary>
    /// The codec bindings, keyed by AssetType prefix. Today only gfx is wired; this is the one
    /// place a new asset type (e.g. BRR under "audio.") gets added -- register a binding, and the
    /// per-asset build edges pick it up by prefix. See regions-as-partition-plan.md §B.3.
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
            SeedRule = "gfx_seed",
            CompileCommand = $"python ${SharedToolVar} compile --name $name $search_roots --out $out",
            CompileDescription = "gfxpack compile $name",
            SeedCommand = $"python ${SharedToolVar} seed --name $name $search_roots",
            SeedDescription = "gfxpack seed $name",
        },
    };

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

    /// <summary>Collect the asset regions that the build needs to rebuild.</summary>
    public static IReadOnlyList<BuildAssetEntry> CollectAssets(IEnumerable<IRegion> regions) =>
        regions
            .Where(r => r.ExportType == RegionExportType.Asset)
            .Select(r => new BuildAssetEntry
            {
                Name = RegionAssetUtil.GetAssetName(r),
                AssetType = r.AssetType,
            })
            .OrderBy(a => a.Name, StringComparer.Ordinal) // deterministic output => clean diffs
            .ToList();

    public string Generate(IReadOnlyList<BuildAssetEntry> assets)
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
        sb.AppendLine("#   ninja seed       one-time: create missing PNGs from the exported .bin seeds");
        sb.AppendLine("# ============================================================================");
        sb.AppendLine();
        sb.AppendLine("ninja_required_version = 1.10");
        sb.AppendLine();
        sb.AppendLine($"include {ConfigFileName}");
        sb.AppendLine();
        // The shared tool is always declared: romcheck (the `verify` oracle) lives in it,
        // independent of which codec asset types the project uses.
        sb.AppendLine($"{SharedToolVar} = {s.ToolsDir}/{SharedToolFile}");
        // Any codec binding that runs off its OWN script (not the shared one) declares its var here.
        foreach (var binding in toolBindings.Where(b => b.ToolVar != SharedToolVar && b.ToolFile != null))
            sb.AppendLine($"{binding.ToolVar} = {s.ToolsDir}/{binding.ToolFile}");
        sb.AppendLine($"main_asm = {s.MainAsmPath}");
        sb.AppendLine();
        sb.AppendLine("# Asset layer search path, highest priority first. Mod layers come from");
        sb.AppendLine($"# $mod_roots in {ConfigFileName}; the base layer is always last and must be complete.");
        sb.AppendLine($"search_roots = $mod_roots --search {RegionAssetExportService.BaseAssetLayer}");
        sb.AppendLine();

        sb.AppendLine("# ---- rules ----------------------------------------------------------------");
        sb.AppendLine();
        // One compile + one seed rule per registered codec binding. Emitted in registration order
        // so output stays deterministic (build.ninja is checked in).
        foreach (var binding in toolBindings)
        {
            sb.AppendLine($"rule {binding.CompileRule}");
            sb.AppendLine($"  command = {binding.CompileCommand}");
            sb.AppendLine($"  description = {binding.CompileDescription}");
            sb.AppendLine();
            sb.AppendLine($"rule {binding.SeedRule}");
            sb.AppendLine($"  command = {binding.SeedCommand}");
            sb.AppendLine($"  description = {binding.SeedDescription}");
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
        var seedTargets = new List<string>();

        if (assets.Count == 0)
        {
            sb.AppendLine("# (no asset-typed regions in this project yet)");
            sb.AppendLine();
        }

        foreach (var asset in assets)
        {
            var binding = BindingFor(asset.AssetType);
            var outBin = $"{RegionAssetExportService.BuildAssetDir}/{asset.Name}{binding.CompiledExtension}";
            var src = $"{RegionAssetExportService.BaseAssetLayer}/{asset.Name}{binding.SourceExtension}";
            var srcJson = $"{RegionAssetExportService.BaseAssetLayer}/{asset.Name}.json";
            var toolRef = $"${binding.ToolVar}";

            sb.AppendLine($"# {asset.Name}  ({asset.AssetType})");
            sb.AppendLine($"build {outBin}: {binding.CompileRule} {src} | {srcJson} {toolRef}");
            sb.AppendLine($"  name = {asset.Name}");
            sb.AppendLine();

            var seedTarget = $"seed-{asset.Name.Replace('/', '-')}";
            sb.AppendLine($"build {seedTarget}: {binding.SeedRule} | {toolRef}");
            sb.AppendLine($"  name = {asset.Name}");
            sb.AppendLine();

            compiledBins.Add(outBin);
            seedTargets.Add(seedTarget);
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

        if (seedTargets.Count > 0)
        {
            sb.AppendLine($"build seed: phony {string.Join(" ", seedTargets)}");
            sb.AppendLine();
        }
        else
        {
            sb.AppendLine("build seed: phony");
            sb.AppendLine();
        }

        sb.AppendLine("default $out_rom");

        return sb.ToString();
    }

    /// <summary>
    /// Write build.ninja (always regenerated) into the export root, and seed
    /// build-config.ninja alongside it (only if missing).
    /// </summary>
    public void WriteTo(string exportRootDir, IReadOnlyList<BuildAssetEntry> assets)
    {
        Directory.CreateDirectory(exportRootDir);
        SeedConfigIfMissing(exportRootDir);
        var path = Path.Combine(exportRootDir, "build.ninja");
        File.WriteAllText(path, Generate(assets), new UTF8Encoding(false));
    }
}
