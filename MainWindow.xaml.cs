using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using TerminalStudio.Models;
using TerminalStudio.Services;

namespace TerminalStudio;

public partial class MainWindow : Window
{
    private readonly Dictionary<string, TerminalSession> _sessions = new();
    private readonly ObservableCollection<TabItemModel> _tabItems = new();
    private readonly ConfigService _configService;
    private string? _activeTabId;
    private TabItemModel? _editingTab;

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
            Tabs = _tabItems.Select(t => t.Config).ToList()
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
                var defaultTab = new TerminalConfig
                {
                    Id = "1",
                    Title = "PowerShell",
                    CommandLine = "powershell.exe",
                    Type = "PowerShell"
                };
                CreateTab(defaultTab);
            }
            else
            {
                foreach (var tab in config.Tabs)
                {
                    CreateTab(tab);
                }

                if (!string.IsNullOrEmpty(config.ActiveTabId) && _sessions.ContainsKey(config.ActiveTabId))
                {
                    ActivateTab(config.ActiveTabId);
                }
            }
        };
    }

    private void CreateTab(TerminalConfig config)
    {
        string tabId = config.Id;
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
        var tabModel = new TabItemModel
        {
            Config = config
        };
        _tabItems.Add(tabModel);

        session.Start(config.CommandLine, config.WorkingDirectory, config.Proxy);
        ActivateTab(tabId);
        SaveSessionConfig();
    }

    private void ActivateTab(string tabId)
    {
        _activeTabId = tabId;
        foreach (var tab in _tabItems)
        {
            tab.IsActive = (tab.Id == tabId);
        }

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
        if (sender is FrameworkElement elem && elem.DataContext is TabItemModel model)
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
        CreateTab(new TerminalConfig
        {
            Id = Guid.NewGuid().ToString(),
            Title = "PowerShell",
            CommandLine = "powershell.exe",
            Type = "PowerShell",
            WorkingDirectory = Environment.CurrentDirectory
        });
    }

    private void btnAddCMD_Click(object sender, RoutedEventArgs e)
    {
        CreateTab(new TerminalConfig
        {
            Id = Guid.NewGuid().ToString(),
            Title = "CMD",
            CommandLine = "cmd.exe",
            Type = "CMD",
            WorkingDirectory = Environment.CurrentDirectory
        });
    }

    private void btnAddWSL_Click(object sender, RoutedEventArgs e)
    {
        CreateTab(new TerminalConfig
        {
            Id = Guid.NewGuid().ToString(),
            Title = "WSL",
            CommandLine = "wsl.exe",
            Type = "WSL",
            WorkingDirectory = Environment.CurrentDirectory
        });
    }

    private void TabSettings_Click(object sender, RoutedEventArgs e)
    {
        if (sender is Button btn && btn.Tag is TabItemModel model)
        {
            _editingTab = model;
            txtTabTitle.Text = model.Title;

            var proxy = model.Config.Proxy;
            cmbProxyMode.SelectedIndex = proxy.Mode switch
            {
                ProxyMode.Direct => 0,
                ProxyMode.LocalVPN => 1,
                ProxyMode.Custom => 2,
                _ => 0
            };

            txtProxyAddress.Text = proxy.Address ?? "http://127.0.0.1:10809";
            txtNoProxy.Text = proxy.NoProxy ?? "localhost,127.0.0.1";
            txtProxyStatus.Text = "";

            UpdateProxyFieldsVisibility();

            settingsPopup.PlacementTarget = btn;
            settingsPopup.IsOpen = true;
        }
    }

    private void cmbProxyMode_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        UpdateProxyFieldsVisibility();
    }

    private void UpdateProxyFieldsVisibility()
    {
        if (panelProxyFields == null || cmbProxyMode == null) return;

        if (cmbProxyMode.SelectedIndex == 0)
        {
            panelProxyFields.Visibility = Visibility.Collapsed;
        }
        else
        {
            panelProxyFields.Visibility = Visibility.Visible;
            if (cmbProxyMode.SelectedIndex == 1 && string.IsNullOrWhiteSpace(txtProxyAddress.Text))
            {
                txtProxyAddress.Text = "http://127.0.0.1:10809";
            }
        }
    }

    private async void btnCheckProxy_Click(object sender, RoutedEventArgs e)
    {
        txtProxyStatus.Text = "Checking...";
        txtProxyStatus.Foreground = new SolidColorBrush(Color.FromRgb(170, 170, 170));

        bool ok = await NetworkUtils.CheckProxyAvailableAsync(txtProxyAddress.Text);
        if (ok)
        {
            txtProxyStatus.Text = "Available";
            txtProxyStatus.Foreground = new SolidColorBrush(Color.FromRgb(78, 201, 176));
        }
        else
        {
            txtProxyStatus.Text = "Unavailable";
            txtProxyStatus.Foreground = new SolidColorBrush(Color.FromRgb(244, 71, 71));
        }
    }

    private void btnApplySettings_Click(object sender, RoutedEventArgs e)
    {
        if (_editingTab == null) return;

        settingsPopup.IsOpen = false;
        _editingTab.Title = txtTabTitle.Text.Trim();

        var newMode = cmbProxyMode.SelectedIndex switch
        {
            0 => ProxyMode.Direct,
            1 => ProxyMode.LocalVPN,
            2 => ProxyMode.Custom,
            _ => ProxyMode.Direct
        };

        var oldProxy = _editingTab.Config.Proxy;
        bool proxyChanged = oldProxy.Mode != newMode ||
                           !string.Equals(oldProxy.Address, txtProxyAddress.Text.Trim(), StringComparison.OrdinalIgnoreCase) ||
                           !string.Equals(oldProxy.NoProxy, txtNoProxy.Text.Trim(), StringComparison.OrdinalIgnoreCase);

        _editingTab.Config.Proxy = new ProxyConfig
        {
            Mode = newMode,
            Address = txtProxyAddress.Text.Trim(),
            NoProxy = txtNoProxy.Text.Trim()
        };

        SaveSessionConfig();

        if (proxyChanged)
        {
            var dialog = new RestartConfirmDialog
            {
                Owner = this
            };

            if (dialog.ShowDialog() == true && dialog.RestartAccepted)
            {
                if (_sessions.TryGetValue(_editingTab.Id, out var session))
                {
                    session.Restart(_editingTab.Config.Proxy, _editingTab.WorkingDirectory);
                }
            }
        }
    }
}
