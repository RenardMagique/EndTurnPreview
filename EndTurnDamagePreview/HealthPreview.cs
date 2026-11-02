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
    private readonly Color nativePoisonModulate;
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
            aura = new HealthBarAura(bar, bar.HpBarContainer);
        }
        nativeMiddlegroundLeft = middleground.Position.X;
        nativeMiddlegroundGap = foreground.OffsetRight - middleground.OffsetRight;
        nativePoisonModulate = poisonForeground.SelfModulate;
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
        poisonForeground.SelfModulate = nativePoisonModulate;
        bar.RefreshValues();
    }

    private void AnimateHp(int target, int potentialTarget)
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
        hpLabel.SetTextAutoSize(progress >= 1f && potentialTarget != target
            ? $"{target}–{potentialTarget}/{creature.MaxHp}"
            : $"{displayed}/{creature.MaxHp}");
    }

    private void ApplyBlockPreview(int remaining, int potentialRemaining)
    {
        blockPreviewApplied = true;
        if (remaining > 0)
        {
            breakEffectShown = false;
            blockContainer.Visible = true;
            blockOutline.Visible = true;
            blockLabel.SetTextAutoSize(potentialRemaining == remaining
                ? remaining.ToString()
                : $"{remaining}–{potentialRemaining}");
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
            ApplyBlockPreview(forecast.BlockRemaining, forecast.BlockPotentialRemaining);
            AnimateHp(forecast.HpRemaining, forecast.HpPotentialRemaining);
            aura?.Show(forecast.Randomized);

            // Reproduce an actual hit with the two layers already owned by NHealthBar:
            // red foreground = projected HP; orange middleground = HP about to be lost.
            // Their native textures, clipping and draw order preserve the exact bar shape.
            float maximumWidth = GetNativeBarWidth(creature.MaxHp);
            float currentWidth = GetNativeBarWidth(creature.CurrentHp);
            float projectedWidth = GetNativeBarWidth(forecast.HpRemaining);
            float potentialWidth = GetNativeBarWidth(forecast.HpPotentialRemaining);
            bool losingHp = forecast.HpLost > 0 && currentWidth > projectedWidth;

            foreground.Visible = forecast.HpRemaining > 0;
            foreground.OffsetRight = projectedWidth - maximumWidth;
            middleground.Position = new Vector2(nativeMiddlegroundLeft, middleground.Position.Y);
            middleground.OffsetRight = currentWidth - maximumWidth - nativeMiddlegroundGap;
            middleground.Visible = losingHp;
            bool randomHpRange = forecast.Randomized && potentialWidth > projectedWidth;
            if (randomHpRange)
            {
                // Reuse the game's native poison segment. It is already clipped and
                // nine-patched to the exact angled health-bar silhouette.
                int patchMarginLeft = ((NinePatchRect)poisonForeground).PatchMarginLeft;
                poisonForeground.OffsetLeft = Math.Max(0f, projectedWidth - patchMarginLeft);
                poisonForeground.OffsetRight = potentialWidth - maximumWidth;
                poisonForeground.SelfModulate = new Color("A86BE8");
                poisonForeground.Visible = true;
            }
            else
            {
                poisonForeground.Visible = false;
            }
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
        private static readonly Color EndTurnBlue = new("5A92AA");
        private static readonly Color RandomPurple = new("8F63C9");
        private static readonly float[] LayerAlpha = [0.06f, 0.09f, 0.14f];
        private readonly NHealthBar bar;
        private readonly Control container;
        private readonly Polygon2D[] layers;
        private Vector2 lastSize;
        private Transform2D lastTransform;

        internal HealthBarAura(NHealthBar bar, Control container)
        {
            this.bar = bar;
            this.container = container;
            layers =
            [
                CreateLayer("EndTurnPreviewAuraOuter", 0.06f),
                CreateLayer("EndTurnPreviewAuraMiddle", 0.09f),
                CreateLayer("EndTurnPreviewAuraInner", 0.14f)
            ];
            foreach (var layer in layers)
            {
                bar.AddChild(layer);
                bar.MoveChild(layer, 0);
            }
            Hide();
        }

        private static Polygon2D CreateLayer(string name, float alpha) => new()
        {
            Name = name,
            Color = new Color(EndTurnBlue, alpha)
        };

        internal void Show(bool randomized)
        {
            UpdateGeometry();
            Color color = randomized ? RandomPurple : EndTurnBlue;
            for (int i = 0; i < layers.Length; i++)
            {
                layers[i].Color = new Color(color, LayerAlpha[i]);
                layers[i].Visible = true;
            }
        }

        internal void Hide()
        {
            foreach (var layer in layers) layer.Visible = false;
        }

        private void UpdateGeometry()
        {
            Vector2 size = container.Size;
            Transform2D relativeTransform = bar.GetGlobalTransformWithCanvas().AffineInverse()
                * container.GetGlobalTransformWithCanvas();
            if (size.IsEqualApprox(lastSize) && relativeTransform.IsEqualApprox(lastTransform)) return;
            lastSize = size;
            lastTransform = relativeTransform;

            // The progressively larger polygons leave only a faint halo visible
            // around the native angled bar that is drawn above them.
            layers[0].Polygon = CreateBevelledBar(size, 12f, 10f, relativeTransform);
            layers[1].Polygon = CreateBevelledBar(size, 8f, 8f, relativeTransform);
            layers[2].Polygon = CreateBevelledBar(size, 4f, 6f, relativeTransform);
        }

        private static Vector2[] CreateBevelledBar(
            Vector2 size,
            float padding,
            float bevel,
            Transform2D transform)
        {
            float left = -padding;
            float top = -padding;
            float right = size.X + padding;
            float bottom = size.Y + padding;
            return
            [
                transform * new Vector2(left + bevel, top),
                transform * new Vector2(right - bevel, top),
                transform * new Vector2(right, (top + bottom) * 0.5f),
                transform * new Vector2(right - bevel, bottom),
                transform * new Vector2(left + bevel, bottom),
                transform * new Vector2(left, (top + bottom) * 0.5f)
            ];
        }
    }

    private float GetNativeBarWidth(int hp)
    {
        if (hp <= 0) return 0f;
        return bar.Call(NHealthBar.MethodName.GetFgWidth, hp).AsSingle();
    }
}
