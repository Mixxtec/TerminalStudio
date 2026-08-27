using System;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using Microsoft.Win32;
using TerminalStudio.Models;
using TerminalStudio.Services;

namespace TerminalStudio;

public partial class AddProfileDialog : Window
{
    private static SolidColorBrush SuccessBrush => (SolidColorBrush)Application.Current.Resources["BrushSuccess"];
    private static SolidColorBrush ErrorBrush => (SolidColorBrush)Application.Current.Resources["BrushDanger"];

    public ShellProfile? CreatedProfile { get; private set; }

    public AddProfileDialog()
    {
        InitializeComponent();
        Loaded += (s, e) => txtCommandLine.Focus();
    }

    private void TxtCommandLine_TextChanged(object sender, TextChangedEventArgs e)
    {
        string text = txtCommandLine.Text.Trim();
        if (string.IsNullOrEmpty(text))
        {
            txtValidationStatus.Text = string.Empty;
            return;
        }

        if (PathResolver.TryResolve(text, out string resolved))
        {
            txtValidationStatus.Foreground = SuccessBrush;
            txtValidationStatus.Text = $"Found: {resolved}";
        }
        else
        {
            txtValidationStatus.Foreground = ErrorBrush;
            txtValidationStatus.Text = "Executable not found in system PATH or invalid path";
        }
    }


    private void BtnCancel_Click(object sender, RoutedEventArgs e)
    {
        DialogResult = false;
        Close();
    }

    private void BtnAdd_Click(object sender, RoutedEventArgs e)
    {
        string command = txtCommandLine.Text.Trim();
        if (string.IsNullOrWhiteSpace(command))
        {
            txtValidationStatus.Foreground = ErrorBrush;
            txtValidationStatus.Text = "Please specify an executable or command";
            return;
        }

        if (!PathResolver.TryResolve(command, out string resolvedPath))
        {
            txtValidationStatus.Foreground = ErrorBrush;
            txtValidationStatus.Text = "Executable not found in system PATH or invalid path";
            return;
        }

        string fileName = Path.GetFileNameWithoutExtension(resolvedPath);
        string title = string.IsNullOrEmpty(fileName)
            ? command
            : char.ToUpperInvariant(fileName[0]) + fileName.Substring(1);

        CreatedProfile = new ShellProfile
        {
            Id = Guid.NewGuid().ToString(),
            Title = title,
            CommandLine = command,
            Type = "Custom",
            WorkingDirectory = null,
            IsCustom = true
        };

        DialogResult = true;
        Close();
    }
}
