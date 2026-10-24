using Godot;
using MegaCrit.Sts2.Core.Entities.Creatures;
using MegaCrit.Sts2.Core.Nodes.Combat;
using MegaCrit.Sts2.Core.Nodes.Vfx;
using MegaCrit.Sts2.addons.mega_text;

namespace EndTurnDamagePreview;

// Changes only the native health-bar presentation; combat values remain untouched.
internal sealed class HealthPreview
{
    private readonly NHealthBar bar;
    private readonly Creature creature;
    private readonly Control foreground;
    private readonly Control middleground;
    private readonly Control poisonForeground;
    private readonly Control doomForeground;
    private readonly Control blockContainer;
    private readonly MegaLabel blockLabel;
    private readonly Control blockOutline;
    private readonly MegaLabel hpLabel;
    private readonly HealthBarAura? aura;
    private readonly float nativeMiddlegroundLeft;
    private readonly float nativeMiddlegroundGap;
    private bool blockPreviewApplied;
    private bool breakEffectShown;
    private bool hpAnimationActive;
    private int hpAnimationFrom;
    private int hpAnimationTarget;
    private ulong hpAnimationStartedAt;
    private bool errorLogged;

    internal bool IsAlive => GodotObject.IsInstanceValid(bar) && bar.IsInsideTree();

    internal HealthPreview(NHealthBar bar, Creature creature)
    {
        this.bar = bar;
        this.creature = creature;
        foreground = bar.GetNode<Control>("%HpForeground");
        middleground = bar.GetNode<Control>("%HpMiddleground");
        poisonForeground = bar.GetNode<Control>("%PoisonForeground");
        doomForeground = bar.GetNode<Control>("%DoomForeground");
        blockContainer = bar.GetNode<Control>("%BlockContainer");
        blockLabel = bar.GetNode<MegaLabel>("%BlockLabel");
        blockOutline = bar.GetNode<Control>("%BlockOutline");
        hpLabel = bar.GetNode<MegaLabel>("%HpLabel");
        if (creature.IsPlayer)
        {
            aura = new HealthBarAura(bar.HpBarContainer);
        }
        nativeMiddlegroundLeft = middleground.Position.X;
        nativeMiddlegroundGap = foreground.OffsetRight - middleground.OffsetRight;
        var timer = new Godot.Timer { Name = "EndTurnPreviewTimer", WaitTime = 0.02, Autostart = true };
        bar.AddChild(timer);
        timer.Timeout += Refresh;
    }

    private void Hide()
    {
        RestoreNativeUi();
    }

    private void RestoreNativeUi()
    {
        aura?.Hide();
        if (!blockPreviewApplied && !hpAnimationActive) return;
        blockPreviewApplied = false;
        breakEffectShown = false;
        hpAnimationActive = false;
        bar.RefreshValues();
    }

    private void AnimateHp(int target)
    {
        int current = creature.CurrentHp;
        if (!hpAnimationActive || hpAnimationFrom != current || hpAnimationTarget != target)
        {
            hpAnimationActive = true;
            hpAnimationFrom = current;
            hpAnimationTarget = target;
            hpAnimationStartedAt = Time.GetTicksMsec();
        }
        const float durationMs = 325f;
        float progress = Math.Clamp((Time.GetTicksMsec() - hpAnimationStartedAt) / durationMs, 0f, 1f);
        // Ease out so the final predicted value remains easy to read.
        float eased = 1f - MathF.Pow(1f - progress, 3f);
        int displayed = (int)MathF.Round(hpAnimationFrom + (hpAnimationTarget - hpAnimationFrom) * eased);
        hpLabel.SetTextAutoSize($"{displayed}/{creature.MaxHp}");
    }

    private void ApplyBlockPreview(int remaining)
    {
        blockPreviewApplied = true;
        if (remaining > 0)
        {
            breakEffectShown = false;
            blockContainer.Visible = true;
            blockOutline.Visible = true;
            blockLabel.SetTextAutoSize(remaining.ToString());
            foreground.SelfModulate = new Color("3B6FA3");
            hpLabel.AddThemeColorOverride("font_outline_color", new Color("1B3045"));
            return;
        }

        if (creature.Block > 0 && !breakEffectShown)
        {
            breakEffectShown = true;
            var effect = NBlockBrokenVfx.Create();
            if (effect != null)
            {
                bar.AddChild(effect);
                effect.GlobalPosition = blockContainer.GlobalPosition + blockContainer.Size * 0.5f;
            }
        }
        blockContainer.Visible = false;
        blockOutline.Visible = false;
        foreground.SelfModulate = new Color("F1373E");
        hpLabel.AddThemeColorOverride("font_outline_color", new Color("900000"));
    }

    internal void Refresh()
    {
        if (!IsAlive) return;
        try
        {
            if (!HoverPreview.TryGetCombat(creature, out var combat) || !bar.IsVisibleInTree())
            {
                Hide();
                return;
            }
            var forecast = SimulationClient.Get(creature);
            if (forecast.Unknown)
            {
                RestoreNativeUi();
                return;
            }
            ApplyBlockPreview(forecast.BlockRemaining);
            AnimateHp(forecast.HpRemaining);
            aura?.Show();

            // Reproduce an actual hit with the two layers already owned by NHealthBar:
            // red foreground = projected HP; orange middleground = HP about to be lost.
            // Their native textures, clipping and draw order preserve the exact bar shape.
            float maximumWidth = GetNativeBarWidth(creature.MaxHp);
            float currentWidth = GetNativeBarWidth(creature.CurrentHp);
            float projectedWidth = GetNativeBarWidth(forecast.HpRemaining);
            bool losingHp = forecast.HpLost > 0 && currentWidth > projectedWidth;

            foreground.Visible = forecast.HpRemaining > 0;
            foreground.OffsetRight = projectedWidth - maximumWidth;
            middleground.Position = new Vector2(nativeMiddlegroundLeft, middleground.Position.Y);
            middleground.OffsetRight = currentWidth - maximumWidth - nativeMiddlegroundGap;
            middleground.Visible = losingHp;
            poisonForeground.Visible = false;
            doomForeground.Visible = false;
        }
        catch (Exception ex)
        {
            Hide();
            if (!errorLogged) { GD.PrintErr(MainFile.ModId + ": " + ex); errorLogged = true; }
        }
    }

    // A resolution-independent glow made from three soft, bevelled silhouettes.
    // It lives inside the native HP container, so UI scaling and layout changes
    // are inherited directly from the game rather than reconstructed from screen coordinates.
    private sealed class HealthBarAura
    {
        private static readonly Color EndTurnBlue = new("42697A");
        private readonly Control container;
        private readonly Polygon2D[] layers;
        private Vector2 lastSize;

        internal HealthBarAura(Control container)
        {
            this.container = container;
            layers =
            [
                CreateLayer("EndTurnPreviewAuraOuter", 0.035f),
                CreateLayer("EndTurnPreviewAuraMiddle", 0.055f),
                CreateLayer("EndTurnPreviewAuraInner", 0.085f)
            ];
            foreach (var layer in layers) container.AddChild(layer);
            Hide();
        }

        private static Polygon2D CreateLayer(string name, float alpha) => new()
        {
            Name = name,
            Color = new Color(EndTurnBlue, alpha),
            ZIndex = -100
        };

        internal void Show()
        {
            UpdateGeometry();
            foreach (var layer in layers) layer.Visible = true;
        }

        internal void Hide()
        {
            foreach (var layer in layers) layer.Visible = false;
        }

        private void UpdateGeometry()
        {
            Vector2 size = container.Size;
            if (size.IsEqualApprox(lastSize)) return;
            lastSize = size;

            // The progressively larger polygons leave only a faint halo visible
            // around the native angled bar that is drawn above them.
            layers[0].Polygon = CreateBevelledBar(size, 10f, 9f);
            layers[1].Polygon = CreateBevelledBar(size, 7f, 7f);
            layers[2].Polygon = CreateBevelledBar(size, 4f, 5f);
        }

        private static Vector2[] CreateBevelledBar(Vector2 size, float padding, float bevel)
        {
            float left = -padding;
            float top = -padding;
            float right = size.X + padding;
            float bottom = size.Y + padding;
            return
            [
                new(left + bevel, top),
                new(right - bevel, top),
                new(right, (top + bottom) * 0.5f),
                new(right - bevel, bottom),
                new(left + bevel, bottom),
                new(left, (top + bottom) * 0.5f)
            ];
        }
    }

    private float GetNativeBarWidth(int hp)
    {
        if (hp <= 0) return 0f;
        return bar.Call(NHealthBar.MethodName.GetFgWidth, hp).AsSingle();
    }
}
