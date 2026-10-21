using LibProsperoPkg;
using LibProsperoPkg.PKG;

namespace PS5PKGTool.Core.Backends;

/// <summary>Inputs for a LibProsperoPkg directory build.</summary>
internal sealed class LibProsperoPkgBuildRequest
{
    public required string SourceFolder { get; init; }
    public required string OutputFolder { get; init; }
    public required string ContentId { get; init; }
    public required string Passcode { get; init; }

    /// <summary>
    /// DRM bucket for a generated <c>sce_sys/param.json</c> ("free", "standard", "freemium"). Null
    /// leaves the generated file at the engine default. An existing param.json is always used
    /// verbatim, so a DRM override on a real dump is applied by patching that file (see
    /// <see cref="LppBackend"/>).
    /// </summary>
    public string? DrmType { get; init; }

    /// <summary>Convert raw ELF modules into debug fake-SELF containers before packing.</summary>
    public bool FakeSignModules { get; init; }

    /// <summary>Build a license-free debug package (no license record; the mount key is derived).</summary>
    public bool LicenseFree { get; init; }

    public Action<string>? Log { get; init; }
}

/// <summary>
/// The LibProsperoPkg engine, compiled from source as part of this solution out of the
/// <c>third_party/LibProsperoPkg</c> git submodule. The library is linked directly, so there is no
/// isolated load context and no vendored assembly payload any more; the public surface used here is
/// deliberately tiny.
/// </summary>
internal static class LibProsperoPkgEngine
{
    /// <summary>True when the engine is available. It is linked into the app, so this is always true.</summary>
    public static bool Available => true;

    /// <summary>Builds a package from a folder and returns the produced package path.</summary>
    public static string Build(LibProsperoPkgBuildRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);

        var options = new ProsperoBuildOptions
        {
            Mode = ProsperoPackageMode.Application,
            OutputFormat = ProsperoOutputFormat.DebugImage,
            SourceFolder = request.SourceFolder,
            OutputFolder = request.OutputFolder,
            ContentId = request.ContentId,
            Passcode = request.Passcode,
            ApplicationDrmType = request.DrmType,
            FakeSignSelfModules = request.FakeSignModules,
            LicenseFree = request.LicenseFree,
        };

        ProsperoBuildResult result = ProsperoPackageBuilder.Build(options, request.Log);
        foreach (string warning in result.Warnings)
            request.Log?.Invoke("Warning: " + warning);
        return result.OutputPath;
    }

    /// <summary>Validates a package against the console-side acceptance gate.</summary>
    public static (bool Valid, string Message) Validate(string packagePath)
    {
        try
        {
            ProsperoAcceptanceReport report = ProsperoPkgValidator.Validate(packagePath);
            string message = string.Join("; ", report.Checks.Select(check =>
                check.Name + ": " + check.Detail));
            return (report.Accepted, message);
        }
        catch (Exception ex)
        {
            return (false, ex.Message);
        }
    }

    /// <summary>Extracts a package's inner filesystem and returns the number of files written.</summary>
    public static int Extract(string packagePath, string outputDirectory, string passcode)
    {
        ProsperoPackageManifest manifest =
            ProsperoPackageExtractor.Extract(packagePath, outputDirectory, passcode);
        return manifest.ExtractedFileCount;
    }
}
