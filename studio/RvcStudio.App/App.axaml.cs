using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;

namespace RvcStudio.App;

public partial class App : Application
{
    public override void Initialize() => AvaloniaXamlLoader.Load(this);

    public override void OnFrameworkInitializationCompleted()
    {
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            desktop.MainWindow = UpdateTransaction.NeedsRecovery(AppContext.BaseDirectory)
                ? new UpdateRecoveryWindow() : new MainWindow();
        }

        base.OnFrameworkInitializationCompleted();
    }
}
