namespace BejView.Tests;

/// <summary>Finds files in the repo (the folder holding BejView.slnx), on Windows and in the WSL copy alike.</summary>
internal static class TestPaths
{
    public static string RepoRoot { get; } = Find();

    private static string Find()
    {
        for (var d = new DirectoryInfo(AppContext.BaseDirectory); d != null; d = d.Parent)
            if (File.Exists(Path.Combine(d.FullName, "BejView.slnx"))) return d.FullName;
        throw new InvalidOperationException($"BejView.slnx not found above {AppContext.BaseDirectory}.");
    }
}
