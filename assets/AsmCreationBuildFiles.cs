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

    /// <summary>
    /// The service the assembly pass exported assets through. Read after that pass for the
    /// build nodes it accumulated: what was actually exported, straight from the exporters that
    /// wrote the manifests, rather than a second reading of the project's regions.
    /// </summary>
    public IRegionAssetExportService AssetExportService { get; init; }

    public BuildFileGeneratorSettings GeneratorSettings { get; init; }

    protected override void Execute()
    {
        if (string.IsNullOrEmpty(ExportRootDir))
            return;

        var assets = AssetExportService?.ExportedBuildNodes ?? [];

        new BuildFileGenerator(GeneratorSettings).WriteTo(ExportRootDir, assets);

        // vendor the codecs in, so the game repo can build with no Diz present
        var vendoring = new ToolVendoring();
        vendoring.VendorInto(ExportRootDir);
        vendoring.WriteWrapper(ExportRootDir);
        vendoring.WriteBuildingDoc(ExportRootDir);
    }
}
