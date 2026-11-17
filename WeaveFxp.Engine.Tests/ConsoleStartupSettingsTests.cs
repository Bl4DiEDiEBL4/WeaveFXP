using System.Text.Json;
using WeaveFxp.Engine.Core;
using WeaveFxp.Engine.Models;

internal static class ConsoleStartupSettingsTests
{
    public static void Run(Action<bool, string> check)
    {
        var legacy = JsonSerializer.Deserialize<AppSettings>("{}")!.WithDefaults();
        check(!legacy.ShowConsoleOnStartup, "existing settings keep the startup console hidden by default");
        var folder = Path.Combine(Path.GetTempPath(), "weave-console-startup-" + Guid.NewGuid().ToString("N"));
        var state = Path.Combine(folder, "state.json");
        var store = new JsonStore(state);
        var settings = store.Settings();
        settings.TrayIconEnabled = false;
        settings.ShowConsoleOnStartup = true;
        store.UpdateSettings(settings);
        var reopened = new JsonStore(state);
        check(reopened.Settings().ShowConsoleOnStartup && !reopened.Settings().TrayIconEnabled,
            "explicit console-on-startup choice persists independently of the tray setting");
        check(reopened.Settings().Public().ShowConsoleOnStartup,
            "settings form preserves the explicit console choice");
        var off = reopened.Settings();
        off.ShowConsoleOnStartup = false;
        reopened.UpdateSettings(off);
        check(!new JsonStore(state).Settings().ShowConsoleOnStartup,
            "turning console-on-startup off survives reopening settings");
    }
}
