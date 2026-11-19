using System.Globalization;
using System.Runtime.InteropServices;

namespace Tabseeker.Core;

public static class Format
{
    public static string Size(long bytes)
    {
        if (bytes < 0) return "";
        string[] units = { "B", "KB", "MB", "GB", "TB" };
        double v = bytes;
        int u = 0;
        while (v >= 1024 && u < units.Length - 1) { v /= 1024; u++; }
        return u == 0 ? $"{bytes:N0} B" : v.ToString(v < 10 ? "0.0" : "0", CultureInfo.InvariantCulture) + " " + units[u];
    }
}

/// <summary>Explorer-like "natural" ordering: file2 &lt; file10.</summary>
public sealed class NaturalComparer : IComparer<string>
{
    public static readonly NaturalComparer Instance = new();

    [DllImport("shlwapi.dll", CharSet = CharSet.Unicode)]
    private static extern int StrCmpLogicalW(string a, string b);

    public int Compare(string? x, string? y)
    {
        if (ReferenceEquals(x, y)) return 0;
        if (x == null) return -1;
        if (y == null) return 1;
        return OperatingSystem.IsWindows() ? StrCmpLogicalW(x, y) : string.Compare(x, y, StringComparison.OrdinalIgnoreCase);
    }
}
