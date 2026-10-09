using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.IO;
using System.Runtime.CompilerServices;
using System.Windows.Input;

namespace CoreScope.Core;

/// <summary>Minimal INotifyPropertyChanged base for view models.</summary>
public abstract class ObservableObject : INotifyPropertyChanged
{
    public event PropertyChangedEventHandler? PropertyChanged;

    protected void OnPropertyChanged([CallerMemberName] string? name = null) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));

    protected bool Set<T>(ref T field, T value, [CallerMemberName] string? name = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value)) return false;
        field = value;
        OnPropertyChanged(name);
        return true;
    }
}

public sealed class RelayCommand : ICommand
{
    private readonly Action<object?> _execute;

    public RelayCommand(Action<object?> execute) => _execute = execute;
    public RelayCommand(Action execute) => _execute = _ => execute();

    public event EventHandler? CanExecuteChanged { add { } remove { } }
    public bool CanExecute(object? parameter) => true;
    public void Execute(object? parameter) => _execute(parameter);
}

/// <summary>
/// Plain-text log written next to the executable (falls back to LocalAppData).
/// Kept deliberately simple: it exists so problems on a real machine can be diagnosed.
/// </summary>
/// <summary>Where CoreScope may write files. The install folder is read-only for Store (MSIX) installs.</summary>
public static class AppPaths
{
    public static string DataFolder
    {
        get
        {
            var dir = System.IO.Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "CoreScope");
            Directory.CreateDirectory(dir);
            return dir;
        }
    }

    /// <summary>Next to the exe when that's writable (portable/zip installs), otherwise the data folder.</summary>
    public static string Writable(string fileName)
    {
        var beside = System.IO.Path.Combine(AppContext.BaseDirectory, fileName);
        try
        {
            using (File.Open(beside, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.ReadWrite)) { }
            return beside;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return System.IO.Path.Combine(DataFolder, fileName);
        }
    }
}

public static class Log
{
    private static readonly object Gate = new();
    private static string? _path;

    public static string Path => _path ??= ResolvePath();

    private static string ResolvePath()
    {
        string[] candidates =
        {
            System.IO.Path.Combine(AppContext.BaseDirectory, "corescope.log"),
            System.IO.Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "CoreScope", "corescope.log"),
        };
        foreach (var candidate in candidates)
        {
            try
            {
                Directory.CreateDirectory(System.IO.Path.GetDirectoryName(candidate)!);
                File.WriteAllText(candidate, $"CoreScope log started {DateTime.Now:yyyy-MM-dd HH:mm:ss}{Environment.NewLine}");
                return candidate;
            }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }
        return candidates[^1];
    }

    public static void Info(string message) => Write("INFO ", message);

    public static void Error(string context, Exception ex) =>
        Write("ERROR", $"{context}: {ex.GetType().Name}: {ex.Message}");

    public static void WriteFile(string fileName, string content)
    {
        try
        {
            var dir = System.IO.Path.GetDirectoryName(Path)!;
            File.WriteAllText(System.IO.Path.Combine(dir, fileName), content);
        }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }

    private static void Write(string level, string message)
    {
        lock (Gate)
        {
            try { File.AppendAllText(Path, $"{DateTime.Now:HH:mm:ss.fff} {level} {message}{Environment.NewLine}"); }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }
    }
}
