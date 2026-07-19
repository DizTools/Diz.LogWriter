using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;

namespace Diz.LogWriter.assets;

/// <summary>
/// Copies the stock asset codecs out of the Diz install and into the exported game repo,
/// so the repo can rebuild the ROM with no Diz anywhere in the picture. Diz is an
/// authoring tool, not a build dependency.
/// </summary>
public class ToolVendoring
{
    // Vendored tools are REGENERATED on every export -- anything here can be clobbered.
    public const string VendorDir = "tools/vendor/dizpack";

    // Hand-authored, game-specific tools live here and are NEVER touched by export.
    // Searched BEFORE the vendor dir, so a project can override a stock codec without
    // forking Diz.
    public const string GameToolsDir = "tools/game";

    // Sentinel files that identify a real dizpack source dir during the walk-up. NOT the full
    // vendoring list -- the set of codecs to ship is DISCOVERED (see DiscoverToolFiles) so a
    // newly added codec (e.g. binpack.py) vendors automatically without editing this class.
    private static readonly string[] RequiredToolFiles = ["gfxpack.py", "requirements.txt"];

    // Extensions of files that get vendored out of the source dir. Everything the game repo
    // needs to run the codecs with no Diz present: the Python scripts and their pip manifest.
    private static readonly string[] VendorableExtensions = [".py", ".txt"];

    /// <summary>
    /// Every file in the source dir worth vendoring (top-level only; skips __pycache__ and other
    /// subdirs). Sorted for a deterministic, clean-diff copy order.
    /// </summary>
    private static IEnumerable<string> DiscoverToolFiles(string sourceToolsDir) =>
        !Directory.Exists(sourceToolsDir)
            ? [] // missing source degrades to "vendor nothing", same as the old per-file existence check
            : Directory.EnumerateFiles(sourceToolsDir)
                .Where(f => VendorableExtensions.Contains(Path.GetExtension(f), StringComparer.OrdinalIgnoreCase))
                .Select(Path.GetFileName)
                .OrderBy(n => n, StringComparer.Ordinal);

    /// <summary>
    /// Find the dizpack source directory shipped alongside Diz. Returns null if not found,
    /// which callers should treat as "skip vendoring" rather than a hard failure -- a dev
    /// build run from an IDE won't have the same layout as an installed copy.
    /// </summary>
    public static string FindSourceToolsDir(string searchStartDir = null)
    {
        // anchor on this assembly's location, not AppContext.BaseDirectory: when Diz is
        // loaded as a plugin (e.g. the PowerShell module), BaseDirectory is the HOST's
        // install dir and the walk-up would never find our tools.
        var dir = searchStartDir
                  ?? Path.GetDirectoryName(typeof(ToolVendoring).Assembly.Location)
                  ?? AppContext.BaseDirectory;

        // walk up looking for tools/dizpack -- handles both an installed layout and running
        // out of bin/Debug/net9.0-windows/ during development.
        for (var i = 0; i < 8 && !string.IsNullOrEmpty(dir); ++i)
        {
            var candidate = Path.Combine(dir, "tools", "dizpack");
            if (Directory.Exists(candidate) &&
                RequiredToolFiles.All(f => File.Exists(Path.Combine(candidate, f))))
            {
                return candidate;
            }

            dir = Path.GetDirectoryName(dir);
        }

        return null;
    }

    /// <summary>
    /// Copy the stock tools into the export root. Returns the list of files written,
    /// or an empty list if the source tools couldn't be located.
    /// </summary>
    public IReadOnlyList<string> VendorInto(string exportRootDir, string sourceToolsDir = null)
    {
        sourceToolsDir ??= FindSourceToolsDir();
        if (sourceToolsDir == null)
            return [];

        var destDir = Path.Combine(exportRootDir, VendorDir.Replace('/', Path.DirectorySeparatorChar));
        Directory.CreateDirectory(destDir);

        var written = new List<string>();
        foreach (var file in DiscoverToolFiles(sourceToolsDir))
        {
            var src = Path.Combine(sourceToolsDir, file);
            if (!File.Exists(src))
                continue;

            var dest = Path.Combine(destDir, file);
            File.Copy(src, dest, overwrite: true);
            written.Add(dest);
        }

        WriteReadme(destDir);
        return written;
    }

    private static void WriteReadme(string destDir)
    {
        File.WriteAllText(
            Path.Combine(destDir, "README.md"),
            Templates.Load("dizpack-README.md"),
            new UTF8Encoding(false));
    }

    /// <summary>
    /// Write the wrapper script: one command for the common case, plus a guard that keeps
    /// `verify` honest when mod layers are configured in build-config.ninja.
    /// </summary>
    public void WriteWrapper(string exportRootDir)
    {
        File.WriteAllText(
            Path.Combine(exportRootDir, "build-assets.sh"),
            Templates.Load("build-assets.sh"),
            new UTF8Encoding(false));
    }

    /// <summary>
    /// Write BUILDING.md at the repo root: the "I just want a ROM" quick start for people
    /// who will never look inside tools/. Regenerated on every export, like the wrapper.
    /// </summary>
    public void WriteBuildingDoc(string exportRootDir)
    {
        File.WriteAllText(
            Path.Combine(exportRootDir, "BUILDING.md"),
            Templates.Load("BUILDING.md"),
            new UTF8Encoding(false));
    }
}
