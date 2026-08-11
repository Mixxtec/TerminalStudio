using System.Windows;
using TerminalStudio.Services;

namespace TerminalStudio;

/// <summary>
/// Interaction logic for App.xaml
/// </summary>
public partial class App : Application
{
    protected override void OnStartup(StartupEventArgs e)
    {
        NativeMethods.FreeConsole();
        base.OnStartup(e);
    }
}
