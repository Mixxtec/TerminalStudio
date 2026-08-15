using System.ComponentModel;
using System.Runtime.CompilerServices;

namespace TerminalStudio.Models;

public class TabItemModel : INotifyPropertyChanged
{
    private string _id = "";
    private string _title = "";
    private string _commandLine = "powershell.exe";
    private string? _workingDirectory;
    private bool _isActive;
    private bool _hasError;
    private TerminalConfig _config = new();

    public string Id
    {
        get => _id;
        set => SetField(ref _id, value);
    }

    public string Title
    {
        get => _title;
        set
        {
            if (SetField(ref _title, value))
            {
                _config.Title = value;
            }
        }
    }

    public string CommandLine
    {
        get => _commandLine;
        set
        {
            if (SetField(ref _commandLine, value))
            {
                _config.CommandLine = value;
            }
        }
    }

    public string? WorkingDirectory
    {
        get => _workingDirectory;
        set
        {
            if (SetField(ref _workingDirectory, value))
            {
                _config.WorkingDirectory = value;
            }
        }
    }

    public bool IsActive
    {
        get => _isActive;
        set => SetField(ref _isActive, value);
    }

    public bool HasError
    {
        get => _hasError;
        set => SetField(ref _hasError, value);
    }

    public TerminalConfig Config
    {
        get => _config;
        set
        {
            if (SetField(ref _config, value))
            {
                _id = value.Id;
                _title = value.Title;
                _commandLine = value.CommandLine;
                _workingDirectory = value.WorkingDirectory;
                OnPropertyChanged(nameof(Id));
                OnPropertyChanged(nameof(Title));
                OnPropertyChanged(nameof(CommandLine));
                OnPropertyChanged(nameof(WorkingDirectory));
            }
        }
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    protected void OnPropertyChanged([CallerMemberName] string? propertyName = null)
    {
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
    }

    protected bool SetField<T>(ref T field, T value, [CallerMemberName] string? propertyName = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value)) return false;
        field = value;
        OnPropertyChanged(propertyName);
        return true;
    }
}
