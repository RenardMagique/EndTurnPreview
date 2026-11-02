using System.Reflection;
using BaseLib.Config;
using Godot;
using HarmonyLib;
using MegaCrit.Sts2.Core.Modding;

namespace EndTurnDamagePreview;

[ModInitializer(nameof(Initialize))]
public static class MainFile
{
    public const string ModId = "EndTurnDamagePreview";
    public const string Version = "v0.15.0";

    public static void Initialize()
    {
        ModConfigRegistry.Register(ModId, new PreviewConfig());
        new Harmony(ModId).PatchAll(Assembly.GetExecutingAssembly());
        MegaCrit.Sts2.Core.Combat.CombatManager.Instance.StateTracker.CombatStateChanged += _ => SimulationClient.Invalidate();
        SimulationClient.StartWarm();
        GD.Print(ModId + " " + Version + " initialized");
    }
}
