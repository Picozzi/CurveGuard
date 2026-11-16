namespace CurveGuard;

internal static class ProjectPaths
{
    public static string? RootDirectory { get; } = FindRootDirectory();

    public static string ReportDirectory => Path.Combine(RootDirectory ?? AppContext.BaseDirectory, "reports");

    private static string? FindRootDirectory()
    {
        for (DirectoryInfo? directory = new(AppContext.BaseDirectory); directory is not null; directory = directory.Parent)
        {
            if (File.Exists(Path.Combine(directory.FullName, "CurveGuard.csproj")))
                return directory.FullName;
        }

        return null;
    }
}
