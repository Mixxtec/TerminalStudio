namespace TerminalStudio.Services;

public class TerminalConfig
{
    public string Id { get; set; } = "";
    public string Title { get; set; } = "";
    public string CommandLine { get; set; } = "";
}
public class SessionConfig
{
    public string? ActiveTabId { get; set; }
    public List<TerminalConfig> Tabs { get; set; } = new List<TerminalConfig>();
}