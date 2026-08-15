using System.Text.Json.Serialization;

namespace TerminalStudio.Models;

[JsonConverter(typeof(JsonStringEnumConverter))]
public enum ProxyMode
{
    Direct,
    LocalVPN,
    Custom
}

[JsonConverter(typeof(JsonStringEnumConverter))]
public enum AutoStartTrigger
{
    OnAppStart,
    OnTabFocus
}

public class ProxyConfig
{
    public ProxyMode Mode { get; set; } = ProxyMode.Direct;
    public string? Address { get; set; } = "http://127.0.0.1:10809";
    public string? NoProxy { get; set; } = "localhost,127.0.0.1";
}

public class TerminalConfig
{
    public string Id { get; set; } = Guid.NewGuid().ToString();
    public string Type { get; set; } = "PowerShell";
    public string Title { get; set; } = "";
    public string CommandLine { get; set; } = "powershell.exe";
    public string? WorkingDirectory { get; set; }
    public string? Distribution { get; set; }

    public bool AutoStartEnabled { get; set; } = false;
    public AutoStartTrigger AutoStartTrigger { get; set; } = AutoStartTrigger.OnAppStart;
    public List<string> Commands { get; set; } = new();
    public List<string> RecentCommands { get; set; } = new();

    public ProxyConfig Proxy { get; set; } = new();
    public string? AdapterName { get; set; }
}

public class SessionConfig
{
    public string? ActiveTabId { get; set; }
    public List<TerminalConfig> Tabs { get; set; } = new();
}