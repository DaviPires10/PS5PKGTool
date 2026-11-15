namespace PS5PKGTool.Core.Services;

/// <summary>
/// Case rules for <em>host filesystem</em> paths. Windows treats paths as case-insensitive, while
/// Linux and macOS filesystems distinguish case, so folding case there made two different folders
/// (or files) look like the same entry.
/// </summary>
/// <remarks>
/// Only paths that point at the real filesystem belong here. Paths inside a package image
/// (exFAT/PFS/CNT entries) and labels, ids and tokens keep their own comparison rules.
/// </remarks>
public static class PathIdentity
{
    /// <summary>Comparison to use for host paths on this platform.</summary>
    public static StringComparison Comparison =>
        OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;

    /// <summary>Comparer/hash-set equality to use for host paths on this platform.</summary>
    public static StringComparer Comparer =>
        OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal;

    /// <summary>True when two host paths name the same entry.</summary>
    public static bool Equals(string? left, string? right) => string.Equals(left, right, Comparison);

    /// <summary>
    /// True when <paramref name="path"/> is <paramref name="folder"/> itself or sits below it.
    /// </summary>
    public static bool IsSameOrUnder(string path, string folder)
    {
        string prefix = folder.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        return path.Equals(prefix, Comparison) ||
            path.StartsWith(prefix + Path.DirectorySeparatorChar, Comparison);
    }
}
