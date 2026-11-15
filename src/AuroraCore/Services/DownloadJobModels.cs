using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using AuroraCore.Download;

namespace AuroraCore.Services;

/// <summary>Round-72: state of one user-visible download job.</summary>
public enum JobState
{
    Queued,
    /// <summary>Round-96: the job exists and is visible BEFORE the first byte
    /// moves (resolving metadata, fetching the installer jar, pre-filling
    /// libraries). "正在安装…" with an empty list was this state going
    /// unrepresented.</summary>
    Preparing,
    Running,
    Done,
    Failed,
    Canceled
}

/// <summary>One step of a job's flow chart (下载原版 json → 支持库 → 资源…).
/// Mirrors the round-47 PCL-style stage list, but lives on the JOB so several
/// installs can run at once without sharing one global stage collection.</summary>
public sealed class JobStage : INotifyPropertyChanged
{
    public JobStage(string key, string label)
    {
        Key = key;
        Label = label;
    }

    public string Key { get; }
    public string Label { get; }

    private string _state = "Pending"; // Pending / Running / Done / Failed
    public string State
    {
        get => _state;
        set { if (Field(ref _state, value)) Raise(nameof(Marker)); }
    }

    private string? _percentText;
    public string? PercentText
    {
        get => _percentText;
        set { if (Field(ref _percentText, value)) Raise(nameof(Marker)); }
    }

    public string Marker => State switch
    {
        "Done" => "✓",
        "Failed" => "✕",
        "Running" => string.IsNullOrEmpty(PercentText) ? "···" : PercentText!,
        _ => ""
    };

    public event PropertyChangedEventHandler? PropertyChanged;

    private bool Field<T>(ref T field, T value, [System.Runtime.CompilerServices.CallerMemberName] string? name = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value)) return false;
        field = value;
        Raise(name);
        return true;
    }

    private void Raise(string? name) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}
