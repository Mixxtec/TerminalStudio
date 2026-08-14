using System.Collections.ObjectModel;
using System.IO;
using System.Text;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using TerminalStudio.Services;
using TerminalStudio.Models;

namespace TerminalStudio;

public partial class MainWindow : Window
{
    private readonly Dictionary<string, TerminalSession> _sessions = new();
    private readonly ObservableCollection<TabItemModel> _tabItems = new();
    private readonly ConfigService _configService;
    private string? _activeTabId;

    public MainWindow()
    {
        InitializeComponent();
        tabsControl.ItemsSource = _tabItems;

        string configPath = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "TerminalStudio",
            "session.json"
        );
        _configService = new ConfigService(configPath);

        Loaded += Window_Loaded;
        Closing += (s, e) =>
        {
            SaveSessionConfig();

            foreach (var session in _sessions.Values)
            {
                session.Dispose();
            }
            _sessions.Clear();
        };
    }

    private void SaveSessionConfig()
    {
        var config = new SessionConfig
        {
            ActiveTabId = _activeTabId,
            Tabs = _tabItems.Select(t => new TerminalConfig
            {
                Id = t.Id,
                Title = t.Title,
                CommandLine = t.CommandLine,
                WorkingDirectory = t.WorkingDirectory
            }).ToList()
        };

        string configPath = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "TerminalStudio",
            "session.json"
        );
        string? dir = Path.GetDirectoryName(configPath);
        if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir))
        {
            Directory.CreateDirectory(dir);
        }

        _configService.SaveConfig(config);
    }

    private async void Window_Loaded(object sender, RoutedEventArgs e)
    {
        await webView.EnsureCoreWebView2Async();
        webView.CoreWebView2.Settings.AreDevToolsEnabled = true;

        webView.CoreWebView2.WebMessageReceived += (s, args) =>
        {
            try
            {
                using var doc = JsonDocument.Parse(args.WebMessageAsJson);
                var root = doc.RootElement;
                string type = root.GetProperty("type").GetString() ?? "";

                if (!root.TryGetProperty("tabId", out var tabIdElem)) return;
                string tabId = tabIdElem.GetString() ?? "";

                if (type.Equals("input", StringComparison.OrdinalIgnoreCase))
                {
                    string inputData = root.GetProperty("data").GetString() ?? "";
                    if (_sessions.TryGetValue(tabId, out var session))
                    {
                        session.WriteInput(inputData);
                    }
                }
                else if (type.Equals("resize", StringComparison.OrdinalIgnoreCase))
                {
                    short cols = (short)root.GetProperty("cols").GetInt16();
                    short rows = (short)root.GetProperty("rows").GetInt16();
                    if (_sessions.TryGetValue(tabId, out var session))
                    {
                        session.Resize(cols, rows);
                    }
                }
                else if (type.Equals("cwd", StringComparison.OrdinalIgnoreCase))
                {
                    string cwd = root.GetProperty("cwd").GetString() ?? "";
                    var tab = _tabItems.FirstOrDefault(t => t.Id == tabId);
                    if (tab != null && !string.IsNullOrEmpty(cwd))
                    {
                        tab.WorkingDirectory = cwd;
                        SaveSessionConfig();
                    }
                }
            }
            catch { }
        };

        string htmlPath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "Assets", "terminal.html");
        webView.CoreWebView2.Navigate(htmlPath);

        webView.CoreWebView2.NavigationCompleted += (s, args) =>
        {
            var config = _configService.LoadConfig();

            if (config.Tabs.Count == 0)
            {
                CreateTab("1", "PowerShell", "powershell.exe");
            }
            else
            {
                foreach (var tab in config.Tabs)
                {
                    CreateTab(tab.Id, tab.Title, tab.CommandLine, tab.WorkingDirectory);
                }

                if (!string.IsNullOrEmpty(config.ActiveTabId) && _sessions.ContainsKey(config.ActiveTabId))
                {
                    ActivateTab(config.ActiveTabId);
                }
            }
        };
    }

    private void CreateTab(string tabId, string title, string commandLine = "powershell.exe", string? workingDirectory = null)
    {
        string createMsg = JsonSerializer.Serialize(new { type = "create", tabId });
        webView.CoreWebView2.PostWebMessageAsJson(createMsg);

        var session = new TerminalSession();
        session.OutputReceived += data =>
        {
            string text = Encoding.UTF8.GetString(data);
            Dispatcher.Invoke(() =>
            {
                string outputMsg = JsonSerializer.Serialize(new { type = "output", tabId, data = text });
                webView.CoreWebView2.PostWebMessageAsJson(outputMsg);
            });
        };

        _sessions[tabId] = session;
        _tabItems.Add(new TabItemModel { Id = tabId, Title = title, CommandLine = commandLine, WorkingDirectory = workingDirectory });

        session.Start(commandLine, workingDirectory);
        ActivateTab(tabId);
        SaveSessionConfig();
    }

    private void ActivateTab(string tabId)
    {
        _activeTabId = tabId;
        string activateMsg = JsonSerializer.Serialize(new { type = "activate", tabId });
        webView.CoreWebView2.PostWebMessageAsJson(activateMsg);
        SaveSessionConfig();
    }

    private void RemoveTab(string tabId)
    {
        if (_sessions.TryGetValue(tabId, out var session))
        {
            session.Dispose();
            _sessions.Remove(tabId);
        }

        var tabItem = _tabItems.FirstOrDefault(t => t.Id == tabId);
        if (tabItem != null)
        {
            _tabItems.Remove(tabItem);
        }

        string removeMsg = JsonSerializer.Serialize(new { type = "remove", tabId });
        webView.CoreWebView2.PostWebMessageAsJson(removeMsg);

        if (_activeTabId == tabId)
        {
            var nextTab = _tabItems.LastOrDefault();
            if (nextTab != null)
            {
                ActivateTab(nextTab.Id);
            }
            else
            {
                _activeTabId = null;
            }
        }
        SaveSessionConfig();
    }

    private void TabHeader_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (sender is TextBlock tb && tb.DataContext is TabItemModel model)
        {
            ActivateTab(model.Id);
        }
    }

    private void CloseTab_Click(object sender, RoutedEventArgs e)
    {
        if (sender is Button btn && btn.Tag is string tabId)
        {
            RemoveTab(tabId);
        }
    }

    private void btnAddTab_Click(object sender, RoutedEventArgs e)
    {
        if (sender is Button button && button.ContextMenu != null)
        {
            button.ContextMenu.IsOpen = true;
        }
    }

    private void btnAddPowerShell_Click(object sender, RoutedEventArgs e)
    {
        string newTabId = Guid.NewGuid().ToString();
        CreateTab(newTabId, "PowerShell", "powershell.exe", Environment.CurrentDirectory);
    }

    private void btnAddCMD_Click(object sender, RoutedEventArgs e)
    {
        string newTabId = Guid.NewGuid().ToString();
        CreateTab(newTabId, "CMD", "cmd.exe", Environment.CurrentDirectory);
    }

    private void btnAddWSL_Click(object sender, RoutedEventArgs e)
    {
        string newTabId = Guid.NewGuid().ToString();
        CreateTab(newTabId, "WSL", "wsl.exe", Environment.CurrentDirectory);
    }
}
