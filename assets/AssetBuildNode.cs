using System.Collections.Generic;
using System.Text.Json.Nodes;

namespace Diz.LogWriter.assets;

/// <summary>
/// One node of the build graph an exported asset produces: what the build has to extract from
/// the ROM and recompile, and everything the ninja edges for it need. Produced by the exporter
/// that wrote the node's manifest, so the manifest and the build edges can never describe
/// different things -- deriving the graph a second time from the region would be free to drift.
///
/// Deliberately a flat record rather than an IRegion: the generator can be tested without a
/// project, and a future non-region asset source can feed it too.
///
/// The grammar is recursive. A leaf carries a typed block and no members; a container carries
/// members and no typed block, plus an optional transform pipeline between the ROM bytes and
/// the buffer its members tile.
/// </summary>
public sealed class AssetBuildNode
{
    /// <summary>Logical name, e.g. "gfx/font". Also the manifest path and the build-edge stem.</summary>
    public string Name { get; init; }

    /// <summary>Codec contract, e.g. "gfx.snes.2bpp". Selects the build tool binding by prefix.</summary>
    public string AssetType { get; init; }

    /// <summary>
    /// Layer-relative paths of files this node's codec reads but the manifest only NAMES
    /// (e.g. a text asset's character table). They become implicit deps of the extract edge,
    /// so editing one re-extracts everything that reads it -- otherwise the build would keep
    /// serving content decoded with the old file.
    /// </summary>
    public IReadOnlyList<string> SharedFiles { get; init; } = [];

    /// <summary>
    /// Transform stages between the ROM bytes and the buffer this node describes, outermost
    /// first (e.g. a decompression stage). Empty for a node whose bytes are used verbatim.
    /// </summary>
    public IReadOnlyList<AssetPipelineStage> Pipeline { get; init; } = [];

    /// <summary>
    /// Child nodes tiling this node's buffer, in declaration order. Empty for a leaf. A node
    /// has either members or a typed block, never both.
    /// </summary>
    public IReadOnlyList<AssetBuildNode> Members { get; init; } = [];
}

/// <summary>
/// One transform in a node's pipeline: a codec that maps the enclosing node's bytes to the
/// buffer the node's members tile, and back. The typed block carries whatever that codec
/// cannot derive and must be told (e.g. a per-blob compression mode), keyed the same way an
/// asset's typed block is.
/// </summary>
public sealed class AssetPipelineStage
{
    /// <summary>Codec contract for the stage, e.g. "compress.&lt;game&gt;.lzss".</summary>
    public string Codec { get; init; }

    /// <summary>Pinned codec version, or null for "latest".</summary>
    public string Version { get; init; }

    /// <summary>Key of the stage's typed block, e.g. "lz". Null when the stage takes no parameters.</summary>
    public string BlockKey { get; init; }

    /// <summary>The stage's typed block, e.g. { mode: 12 }. Null when the stage takes no parameters.</summary>
    public JsonObject Block { get; init; }
}
