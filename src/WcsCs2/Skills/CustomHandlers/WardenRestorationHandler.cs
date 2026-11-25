using System.Drawing;
using CounterStrikeSharp.API.Core;
using WcsCs2.Skills.Primitives;

namespace WcsCs2.Skills.CustomHandlers;

public sealed class WardenRestorationHandler : ISkillHandler
{
    public string Key => "warden_restoration";

    public void Execute(SkillContext ctx)
    {
        var owner = ctx.Owner;
        var pawn = SkillHelpers.GetAlivePawn(owner);
        if (pawn == null)
        {
            ctx.Activated = false;
            return;
        }

        int hp = pawn.Health;
        if (hp >= 175)
        {
            ctx.Activated = false;
            ctx.Services.Effects.PrintChat(owner, "Your health is already at or above 175 HP!");
            return;
        }

        var (minHeal, maxHeal) = GetHealRange(hp);
        int rolled = SkillHelpers.RollInclusive(ctx.Services.Rng, minHeal, maxHeal);

        int newHp;
        if (hp < 10)
        {
            // Legacy script explicitly caps the 1-9 HP tier at 100 HP when hp + rolled >= 100
            newHp = Math.Min(100, hp + rolled);
        }
        else
        {
            newHp = hp + rolled;
        }

        int gained = Math.Max(0, newHp - hp);
        ctx.Services.State.SetHealth(owner, newHp, add: false, updateMaxHealth: newHp > pawn.MaxHealth);

        ctx.Services.Effects.PrintChat(owner, hp < 10 && newHp >= 100
            ? "Restoration restored you to 100 HP!"
            : $"Restoration healed you for +{gained} HP!");

        var origin = SkillHelpers.CloneOrigin(owner);
        if (origin != null)
        {
            float ringRadius = hp < 10 && newHp >= 100 ? 220f : 150f;
            ctx.Services.Effects.DrawBeamRing(origin, ringRadius, Color.FromArgb(220, 255, 255, 255), 3.0f, 1.0f, 18, 15f);
            ctx.Services.Effects.DrawBeamRing(origin, ringRadius * 0.7f, Color.FromArgb(180, 220, 240, 255), 2.5f, 1.0f, 16, 25f);
        }

        ctx.Activated = true;
    }

    private static (int Min, int Max) GetHealRange(int hp) => hp switch
    {
        >= 150 => (2, 8),
        >= 140 => (5, 10),
        >= 130 => (6, 12),
        >= 120 => (8, 14),
        >= 110 => (9, 19),
        >= 100 => (10, 20),
        >= 90 => (12, 22),
        >= 80 => (14, 24),
        >= 70 => (15, 30),
        >= 60 => (20, 40),
        >= 50 => (25, 50),
        >= 40 => (30, 60),
        >= 30 => (35, 70),
        >= 20 => (40, 80),
        >= 10 => (45, 90),
        _ => (50, 100)
    };
}
