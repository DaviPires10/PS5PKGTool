using System.Globalization;
using ProsperoPkgTool.Containers;

namespace PS5PKGTool.Core.Services;

/// <summary>Free-space preflight outcome, mirroring the engine without exposing its types.</summary>
public enum Ps5DiskSpaceStatus
{
    Ok,
    NearLimit,
    Insufficient
}

/// <summary>Result of a free-space preflight, with a human-readable description.</summary>
public readonly record struct Ps5DiskSpaceCheck(Ps5DiskSpaceStatus Status, string Message);

/// <summary>
/// Free-space preflight for package builds. The estimate mirrors the engine's model (the engine still
/// re-checks exactly while building), while the reporting is kept in this project so the UI can show a
/// compact, organized summary without changing the vendored engine.
/// </summary>
public static class Ps5DiskSpace
{
    private const long Megabyte = 1024L * 1024;
    private const int SafetyPercent = 12;   // error threshold: estimate + 12%
    private const int WarnPercent = 25;     // near-limit threshold: estimate + 25%
    private const long HeadroomBytes = 512L * Megabyte;

    /// <summary>Estimates and checks free space for a package build.</summary>
    /// <param name="rawPayloadBytes">Source payload upper bound (dump total, or image file length).</param>
    /// <param name="outputPath">Destination package path.</param>
    /// <param name="tempDirectory">Workspace folder, or null for the system temp folder.</param>
    public static Ps5DiskSpaceCheck Check(long rawPayloadBytes, string outputPath, string? tempDirectory) =>
        Evaluate(rawPayloadBytes, stagingBytes: 0, outputPath, tempDirectory);

    /// <summary>
    /// Free-space preflight for an extract-then-build: the extracted staging tree plus the build's
    /// temp workspace and output, measured against the affected volumes.
    /// </summary>
    public static Ps5DiskSpaceCheck CheckStagedImage(long rawPayloadBytes, long stagingBytes,
        string outputPath, string? tempDirectory) =>
        Evaluate(rawPayloadBytes, Math.Max(0, stagingBytes), outputPath, tempDirectory);

    /// <summary>True when the exception is the engine's insufficient-free-space failure.</summary>
    public static bool IsInsufficient(Exception exception) => exception is ProsperoInsufficientSpaceException;

    /// <summary>The engine's free-space message for an insufficient-space exception.</summary>
    public static string Describe(Exception exception) =>
        exception is ProsperoInsufficientSpaceException space ? space.Message : exception.Message;

    private static Ps5DiskSpaceCheck Evaluate(long rawPayloadBytes, long stagingBytes,
        string outputPath, string? tempDirectory)
    {
        (long workspaceBytes, long outputBytes) = EstimateParts(Math.Max(0, rawPayloadBytes));

        string workspaceRoot = RootOf(tempDirectory ?? Path.GetTempPath());
        string outputRoot = RootOf(outputPath);
        bool sharedVolume = string.Equals(workspaceRoot, outputRoot, StringComparison.OrdinalIgnoreCase);

        var volumes = new List<VolumeUsage>
        {
            new(workspaceRoot, workspaceBytes + stagingBytes, workspaceBytes, stagingBytes, outputBytes: 0),
        };
        if (sharedVolume)
        {
            volumes[0] = new VolumeUsage(workspaceRoot, workspaceBytes + stagingBytes + outputBytes,
                workspaceBytes, stagingBytes, outputBytes);
        }
        else
        {
            volumes.Add(new VolumeUsage(outputRoot, outputBytes, workspaceBytes: 0, stagingBytes: 0, outputBytes));
        }

        Ps5DiskSpaceStatus status = Ps5DiskSpaceStatus.Ok;
        foreach (VolumeUsage volume in volumes)
        {
            volume.AvailableBytes = AvailableFreeSpaceOrMax(volume.Root);
            volume.RequiredBytes = WithMargin(volume.Bytes, SafetyPercent);
            volume.Status = volume.AvailableBytes < volume.RequiredBytes
                ? Ps5DiskSpaceStatus.Insufficient
                : volume.AvailableBytes < WithMargin(volume.Bytes, WarnPercent)
                    ? Ps5DiskSpaceStatus.NearLimit
                    : Ps5DiskSpaceStatus.Ok;
            if (volume.Status > status) status = volume.Status;
        }

        return new Ps5DiskSpaceCheck(status, Describe(volumes, sharedVolume, stagingBytes > 0));
    }

    private static string Describe(List<VolumeUsage> volumes, bool sharedVolume, bool hasStaging)
    {
        // Only report the tightest volume: the one with the least free space. On a shared volume both
        // the workspace and the output compete for the same space, so show their breakdown.
        VolumeUsage tightest = volumes[0];
        foreach (VolumeUsage volume in volumes)
        {
            if (volume.AvailableBytes < tightest.AvailableBytes)
                tightest = volume;
        }

        var lines = new List<string>();
        if (sharedVolume)
        {
            lines.Add($"Workspace and output share {tightest.Root}.");
            lines.Add(string.Empty);
            lines.Add($"  Workspace needs  ~{Gigabytes(tightest.WorkspaceBytes)} GB");
            if (hasStaging)
                lines.Add($"  Staging needs    ~{Gigabytes(tightest.StagingBytes)} GB");
            lines.Add($"  Output needs     ~{Gigabytes(tightest.OutputBytes)} GB");
            lines.Add($"  Total required   ~{Gigabytes(tightest.RequiredBytes)} GB");
            lines.Add($"  Available        ~{Gigabytes(tightest.AvailableBytes)} GB free");
            if (tightest.Status == Ps5DiskSpaceStatus.Insufficient)
                lines.Add($"  Short by         ~{Gigabytes(tightest.RequiredBytes - tightest.AvailableBytes)} GB");
        }
        else
        {
            string role = tightest.OutputBytes > 0 ? "output" : "workspace";
            lines.Add($"Least free space: {tightest.Root} ({role})");
            lines.Add(string.Empty);
            if (tightest.OutputBytes > 0)
            {
                lines.Add($"  Output needs     ~{Gigabytes(tightest.OutputBytes)} GB");
            }
            else
            {
                lines.Add($"  Workspace needs  ~{Gigabytes(tightest.WorkspaceBytes)} GB");
                if (hasStaging)
                    lines.Add($"  Staging needs    ~{Gigabytes(tightest.StagingBytes)} GB");
            }
            lines.Add($"  Total required   ~{Gigabytes(tightest.RequiredBytes)} GB");
            lines.Add($"  Available        ~{Gigabytes(tightest.AvailableBytes)} GB free");
            if (tightest.Status == Ps5DiskSpaceStatus.Insufficient)
                lines.Add($"  Short by         ~{Gigabytes(tightest.RequiredBytes - tightest.AvailableBytes)} GB");
        }

        return string.Join(Environment.NewLine, lines);
    }

    private static (long Workspace, long Output) EstimateParts(long rawPayloadBytes)
    {
        // Peak model mirroring DiskSpaceGuard.Estimate: the inner image is built into the workspace,
        // then the outer PFS is written straight into the final package (no full-image copy).
        long inner = rawPayloadBytes + Math.Max(16 * Megabyte, rawPayloadBytes / 100);
        long outer = AlignUp(inner, ProsperoOuterPfsBuilder.BlockSize) + 32 * Megabyte;
        long output = ProsperoFihBuilder.HeaderRegionSize + outer + 64 * Megabyte;
        long workspace = inner + 64 * Megabyte;
        return (workspace, output);
    }

    private static long WithMargin(long bytes, int percent)
    {
        try
        {
            return checked((long)Math.Ceiling(bytes * (1.0 + percent / 100.0)) + HeadroomBytes);
        }
        catch (OverflowException)
        {
            return long.MaxValue;
        }
    }

    /// <summary>
    /// Root of the volume that actually holds <paramref name="path"/>. Unlike <see cref="Path.GetPathRoot"/>,
    /// which collapses every Unix path to <c>/</c>, this resolves mounts so a folder on another partition is
    /// not reported against the system volume.
    /// </summary>
    public static string VolumeRootOf(string path) => RootOf(path);

    /// <summary>Free bytes available on <paramref name="root"/>, or null when the volume cannot be read.</summary>
    public static long? AvailableFreeSpace(string root)
    {
        long bytes = AvailableFreeSpaceOrMax(root);
        return bytes == long.MaxValue ? null : bytes;
    }

    private static long AvailableFreeSpaceOrMax(string root)
    {
        try
        {
            return new DriveInfo(root).AvailableFreeSpace;
        }
        catch (ArgumentException) { return long.MaxValue; }
        catch (IOException) { return long.MaxValue; }
        catch (UnauthorizedAccessException) { return long.MaxValue; }
    }

    private static string RootOf(string path)
    {
        try
        {
            string full = Path.GetFullPath(path);
            // Resolve the volume that actually holds the path. Path.GetPathRoot only returns the
            // filesystem root ("/" on Unix), which collapses distinct mounts onto one volume and
            // makes a workspace on another partition look like the system volume.
            string probe = full;
            while (!string.IsNullOrEmpty(probe) && !Directory.Exists(probe))
                probe = Path.GetDirectoryName(probe) ?? string.Empty;
            if (string.IsNullOrEmpty(probe))
                probe = Path.GetPathRoot(full) ?? full;
            try
            {
                DriveInfo drive = new(probe);
                if (!string.IsNullOrWhiteSpace(drive.RootDirectory.FullName))
                    return drive.RootDirectory.FullName;
            }
            catch (ArgumentException) { }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
            return Path.GetPathRoot(full) ?? full;
        }
        catch (ArgumentException) { return path; }
        catch (NotSupportedException) { return path; }
        catch (PathTooLongException) { return path; }
    }

    private static long AlignUp(long value, long block) =>
        value <= 0 ? block : (value + block - 1) / block * block;

    private static string Gigabytes(long bytes) =>
        (bytes / (double)(Megabyte * 1024)).ToString("N1", CultureInfo.InvariantCulture);

    private sealed class VolumeUsage(string root, long bytes, long workspaceBytes, long stagingBytes, long outputBytes)
    {
        public string Root { get; } = root;
        public long Bytes { get; } = bytes;
        public long WorkspaceBytes { get; } = workspaceBytes;
        public long StagingBytes { get; } = stagingBytes;
        public long OutputBytes { get; } = outputBytes;
        public long RequiredBytes { get; set; }
        public long AvailableBytes { get; set; }
        public Ps5DiskSpaceStatus Status { get; set; }
    }
}
