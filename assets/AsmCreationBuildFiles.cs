using System.Collections.Generic;
using System.Linq;
using Diz.Core.Interfaces;

namespace Diz.LogWriter.assets;

/// <summary>
/// Final export step: write the build system next to the generated assembly, and vendor the
/// asset tools in beside it.
///
/// Runs last because it needs to know which regions were actually exported as assets. Like
/// the .asm itself, everything it writes is generated-throwaway-but-checked-in: regenerating
/// should produce a clean diff, and a human should never hand-edit the output.
/// </summary>
public class AsmCreationBuildFiles : AsmCreationBase
{
    /// <summary>Absolute path of the export root (where main.asm and build.ninja live).</summary>
    public string ExportRootDir { get; init; }

    /// <summary>Regions from the project, used to work out what the build must rebuild.</summary>
    public IReadOnlyList<IRegion> Regions { get; init; }

    public BuildFileGeneratorSettings GeneratorSettings { get; init; }

    protected override void Execute()
    {
        if (string.IsNullOrEmpty(ExportRootDir))
            return;

        var assets = BuildFileGenerator.CollectAssets(Regions ?? []);

        new BuildFileGenerator(GeneratorSettings).WriteTo(ExportRootDir, assets);

        // vendor the codecs in, so the game repo can build with no Diz present
        var vendoring = new ToolVendoring();
        vendoring.VendorInto(ExportRootDir);
        vendoring.WriteWrapper(ExportRootDir);
    }
}
