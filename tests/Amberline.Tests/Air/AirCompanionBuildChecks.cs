using System.Diagnostics;

namespace Amberline.Tests.Air;

/// <summary>
/// NOT RETAIL: a public build deletes the companion's web-archive installer (ClientFilesInstaller.cs). This check copies the companion
/// and the projects it compiles from, without that file, and builds the copy: nothing else in the companion may name the file.
/// </summary>
public static class AirCompanionBuildChecks
{


    /// <summary>Copies the source files of a project folder, leaving out build output and <paramref name="skipFile"/> at its top level.</summary>
    private static void CopySources(string source, string destination, string? skipFile)
    {
        Directory.CreateDirectory(destination);
        foreach (var file in Directory.GetFiles(source))
            if (Path.GetFileName(file) != skipFile) File.Copy(file, Path.Combine(destination, Path.GetFileName(file)));
        foreach (var directory in Directory.GetDirectories(source))
            if (Path.GetFileName(directory) is not ("bin" or "obj"))
                CopySources(directory, Path.Combine(destination, Path.GetFileName(directory)), null);
    }

    private static string FindWorkspaceFile(string relativePath)
    {
        for (var directory = new DirectoryInfo(AppContext.BaseDirectory); directory is not null; directory = directory.Parent)
        {
            var candidate = Path.Combine(directory.FullName, relativePath);
            if (File.Exists(candidate)) return candidate;
        }
        throw new FileNotFoundException("The workspace file was not found.", relativePath);
    }
}
