using System;
using System.Collections.Generic;
using System.Linq;

namespace Diz.LogWriter.assets;

/// <summary>
/// How to run one transform stage of a container's pipeline in the ninja graph: the codec that
/// maps the stored bytes to the buffer the members tile, and back.
///
/// The counterpart of <see cref="BuildToolBinding"/>, which does the same job for a leaf asset's
/// codec. The split exists because the two run at different points: a leaf codec turns bytes into
/// an editable file, a stage codec turns bytes into other bytes.
///
/// A stage is selected by the option block the region author wrote (<see cref="OptionKey"/>), so
/// authoring a compression mode and getting the matching pair of build rules is one act, not two
/// that can disagree.
///
/// Unlike the leaf codecs, a stage codec is usually specific to one game's format, so it does not
/// ship in the shared tool set: it lives under its own key and is copied into an exported repo
/// only when that repo actually declares a stage that uses it.
/// </summary>
public sealed class BuildStageBinding
{
    /// <summary>
    /// Key of the option block on the container region that selects this stage, e.g. "lz". Also
    /// the manifest key the stage's parameters are written under, and the prefix of the ninja
    /// variables the rules read (block key "lz" + parameter "mode" =&gt; $lz_mode).
    /// </summary>
    public string OptionKey { get; init; }

    /// <summary>Codec contract written into the manifest's pipeline entry.</summary>
    public string Codec { get; init; }

    /// <summary>
    /// Key of the game-specific tool set holding this codec. Names a directory of the tools
    /// shipped with Diz, and decides which tools get copied into an exported repo.
    /// </summary>
    public string GameToolKey { get; init; }

    /// <summary>Ninja rule name for the extract direction, e.g. "ctlz_decompress".</summary>
    public string DecodeRule { get; init; }

    /// <summary>Ninja variable name of the extract-direction script, referenced as $ToolVar.</summary>
    public string DecodeToolVar { get; init; }

    /// <summary>Script filename under the vendored game-tool dir, e.g. "ctlz.py".</summary>
    public string DecodeToolFile { get; init; }

    /// <summary>Full `command =` line body for the extract-direction rule.</summary>
    public string DecodeCommand { get; init; }

    /// <summary>Ninja rule name for the build direction, e.g. "ctlz_compress".</summary>
    public string EncodeRule { get; init; }

    /// <summary>Ninja variable name of the build-direction script, referenced as $ToolVar.</summary>
    public string EncodeToolVar { get; init; }

    /// <summary>Script filename under the vendored game-tool dir, e.g. "ctlzpack.py".</summary>
    public string EncodeToolFile { get; init; }

    /// <summary>Full `command =` line body for the build-direction rule.</summary>
    public string EncodeCommand { get; init; }

    /// <summary>
    /// Every (var, file) pair this stage needs declared in the generated build. Distinct, because
    /// a stage whose two directions live in one script would otherwise declare it twice.
    /// </summary>
    public IEnumerable<(string ToolVar, string ToolFile)> ToolFiles =>
        new[] { (DecodeToolVar, DecodeToolFile), (EncodeToolVar, EncodeToolFile) }
            .Where(t => t.Item1 != null && t.Item2 != null)
            .Distinct();
}

/// <summary>
/// The registered pipeline stages. This is the one place a new container transform gets added:
/// register a stage, and containers whose options carry its block pick it up.
/// </summary>
public static class BuildStageBindings
{
    /// <summary>
    /// Directory (relative to the export root of a game repo) that game-specific tools are
    /// copied into. Regenerated on every export, exactly like the shared tool dir.
    /// </summary>
    public const string VendorDir = "tools/vendor/game";

    /// <summary>
    /// The LZSS variant used by Chrono Trigger. The offset width ("mode") is per-blob metadata
    /// that cannot be derived from the plaintext -- the encoder needs to be told which one the
    /// original used, or it produces valid but different bytes -- so it is authored on the region
    /// and travels in the manifest. Every other encoder knob is pinned inside the tool and
    /// deliberately absent from the command line, because a forgotten flag would silently produce
    /// plausible wrong bytes.
    ///
    /// Both commands are also handed the container's manifest, which every stage edge already
    /// depends on. That makes the mode's two records -- the one baked into this command line at
    /// export time and the one in the manifest -- checkable against each other while the build
    /// runs, so a manifest edited without a re-export halts instead of being silently overridden.
    /// It also lets the encoder compare what it produced against the bytes the manifest says came
    /// out of the ROM, and report a rebuilt blob that changed size, whose real consequence would
    /// otherwise surface much later as an unrelated-looking assembler error.
    /// </summary>
    public static readonly BuildStageBinding CtLzss = new()
    {
        OptionKey = "lz",
        Codec = "compress.ct.lzss",
        GameToolKey = "ct",

        DecodeRule = "ctlz_decompress",
        DecodeToolVar = "ctlz",
        DecodeToolFile = "ctlz.py",
        DecodeCommand =
            "python $ctlz decompress --in $in --out $out --expect-mode $lz_mode --manifest $manifest",

        EncodeRule = "ctlz_compress",
        EncodeToolVar = "ctlzpack",
        EncodeToolFile = "ctlzpack.py",
        EncodeCommand =
            "python $ctlzpack compress --in $in --out $out --mode $lz_mode --manifest $manifest",
    };

    public static readonly IReadOnlyList<BuildStageBinding> Default = [CtLzss];

    /// <summary>Stage whose option block is <paramref name="optionKey"/>, or null.</summary>
    public static BuildStageBinding ByOptionKey(string optionKey) =>
        Default.FirstOrDefault(b => string.Equals(b.OptionKey, optionKey, StringComparison.Ordinal));

    /// <summary>The registered option keys, for error messages that can name the alternatives.</summary>
    public static IEnumerable<string> OptionKeys => Default.Select(b => b.OptionKey);
}
