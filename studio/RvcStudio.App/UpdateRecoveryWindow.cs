using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Layout;

namespace RvcStudio.App;

public sealed class UpdateRecoveryWindow : Window
{
    public UpdateRecoveryWindow()
    {
        Title = "RVC Studio 更新恢复"; Width = 500; Height = 240; CanResize = false;
        var message = new TextBlock { Text = "检测到上次更新未完成，需要恢复后才能启动。请点击恢复并允许管理员权限。", TextWrapping = Avalonia.Media.TextWrapping.Wrap };
        var button = new Button { Content = "恢复并重新启动", HorizontalAlignment = HorizontalAlignment.Right };
        var panel = new StackPanel { Margin = new Thickness(24), Spacing = 24 };
        panel.Children.Add(message); panel.Children.Add(button); Content = panel;
        button.Click += async (_, _) =>
        {
            button.IsEnabled = false;
            try
            {
                await UpdateRunner.BeginAsync("", true);
                if (Application.Current?.ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop) desktop.Shutdown();
            }
            catch (Exception ex) { message.Text = ex.Message; button.IsEnabled = true; }
        };
    }
}
