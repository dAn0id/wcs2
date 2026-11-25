using System.Drawing;
using CounterStrikeSharp.API.Core;
using CounterStrikeSharp.API.Modules.Utils;
using WcsCs2.Config;

namespace WcsCs2.Skills.Primitives;

public sealed class GiveWeaponPrimitive : ISkillPrimitive
{
    public string Type => "give_weapon";

    public void Execute(SkillContext ctx, SkillActionConfig action)
    {
        var target = ctx.ResolveSingleTarget(action.Target);
        if (!SkillHelpers.IsAlive(target)) return;

        bool stripPrimary = action.GetBool("strip_primary", false);
        bool stripSecondary = action.GetBool("strip_secondary", false);
        var weapons = action.GetStringList("weapons");
        string single = action.GetString("weapon");
        if (!string.IsNullOrEmpty(single))
            weapons.Add(single);

        if (stripPrimary || stripSecondary)
        {
            ctx.Services.Weapons.StripWeapons(target!, keepKnife: true, keepC4: true);
            if (weapons.Count > 0)
            {
                var services = ctx.Services;
                var listCopy = weapons.ToList();
                services.State.SchedulePlayerTimer(target!, 0.3f, () =>
                {
                    if (!SkillHelpers.IsAlive(target)) return;
                    foreach (var w in listCopy)
                        services.Weapons.GiveWeapon(target!, w);
                }, repeat: false, tag: "give_weapon_delayed");
            }
        }
        else
        {
            foreach (var w in weapons)
                ctx.Services.Weapons.GiveWeapon(target!, w);
        }

        string msg = action.GetString("message");
        if (!string.IsNullOrEmpty(msg))
            ctx.Services.Effects.PrintChat(target!, SkillHelpers.FormatMessage(msg, ctx));
    }
}

public sealed class DropWeaponPrimitive : ISkillPrimitive
{
    public string Type => "drop_weapon";

    public void Execute(SkillContext ctx, SkillActionConfig action)
    {
        var victim = ctx.ResolveSingleTarget(action.Target == "self" ? "victim" : action.Target);
        if (!SkillHelpers.IsAlive(victim)) return;

        bool primaryOnly = action.GetBool("primary_only", true);
        ctx.Services.Weapons.DropWeapon(victim!, primaryOnly: primaryOnly);

        string attackerMsg = action.GetString("attacker_message");
        if (!string.IsNullOrEmpty(attackerMsg) && SkillHelpers.IsValidPlayer(ctx.Owner))
            ctx.Services.Effects.PrintCenter(ctx.Owner, SkillHelpers.FormatMessage(attackerMsg, ctx));

        string victimMsg = action.GetString("victim_message");
        if (!string.IsNullOrEmpty(victimMsg))
            ctx.Services.Effects.PrintCenter(victim!, SkillHelpers.FormatMessage(victimMsg, ctx));
    }
}

public sealed class SetClipAmmoPrimitive : ISkillPrimitive
{
    public string Type => "set_clip_ammo";

    public void Execute(SkillContext ctx, SkillActionConfig action)
    {
        var target = ctx.ResolveSingleTarget(action.Target);
        if (!SkillHelpers.IsAlive(target)) return;

        string weapon = action.GetString("weapon", "secondary");
        int? clip = SkillHelpers.HasKey(action, "clip") ? action.GetLevelInt("clip", ctx.SkillLevel) : null;
        int? reserve = SkillHelpers.HasKey(action, "reserve") ? action.GetLevelInt("reserve", ctx.SkillLevel) : null;

        ctx.Services.Weapons.SetClipAndAmmo(target!, weapon, clip, reserve);
    }
}

public sealed class BeamPrimitive : ISkillPrimitive
{
    public string Type => "beam";

    public void Execute(SkillContext ctx, SkillActionConfig action)
    {
        string fromSel = action.GetString("from", "attacker");
        string toSel = action.GetString("to", "victim");
        var fromPlayer = ctx.ResolveSingleTarget(fromSel);
        var toPlayer = ctx.ResolveSingleTarget(toSel);

        var start = SkillHelpers.CloneOrigin(fromPlayer);
        var end = SkillHelpers.CloneOrigin(toPlayer);
        if (start == null || end == null) return;

        float zOffset = action.GetLevelFloat("z_offset", ctx.SkillLevel, 25f);
        float width = action.GetLevelFloat("width", ctx.SkillLevel, 2.5f);
        float duration = action.GetLevelFloat("duration", ctx.SkillLevel, 0.7f);
        Color color = SkillHelpers.ReadColor(action, "color", Color.FromArgb(255, 255, 255, 255));

        ctx.Services.Effects.DrawBeamPoints(
            new Vector(start.X, start.Y, start.Z + zOffset),
            new Vector(end.X, end.Y, end.Z + zOffset),
            color, width, duration);
    }
}

public sealed class BeamRingPrimitive : ISkillPrimitive
{
    public string Type => "beam_ring";

    public void Execute(SkillContext ctx, SkillActionConfig action)
    {
        var target = ctx.ResolveSingleTarget(action.Target);
        var origin = SkillHelpers.CloneOrigin(target);
        if (origin == null) return;

        float radius = action.GetLevelFloat("radius", ctx.SkillLevel, 65f);
        float width = action.GetLevelFloat("width", ctx.SkillLevel, 2.5f);
        float duration = action.GetLevelFloat("duration", ctx.SkillLevel, 1.0f);
        float zOffset = action.GetLevelFloat("z_offset", ctx.SkillLevel, 15f);
        int count = Math.Clamp(action.GetLevelInt("count", ctx.SkillLevel, 1), 1, 6);
        float zStep = action.GetLevelFloat("z_step", ctx.SkillLevel, 10f);
        Color color = SkillHelpers.ReadColor(action, "color", Color.FromArgb(255, 255, 255, 255));

        for (int i = 0; i < count; i++)
        {
            ctx.Services.Effects.DrawBeamRing(origin, radius, color, width, duration, 14, zOffset + i * zStep);
        }
    }
}

public sealed class SoundPrimitive : ISkillPrimitive
{
    public string Type => "sound";

    public void Execute(SkillContext ctx, SkillActionConfig action)
    {
        string soundEvent = action.GetString("sound_event");
        if (string.IsNullOrEmpty(soundEvent)) return;

        if (string.Equals(action.Target, "all", StringComparison.OrdinalIgnoreCase))
        {
            ctx.Services.Effects.PlaySoundAll(soundEvent);
            return;
        }

        var target = ctx.ResolveSingleTarget(action.Target);
        if (SkillHelpers.IsValidPlayer(target))
            ctx.Services.Effects.PlaySound(target!, soundEvent);
    }
}

public sealed class MessagePrimitive : ISkillPrimitive
{
    public string Type => "message";

    public void Execute(SkillContext ctx, SkillActionConfig action)
    {
        string text = action.GetString("text");
        if (string.IsNullOrEmpty(text)) return;

        var target = ctx.ResolveSingleTarget(action.Target);
        if (!SkillHelpers.IsValidPlayer(target)) return;

        string formatted = SkillHelpers.FormatMessage(text, ctx);
        if (action.GetBool("center", false))
            ctx.Services.Effects.PrintCenter(target!, formatted);
        else
            ctx.Services.Effects.PrintChat(target!, formatted);
    }
}
