using BaseLib.Config.UI;
using Godot;
using HarmonyLib;
using MegaCrit.Sts2.Core.Nodes;

namespace EndTurnDamagePreview;

internal static class InputShortcut
{
    private const string KeyPrefix = "Key:";
    private const string MousePrefix = "Mouse:";
    private static PreviewConfig? capturingConfig;
    private static NConfigButton? capturingButton;

    internal static string DisplayName => TryParse(out var key, out var mouse)
        ? key != Key.None ? KeyName(key) : MouseName(mouse)
        : "Unassigned";

    internal static bool IsPressed
    {
        get
        {
            if (!TryParse(out var key, out var mouse)) return false;
            return key != Key.None ? Input.IsKeyPressed(key) : Input.IsMouseButtonPressed(mouse);
        }
    }

    internal static void BeginCapture(PreviewConfig config, NConfigButton button)
    {
        capturingConfig = config;
        capturingButton = button;
        SetButtonText(button, "Press a key or mouse button…");
    }

    internal static void RefreshButton(NConfigButton button)
    {
        if (GodotObject.IsInstanceValid(button)) SetButtonText(button, DisplayName);
    }

    internal static void Observe(InputEvent input)
    {
        if (capturingButton == null) return;
        if (!GodotObject.IsInstanceValid(capturingButton) || !capturingButton.IsInsideTree())
        {
            StopCapture();
            return;
        }

        string? binding = input switch
        {
            InputEventKey { Pressed: true, Echo: false, Keycode: Key.Escape } => null,
            InputEventKey { Pressed: true, Echo: false, Keycode: Key.Backspace or Key.Delete } => "",
            InputEventKey { Pressed: true, Echo: false, Keycode: not Key.None } key => KeyPrefix + key.Keycode,
            InputEventMouseButton { Pressed: true, ButtonIndex: >= MouseButton.Left and <= MouseButton.Xbutton2 } mouse => MousePrefix + mouse.ButtonIndex,
            _ => null
        };
        if (binding == null)
        {
            if (input is InputEventKey { Pressed: true, Echo: false, Keycode: Key.Escape })
            {
                RefreshButton(capturingButton);
                StopCapture();
            }
            return;
        }

        PreviewConfig.SimulationShortcut = binding;
        RefreshButton(capturingButton);
        capturingConfig?.Changed();
        capturingConfig?.SaveDebounced();
        capturingButton.GetViewport().SetInputAsHandled();
        StopCapture();
    }

    private static bool TryParse(out Key key, out MouseButton mouse)
    {
        key = Key.None;
        mouse = MouseButton.None;
        string value = PreviewConfig.SimulationShortcut;
        if (value.StartsWith(KeyPrefix, StringComparison.Ordinal)
            && Enum.TryParse(value[KeyPrefix.Length..], out key) && key != Key.None) return true;
        if (value.StartsWith(MousePrefix, StringComparison.Ordinal)
            && Enum.TryParse(value[MousePrefix.Length..], out mouse) && mouse != MouseButton.None) return true;
        key = Key.None;
        mouse = MouseButton.None;
        return false;
    }

    private static string KeyName(Key key) => key.ToString();

    private static string MouseName(MouseButton mouse) => mouse switch
    {
        MouseButton.Left => "Mouse Left",
        MouseButton.Right => "Mouse Right",
        MouseButton.Middle => "Mouse Middle",
        MouseButton.Xbutton1 => "Mouse 4",
        MouseButton.Xbutton2 => "Mouse 5",
        _ => mouse.ToString()
    };

    private static void SetButtonText(NConfigButton button, string text) => button.GetNode<Label>("Label").Text = text;

    private static void StopCapture()
    {
        capturingConfig = null;
        capturingButton = null;
    }
}

[HarmonyPatch(typeof(NGame), nameof(NGame._Input))]
internal static class InputShortcutPatch
{
    private static void Prefix(InputEvent __0) => InputShortcut.Observe(__0);
}
