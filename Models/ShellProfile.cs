namespace TerminalStudio.Models;

public class ShellProfile
{
    public string Id { get; set; } = Guid.NewGuid().ToString();
    public string Title { get; set; } = "";
    public string CommandLine { get; set; } = "powershell.exe";
    public string Type { get; set; } = "PowerShell";
    public string? WorkingDirectory { get; set; }
    public string? Distribution { get; set; }
    public bool IsCustom { get; set; }
    public string? ShortcutText { get; set; }
}
