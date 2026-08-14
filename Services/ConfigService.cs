using System.IO;
using System.Text.Json;
using TerminalStudio.Models;

namespace TerminalStudio.Services;

public class ConfigService
{
    private readonly string _configFilePath;

    public ConfigService(string configFilePath)
    {
        _configFilePath = configFilePath;
    }

    public SessionConfig LoadConfig()
    {
        if (!File.Exists(_configFilePath))
        {
            return new SessionConfig();
        }

        var json = File.ReadAllText(_configFilePath);
        return JsonSerializer.Deserialize<SessionConfig>(json) ?? new SessionConfig();
    }

    public void SaveConfig(SessionConfig config)
    {
        var json = JsonSerializer.Serialize(config, new JsonSerializerOptions { WriteIndented = true });
        File.WriteAllText(_configFilePath, json);
    }
}