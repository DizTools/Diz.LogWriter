using System;
using System.IO;
using System.Linq;
using System.Text;

namespace Diz.LogWriter.assets;

/// <summary>
/// Loads the generated-file texts embedded from assets/templates/. Each template's bytes
/// ARE the output bytes -- writers emit the loaded text verbatim, so trailing-newline
/// choices live in the template files, not in code.
///
/// Direction note: templates are static text today. If one ever needs dynamic values,
/// the plan is to introduce a Twig-style template engine (e.g. Scriban) and render --
/// NOT to grow C# string interpolation/concatenation here.
/// </summary>
public static class Templates
{
    /// <summary>
    /// Load an embedded template by file name (e.g. "BUILDING.md"). Reads as UTF-8 and
    /// normalizes CRLF to LF as a safety net against line-ending mangling on checkout.
    /// </summary>
    public static string Load(string fileName)
    {
        var assembly = typeof(Templates).Assembly;
        var resourceName = assembly.GetManifestResourceNames()
            .SingleOrDefault(n => n.EndsWith("." + fileName, StringComparison.Ordinal));
        if (resourceName == null)
            throw new FileNotFoundException(
                $"Embedded template '{fileName}' not found in assembly '{assembly.GetName().Name}'. " +
                "Check assets/templates/ and the EmbeddedResource entry in Diz.LogWriter.csproj.");

        using var stream = assembly.GetManifestResourceStream(resourceName)
            ?? throw new FileNotFoundException($"Embedded template stream '{resourceName}' could not be opened.");
        using var reader = new StreamReader(stream, Encoding.UTF8);
        return reader.ReadToEnd().Replace("\r\n", "\n");
    }
}
