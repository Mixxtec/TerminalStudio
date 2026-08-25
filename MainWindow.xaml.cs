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
        Activated += (s, e) =>
        {
            Dispatcher.BeginInvoke(new Action(() => webView?.Focus()), System.Windows.Threading.DispatcherPriority.Input);
        };
        SizeChanged += (s, e) => SendFitMessage();
        StateChanged += (s, e) =>
        {
            UpdateWindowBorder();
            UpdateMaximizeButtonIcon();
            SendFitMessage();
        };
        Closing += (s, e) =>
        {
            SaveSessionConfig();

            foreach (var session in _sessions.Values)
            {
                session.Dispose();
            }
            _sessions.Clear();
        };
        PreviewKeyDown += MainWindow_PreviewKeyDown;
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
        webView.CoreWebView2.Settings.AreDefaultContextMenusEnabled = false;
        webView.CoreWebView2.PermissionRequested += (s, args) =>
        {
            if (args.PermissionKind == Microsoft.Web.WebView2.Core.CoreWebView2PermissionKind.ClipboardRead)
            {
                args.State = Microsoft.Web.WebView2.Core.CoreWebView2PermissionState.Allow;
            }
        };

        webView.CoreWebView2.WebMessageReceived += (s, args) =>
        {
            try
            {
                using var doc = JsonDocument.Parse(args.WebMessageAsJson);
                var root = doc.RootElement;
                string type = root.GetProperty("type").GetString() ?? "";

                if (type.Equals("clipboard_set", StringComparison.OrdinalIgnoreCase))
                {
                    if (root.TryGetProperty("text", out var textElem))
                    {
                        string text = textElem.GetString() ?? "";
                        if (!string.IsNullOrEmpty(text))
                        {
                            try { Clipboard.SetText(text); } catch { }
                        }
                    }
                    return;
                }

                if (!root.TryGetProperty("tabId", out var tabIdElem)) return;
                string tabId = tabIdElem.GetString() ?? "";

                if (type.Equals("clipboard_paste_request", StringComparison.OrdinalIgnoreCase))
                {
                    try
                    {
                        if (Clipboard.ContainsText())
                        {
                            string pasteText = Clipboard.GetText();
                            if (!string.IsNullOrEmpty(pasteText))
                            {
                                var msg = new { type = "paste", tabId, text = pasteText };
                                string json = JsonSerializer.Serialize(msg);
                                webView.CoreWebView2.PostWebMessageAsJson(json);
                            }
                        }
                    }
                    catch { }
                    return;
                }

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

    private void SendFitMessage()
    {
        if (webView?.CoreWebView2 != null)
        {
            string fitMsg = JsonSerializer.Serialize(new { type = "fit" });
            webView.CoreWebView2.PostWebMessageAsJson(fitMsg);
        }
    }

    private void ActivateTab(string tabId)
    {
        _activeTabId = tabId;
        foreach (var tab in _tabItems)
        {
            tab.IsActive = (tab.Id == tabId);
        }

        if (webView?.CoreWebView2 != null)
        {
            string activateMsg = JsonSerializer.Serialize(new { type = "activate", tabId });
            webView.CoreWebView2.PostWebMessageAsJson(activateMsg);
        }

        Dispatcher.BeginInvoke(new Action(() =>
        {
            webView?.Focus();
        }), System.Windows.Threading.DispatcherPriority.Input);

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

    private void btnAddDefaultTab_Click(object sender, RoutedEventArgs e)
    {
        btnAddPowerShell_Click(sender, e);
    }

    private void btnTabDropdown_Click(object sender, RoutedEventArgs e)
    {
        if (sender is Button button && button.ContextMenu != null)
        {
            button.ContextMenu.PlacementTarget = button;
            button.ContextMenu.IsOpen = true;
        }
    }

    private void MainWindow_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (Keyboard.Modifiers == (ModifierKeys.Control | ModifierKeys.Shift))
        {
            if (e.Key == Key.D1 || e.Key == Key.NumPad1)
            {
                btnAddPowerShell_Click(this, new RoutedEventArgs());
                e.Handled = true;
            }
            else if (e.Key == Key.D2 || e.Key == Key.NumPad2)
            {
                btnAddCMD_Click(this, new RoutedEventArgs());
                e.Handled = true;
            }
            else if (e.Key == Key.D3 || e.Key == Key.NumPad3)
            {
                btnAddWSL_Click(this, new RoutedEventArgs());
                e.Handled = true;
            }
        }
        else if (Keyboard.Modifiers == ModifierKeys.Control)
        {
            if (e.Key == Key.T)
            {
                btnAddDefaultTab_Click(this, new RoutedEventArgs());
                e.Handled = true;
            }
            else if (e.Key == Key.W && !string.IsNullOrEmpty(_activeTabId))
            {
                RemoveTab(_activeTabId);
                e.Handled = true;
            }
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

    private void MinimizeButton_Click(object sender, RoutedEventArgs e)
    {
        WindowState = WindowState.Minimized;
    }

    private void MaximizeButton_Click(object sender, RoutedEventArgs e)
    {
        WindowState = WindowState == WindowState.Maximized ? WindowState.Normal : WindowState.Maximized;
    }

    private void CloseButton_Click(object sender, RoutedEventArgs e)
    {
        Close();
    }

    private void UpdateMaximizeButtonIcon()
    {
        if (pathMaximize != null)
        {
            pathMaximize.Data = WindowState == WindowState.Maximized
                ? (Geometry)FindResource("IconRestore")
                : (Geometry)FindResource("IconMaximize");
        }
    }

    private void TabsScrollViewer_PreviewMouseWheel(object sender, MouseWheelEventArgs e)
    {
        if (sender is ScrollViewer scrollViewer)
        {
            if (e.Delta < 0)
                scrollViewer.LineRight();
            else
                scrollViewer.LineLeft();
            e.Handled = true;
        }
    }

    private void TabResizeThumb_DragDelta(object sender, DragDeltaEventArgs e)
    {
        if (sender is FrameworkElement elem && elem.DataContext is TabItemModel model)
        {
            double currentWidth = model.CustomWidth ?? (VisualTreeHelper.GetParent(elem) is FrameworkElement parent ? parent.ActualWidth : 120);
            if (currentWidth <= 0) currentWidth = 120;
            double newWidth = Math.Clamp(currentWidth + e.HorizontalChange, 75, 350);
            model.CustomWidth = newWidth;
            UpdateTabsContainerLayout();
        }
    }

    private void TabResizeThumb_MouseDoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (sender is FrameworkElement elem && elem.DataContext is TabItemModel model)
        {
            model.CustomWidth = null;
            UpdateTabsContainerLayout();
            e.Handled = true;
        }
    }

    private void UpdateTabsContainerLayout()
    {
        if (tabsHostContainer != null && addTabGroup != null && tabsScrollViewer != null)
        {
            double availableWidth = Math.Max(0, tabsHostContainer.ActualWidth - addTabGroup.ActualWidth - addTabGroup.Margin.Left - addTabGroup.Margin.Right - 4);
            if (availableWidth > 0)
            {
                tabsScrollViewer.MaxWidth = availableWidth;
            }
        }
    }

    private void TabsHostContainer_SizeChanged(object sender, SizeChangedEventArgs e)
    {
        UpdateTabsContainerLayout();
    }

    private void TabsControl_SizeChanged(object sender, SizeChangedEventArgs e)
    {
        UpdateTabsContainerLayout();
    }

    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);
        try
        {
            var hwnd = new System.Windows.Interop.WindowInteropHelper(this).Handle;
            int preference = NativeMethods.DWMWCP_ROUND;
            NativeMethods.DwmSetWindowAttribute(hwnd, NativeMethods.DWMWA_WINDOW_CORNER_PREFERENCE, ref preference, sizeof(int));
        }
        catch { }
    }

    private void UpdateWindowBorder()
    {
        if (rootWindowBorder == null) return;
        if (WindowState == WindowState.Maximized)
        {
            rootWindowBorder.CornerRadius = new CornerRadius(0);
            rootWindowBorder.BorderThickness = new Thickness(0);
            rootWindowBorder.Margin = new Thickness(6);
        }
        else
        {
            rootWindowBorder.CornerRadius = new CornerRadius(8);
            rootWindowBorder.BorderThickness = new Thickness(1);
            rootWindowBorder.Margin = new Thickness(0);
        }
    }
}
