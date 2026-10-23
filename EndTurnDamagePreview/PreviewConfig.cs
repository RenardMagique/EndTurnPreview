using System.Reflection;
using BaseLib.Config;
using BaseLib.Config.UI;
using Godot;

namespace EndTurnDamagePreview;

internal enum SimulationDisplay
{
    Hover,
    Always,
    Disabled
}

internal sealed class PreviewConfig : SimpleModConfig
{
    public static SimulationDisplay Simulation { get; set; } = SimulationDisplay.Hover;

    [ConfigHideInUI]
    public static string SimulationShortcut { get; set; } = "";

    // Kept for one release so existing configurations migrate without losing their preference.
    [ConfigHideInUI, ConfigIgnoreRestoreDefaults]
    public static bool AlwaysVisible { get; set; }

    public PreviewConfig()
    {
        if (!AlwaysVisible) return;
        Simulation = SimulationDisplay.Always;
        AlwaysVisible = false;
        Save();
    }

    public override void SetupConfigUI(Control optionContainer)
    {
        PropertyInfo modeProperty = typeof(PreviewConfig).GetProperty(nameof(Simulation))!;
        Control dropdown = CreateRawDropdownControl(modeProperty);
        var modeLabel = ModConfig.CreateRawLabelControl("Simulation", 28);
        var modeRow = new NConfigOptionRow(ModPrefix, modeProperty.Name, modeLabel, dropdown)
        {
            UniqueNameInOwner = true
        };
        optionContainer.AddChild(modeRow);

        NConfigButton? shortcutButton = null;
        shortcutButton = CreateRawButtonControl(InputShortcut.DisplayName, () => InputShortcut.BeginCapture(this, shortcutButton!));
        var shortcutLabel = ModConfig.CreateRawLabelControl("Simulation shortcut", 28);
        var shortcutRow = new NConfigOptionRow(ModPrefix, nameof(SimulationShortcut), shortcutLabel, shortcutButton)
        {
            UniqueNameInOwner = true
        };
        optionContainer.AddChild(shortcutRow);

        Action refreshShortcut = () => InputShortcut.RefreshButton(shortcutButton);
        OnConfigReloaded += refreshShortcut;
        _configReloadedHandlers.Add(refreshShortcut);
        AddRestoreDefaultsButton(optionContainer);
        SetupFocusNeighbors(optionContainer);
    }
}
