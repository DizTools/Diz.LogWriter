using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Nodes;
using Diz.Core.Interfaces;

namespace Diz.LogWriter.assets;

/// <summary>
/// Asset export for a packed container: one region of the ROM holding several assets, usually
/// behind a transform such as compression.
///
/// A container describes its bytes by the MEMBERS tiling them rather than by a codec's geometry,
/// so its manifest carries a members list and no typed block. Each member also gets an ordinary
/// leaf manifest of its own, written here by handing the member to the same exporter that would
/// have handled it as a standalone region. That is what keeps a member indistinguishable from a
/// top-level asset everywhere downstream: it forks, it takes a mod override, it is verified, and
/// its build edges have the same shape -- none of which would work if members were described
/// inline in the container's manifest, because overriding one member would then mean owning a
/// copy of every sibling's geometry too.
///
/// What Diz cannot see, and therefore does not invent: the container's bytes as stored are the
/// only ones it has. The unpacked buffer -- the thing members are offsets into -- exists only
/// after the build has run the pipeline. So the decomposition (which members, where, how long,
/// and what each hashes to) is AUTHORED on the region, in the same free-form options field that
/// carries every other parameter Diz has no way to derive. Authoring is checked hard here, and
/// the authored hashes are then checked against the real bytes by the build; a wrong one halts
/// naming the member rather than producing a plausible ROM.
///
/// Authored shape:
/// <code>
/// {
///   "lz": { "mode": 12 },
///   "members": [
///     { "name": "gfx/main_font", "at": 0, "len": 9216,
///       "type": "gfx.snes.2bpp", "sha256": "...", "options": { "cell_h": 12 } }
///   ]
/// }
/// </code>
/// The optional transform block ("lz" here) selects a pipeline stage by its key; omitting it
/// means the stored bytes ARE the buffer. Members must tile that buffer with no hole and no
/// overlap, in ascending order -- declaration order is data, because reassembly concatenates in
/// it -- and the tiling is checked again at build time against the buffer's real length, which is
/// the half of the check that cannot be done here.
/// </summary>
public class ContainerRegionAssetExporter : BinaryAssetExporterBase
{
    /// <summary>AssetType prefix a container region carries, e.g. "blob.container".</summary>
    public const string TypePrefix = "blob.";

    private const string MembersOptionKey = "members";

    private readonly IReadOnlyList<IRegionAssetExporter> memberExporters;

    /// <param name="memberExporters">
    /// The exporters a member is dispatched to, normally the same leaf exporters registered for
    /// top-level regions. A container is deliberately NOT among them: the grammar is recursive but
    /// only one level is built, and a nested container would silently produce a manifest nothing
    /// in the build graph knows how to unpack.
    /// </param>
    public ContainerRegionAssetExporter(IEnumerable<IRegionAssetExporter> memberExporters)
    {
        this.memberExporters = memberExporters?.ToList() ?? [];
    }

    protected override string AssetTypePrefix => TypePrefix;

    protected override string CompiledExtension => ".bin";

    // ---- the manifest -------------------------------------------------------------------

    protected override void Validate(RegionAssetExportRequest request)
    {
        // parsing IS the validation: every check below reports the region and the member it
        // failed on, and nothing is written until all of them pass.
        var parsed = Parse(request);

        foreach (var member in parsed.Members)
            ExporterFor(member, request.Region);
    }

    /// <summary>Null: a container is described by its members, never by a codec's geometry.</summary>
    protected override AssetManifestBlock BuildTypeBlock(RegionAssetExportRequest request) => null;

    protected override JsonArray BuildPipeline(RegionAssetExportRequest request)
    {
        var parsed = Parse(request);
        if (parsed.Stage == null)
            return null;

        return
        [
            new JsonObject
            {
                ["codec"] = parsed.Stage.Codec,
                [parsed.Stage.OptionKey] = parsed.StageBlock.DeepClone(),
            },
        ];
    }

    /// <summary>
    /// The members as REFERENCES -- name, offset, length -- and nothing else. Everything about
    /// how a member is decoded lives in its own manifest, so there is exactly one description of
    /// it and no way for two copies to drift.
    /// </summary>
    protected override JsonArray BuildMembers(RegionAssetExportRequest request)
    {
        var array = new JsonArray();
        foreach (var member in Parse(request).Members)
        {
            array.Add(new JsonObject
            {
                ["name"] = member.Name,
                ["at"] = member.At,
                ["len"] = member.Length,
            });
        }

        return array;
    }

    /// <summary>
    /// Export each member as an asset in its own right -- writing its leaf manifest -- and report
    /// the container node with those members hanging off it. The member's assembly directive is
    /// discarded: a member is not placed by the assembler, the container it is packed into is.
    /// </summary>
    protected override IReadOnlyList<AssetBuildNode> BuildNodes(RegionAssetExportRequest request)
    {
        var parsed = Parse(request);
        var containerName = RegionAssetUtil.GetAssetName(request.Region);

        var memberNodes = new List<AssetBuildNode>();
        foreach (var member in parsed.Members)
        {
            var result = ExporterFor(member, request.Region).Export(new RegionAssetExportRequest
            {
                Region = new MemberRegion(member),
                Bytes = null,               // only the build ever holds a member's bytes
                Member = new AssetMemberContext
                {
                    ContainerName = containerName,
                    At = member.At,
                    Length = member.Length,
                    Sha256 = member.Sha256,
                },
                ManifestRootDir = request.ManifestRootDir,
                AssetRefPrefix = request.AssetRefPrefix,
            });

            memberNodes.AddRange(result.BuildNodes);
        }

        return
        [
            new AssetBuildNode
            {
                Name = containerName,
                AssetType = RegionAssetUtil.GetAssetType(request.Region),
                Pipeline = parsed.Stage == null
                    ? []
                    :
                    [
                        new AssetPipelineStage
                        {
                            Codec = parsed.Stage.Codec,
                            BlockKey = parsed.Stage.OptionKey,
                            Block = parsed.StageBlock,
                        },
                    ],
                Members = memberNodes,
            },
        ];
    }

    private IRegionAssetExporter ExporterFor(MemberSpec member, IRegion region) =>
        memberExporters.FirstOrDefault(e => e.CanExport(new MemberRegion(member)))
        ?? throw new InvalidOperationException(
            $"Region '{region.RegionName}': member '{member.Name}' has asset type " +
            $"'{member.Type}', which no exporter handles. A member is an ordinary asset and " +
            "needs a codec contract that something can decode.");

    // ---- authoring ----------------------------------------------------------------------

    private sealed record MemberSpec(
        string Name, int At, int Length, string Type, string Sha256, string Version, string Options);

    private sealed record ParsedContainer(
        BuildStageBinding Stage, JsonObject StageBlock, IReadOnlyList<MemberSpec> Members);

    /// <summary>
    /// Read the container's decomposition off the region. Re-parsed per call rather than cached:
    /// the exporters are stateless and shared across regions, and parsing is cheap next to being
    /// the kind of state that survives into the wrong region's manifest.
    /// </summary>
    private static ParsedContainer Parse(RegionAssetExportRequest request)
    {
        var region = request.Region;
        var options = ParseOptionsObject(region);

        BuildStageBinding stage = null;
        JsonObject stageBlock = null;
        foreach (var (key, value) in options)
        {
            if (key == MembersOptionKey)
                continue;

            var binding = BuildStageBindings.ByOptionKey(key)
                ?? throw new InvalidOperationException(
                    $"Region '{region.RegionName}': Asset Options key \"{key}\" does not name a " +
                    $"known transform stage. Expected \"{MembersOptionKey}\" or one of: " +
                    $"{string.Join(", ", BuildStageBindings.OptionKeys)}.");

            if (stage != null)
                throw new InvalidOperationException(
                    $"Region '{region.RegionName}': Asset Options declare more than one transform " +
                    $"stage (\"{stage.OptionKey}\" and \"{key}\"). A container is unpacked by one " +
                    "stage; chaining is not implemented, and guessing an order would be wrong " +
                    "half the time.");

            if (value is not JsonObject block)
                throw new InvalidOperationException(
                    $"Region '{region.RegionName}': Asset Options \"{key}\" must be an object of " +
                    $"stage parameters (e.g. {{\"mode\": 12}}), not a " +
                    $"{value?.GetValueKind().ToString() ?? "null"}.");

            if (block.Count == 0)
                throw new InvalidOperationException(
                    $"Region '{region.RegionName}': Asset Options \"{key}\" is empty. A stage that " +
                    "takes no parameters would be indistinguishable from one whose parameters " +
                    "were forgotten.");

            foreach (var (paramName, paramValue) in block)
            {
                if (paramValue == null || paramValue.GetValueKind() is not
                        (JsonValueKind.Number or JsonValueKind.String))
                    throw new InvalidOperationException(
                        $"Region '{region.RegionName}': Asset Options \"{key}.{paramName}\" must be " +
                        "a number or a string -- stage parameters are passed to the codec on the " +
                        "build command line, which has no way to carry anything else.");
            }

            stage = binding;
            stageBlock = block.DeepClone().AsObject();
        }

        return new ParsedContainer(stage, stageBlock, ParseMembers(options, region));
    }

    private static JsonObject ParseOptionsObject(IRegion region)
    {
        var raw = region.AssetOptions;
        if (string.IsNullOrWhiteSpace(raw))
            throw new InvalidOperationException(
                $"Region '{region.RegionName}' is a container but has no Asset Options. A " +
                "container is defined by the members tiling its buffer, and nothing in the ROM " +
                "says what they are, so the decomposition has to be authored: " +
                "{\"members\": [{\"name\": ..., \"at\": 0, \"len\": N, \"type\": ..., " +
                "\"sha256\": ...}]}.");

        JsonNode parsed;
        try
        {
            parsed = JsonNode.Parse(raw);
        }
        catch (JsonException ex)
        {
            throw new InvalidOperationException(
                $"Region '{region.RegionName}': Asset Options is not valid JSON: {ex.Message}", ex);
        }

        if (parsed is not JsonObject obj)
            throw new InvalidOperationException(
                $"Region '{region.RegionName}': Asset Options must be a JSON object, not a " +
                $"{parsed?.GetValueKind().ToString() ?? "null"}.");

        return obj;
    }

    private static IReadOnlyList<MemberSpec> ParseMembers(JsonObject options, IRegion region)
    {
        if (!options.TryGetPropertyValue(MembersOptionKey, out var node) || node is not JsonArray array)
            throw new InvalidOperationException(
                $"Region '{region.RegionName}': Asset Options must set a \"{MembersOptionKey}\" " +
                "array. That array IS the container -- without it there is nothing to unpack into.");

        if (array.Count == 0)
            throw new InvalidOperationException(
                $"Region '{region.RegionName}': \"{MembersOptionKey}\" is empty. Every byte of the " +
                "buffer belongs to some member, so a container has at least one -- a buffer nobody " +
                "has decomposed yet is ONE verbatim member covering all of it, not zero.");

        var members = new List<MemberSpec>();
        var cursor = 0;
        for (var i = 0; i < array.Count; ++i)
        {
            var where = $"Region '{region.RegionName}': {MembersOptionKey}[{i}]";
            if (array[i] is not JsonObject entry)
                throw new InvalidOperationException(
                    $"{where} must be an object with \"name\", \"at\", \"len\", \"type\" and \"sha256\".");

            var name = RequiredString(entry, "name", where).Replace('\\', '/').Trim('/');
            if (members.Any(m => m.Name == name))
                throw new InvalidOperationException(
                    $"{where}: member name '{name}' is declared twice. Names are file paths -- two " +
                    "members sharing one would overwrite each other when the buffer is split.");

            var at = RequiredInt(entry, "at", 0, where);
            var length = RequiredInt(entry, "len", 1, where);

            // Members tile the buffer: no hole, no overlap, ascending. Declaration order is data,
            // because reassembly concatenates in it -- a list that is merely a permutation would
            // rebuild the buffer scrambled and still pass every length check downstream.
            if (at != cursor)
            {
                var problem = at > cursor
                    ? $"leaves a HOLE at bytes 0x{cursor:X}..0x{at:X} ({at - cursor} unclaimed)"
                    : $"OVERLAPS the member before it at bytes 0x{at:X}..0x{cursor:X} " +
                      $"({cursor - at} bytes claimed twice)";
                throw new InvalidOperationException(
                    $"{where} ('{name}') {problem}. Members must tile the buffer exactly, in " +
                    "ascending order -- declare any gap as an explicit verbatim member rather " +
                    "than losing those bytes.");
            }

            cursor = at + length;

            members.Add(new MemberSpec(
                Name: name,
                At: at,
                Length: length,
                Type: RequiredString(entry, "type", where),
                Sha256: RequiredSha256(entry, where),
                Version: OptionalString(entry, "ver", where),
                Options: OptionalObjectText(entry, "options", where)));
        }

        return members;
    }

    private static string RequiredString(JsonObject entry, string key, string where)
    {
        if (!entry.TryGetPropertyValue(key, out var node) || node == null ||
            node.GetValueKind() != JsonValueKind.String)
            throw new InvalidOperationException($"{where}: \"{key}\" must be a non-empty string.");
        var value = node.GetValue<string>();
        if (string.IsNullOrWhiteSpace(value))
            throw new InvalidOperationException($"{where}: \"{key}\" must not be empty.");
        return value;
    }

    private static string OptionalString(JsonObject entry, string key, string where)
    {
        if (!entry.TryGetPropertyValue(key, out var node) || node == null)
            return null;
        return RequiredString(entry, key, where);
    }

    private static int RequiredInt(JsonObject entry, string key, int minimum, string where)
    {
        if (!entry.TryGetPropertyValue(key, out var node) || node == null ||
            node.GetValueKind() != JsonValueKind.Number || !node.AsValue().TryGetValue<int>(out var value))
            throw new InvalidOperationException($"{where}: \"{key}\" must be an integer.");
        if (value < minimum)
            throw new InvalidOperationException(
                $"{where}: \"{key}\" must be >= {minimum}, got {value}." +
                (key == "len" ? " A zero-length member claims no bytes and cannot be told apart " +
                                "from a typo; drop it instead." : ""));
        return value;
    }

    /// <summary>
    /// The member's hash, checked for shape here and for truth by the build. Diz cannot compute
    /// it -- the bytes it covers do not exist until the container has been unpacked -- so the
    /// only thing it can do is refuse a value that could never match anything, and refuse to
    /// leave it out. Leaving it out would make the member indistinguishable from a hand-authored
    /// asset, which is exactly the class of asset the codecs decode without checking.
    /// </summary>
    private static string RequiredSha256(JsonObject entry, string where)
    {
        var value = RequiredString(entry, "sha256", where);
        if (value.Length != 64 || !value.All(c => c is >= '0' and <= '9' or >= 'a' and <= 'f'))
            throw new InvalidOperationException(
                $"{where}: \"sha256\" must be 64 lowercase hex digits -- the sha256 of this " +
                $"member's bytes as they appear in the unpacked buffer -- got \"{value}\". It is " +
                "what the build checks the unpacked bytes against, and the only thing that can " +
                "catch a decomposition that is plausible but wrong.");
        return value;
    }

    private static string OptionalObjectText(JsonObject entry, string key, string where)
    {
        if (!entry.TryGetPropertyValue(key, out var node) || node == null)
            return null;
        if (node is not JsonObject obj)
            throw new InvalidOperationException(
                $"{where}: \"{key}\" must be a JSON object of codec parameters, not a " +
                $"{node.GetValueKind()}.");
        return obj.ToJsonString();
    }

    /// <summary>
    /// A member presented to a leaf exporter as the region it would have been. It is not a
    /// project region and never becomes one: it has no address range, because a member has no
    /// place in the ROM of its own -- its bytes live inside the container's compressed span.
    /// Everything a leaf exporter reads to describe an asset (name, type, version, options) is
    /// carried; the address fields are the ones the member source envelope deliberately omits.
    /// </summary>
    private sealed class MemberRegion : IRegion
    {
        public MemberRegion(MemberSpec member)
        {
            RegionName = member.Name;
            AssetName = member.Name;
            AssetType = member.Type;
            AssetVersion = member.Version;
            AssetOptions = member.Options;
        }

        public int StartSnesAddress { get; set; }
        public int EndSnesAddress { get; set; }
        public string RegionName { get; set; }
        public string ContextToApply { get; set; }
        public int Priority { get; set; }
        public bool ExportSeparateFile { get; set; }
        public RegionExportType ExportType { get; set; } = RegionExportType.Asset;
        public string AssetType { get; set; }
        public string AssetVersion { get; set; }
        public string AssetName { get; set; }
        public string AssetOptions { get; set; }

        public event PropertyChangedEventHandler PropertyChanged
        {
            add { }
            remove { }
        }
    }
}
