using System.Windows;
using System.Windows.Threading;

namespace ShaderCalc.App;

public partial class App : Application
{
    public App()
    {
        // Fluent theme following the Windows light/dark setting
        ThemeMode = ThemeMode.System;
        DispatcherUnhandledException += OnDispatcherUnhandledException;
    }

    private void OnDispatcherUnhandledException(object sender, DispatcherUnhandledExceptionEventArgs e)
    {
        MessageBox.Show(e.Exception.ToString(), "ShaderCalc error", MessageBoxButton.OK, MessageBoxImage.Error);
        e.Handled = true;
    }
}
