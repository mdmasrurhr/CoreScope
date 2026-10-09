using System;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using CoreScope.Core;
using CoreScope.Core.Theming;
using CoreScope.Core.ViewModels;
using Microsoft.Win32;

namespace CoreScope.Views;

public partial class AppearanceView : UserControl
{
    private const string Filter = "CoreScope theme (*.corescope-theme.json)|*.corescope-theme.json|JSON (*.json)|*.json";

    public AppearanceView() => InitializeComponent();

    private AppearanceViewModel? Vm => DataContext as AppearanceViewModel;

    private void Export_Click(object sender, RoutedEventArgs e)
    {
        if (Vm is not { } vm) return;
        var dialog = new SaveFileDialog { Title = "Export theme", Filter = Filter, FileName = "my-theme" + ThemeFile.Extension, AddExtension = true };
        if (dialog.ShowDialog() != true) return;
        try
        {
            File.WriteAllText(dialog.FileName, vm.ExportJson());
            vm.ReportShare("Saved to " + dialog.FileName);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Log.Error("Exporting theme", ex);
            vm.ReportShare("Couldn't save the file: " + ex.Message);
        }
    }

    private void Import_Click(object sender, RoutedEventArgs e)
    {
        if (Vm is not { } vm) return;
        var dialog = new OpenFileDialog { Title = "Import theme", Filter = Filter, CheckFileExists = true };
        if (dialog.ShowDialog() != true) return;
        try
        {
            if (new FileInfo(dialog.FileName).Length > ThemeFile.MaxBytes) { vm.ReportShare("That file is too large to be a CoreScope theme."); return; }
            vm.Import(File.ReadAllText(dialog.FileName));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Log.Error("Importing theme", ex);
            vm.ReportShare("Couldn't read the file: " + ex.Message);
        }
    }
}
