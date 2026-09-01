using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using TerminalStudio.Models;

namespace TerminalStudio.Services;

public class ShellDiscoveryService
{
    private List<string>? _cachedWslDistros;
    private DateTime _lastWslScan = DateTime.MinValue;

    public async Task<List<ShellProfile>> GetDiscoveredProfilesAsync(IEnumerable<ShellProfile>? customProfiles = null)
    {
        var profiles = new List<ShellProfile>();

        profiles.Add(new ShellProfile
        {
            Id = "powershell",
            Title = "Windows PowerShell",
            CommandLine = "powershell.exe",
            Type = "PowerShell",
            ShortcutText = "Ctrl+Shift+1",
            IsCustom = false
        });

        if (TryFindPowerShell7(out string pwshCommand))
        {
            profiles.Add(new ShellProfile
            {
                Id = "pwsh",
                Title = "PowerShell 7",
                CommandLine = pwshCommand,
                Type = "PowerShell7",
                IsCustom = false
            });
        }

        profiles.Add(new ShellProfile
        {
            Id = "cmd",
            Title = "Command Prompt",
            CommandLine = "cmd.exe",
            Type = "CMD",
            ShortcutText = "Ctrl+Shift+2",
            IsCustom = false
        });

        if (TryFindGitBash(out string gitBashCommand))
        {
            profiles.Add(new ShellProfile
            {
                Id = "gitbash",
                Title = "Git Bash",
                CommandLine = gitBashCommand,
                Type = "GitBash",
                IsCustom = false
            });
        }

        var wslDistros = await GetWslDistrosAsync();
        foreach (var distro in wslDistros)
        {
            profiles.Add(new ShellProfile
            {
                Id = $"wsl_{distro}",
                Title = distro,
                CommandLine = $"wsl.exe -d \"{distro}\"",
                Type = "WSL",
                Distribution = distro,
                ShortcutText = distro.Equals(wslDistros[0], StringComparison.OrdinalIgnoreCase) ? "Ctrl+Shift+3" : null,
                IsCustom = false
            });
        }

        if (customProfiles != null)
        {
            foreach (var custom in customProfiles)
            {
                profiles.Add(custom);
            }
        }

        return profiles;
    }

    private static bool TryFindPowerShell7(out string commandLine)
    {
        if (PathResolver.TryResolve("pwsh.exe", out string resolvedPath))
        {
            commandLine = "pwsh.exe";
            return true;
        }

        string programFiles = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles);
        string pwshStandard = Path.Combine(programFiles, "PowerShell", "7", "pwsh.exe");
        if (File.Exists(pwshStandard))
        {
            commandLine = $"\"{pwshStandard}\"";
            return true;
        }

        commandLine = string.Empty;
        return false;
    }

    private static bool TryFindGitBash(out string commandLine)
    {
        string programFiles = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles);
        string gitBash64 = Path.Combine(programFiles, "Git", "bin", "bash.exe");
        if (File.Exists(gitBash64))
        {
            commandLine = $"\"{gitBash64}\" --login -i";
            return true;
        }

        string localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        string gitBashUser = Path.Combine(localAppData, "Programs", "Git", "bin", "bash.exe");
        if (File.Exists(gitBashUser))
        {
            commandLine = $"\"{gitBashUser}\" --login -i";
            return true;
        }

        string programFilesX86 = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86);
        string gitBash32 = Path.Combine(programFilesX86, "Git", "bin", "bash.exe");
        if (File.Exists(gitBash32))
        {
            commandLine = $"\"{gitBash32}\" --login -i";
            return true;
        }

        if (PathResolver.TryResolve("bash.exe", out string resolved))
        {
            if (!resolved.Contains("System32", StringComparison.OrdinalIgnoreCase))
            {
                commandLine = $"\"{resolved}\" --login -i";
                return true;
            }
        }

        commandLine = string.Empty;
        return false;
    }

    public async Task<List<string>> GetWslDistrosAsync()
    {
        if (_cachedWslDistros != null && (DateTime.UtcNow - _lastWslScan).TotalSeconds < 10)
        {
            return _cachedWslDistros;
        }

        if (!PathResolver.TryResolve("wsl.exe", out _))
        {
            _cachedWslDistros = new List<string>();
            return _cachedWslDistros;
        }

        try
        {
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(2));
            var distros = await Task.Run(() =>
            {
                var list = new List<string>();
                var psi = new ProcessStartInfo
                {
                    FileName = "wsl.exe",
                    Arguments = "-l -q",
                    UseShellExecute = false,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    CreateNoWindow = true,
                    StandardOutputEncoding = Encoding.Unicode
                };

                using var process = Process.Start(psi);
                if (process == null) return list;

                string output = process.StandardOutput.ReadToEnd();
                process.WaitForExit(1500);

                var rawLines = output.Split(new[] { "\r\n", "\r", "\n" }, StringSplitOptions.RemoveEmptyEntries);
                foreach (var line in rawLines)
                {
                    string cleaned = line.Replace("\0", string.Empty).Trim();
                    if (!string.IsNullOrEmpty(cleaned) && !cleaned.StartsWith("Error", StringComparison.OrdinalIgnoreCase))
                    {
                        list.Add(cleaned);
                    }
                }

                return list;
            }, cts.Token);

            _cachedWslDistros = distros;
            _lastWslScan = DateTime.UtcNow;
            return distros;
        }
        catch
        {
            _cachedWslDistros = new List<string>();
            return _cachedWslDistros;
        }
    }
}
