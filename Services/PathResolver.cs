using System;
using System.IO;

namespace TerminalStudio.Services;

public static class PathResolver
{
    private static readonly string[] ExecutableExtensions = { ".exe", ".cmd", ".bat", ".com" };

    public static bool TryResolve(string input, out string resolvedFullPath)
    {
        resolvedFullPath = string.Empty;
        if (string.IsNullOrWhiteSpace(input))
        {
            return false;
        }

        string trimmed = input.Trim().Trim('"');

        if (Path.IsPathRooted(trimmed) || trimmed.Contains(Path.DirectorySeparatorChar) || trimmed.Contains(Path.AltDirectorySeparatorChar))
        {
            if (File.Exists(trimmed))
            {
                resolvedFullPath = Path.GetFullPath(trimmed);
                return true;
            }

            foreach (var ext in ExecutableExtensions)
            {
                string withExt = trimmed + ext;
                if (File.Exists(withExt))
                {
                    resolvedFullPath = Path.GetFullPath(withExt);
                    return true;
                }
            }

            return false;
        }

        string baseDir = AppDomain.CurrentDomain.BaseDirectory;
        if (CheckDirectory(baseDir, trimmed, out resolvedFullPath))
        {
            return true;
        }

        string? pathEnv = Environment.GetEnvironmentVariable("PATH");
        if (!string.IsNullOrEmpty(pathEnv))
        {
            string[] paths = pathEnv.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            foreach (string dir in paths)
            {
                if (CheckDirectory(dir, trimmed, out resolvedFullPath))
                {
                    return true;
                }
            }
        }

        string localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        string windowsAppsDir = Path.Combine(localAppData, "Microsoft", "WindowsApps");
        if (CheckDirectory(windowsAppsDir, trimmed, out resolvedFullPath))
        {
            return true;
        }

        return false;
    }

    private static bool CheckDirectory(string directory, string fileName, out string resolvedFullPath)
    {
        resolvedFullPath = string.Empty;
        if (!Directory.Exists(directory))
        {
            return false;
        }

        try
        {
            string directFile = Path.Combine(directory, fileName);
            if (File.Exists(directFile))
            {
                resolvedFullPath = Path.GetFullPath(directFile);
                return true;
            }

            string ext = Path.GetExtension(fileName);
            if (string.IsNullOrEmpty(ext))
            {
                foreach (var tryExt in ExecutableExtensions)
                {
                    string candidate = Path.Combine(directory, fileName + tryExt);
                    if (File.Exists(candidate))
                    {
                        resolvedFullPath = Path.GetFullPath(candidate);
                        return true;
                    }
                }
            }
        }
        catch
        {
        }

        return false;
    }
}
