using System.Windows;

namespace PatchAdamsSharp;

public partial class App : Application
{
    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        ThemePalette.Current.Apply(AppPreferencesStore.Load().Theme);
        // Includes all WPF dialogs, including those created in code.
        EventManager.RegisterClassHandler(typeof(Window), FrameworkElement.LoadedEvent,
            new RoutedEventHandler((sender, _) => ThemePalette.Current.ApplyTitleBar((Window)sender)));
    }
}
