using Godot;
using HarmonyLib;
using MegaCrit.Sts2.Core.Combat;
using MegaCrit.Sts2.Core.Context;
using MegaCrit.Sts2.Core.Entities.Creatures;
using MegaCrit.Sts2.Core.Nodes.Combat;

namespace EndTurnDamagePreview;

internal static class HoverPreview
{
    private static NEndTurnButton? button;
    private static CombatState? combat;
    private static bool hovered;
    private static string? lastPrepareError;
    private static readonly List<WeakReference<HealthPreview>> bars = new();

    internal static void Bind(NEndTurnButton owner, CombatState state)
    {
        button = owner;
        combat = state;
        SimulationClient.Reset();
        if (!owner.HasNode("EndTurnSimulationTimer"))
        {
            var timer = new Godot.Timer { Name = "EndTurnSimulationTimer", WaitTime = 0.02, Autostart = true };
            owner.AddChild(timer);
            timer.Timeout += () => Prepare(owner);
        }
        SetHover(owner, false);
    }

    private static void Prepare(NEndTurnButton owner)
    {
        if (owner != button || combat == null) return;
        try
        {
            var player = LocalContext.GetMe(combat);
            if (!CombatManager.Instance.IsInProgress || combat.CurrentSide != CombatSide.Player
                || player == null || !player.Creature.IsAlive || CombatManager.Instance.IsPlayerReadyToEndTurn(player)) return;
            SimulationClient.Prepare(combat);
            lastPrepareError = null;
        }
        catch (Exception ex)
        {
            string message = ex.GetType().Name + ": " + ex.Message;
            if (message != lastPrepareError)
            {
                lastPrepareError = message;
                GD.PrintErr(MainFile.ModId + ": background preview failed: " + message);
            }
            SimulationClient.Reset();
        }
    }

    internal static void SetHover(NEndTurnButton owner, bool active)
    {
        if (owner != button) return;
        hovered = active;
        RefreshBars();
    }

    internal static void Release(NEndTurnButton owner)
    {
        if (owner != button) return;
        hovered = false;
        SimulationClient.Reset();
        button = null;
        combat = null;
        RefreshBars();
    }

    internal static bool TryGetCombat(Creature creature, out CombatState state)
    {
        state = combat!;
        var player = combat == null ? null : LocalContext.GetMe(combat);
        bool isOwnedCreature = player != null && (ReferenceEquals(player.Creature, creature)
            || creature.IsPet && ReferenceEquals(creature.PetOwner, player));
        bool automaticDisplay = PreviewConfig.Simulation switch
        {
            SimulationDisplay.Hover => hovered,
            SimulationDisplay.Always => true,
            _ => false
        };
        return (automaticDisplay || InputShortcut.IsPressed) && combat != null && GodotObject.IsInstanceValid(button)
            && button!.IsVisibleInTree() && button.IsEnabled
            && CombatManager.Instance.IsInProgress && combat.CurrentSide == CombatSide.Player
            && creature.IsAlive && isOwnedCreature
            && !CombatManager.Instance.IsPlayerReadyToEndTurn(player!);
    }

    internal static void Register(HealthPreview bar)
    {
        bars.Add(new(bar));
    }

    private static void RefreshBars()
    {
        for (int i = bars.Count - 1; i >= 0; i--)
        {
            if (bars[i].TryGetTarget(out var bar) && bar.IsAlive) bar.Refresh();
            else bars.RemoveAt(i);
        }
    }
}

[HarmonyPatch(typeof(NEndTurnButton), nameof(NEndTurnButton.Initialize))]
internal static class BindButtonPatch
{
    private static void Postfix(NEndTurnButton __instance, CombatState __0) => HoverPreview.Bind(__instance, __0);
}

[HarmonyPatch(typeof(NEndTurnButton), "OnFocus")]
internal static class HoverButtonPatch
{
    private static void Postfix(NEndTurnButton __instance) => HoverPreview.SetHover(__instance, true);
}

[HarmonyPatch(typeof(NEndTurnButton), "OnUnfocus")]
internal static class LeaveButtonPatch
{
    private static void Postfix(NEndTurnButton __instance) => HoverPreview.SetHover(__instance, false);
}

[HarmonyPatch(typeof(NEndTurnButton), nameof(NEndTurnButton.CallReleaseLogic))]
internal static class EndTurnPatch
{
    private static void Prefix(NEndTurnButton __instance) => HoverPreview.SetHover(__instance, false);
}

[HarmonyPatch(typeof(NEndTurnButton), nameof(NEndTurnButton._ExitTree))]
internal static class RemoveButtonPatch
{
    private static void Prefix(NEndTurnButton __instance) => HoverPreview.Release(__instance);
}

[HarmonyPatch(typeof(NHealthBar), nameof(NHealthBar.SetCreature))]
internal static class HealthBarPatch
{
    private static void Postfix(NHealthBar __instance, Creature __0)
    {
        if ((__0.IsPlayer || __0.IsPet) && !__instance.HasNode("EndTurnPreviewTimer"))
            HoverPreview.Register(new HealthPreview(__instance, __0));
    }
}
