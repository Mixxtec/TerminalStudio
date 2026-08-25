using System.Windows;

namespace TerminalStudio;

public partial class RestartConfirmDialog : Window
{
    public bool RestartAccepted { get; private set; }

    public RestartConfirmDialog()
    {
        InitializeComponent();
    }

    private void BtnAccept_Click(object sender, RoutedEventArgs e)
    {
        RestartAccepted = true;
        DialogResult = true;
        Close();
    }

    private void BtnReject_Click(object sender, RoutedEventArgs e)
    {
        RestartAccepted = false;
        DialogResult = false;
        Close();
    }
}
