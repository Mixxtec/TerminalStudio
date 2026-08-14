namespace TerminalStudio.Models;

public class TabItemModel
{
    public string Id { get; set; } = "";
    public string Title { get; set; } = "";
    public string CommandLine { get; set; } = "";
    public string? WorkingDirectory { get; set; }
}
