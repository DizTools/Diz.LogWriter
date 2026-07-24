using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Xml;
using CsvHelper;
using Diz.Core.Interfaces;
using Diz.Core.model;
using Diz.Core.util;
using Diz.LogWriter.assemblyGenerators;
using ExtendedXmlSerializer;
using ExtendedXmlSerializer.Configuration;
using JetBrains.Annotations;
using YamlDotNet.Serialization;

namespace Diz.LogWriter;

public abstract class AsmStepExtraLabelOutputBase : AsmCreationBase
{
    public LabelTracker LabelTracker { get; init; }
        
    public static IOrderedEnumerable<int> GetLabelSnesAddressesSorted(IReadOnlyLabelProvider readOnlyLabelProvider)
    {
        // keep these sorted so that the output is consistent
        return readOnlyLabelProvider.Labels
            .Select(x=>x.Key)
            .OrderBy(x=>x);
    }
}

// TODO: we can probably refactor/recombine a few of these related classes together

// generate labels.asm. this is a list of labels that are NOT defined implicitly in the main .asm files
// this will be a list of "unvisited" labels. it will NOT contain VISITED labels.
//
// this would be better named as:
// "implicitly defined" (label address is inferred from its position in the assembly output and not explicitly defined, like ROM addresses)
// vs
// "explcititly defined" (label address is defined explicitly, i.e. RAM addresses)
public class AsmStepWriteUnvisitedLabels : AsmStepExtraLabelOutputBase
{
    protected override void Execute()
    {
        LogCreator.SwitchOutputStream("labels.asm");

        foreach (var (snesAddress, _) in LabelTracker.UnvisitedLabels)
        {
            WriteUnusedLabel(snesAddress);
        }
    }

    private void WriteUnusedLabel(int snesAddress)
    {
        var outputLines = LogCreator.LineGenerator.GenerateSpecialLines(
            type: "labelassign",
            context: new LineGenerator.TokenExtraContextSnes(snesAddress)
        );
        
        foreach (var outputLine in outputLines) {
            LogCreator.WriteLine(outputLine);
        }
    }
}

// write all labels, regardless of whether they're used.
// output in a format that could be used directly for disassembly or importing into bsnes/etc
// this is all optional: if requested, print all labels regardless of use.
// Useful for debugging, documentation, or reverse engineering workflow.
// this file shouldn't need to be included in the build, it's just reference documentation
public class AsmStepWriteAllLabels : AsmStepExtraLabelOutputBase
{
    public string OutputFilename { get; init; }

    protected override void Execute()
    {
        LogCreator.SwitchOutputStream(OutputFilename);
        OutputHeader();
            
        foreach (var labelSnesAddress in GetLabelSnesAddressesSorted(Data.Labels))
        {
            WriteLabel(labelSnesAddress);
        }
    }

    protected virtual void OutputHeader()
    {
        // NOP
    }

    private void WriteLabel(int snesAddress)
    {
        // maybe not the best place to add formatting
        var category = LabelTracker.UnvisitedLabels.ContainsKey(snesAddress) ? "UNUSED" : "USED";

        OutputLabelAtOffset(category, snesAddress);
    }

    protected virtual void OutputLabelAtOffset(string category, int snesAddress)
    {
        var attribution = BuildAttributionComment(snesAddress);
        var outputLines = LogCreator.LineGenerator.GenerateSpecialLines("labelassign", context: new LineGenerator.TokenExtraContextSnes(snesAddress));
        foreach (var outputLine in outputLines) {
            LogCreator.WriteLine($";!^!-{category}-! {outputLine}{attribution}");
        }
    }

    // Build a trailing asm-comment segment carrying label attribution, e.g.
    // "  ; [author=someone confidence=VeryHigh]". Only the parts that are actually set are
    // included (author only, confidence only, or both); an unspecified confidence ("") is
    // omitted, and an empty author is omitted. If neither is set, returns "" so the line is
    // byte-identical to output without attribution. Kept as a comment so the line stays valid
    // assembly. The label lookup can return null (guarded).
    private string BuildAttributionComment(int snesAddress)
    {
        var label = Data.Labels.GetLabel(snesAddress);
        if (label == null)
            return "";

        var author = label.Author ?? "";
        var confidence = label.Confidence ?? "";

        var segment = "";
        if (!string.IsNullOrWhiteSpace(author))
            segment = $"author={author}";
        if (confidence != "")
            segment += (segment.Length > 0 ? " " : "") + $"confidence={confidence}";

        return segment.Length == 0 ? "" : $"  ; [{segment}]";
    }
}

// same as above except output as a CSV file
public class AsmStepExtraOutputAllLabelsCsv : AsmStepWriteAllLabels
{
    [CanBeNull] private CsvWriter csvWriter;
        
    private record CsvLabelRecord
    {
        public CsvLabelRecord(AssemblyGenerateLabelAssign.PrintableLabelDataAtOffset printableData, string usedStatus)
        {
            Name = printableData.Name;
            Comment = printableData.Comment;
            Author = printableData.Author;
            Confidence = printableData.Confidence ?? "";
            UsedStatus = usedStatus;
            SnesAddress = printableData.SnesAddress;
            SnesAddressHex = printableData.GetSnesAddressFormatted();
        }

        // CsvHelper writes columns in property-declaration order, so the declaration order
        // below is the on-disk column order: SnesAddressHex, SnesAddress, Name, Comment,
        // Author, Confidence, UsedStatus. Author/Confidence are always present (blank when
        // unset) to keep a stable schema. Confidence is a string ("" for unspecified).
        public string SnesAddressHex { get; init; }

        public int SnesAddress { get; init; }

        public string Name { get; init; }
        public string Comment { get; init; }
        public string Author { get; init; }
        public string Confidence { get; init; }
        public string UsedStatus { get; init; }
    } 

    protected override void Execute()
    {
        string output;
        using (var writer = new StringWriter())
        using (csvWriter = new CsvWriter(writer, CultureInfo.InvariantCulture))
        {
            base.Execute();
            output = writer.ToString();
        }
            
        var lines = output.Split(["\r\n", "\r", "\n"], StringSplitOptions.None);
        foreach (var line in lines)
        {
            LogCreator.WriteLine(line);
        }
    }

    protected override void OutputHeader()
    {
        Debug.Assert(csvWriter != null);
        csvWriter.WriteHeader<CsvLabelRecord>();
        csvWriter.NextRecord();
    }

    protected override void OutputLabelAtOffset(string category, int snesAddress)
    {
        if (csvWriter == null)
            return;
        
        var printableLabels = AssemblyGenerateLabelAssign.GetPrintableLabelsDataAtSnesAddress(snesAddress, Data.Labels);
        if (printableLabels == null || printableLabels.Count == 0)
            return;

        var csvLabelRecords = printableLabels
            .Select(printableLabel => new CsvLabelRecord(printableLabel, category));
        
        foreach (var labelRecord in csvLabelRecords)
        {
            csvWriter.WriteRecord(labelRecord);
            csvWriter.NextRecord();
        }
    }
}
    


// export all labels as XML 
public class AsmStepExtraOutputAllLabelsXml : AsmStepWriteAllLabels
{
    protected override void Execute()
    {
        // base.Execute(); // don't call, we're doing everything manually
        LogCreator.SwitchOutputStream(OutputFilename);
        
        var serializerConfig = GetLabelXmlSerializer();
        
        var xmlStr = serializerConfig.Create().Serialize(
            new XmlWriterSettings {Indent = true},
            Data.Labels.Labels
        );
        
        LogCreator.WriteLine(xmlStr); // writes entire XML file (with newlines etc)
    }
    
    public IConfigurationContainer GetLabelXmlSerializer()
    {
        return new ConfigurationContainer()
            .EnableImplicitTyping(typeof(ContextMapping))
            
            .Type<Label>()
            .EnableImplicitTyping()
            
            .UseOptimizedNamespaces()
            .UseAutoFormatting();
    }
}
    

// same as above except output as a BSNES .sym file, for use with the BSNES debugger
public class AsmStepExtraOutputBsneSymFile : AsmStepWriteAllLabels
{ 
    protected override void Execute()
    {
        // this will print all the labels
        base.Execute();
            
        // with that done, we also need to print the comments:
        LogCreator.WriteLine("\n");
        LogCreator.WriteLine("[comments]");
            
        foreach (var labelSnesAddress in GetLabelSnesAddressesSorted(Data.Labels))
        {
            var commentText = Data.GetCommentText(labelSnesAddress);
            if (commentText.Length == 0)
                continue;
                
            WriteLineBsnesFormattedAddressAndText(labelSnesAddress, commentText);
        }
    }
        
    // sample bsnes .sym file format:
        
    // [labels]
    // 00:2100 SNES.INIDISP
    // c2:0aae fn_some_function_battle_whatever
    //
    // [comments]
    // c0:0082 Read variable X and multiply by 42, that is the screen brightness
    // c0:0097 This is the main loop
        
    protected override void OutputHeader()
    {
        LogCreator.WriteLine("[labels]");
    }

    protected override void OutputLabelAtOffset(string category, int snesAddress)
    {
        var printableLabels = AssemblyGenerateLabelAssign.GetPrintableLabelsDataAtSnesAddress(snesAddress, Data.Labels);
        if (printableLabels == null || printableLabels.Count == 0)
            return;
            
        // we're going to exclude a few auto-generated labels from this just keep BSNES exports de-cluttered.
        foreach (var printableLabel in printableLabels)
        {
            if (printableLabel.Name.StartsWith("CODE_") || printableLabel.Name.StartsWith("LOOSE_OP_") || RomUtil.IsValidPlusMinusLabel(printableLabel.Name))
                return;

            WriteLineBsnesFormattedAddressAndText(printableLabel.SnesAddress, printableLabel.Name);
        }
    }

    private void WriteLineBsnesFormattedAddressAndText(int snesAddress, string text)
    {
        // always output snes addresses (not offsets)
        // "c2:0aae Well hey son this is some fancy label or comment text for BSNES"
        var printableSnesAddress = FormatAs24BitBsnesAddress(snesAddress);
        var formattedText = text.ReplaceLineEndings("");
        LogCreator.WriteLine($"{printableSnesAddress} {formattedText}");
    }

    private static string FormatAs24BitBsnesAddress(int address)
    {
        // print a 24bit number in the format BSNES .sym files like:
        // "c2:0aae"
        return address.ToString("X6").Insert(2, ":");

        // (this function doesn't care if it's SNES address or offset, it's printing the number)
    }
}

// Emit export-manifest.yaml: attribution statistics over EVERY persistent (project) label,
// INCLUDING labels whose author was excluded from the actual export output. The YAML body is
// produced by YamlDotNet over an insertion-ordered in-memory structure (so it handles quoting/
// escaping and key order). Skipped in single-file mode, which has no separate sidecar file.
public class AsmStepExtraOutputManifestYaml : AsmStepExtraLabelOutputBase
{
    // the bucket name for a label whose confidence is unspecified (""); it always sorts LAST in
    // the per-author confidence breakdown, after every named confidence level.
    private const string UnspecifiedConfidenceBucket = "unspecified";

    // labels with no author land here; it always sorts LAST, after every named author.
    private const string UnknownAuthorBucket = "unknown";

    protected override void Execute()
    {
        LogCreator.SwitchOutputStream("export-manifest.yaml");

        // Read the UNFILTERED persistent label set: never temporary labels, and never the
        // export author-filter -- so the stats report every author even when an export
        // exclusion is active. LogCreator already casts the shared label service to
        // LabelsServiceWithTemp for the export filter; the accessor is reached the same way.
        var labels = (Data.Labels as LabelsServiceWithTemp)?.AllPersistentLabelsUnfiltered
                     ?? Enumerable.Empty<KeyValuePair<int, IAnnotationLabel>>();

        // write line-by-line so each line gets the platform newline, consistent with the rest
        // of the export (rather than embedding raw '\n' from the builder).
        foreach (var line in BuildManifestYaml(labels.Select(kvp => kvp.Value)).Split('\n'))
            LogCreator.WriteLine(line);
    }

    private string BuildManifestYaml(IEnumerable<IAnnotationLabel> labels)
    {
        // author bucket -> (confidence bucket name -> count). the confidence buckets are dynamic
        // over whatever confidence strings actually appear: "" maps to "unspecified", any other
        // level is lowercased for the key (so "VeryHigh" -> "veryhigh").
        var buckets = new Dictionary<string, Dictionary<string, int>>();
        var total = 0;

        foreach (var label in labels)
        {
            total++;
            var author = label.Author ?? "";
            var bucketKey = string.IsNullOrWhiteSpace(author) ? UnknownAuthorBucket : author.Trim();

            if (!buckets.TryGetValue(bucketKey, out var counts))
            {
                counts = new Dictionary<string, int>();
                buckets[bucketKey] = counts;
            }

            var confidence = label.Confidence ?? "";
            var confidenceKey = string.IsNullOrEmpty(confidence)
                ? UnspecifiedConfidenceBucket
                : confidence.ToLowerInvariant();
            counts[confidenceKey] = counts.GetValueOrDefault(confidenceKey) + 1;
        }

        // named authors sorted case-insensitively ascending; the "unknown" bucket always last.
        var orderedAuthors = buckets.Keys
            .Where(k => k != UnknownAuthorBucket)
            .OrderBy(k => k, StringComparer.OrdinalIgnoreCase)
            .ToList();
        if (buckets.ContainsKey(UnknownAuthorBucket))
            orderedAuthors.Add(UnknownAuthorBucket);

        // Build the manifest as an insertion-ordered structure so YamlDotNet emits keys in this
        // exact order. Dictionary<string, object> preserves insertion order for enumeration, which
        // the serializer walks; nesting mirrors the export:/stats: shape.
        var attribution = new Dictionary<string, object>();
        foreach (var authorKey in orderedAuthors)
        {
            var counts = buckets[authorKey];

            // the manifest step has no access to the project's ConfidenceLevels vocabulary order,
            // so it cannot reproduce worst->best ordering. instead: named levels alphabetical,
            // "unspecified" always last, for a deterministic and stable output.
            var orderedConfidence = counts.Keys
                .Where(k => k != UnspecifiedConfidenceBucket)
                .OrderBy(k => k, StringComparer.Ordinal)
                .ToList();
            if (counts.ContainsKey(UnspecifiedConfidenceBucket))
                orderedConfidence.Add(UnspecifiedConfidenceBucket);

            var confidence = new Dictionary<string, object>();
            foreach (var confidenceKey in orderedConfidence)
                confidence[confidenceKey] = counts[confidenceKey];

            attribution[authorKey] = new Dictionary<string, object>
            {
                ["all"] = counts.Values.Sum(),
                ["confidence"] = confidence,
            };
        }

        var excluded = LogCreator.Settings.ExcludedLabelAuthors;
        var excludedAuthors = excluded == null ? new List<string>() : excluded.ToList();

        var root = new Dictionary<string, object>
        {
            ["export"] = new Dictionary<string, object>
            {
                ["excluded_authors"] = excludedAuthors,
            },
            ["stats"] = new Dictionary<string, object>
            {
                ["labels"] = new Dictionary<string, object>
                {
                    ["total"] = total,
                    ["attribution"] = attribution,
                },
            },
        };

        var body = new SerializerBuilder().Build().Serialize(root);

        // YamlDotNet doesn't emit leading document comments, so prepend the fixed banner by hand.
        const string banner =
            "# Diz export manifest\n" +
            "# Attribution stats cover EVERY persistent project label, including any authors\n" +
            "# excluded from the export output (listed under export.excluded_authors).\n";

        // WriteLine adds the final newline; avoid a doubled trailing blank line.
        return (banner + body).TrimEnd('\n');
    }
}