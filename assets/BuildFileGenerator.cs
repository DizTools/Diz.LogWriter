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

    /// <summary>
    /// Ninja rule name for the buffer-mode extract step, e.g. "gfx_decode": the same decode as
    /// `extract`, reading a file instead of slicing the ROM. Used for an asset packed inside a
    /// container, whose bytes are cut out of the unpacked buffer rather than the cartridge.
    /// </summary>
    public string DecodeRule { get; init; }

    /// <summary>
    /// Ninja rule name for the buffer-mode compile step, e.g. "gfx_encode": the same encoder as
    /// `compile`, but the output is a fragment to be packed back into a buffer rather than a
    /// payload the assembler incbin's.
    /// </summary>
    public string EncodeRule { get; init; }

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
            DecodeRule = "gfx_decode",
            EncodeRule = "gfx_encode",
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
            DecodeRule = "audio_decode",
            EncodeRule = "audio_encode",
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
            DecodeRule = "text_decode",
            EncodeRule = "text_encode",
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
            DecodeRule = "raw_decode",
            EncodeRule = "raw_encode",
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

    /// <summary>
    /// The buffer-mode extract command: `extract` with the ROM slice replaced by a file read,
    /// for an asset whose bytes were cut out of a container instead of the cartridge. The
    /// manifest is still an explicit path for the same reason -- this is the ground-truth
    /// direction, and a mod layer must not be able to redirect it.
    /// </summary>
    private static string DecodeCommandFor(string toolVar) =>
        $"python ${toolVar} decode --manifest $manifest --in $in --out $out $search_roots";

    /// <summary>
    /// The buffer-mode compile command. It addresses the asset by LOGICAL NAME rather than input
    /// path, exactly as `compile` does, so layer resolution is identical and a mod overrides a
    /// container member the same way it overrides any other asset.
    /// </summary>
    private static string EncodeCommandFor(string toolVar) =>
        $"python ${toolVar} encode --name $name --in-dir $indir --out $out $search_roots";

    /// <summary>Ninja var name of the generic container slicer/splitter/joiner.</summary>
    private const string NodepackToolVar = "nodepack";

    /// <summary>The container tool's filename (vendored alongside the codecs).</summary>
    public const string NodepackToolFile = "nodepack.py";

    private const string SliceRule = "blob_slice";
    private const string SplitRule = "blob_split";
    private const string JoinRule = "blob_join";

    /// <summary>Sub-dir of the build tier holding chain intermediates on the extract side.</summary>
    private const string ExtractIntermediateDir = "extract";

    /// <summary>Sub-dir holding each member's re-encoded bytes, waiting to be packed back.</summary>
    private const string EncodeIntermediateDir = "encode";

    /// <summary>Sub-dir holding a container's reassembled buffer, before the pipeline runs.</summary>
    private const string JoinIntermediateDir = "join";

    /// <summary>Extension of a container's bytes exactly as they sit in the ROM.</summary>
    private const string StoredExtension = ".raw";

    /// <summary>Extension of a container's unpacked buffer -- what its members tile.</summary>
    private const string BufferExtension = ".plain";

    /// <summary>Extension of one member's bytes, cut out of the buffer or headed back into it.</summary>
    private const string MemberExtension = ".bin";

    private readonly BuildFileGeneratorSettings settings;
    private readonly IReadOnlyList<BuildToolBinding> toolBindings;
    private readonly IReadOnlyList<BuildStageBinding> stageBindings;

    public BuildFileGenerator(
        BuildFileGeneratorSettings settings = null,
        IReadOnlyList<BuildToolBinding> toolBindings = null,
        IReadOnlyList<BuildStageBinding> stageBindings = null)
    {
        this.settings = settings ?? new BuildFileGeneratorSettings();
        this.toolBindings = toolBindings ?? DefaultToolBindings;
        this.stageBindings = stageBindings ?? BuildStageBindings.Default;
    }

    private BuildToolBinding BindingFor(string assetType) =>
        toolBindings.FirstOrDefault(b => b.Handles(assetType))
        ?? throw new InvalidOperationException(
            $"No build tool is registered for asset type '{assetType}'. " +
            $"Known prefixes: {string.Join(", ", toolBindings.Select(b => b.TypePrefix))}. " +
            "Register a BuildToolBinding for it (see BuildFileGenerator.DefaultToolBindings).");

    private BuildStageBinding StageBindingFor(AssetPipelineStage stage) =>
        stageBindings.FirstOrDefault(b => string.Equals(b.Codec, stage.Codec, StringComparison.Ordinal))
        ?? throw new InvalidOperationException(
            $"No build tool is registered for pipeline codec '{stage.Codec}'. " +
            $"Known codecs: {string.Join(", ", stageBindings.Select(b => b.Codec))}.");

    /// <summary>
    /// The one transform stage a container declares, or null if its stored bytes ARE its buffer.
    /// More than one is rejected rather than guessed at: chaining would need an intermediate
    /// naming scheme, and inventing one silently would produce a build graph nobody authored.
    /// </summary>
    private BuildStageBinding StageFor(AssetBuildNode container)
    {
        var pipeline = container.Pipeline ?? [];
        if (pipeline.Count > 1)
            throw new InvalidOperationException(
                $"Container '{container.Name}' declares {pipeline.Count} pipeline stages. " +
                "Exactly one transform between the stored bytes and the member buffer is " +
                "supported; chained stages are not implemented.");

        return pipeline.Count == 0 ? null : StageBindingFor(pipeline[0]);
    }

    /// <summary>
    /// Keys of the game-specific tool sets the emitted build actually references. An export with
    /// no container declaring a stage names none, and nothing game-specific gets copied out --
    /// which is the point of keying them separately from the shared codecs.
    /// </summary>
    public IReadOnlyList<string> GameToolKeys(IReadOnlyList<AssetBuildNode> assets) =>
        (assets ?? [])
        .Select(StageFor)
        .Where(stage => stage?.GameToolKey != null)
        .Select(stage => stage.GameToolKey)
        .Distinct(StringComparer.Ordinal)
        .OrderBy(key => key, StringComparer.Ordinal)
        .ToList();

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

        // A container needs a whole family of rules and tool declarations no leaf asset uses.
        // They are emitted only when something actually needs them, so a project with no
        // containers gets exactly the build file it got before containers existed -- there is
        // no way to read an inert rule block and tell whether it is meaningful.
        var containers = assets.Where(a => a.Members is { Count: > 0 }).ToList();
        var packing = containers.Count > 0;
        var stages = stageBindings
            .Where(b => containers.Select(StageFor).Any(used => used == b))
            .ToList();

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
        if (packing)
        {
            // The container tool ships with the shared codecs -- cutting a buffer at declared
            // offsets is format-agnostic. The pipeline codecs do not: a compression format
            // belongs to the game that uses it, so those come from the game-tool dir.
            sb.AppendLine($"{NodepackToolVar} = {s.ToolsDir}/{NodepackToolFile}");
            foreach (var (toolVar, toolFile) in stages.SelectMany(b => b.ToolFiles).Distinct())
                sb.AppendLine($"{toolVar} = {BuildStageBindings.VendorDir}/{toolFile}");
        }
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

            // The buffer-mode pair: the same two directions, for an asset packed inside a
            // container rather than sitting at a ROM offset of its own.
            if (!packing)
                continue;

            sb.AppendLine($"rule {binding.DecodeRule}");
            sb.AppendLine($"  command = {DecodeCommandFor(binding.ToolVar)}");
            sb.AppendLine($"  description = {binding.ToolVar} decode $out");
            sb.AppendLine();
            sb.AppendLine($"rule {binding.EncodeRule}");
            sb.AppendLine($"  command = {EncodeCommandFor(binding.ToolVar)}");
            sb.AppendLine($"  description = {binding.ToolVar} encode $name");
            sb.AppendLine();
        }

        if (packing)
        {
            // Cutting a container up and putting it back: pure offset arithmetic driven by the
            // container's own manifest, with no idea what any member contains. `split` is one
            // edge with N outputs and `join` one edge with N inputs, so the fan-out is ordinary
            // ninja dependency tracking rather than machinery of its own.
            sb.AppendLine($"rule {SliceRule}");
            sb.AppendLine($"  command = python ${NodepackToolVar} slice --manifest $manifest --rom $orig_rom --out $out");
            sb.AppendLine($"  description = {NodepackToolVar} slice $out");
            sb.AppendLine();
            sb.AppendLine($"rule {SplitRule}");
            sb.AppendLine($"  command = python ${NodepackToolVar} split --manifest $manifest --in $in --outdir $outdir");
            sb.AppendLine($"  description = {NodepackToolVar} split $manifest");
            sb.AppendLine();
            sb.AppendLine($"rule {JoinRule}");
            sb.AppendLine($"  command = python ${NodepackToolVar} join --manifest $manifest --outdir $indir --out $out");
            sb.AppendLine($"  description = {NodepackToolVar} join $out");
            sb.AppendLine();

            // The transform between a container's stored bytes and the buffer its members tile.
            // Only the parameters the codec cannot derive from the data appear on the command
            // line; everything else is pinned inside the tool, because a build that forgot a
            // flag would emit bytes that are valid and wrong.
            foreach (var stage in stages)
            {
                sb.AppendLine($"rule {stage.DecodeRule}");
                sb.AppendLine($"  command = {stage.DecodeCommand}");
                sb.AppendLine($"  description = {stage.DecodeToolVar} $out");
                sb.AppendLine();
                sb.AppendLine($"rule {stage.EncodeRule}");
                sb.AppendLine($"  command = {stage.EncodeCommand}");
                sb.AppendLine($"  description = {stage.EncodeToolVar} $out");
                sb.AppendLine();
            }
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
            if (asset.Members is { Count: > 0 })
            {
                compiledBins.Add(AppendContainer(sb, asset, extractedSources));
                continue;
            }

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
    /// Emit the edges for one container and everything packed inside it, and return the compiled
    /// payload the assembler incbin's.
    ///
    /// The chain, in the extract direction: cut the container's stored bytes out of the ROM, run
    /// the pipeline to get the buffer, split that buffer into one file per member, and decode each
    /// member into its editable source. The build direction is the exact inverse. Every step is an
    /// ordinary ninja edge, so unrelated assets still build in parallel and nothing needs a
    /// notion of passes; the fan-out is carried by split's N outputs and join's N inputs.
    ///
    /// A member's decode/encode edges are deliberately the same shape as a top-level asset's --
    /// same rules, same manifest dependency, same shared-file handling -- because a member IS an
    /// ordinary asset that merely happens to live inside something. The splitter stays generic
    /// offset arithmetic and never learns what a tile or a text record is.
    /// </summary>
    private string AppendContainer(StringBuilder sb, AssetBuildNode container, List<string> extractedSources)
    {
        var s = settings;
        var stage = StageFor(container);
        var manifest = $"{s.ManifestDir}/{container.Name}.json";
        var packRef = $"${NodepackToolVar}";

        var outBin = $"{s.BuildDir}/{RegionAssetExportService.AssetSubDir}/{container.Name}{MemberExtension}";
        var stored = $"{s.BuildDir}/{ExtractIntermediateDir}/{container.Name}{StoredExtension}";
        var splitDir = $"{s.BuildDir}/{ExtractIntermediateDir}";
        var encodeDir = $"{s.BuildDir}/{EncodeIntermediateDir}";

        // With no transform the stored bytes ARE the buffer, so the two chain ends collapse onto
        // the slice output and the compiled payload and the pipeline edges simply do not exist.
        var buffer = stage == null ? stored : $"{s.BuildDir}/{ExtractIntermediateDir}/{container.Name}{BufferExtension}";
        var rejoined = stage == null ? outBin : $"{s.BuildDir}/{JoinIntermediateDir}/{container.Name}{BufferExtension}";

        sb.AppendLine($"# {container.Name}  ({container.AssetType}, " +
                      $"{container.Members.Count} member(s)" +
                      $"{(stage == null ? "" : $" behind {stage.Codec}")})");

        sb.AppendLine($"build {stored}: {SliceRule} | {manifest} {packRef} $orig_rom");
        sb.AppendLine($"  manifest = {manifest}");
        sb.AppendLine();

        if (stage != null)
        {
            sb.AppendLine($"build {buffer}: {stage.DecodeRule} {stored} | {manifest} ${stage.DecodeToolVar}");
            AppendStageVars(sb, container.Pipeline[0]);
            sb.AppendLine();
        }

        // ONE edge with every member as an output: ninja then knows that building any member
        // means running the splitter once, and re-running it cannot produce a half-split state.
        var memberBuffers = container.Members
            .Select(m => $"{splitDir}/{m.Name}{MemberExtension}").ToList();
        sb.AppendLine($"build {string.Join(" ", memberBuffers)}: {SplitRule} {buffer} | {manifest} {packRef}");
        sb.AppendLine($"  manifest = {manifest}");
        sb.AppendLine($"  outdir = {splitDir}");
        sb.AppendLine();

        var encoded = new List<string>();
        foreach (var member in container.Members)
        {
            var binding = BindingFor(member.AssetType);
            var memberBuffer = $"{splitDir}/{member.Name}{MemberExtension}";
            var memberManifest = $"{s.ManifestDir}/{member.Name}.json";
            var editable = $"{s.ExtractedDir}/{member.Name}{binding.SourceExtension}";
            var memberEncoded = $"{encodeDir}/{member.Name}{MemberExtension}";
            var toolRef = $"${binding.ToolVar}";

            var decodeDeps = string.Join(" ",
                new[] { memberManifest, toolRef }.Concat(SharedFileDeps(member)));

            sb.AppendLine($"build {editable}: {binding.DecodeRule} {memberBuffer} | {decodeDeps}");
            sb.AppendLine($"  manifest = {memberManifest}");
            sb.AppendLine();
            sb.AppendLine($"build {memberEncoded}: {binding.EncodeRule} {editable} | {memberManifest} {toolRef}");
            sb.AppendLine($"  name = {member.Name}");
            sb.AppendLine($"  indir = {s.ExtractedDir}");
            sb.AppendLine();

            encoded.Add(memberEncoded);
            extractedSources.Add(editable);
        }

        // ONE edge taking every member back: the buffer cannot be rebuilt from a subset, and
        // saying so is what makes a stale member impossible rather than merely unlikely.
        sb.AppendLine($"build {rejoined}: {JoinRule} {string.Join(" ", encoded)} | {manifest} {packRef}");
        sb.AppendLine($"  manifest = {manifest}");
        sb.AppendLine($"  indir = {encodeDir}");
        sb.AppendLine();

        if (stage != null)
        {
            sb.AppendLine($"build {outBin}: {stage.EncodeRule} {rejoined} | {manifest} ${stage.EncodeToolVar}");
            AppendStageVars(sb, container.Pipeline[0]);
            sb.AppendLine();
        }

        return outBin;
    }

    /// <summary>
    /// The stage's parameters as ninja variables, named &lt;block&gt;_&lt;parameter&gt; so the
    /// rule bodies can reference them ($lz_mode). Sorted, because build.ninja is checked in and
    /// the order authored options happened to be typed in must not show up as a diff.
    /// </summary>
    private static void AppendStageVars(StringBuilder sb, AssetPipelineStage stage)
    {
        foreach (var (key, value) in (stage.Block ?? []).OrderBy(p => p.Key, StringComparer.Ordinal))
            sb.AppendLine($"  {stage.BlockKey}_{key} = {value}");
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
