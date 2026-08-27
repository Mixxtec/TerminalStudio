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
using System.Windows.Media.Animation;
using TerminalStudio.Models;
using TerminalStudio.Services;

namespace TerminalStudio;

public partial class MainWindow : Window
{
    private readonly Dictionary<string, TerminalSession> _sessions = new();
    private readonly ObservableCollection<TabItemModel> _tabItems = new();
    private readonly ConfigService _configService;
    private readonly ShellDiscoveryService _discoveryService = new();
    private List<ShellProfile> _customProfiles = new();
    private string? _defaultProfileId;
    private string? _activeTabId;
    private TabItemModel? _editingTab;
    private bool _isDraggingTab;
    private TabItemModel? _draggedTab;
    private Point _dragStartPoint;
    private System.Windows.Forms.NotifyIcon? _notifyIcon;
    private bool _isExplicitExit;

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
        Closing += MainWindow_Closing;
        PreviewKeyDown += MainWindow_PreviewKeyDown;

        InitializeNotifyIcon();
    }

    private void SaveSessionConfig()
    {
        var config = new SessionConfig
        {
            ActiveTabId = _activeTabId,
            DefaultProfileId = _defaultProfileId,
            CustomProfiles = _customProfiles.ToList(),
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

        webView.CoreWebView2.NavigationCompleted += async (s, args) =>
        {
            var config = _configService.LoadConfig();
            _customProfiles = config.CustomProfiles ?? new List<ShellProfile>();
            _defaultProfileId = config.DefaultProfileId;

            if (config.Tabs.Count == 0)
            {
                var discovered = await _discoveryService.GetDiscoveredProfilesAsync(_customProfiles);
                var defaultProf = discovered.FirstOrDefault(p => p.Id == _defaultProfileId) ?? discovered.FirstOrDefault();
                if (defaultProf != null)
                {
                    CreateTabFromProfile(defaultProf);
                }
                else
                {
                    var defaultTab = new TerminalConfig
                    {
                        Id = Guid.NewGuid().ToString(),
                        Title = "PowerShell",
                        CommandLine = "powershell.exe",
                        Type = "PowerShell",
                        WorkingDirectory = Environment.CurrentDirectory
                    };
                    CreateTab(defaultTab);
                }
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
            _isDraggingTab = true;
            _draggedTab = model;
            _dragStartPoint = e.GetPosition(this);
            elem.CaptureMouse();
        }
    }

    private void TabHeader_MouseMove(object sender, MouseEventArgs e)
    {
        if (!_isDraggingTab || _draggedTab == null || sender is not FrameworkElement elem)
            return;

        Point currentPoint = e.GetPosition(this);
        Vector diff = currentPoint - _dragStartPoint;

        if (e.LeftButton == MouseButtonState.Pressed && Math.Abs(diff.X) > SystemParameters.MinimumHorizontalDragDistance)
        {
            int oldIndex = _tabItems.IndexOf(_draggedTab);
            if (oldIndex < 0) return;

            Point tabPos = e.GetPosition(tabsControl);
            double currentX = 0;
            int newIndex = oldIndex;

            for (int i = 0; i < _tabItems.Count; i++)
            {
                if (tabsControl.ItemContainerGenerator.ContainerFromIndex(i) is FrameworkElement container)
                {
                    double itemWidth = container.ActualWidth;
                    double itemMidX = currentX + itemWidth / 2.0;

                    if (i < oldIndex && tabPos.X < itemMidX)
                    {
                        newIndex = i;
                        break;
                    }
                    if (i > oldIndex && tabPos.X > itemMidX)
                    {
                        newIndex = i;
                    }

                    currentX += itemWidth;
                }
            }

            if (newIndex != oldIndex && newIndex >= 0 && newIndex < _tabItems.Count)
            {
                var oldPositions = new Dictionary<TabItemModel, double>();
                for (int i = 0; i < _tabItems.Count; i++)
                {
                    if (tabsControl.ItemContainerGenerator.ContainerFromIndex(i) is FrameworkElement container)
                    {
                        Point p = container.TransformToAncestor(tabsControl).Transform(new Point(0, 0));
                        oldPositions[_tabItems[i]] = p.X;
                    }
                }

                _tabItems.Move(oldIndex, newIndex);
                tabsControl.UpdateLayout();
                SaveSessionConfig();

                for (int i = 0; i < _tabItems.Count; i++)
                {
                    var tab = _tabItems[i];
                    if (tabsControl.ItemContainerGenerator.ContainerFromIndex(i) is FrameworkElement container &&
                        oldPositions.TryGetValue(tab, out double oldX))
                    {
                        Point newPos = container.TransformToAncestor(tabsControl).Transform(new Point(0, 0));
                        double deltaX = oldX - newPos.X;

                        if (Math.Abs(deltaX) > 0.5)
                        {
                            var transform = container.RenderTransform as TranslateTransform;
                            if (transform == null)
                            {
                                transform = new TranslateTransform();
                                container.RenderTransform = transform;
                            }

                            var anim = new DoubleAnimation
                            {
                                From = deltaX,
                                To = 0,
                                Duration = TimeSpan.FromMilliseconds(130),
                                EasingFunction = new QuadraticEase { EasingMode = EasingMode.EaseOut }
                            };
                            transform.BeginAnimation(TranslateTransform.XProperty, anim);
                        }
                    }
                }
            }
        }
    }

    private void TabHeader_MouseLeftButtonUp(object sender, MouseButtonEventArgs e)
    {
        if (sender is FrameworkElement elem)
        {
            elem.ReleaseMouseCapture();
        }

        for (int i = 0; i < _tabItems.Count; i++)
        {
            if (tabsControl.ItemContainerGenerator.ContainerFromIndex(i) is FrameworkElement container)
            {
                if (container.RenderTransform is TranslateTransform transform)
                {
                    transform.BeginAnimation(TranslateTransform.XProperty, null);
                    transform.X = 0;
                }
            }
        }

        _isDraggingTab = false;
        _draggedTab = null;
    }

    private void CloseTab_Click(object sender, RoutedEventArgs e)
    {
        if (sender is Button btn && btn.Tag is string tabId)
        {
            RemoveTab(tabId);
        }
    }

    private void CreateTabFromProfile(ShellProfile profile)
    {
        var config = new TerminalConfig
        {
            Id = Guid.NewGuid().ToString(),
            Title = profile.Title,
            CommandLine = profile.CommandLine,
            Type = profile.Type,
            Distribution = profile.Distribution,
            WorkingDirectory = profile.WorkingDirectory ?? Environment.CurrentDirectory
        };
        CreateTab(config);
    }

    private async void btnAddDefaultTab_Click(object sender, RoutedEventArgs e)
    {
        var discovered = await _discoveryService.GetDiscoveredProfilesAsync(_customProfiles);
        var profile = discovered.FirstOrDefault(p => p.Id == _defaultProfileId) ?? discovered.FirstOrDefault();
        if (profile != null)
        {
            CreateTabFromProfile(profile);
        }
        else
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
    }

    private async void btnTabDropdown_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button button) return;

        var discovered = await _discoveryService.GetDiscoveredProfilesAsync(_customProfiles);
        var menu = new ContextMenu
        {
            PlacementTarget = button,
            Placement = PlacementMode.Bottom
        };

        foreach (var profile in discovered)
        {
            var item = new MenuItem
            {
                Header = profile.Title,
                InputGestureText = profile.ShortcutText ?? string.Empty
            };
            item.Click += (s, ev) => CreateTabFromProfile(profile);
            menu.Items.Add(item);
        }

        var addNewItem = new MenuItem
        {
            Header = "+ Add new..."
        };
        addNewItem.Click += (s, ev) =>
        {
            var dialog = new AddProfileDialog
            {
                Owner = this
            };

            if (dialog.ShowDialog() == true && dialog.CreatedProfile != null)
            {
                _customProfiles.Add(dialog.CreatedProfile);
                SaveSessionConfig();
                CreateTabFromProfile(dialog.CreatedProfile);
            }
        };
        menu.Items.Add(addNewItem);

        menu.IsOpen = true;
    }

    private async void MainWindow_PreviewKeyDown(object sender, KeyEventArgs e)
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
                var wslDistros = await _discoveryService.GetWslDistrosAsync();
                string distro = wslDistros.FirstOrDefault() ?? string.Empty;
                string cmd = string.IsNullOrEmpty(distro) ? "wsl.exe" : $"wsl.exe -d \"{distro}\"";
                string title = string.IsNullOrEmpty(distro) ? "WSL" : distro;

                CreateTab(new TerminalConfig
                {
                    Id = Guid.NewGuid().ToString(),
                    Title = title,
                    CommandLine = cmd,
                    Type = "WSL",
                    Distribution = string.IsNullOrEmpty(distro) ? null : distro,
                    WorkingDirectory = Environment.CurrentDirectory
                });
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
        txtProxyStatus.Foreground = (SolidColorBrush)Application.Current.Resources["BrushTextMuted"];

        bool ok = await NetworkUtils.CheckProxyAvailableAsync(txtProxyAddress.Text);
        if (ok)
        {
            txtProxyStatus.Text = "Available";
            txtProxyStatus.Foreground = (SolidColorBrush)Application.Current.Resources["BrushSuccess"];
        }
        else
        {
            txtProxyStatus.Text = "Unavailable";
            txtProxyStatus.Foreground = (SolidColorBrush)Application.Current.Resources["BrushDanger"];
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

    private void InitializeNotifyIcon()
    {
        string iconPath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "Assets", "Icon.ico");
        System.Drawing.Icon? appIcon = null;

        if (File.Exists(iconPath))
        {
            try
            {
                appIcon = new System.Drawing.Icon(iconPath);
            }
            catch { }
        }

        if (appIcon == null)
        {
            appIcon = System.Drawing.SystemIcons.Application;
        }

        var contextMenu = new System.Windows.Forms.ContextMenuStrip
        {
            Renderer = new DarkToolStripRenderer()
        };

        var openItem = new System.Windows.Forms.ToolStripMenuItem("Open TerminalStudio");
        openItem.Click += (s, e) => ShowAndActivateWindow();

        var newTabItem = new System.Windows.Forms.ToolStripMenuItem("New Tab");
        newTabItem.Click += (s, e) =>
        {
            ShowAndActivateWindow();
            btnAddDefaultTab_Click(this, new RoutedEventArgs());
        };

        var exitItem = new System.Windows.Forms.ToolStripMenuItem("Exit");
        exitItem.Click += (s, e) => ExitApplication();

        contextMenu.Items.Add(openItem);
        contextMenu.Items.Add(newTabItem);
        contextMenu.Items.Add(new System.Windows.Forms.ToolStripSeparator());
        contextMenu.Items.Add(exitItem);

        _notifyIcon = new System.Windows.Forms.NotifyIcon
        {
            Icon = appIcon,
            Text = "TerminalStudio",
            Visible = true,
            ContextMenuStrip = contextMenu
        };

        _notifyIcon.DoubleClick += (s, e) => ToggleShowWindow();
    }

    private void ToggleShowWindow()
    {
        if (Visibility == Visibility.Visible && WindowState != WindowState.Minimized)
        {
            Hide();
        }
        else
        {
            ShowAndActivateWindow();
        }
    }

    private void ShowAndActivateWindow()
    {
        Show();
        if (WindowState == WindowState.Minimized)
        {
            WindowState = WindowState.Normal;
        }
        Activate();
        Focus();
    }

    private void MainWindow_Closing(object? sender, System.ComponentModel.CancelEventArgs e)
    {
        if (!_isExplicitExit)
        {
            e.Cancel = true;
            Hide();
            return;
        }

        SaveSessionConfig();

        if (_notifyIcon != null)
        {
            _notifyIcon.Visible = false;
            _notifyIcon.Dispose();
            _notifyIcon = null;
        }

        foreach (var session in _sessions.Values)
        {
            session.Dispose();
        }
        _sessions.Clear();
    }

    public void ExitApplication()
    {
        _isExplicitExit = true;
        Close();
        System.Windows.Application.Current.Shutdown();
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

internal class DarkToolStripRenderer : System.Windows.Forms.ToolStripProfessionalRenderer
{
    public DarkToolStripRenderer() : base(new DarkColorTable()) { }

    protected override void OnRenderItemText(System.Windows.Forms.ToolStripItemTextRenderEventArgs e)
    {
        var colorKey = (e.Item.Selected || e.Item.Pressed) ? "ColorBgDeep" : "ColorTextPrimary";
        if (Application.Current?.Resources[colorKey] is System.Windows.Media.Color wpfColor)
        {
            e.TextColor = System.Drawing.Color.FromArgb(wpfColor.A, wpfColor.R, wpfColor.G, wpfColor.B);
        }
        base.OnRenderItemText(e);
    }
}

internal class DarkColorTable : System.Windows.Forms.ProfessionalColorTable
{
    private static System.Drawing.Color GetResourceColor(string key, System.Drawing.Color fallback)
    {
        if (Application.Current?.Resources[key] is System.Windows.Media.Color c)
        {
            return System.Drawing.Color.FromArgb(c.A, c.R, c.G, c.B);
        }
        return fallback;
    }

    public override System.Drawing.Color ToolStripDropDownBackground => GetResourceColor("ColorBgBase", System.Drawing.Color.FromArgb(30, 30, 30));
    public override System.Drawing.Color ImageMarginGradientBegin => GetResourceColor("ColorBgBase", System.Drawing.Color.FromArgb(30, 30, 30));
    public override System.Drawing.Color ImageMarginGradientMiddle => GetResourceColor("ColorBgBase", System.Drawing.Color.FromArgb(30, 30, 30));
    public override System.Drawing.Color ImageMarginGradientEnd => GetResourceColor("ColorBgBase", System.Drawing.Color.FromArgb(30, 30, 30));
    public override System.Drawing.Color MenuBorder => GetResourceColor("ColorBorderMid", System.Drawing.Color.FromArgb(62, 62, 66));
    public override System.Drawing.Color MenuItemBorder => System.Drawing.Color.Transparent;
    public override System.Drawing.Color MenuItemSelected => GetResourceColor("ColorBgHighlight", System.Drawing.Color.FromArgb(45, 45, 48));
    public override System.Drawing.Color MenuItemSelectedGradientBegin => GetResourceColor("ColorBgHighlight", System.Drawing.Color.FromArgb(45, 45, 48));
    public override System.Drawing.Color MenuItemSelectedGradientEnd => GetResourceColor("ColorBgHighlight", System.Drawing.Color.FromArgb(45, 45, 48));
    public override System.Drawing.Color MenuItemPressedGradientBegin => GetResourceColor("ColorBgPressed", System.Drawing.Color.FromArgb(37, 37, 38));
    public override System.Drawing.Color MenuItemPressedGradientEnd => GetResourceColor("ColorBgPressed", System.Drawing.Color.FromArgb(37, 37, 38));
    public override System.Drawing.Color SeparatorDark => GetResourceColor("ColorBorderMid", System.Drawing.Color.FromArgb(62, 62, 66));
    public override System.Drawing.Color SeparatorLight => GetResourceColor("ColorBgBase", System.Drawing.Color.FromArgb(30, 30, 30));
}
