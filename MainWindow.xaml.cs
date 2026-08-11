using System.IO;
using System.Text;
using System.Text.Json;
using System.Windows;
using TerminalStudio.Services;

namespace TerminalStudio;

public partial class MainWindow : Window
{
    private TerminalSession? _session;

    public MainWindow()
    {
        InitializeComponent();
        Loaded += Window_Loaded;
        Closing += (s, e) => _session?.Dispose();
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

                if (type.Equals("input", StringComparison.OrdinalIgnoreCase))
                {
                    string inputData = root.GetProperty("data").GetString() ?? "";
                    _session?.WriteInput(inputData);
                }
                else if (type.Equals("resize", StringComparison.OrdinalIgnoreCase))
                {
                    short cols = (short)root.GetProperty("cols").GetInt16();
                    short rows = (short)root.GetProperty("rows").GetInt16();
                    _session?.Resize(cols, rows);
                }
            }
            catch { }
        };

        string htmlPath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "Assets", "terminal.html");
        webView.CoreWebView2.Navigate(htmlPath);

        webView.CoreWebView2.NavigationCompleted += (s, args) =>
        {
            string createMsg = JsonSerializer.Serialize(new { type = "create", tabId = "1" });
            webView.CoreWebView2.PostWebMessageAsJson(createMsg);

            _session = new TerminalSession();
            _session.OutputReceived += data =>
            {
                string text = Encoding.UTF8.GetString(data);
                Dispatcher.Invoke(() =>
                {
                    string outputMsg = JsonSerializer.Serialize(new { type = "output", tabId = "1", data = text });
                    webView.CoreWebView2.PostWebMessageAsJson(outputMsg);
                });
            };

            _session.Start("powershell.exe");
        };
    }
}