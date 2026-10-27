using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using Cuebot.App.ViewModels;
using Cuebot.App.Views;
using Cuebot.Core;

namespace Cuebot.App;

public partial class App : Application
{
    private AppHost? _host;

    public override void Initialize() => AvaloniaXamlLoader.Load(this);

    public override void OnFrameworkInitializationCompleted()
    {
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            _host = AppHost.Create(new AppPaths());
            desktop.MainWindow = new MainWindow { DataContext = new MainWindowViewModel(_host) };
            desktop.ShutdownRequested += (_, _) => _host.DisposeAsync().AsTask().GetAwaiter().GetResult();
            _ = _host.StartAsync();
        }
        base.OnFrameworkInitializationCompleted();
    }
}
