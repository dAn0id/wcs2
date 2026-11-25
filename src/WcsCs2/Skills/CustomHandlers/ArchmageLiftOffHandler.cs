using System.Drawing;
using CounterStrikeSharp.API.Core;
using WcsCs2.Skills.Primitives;

namespace WcsCs2.Skills.CustomHandlers;

internal interface IHandlesOwnChance { }

public sealed class ArchmageLiftOffHandler : ISkillHandler
{
    private static readonly int[] DefaultBonusHp = { 5, 9, 12, 15, 18, 20 };

    public string Key => "archmage_lift_off";

    public void Execute(SkillContext ctx)
    {
        var owner = ctx.Owner;
        var pawn = SkillHelpers.GetAlivePawn(owner);
        if (pawn == null)
        {
            ctx.Activated = false;
            return;
        }

        // Legacy Archmage Proudmoore Lift Off is skipped for bots
        if (ctx.OwnerSession.IsBot || owner.IsBot)
        {
            ctx.Activated = false;
            return;
        }

        int bonusHp = ResolveBonusHp(ctx);
        bool isFlying = ctx.Services.State.GetCustomData<bool>(owner, SkillStateKeys.ArchmageFlying);

        if (!isFlying)
        {
            // Toggle ON: enable jetpack flight, grant bonus HP, refund cooldown so player can land anytime
            ctx.Services.State.SetCustomData(owner, SkillStateKeys.ArchmageFlying, true);
            ctx.Services.State.SetCustomData(owner, SkillStateKeys.ArchmageBonusHp, bonusHp);
            ctx.Services.State.SetJetpack(owner, true, thrust: 320f, duration: 0f);

            int newHp = pawn.Health + bonusHp;
            ctx.Services.State.SetHealth(owner, newHp, add: false, updateMaxHealth: newHp > pawn.MaxHealth);

            ctx.Services.Effects.PrintCenter(owner, $"Lift Off activated! (+{bonusHp} HP — press ultimate again to land)");
            var origin = SkillHelpers.CloneOrigin(owner);
            if (origin != null)
            {
                ctx.Services.Effects.DrawBeamRing(origin, 55f, Color.FromArgb(200, 97, 197, 222), 2.5f, 1.0f, 16, 20f);
                ctx.Services.Effects.DrawBeamRing(origin, 65f, Color.FromArgb(220, 153, 217, 234), 2.5f, 1.0f, 16, 25f);
            }

            // Refund cooldown on lift-off (cooldown applies when toggling OFF)
            ctx.Activated = false;
        }
        else
        {
            // Toggle OFF: disable flight, subtract bonus HP (clamped to min 1 HP), apply cooldown
            int storedBonus = ctx.Services.State.GetCustomData<int>(owner, SkillStateKeys.ArchmageBonusHp, bonusHp);
            ctx.Services.State.SetCustomData(owner, SkillStateKeys.ArchmageFlying, false);
            ctx.Services.State.SetCustomData(owner, SkillStateKeys.ArchmageBonusHp, 0);
            ctx.Services.State.SetJetpack(owner, false);

            int newHp = Math.Max(1, pawn.Health - storedBonus);
            ctx.Services.State.SetHealth(owner, newHp, add: false, updateMaxHealth: false);

            ctx.Services.Effects.PrintCenter(owner, "Lift Off deactivated!");
            var origin = SkillHelpers.CloneOrigin(owner);
            if (origin != null)
            {
                ctx.Services.Effects.DrawBeamRing(origin, 45f, Color.FromArgb(200, 97, 197, 222), 2.5f, 0.8f, 16, 20f);
            }

            ctx.Activated = true;
        }
    }

    public void OnCleanup(CCSPlayerController player, IPlayerSession session, bool isDisconnect = false)
    {
        if (isDisconnect || !SkillHelpers.IsValidPlayer(player)) return;
        // PlayerStateManager.ResetTransientState already disables jetpack and clears custom data.
    }

    private static int ResolveBonusHp(SkillContext ctx)
    {
        var action = ctx.Skill?.Actions.FirstOrDefault();
        if (action != null && SkillHelpers.HasKey(action, "bonus_hp"))
            return action.GetLevelInt("bonus_hp", ctx.SkillLevel, 10);

        int idx = Math.Clamp(Math.Max(1, ctx.SkillLevel) - 1, 0, DefaultBonusHp.Length - 1);
        return DefaultBonusHp[idx];
    }
}
