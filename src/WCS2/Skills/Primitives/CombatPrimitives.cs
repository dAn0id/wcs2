using System.Drawing;
using CounterStrikeSharp.API.Core;
using CounterStrikeSharp.API.Modules.Utils;
using WCS2.Config;

namespace WCS2.Skills.Primitives;

public sealed class BonusDamagePrimitive : ISkillPrimitive
{
    public string Type => "bonus_damage";

    public void Execute(SkillContext ctx, SkillActionConfig action)
    {
        var victim = ctx.ResolveSingleTarget(action.Target == "self" ? "victim" : action.Target);
        if (!SkillHelpers.IsAlive(victim) || !SkillHelpers.IsAlive(ctx.Owner))
            return;

        float damage = ResolveDamage(ctx, action);
        if (damage <= 0f) return;

        bool ignoreArmor = action.GetBool("ignore_armor", false);
        string weaponHint = action.GetString("weapon_hint", string.IsNullOrEmpty(ctx.WeaponName) ? "world" : ctx.WeaponName);

        // If inside OnPlayerTakeDamagePre with DamageInfo, fold directly into BonusDamage if not ignoring armor,
        // otherwise apply clean skill damage so kill attribution and damage numbers work reliably.
        if (ctx.DamageInfo != null && !ignoreArmor && victim!.Slot == ctx.Victim?.Slot)
        {
            ctx.BonusDamage += damage;
        }
        else
        {
            ctx.Services.Combat.DealDamage(victim!, ctx.Owner, damage, weaponHint, ignoreArmor: ignoreArmor, isSkillDamage: true);
        }

        string dmgText = ((int)Math.Round(damage)).ToString();
        string centerMsg = action.GetString("center_message");
        if (!string.IsNullOrEmpty(centerMsg))
            ctx.Services.Effects.PrintCenter(ctx.Owner, SkillHelpers.FormatMessage(centerMsg, ctx, dmgText));

        string victimMsg = action.GetString("victim_message");
        if (!string.IsNullOrEmpty(victimMsg) && victim!.Slot != ctx.Owner.Slot)
            ctx.Services.Effects.PrintChat(victim, SkillHelpers.FormatMessage(victimMsg, ctx, dmgText));
    }

    internal static float ResolveDamage(SkillContext ctx, SkillActionConfig action)
    {
        if (SkillHelpers.HasKey(action, "min_damage") || SkillHelpers.HasKey(action, "max_damage"))
        {
            int min = action.GetLevelInt("min_damage", ctx.SkillLevel, 1);
            int max = action.GetLevelInt("max_damage", ctx.SkillLevel, min);
            return SkillHelpers.RollInclusive(ctx.Services.Rng, min, max);
        }
        return action.GetLevelFloat("damage", ctx.SkillLevel, 0f);
    }
}

public sealed class ReflectDamagePrimitive : ISkillPrimitive
{
    public string Type => "reflect_damage";

    public void Execute(SkillContext ctx, SkillActionConfig action)
    {
        var attacker = ctx.ResolveSingleTarget(action.Target == "self" ? "attacker" : action.Target);
        if (!SkillHelpers.IsAlive(attacker) || !SkillHelpers.IsAlive(ctx.Owner))
            return;
        if (attacker!.Slot == ctx.Owner.Slot)
            return;

        float damage = BonusDamagePrimitive.ResolveDamage(ctx, action);
        if (damage <= 0f) return;

        bool ignoreArmor = action.GetBool("ignore_armor", false);
        ctx.Services.Combat.DealDamage(attacker, ctx.Owner, damage, "world", ignoreArmor: ignoreArmor, isSkillDamage: true);

        string dmgText = ((int)Math.Round(damage)).ToString();
        string centerMsg = action.GetString("center_message");
        if (!string.IsNullOrEmpty(centerMsg))
            ctx.Services.Effects.PrintCenter(ctx.Owner, SkillHelpers.FormatMessage(centerMsg, ctx, dmgText));

        string attackerMsg = action.GetString("attacker_message");
        if (!string.IsNullOrEmpty(attackerMsg))
            ctx.Services.Effects.PrintCenter(attacker, SkillHelpers.FormatMessage(attackerMsg, ctx, dmgText));
    }
}

public sealed class VampiricPrimitive : ISkillPrimitive
{
    public string Type => "vampiric";

    public void Execute(SkillContext ctx, SkillActionConfig action)
    {
        var owner = ctx.ResolveSingleTarget(action.Target);
        var pawn = SkillHelpers.GetAlivePawn(owner);
        if (owner == null || pawn == null) return;

        int maxCap = action.GetLevelInt("max_cap", ctx.SkillLevel, 260);
        if (maxCap > 0 && pawn.Health >= maxCap)
        {
            string capMsg = action.GetString("cap_message");
            if (!string.IsNullOrEmpty(capMsg))
                ctx.Services.Effects.PrintCenter(owner, SkillHelpers.FormatMessage(capMsg, ctx, maxCap.ToString()));
            return;
        }

        int heal;
        if (SkillHelpers.HasKey(action, "min_heal") || SkillHelpers.HasKey(action, "max_heal"))
        {
            int min = action.GetLevelInt("min_heal", ctx.SkillLevel, 12);
            int max = action.GetLevelInt("max_heal", ctx.SkillLevel, Math.Max(min, 20));
            heal = SkillHelpers.RollInclusive(ctx.Services.Rng, min, max);
        }
        else
        {
            heal = action.GetLevelInt("amount", ctx.SkillLevel, 15);
        }

        if (heal <= 0) return;

        int targetHp = maxCap > 0 ? Math.Min(pawn.Health + heal, maxCap) : pawn.Health + heal;
        int actualHeal = targetHp - pawn.Health;
        if (actualHeal <= 0) return;

        ctx.Services.State.SetHealth(owner, targetHp, add: false, updateMaxHealth: targetHp > pawn.MaxHealth);

        string centerMsg = action.GetString("center_message");
        if (!string.IsNullOrEmpty(centerMsg))
            ctx.Services.Effects.PrintCenter(owner, SkillHelpers.FormatMessage(centerMsg, ctx, actualHeal.ToString()));
    }
}

public sealed class AoEDamagePrimitive : ISkillPrimitive
{
    public string Type => "aoe_damage";

    public void Execute(SkillContext ctx, SkillActionConfig action)
    {
        var origin = SkillHelpers.CloneOrigin(ctx.Owner);
        if (origin == null)
        {
            if (ShouldRefund(ctx, action)) ctx.Activated = false;
            return;
        }

        float radius = action.GetLevelFloat("radius", ctx.SkillLevel, 250f);
        int maxTargets = action.GetLevelInt("max_targets", ctx.SkillLevel, 20);
        bool respectImmunity = action.GetBool("respect_immunity", true);
        bool refundIfNone = ShouldRefund(ctx, action);
        bool drawBeams = action.GetBool("draw_beams", false);
        bool drawRings = action.GetBool("draw_rings", true);
        string weaponHint = action.GetString("weapon_hint", "hegrenade");
        string sound = action.GetString("sound");

        bool ffa = ctx.Services.Sessions.IsFriendlyFireAllowed();
        CsTeam? enemyTeam = ffa ? null : SkillHelpers.GetEnemyTeam(ctx.Owner.Team);
        if (!ffa && enemyTeam == null)
        {
            if (refundIfNone) ctx.Activated = false;
            return;
        }

        var candidates = ctx.Services.Combat.GetPlayersInRadius(origin, radius, teamFilter: enemyTeam, exclude: ctx.Owner, aliveOnly: true);
        if (candidates.Count == 0)
        {
            if (refundIfNone)
            {
                ctx.Activated = false;
                ctx.Services.Effects.PrintChat(ctx.Owner, "No enemies within range.");
            }
            return;
        }

        Color beamColor = SkillHelpers.ReadColor(action, "beam_color", Color.FromArgb(255, 160, 160, 230));
        Color ringColor = SkillHelpers.ReadColor(action, "ring_color", Color.FromArgb(255, 255, 120, 40));

        // Sort closest first for deterministic targeting
        candidates.Sort((a, b) =>
        {
            var oa = SkillHelpers.CloneOrigin(a);
            var ob = SkillHelpers.CloneOrigin(b);
            float da = oa != null ? DistSq(origin, oa) : float.MaxValue;
            float db = ob != null ? DistSq(origin, ob) : float.MaxValue;
            return da.CompareTo(db);
        });

        int hitCount = 0;
        int immuneCount = 0;
        int totalDamageDealt = 0;
        var casterBeamPos = new Vector(origin.X, origin.Y, origin.Z + 38f);

        foreach (var enemy in candidates)
        {
            if (hitCount >= maxTargets) break;
            if (!SkillHelpers.IsAlive(enemy)) continue;

            if (respectImmunity && SkillHelpers.IsUltimateImmune(ctx.Services, enemy))
            {
                immuneCount++;
                ctx.Services.Effects.PrintChat(enemy, "Your Ultimate Immunity blocked an enemy ultimate!");
                continue;
            }

            float dmg = BonusDamagePrimitive.ResolveDamage(ctx, action);
            if (dmg <= 0f) continue;

            var enemyPos = SkillHelpers.CloneOrigin(enemy);
            ctx.Services.Combat.DealDamage(enemy, ctx.Owner, dmg, weaponHint, ignoreArmor: false, isSkillDamage: true);
            hitCount++;
            totalDamageDealt = (int)Math.Round(dmg);

            if (enemyPos != null)
            {
                if (drawBeams)
                {
                    var targetBeamPos = new Vector(enemyPos.X, enemyPos.Y, enemyPos.Z + 38f);
                    ctx.Services.Effects.DrawBeamPoints(casterBeamPos, targetBeamPos, beamColor, 3.0f, 0.7f);
                }
                if (drawRings)
                {
                    ctx.Services.Effects.DrawBeamRing(enemyPos, 45f, ringColor, 2.5f, 0.6f, 12, 20f);
                }
            }

            string targetMsg = action.GetString("target_message");
            if (!string.IsNullOrEmpty(targetMsg))
                ctx.Services.Effects.PrintCenter(enemy, SkillHelpers.FormatMessage(targetMsg, ctx, ((int)Math.Round(dmg)).ToString()));

            if (!string.IsNullOrEmpty(sound))
                ctx.Services.Effects.PlaySound(enemy, sound);
        }

        if (hitCount == 0)
        {
            if (immuneCount > 0)
                ctx.Services.Effects.PrintChat(ctx.Owner, "All nearby enemies are immune to ultimates!");
            if (refundIfNone)
                ctx.Activated = false;
            return;
        }

        if (drawRings)
        {
            ctx.Services.Effects.DrawBeamRing(origin, Math.Min(radius, 255f), ringColor, 3.5f, 0.8f, 20, 25f);
        }

        if (!string.IsNullOrEmpty(sound))
            ctx.Services.Effects.PlaySound(ctx.Owner, sound);

        string centerMsg = action.GetString("center_message");
        if (!string.IsNullOrEmpty(centerMsg))
            ctx.Services.Effects.PrintCenter(ctx.Owner, SkillHelpers.FormatMessage(centerMsg, ctx, hitCount.ToString(), totalDamageDealt.ToString()));
        else if (ctx.Trigger == SkillTrigger.PlayerUltimate)
            ctx.Services.Effects.PrintCenter(ctx.Owner, $"{ctx.Skill?.Name ?? "Ability"} struck {hitCount} enemy(s)!");
    }

    private static bool ShouldRefund(SkillContext ctx, SkillActionConfig action)
    {
        if (action.Params.ContainsKey("refund_if_none"))
            return action.GetBool("refund_if_none", false);
        return ctx.Trigger == SkillTrigger.PlayerUltimate || ctx.Trigger == SkillTrigger.PlayerAbility;
    }

    private static float DistSq(Vector a, Vector b)
    {
        float dx = a.X - b.X;
        float dy = a.Y - b.Y;
        float dz = a.Z - b.Z;
        return dx * dx + dy * dy + dz * dz;
    }
}

public sealed class BurnDotPrimitive : ISkillPrimitive
{
    private readonly RoundTracker _roundTracker;

    internal BurnDotPrimitive(RoundTracker roundTracker)
    {
        _roundTracker = roundTracker;
    }

    public string Type => "burn_dot";

    public void Execute(SkillContext ctx, SkillActionConfig action)
    {
        var victim = ctx.ResolveSingleTarget(action.Target == "self" ? "victim" : action.Target);
        if (!SkillHelpers.IsAlive(victim) || !SkillHelpers.IsAlive(ctx.Owner))
            return;

        string preventKey = action.GetString("prevent_stack_key");
        if (!string.IsNullOrEmpty(preventKey) && ctx.Services.State.GetCustomData<bool>(victim!, preventKey))
        {
            ctx.Activated = false;
            return;
        }

        float duration = action.GetLevelFloat("duration", ctx.SkillLevel, 3.0f);
        float damagePerTick = action.GetLevelFloat("damage_per_tick", ctx.SkillLevel, 4.0f);
        float interval = Math.Max(0.25f, action.GetLevelFloat("interval", ctx.SkillLevel, 0.5f));
        Color fxColor = SkillHelpers.ReadColor(action, "color", Color.FromArgb(255, 232, 111, 0));

        if (!string.IsNullOrEmpty(preventKey))
        {
            ctx.Services.State.SetCustomData(victim!, preventKey, true);
            ctx.Services.State.SchedulePlayerTimer(victim!, duration, () =>
            {
                if (SkillHelpers.IsValidPlayer(victim))
                    ctx.Services.State.SetCustomData(victim!, preventKey, false);
            }, repeat: false, tag: $"guard_{preventKey}");
        }

        // Visual beam from attacker to victim
        var start = SkillHelpers.CloneOrigin(ctx.Owner);
        var end = SkillHelpers.CloneOrigin(victim);
        if (start != null && end != null)
        {
            ctx.Services.Effects.DrawBeamPoints(
                new Vector(start.X, start.Y, start.Z + 25f),
                new Vector(end.X, end.Y, end.Z + 25f),
                fxColor, 3.0f, 0.7f);
        }

        int totalTicks = Math.Max(1, (int)Math.Round(duration / interval));
        int capturedRound = _roundTracker.CurrentRoundId;
        var services = ctx.Services;
        var attacker = ctx.Owner;

        for (int i = 1; i <= totalTicks; i++)
        {
            float tickDelay = i * interval;
            services.State.SchedulePlayerTimer(victim!, tickDelay, () =>
            {
                if (capturedRound != _roundTracker.CurrentRoundId) return;
                if (!SkillHelpers.IsAlive(victim)) return;

                var validAttacker = SkillHelpers.IsValidPlayer(attacker) ? attacker : null;
                services.Combat.DealDamage(victim!, validAttacker, damagePerTick, "inferno", ignoreArmor: false, isSkillDamage: true);

                var pos = SkillHelpers.CloneOrigin(victim);
                if (pos != null)
                    services.Effects.DrawBeamRing(pos, 32f, fxColor, 2.0f, 0.4f, 10, 15f);
            }, repeat: false, tag: $"dot_{ctx.Skill?.Key ?? "burn"}");
        }

        string attackerMsg = action.GetString("attacker_message");
        if (!string.IsNullOrEmpty(attackerMsg))
            ctx.Services.Effects.PrintChat(ctx.Owner, SkillHelpers.FormatMessage(attackerMsg, ctx, duration.ToString("0.#")));

        string victimMsg = action.GetString("victim_message");
        if (!string.IsNullOrEmpty(victimMsg))
            ctx.Services.Effects.PrintChat(victim!, SkillHelpers.FormatMessage(victimMsg, ctx, duration.ToString("0.#")));
    }
}

public sealed class StealCashPrimitive : ISkillPrimitive
{
    public string Type => "steal_cash";

    public void Execute(SkillContext ctx, SkillActionConfig action)
    {
        var victim = ctx.ResolveSingleTarget(action.Target == "self" ? "victim" : action.Target);
        if (!SkillHelpers.IsAlive(victim) || !SkillHelpers.IsAlive(ctx.Owner))
            return;

        int requested = action.GetLevelInt("amount", ctx.SkillLevel, 50);
        if (requested <= 0) return;

        int victimCash = SkillHelpers.GetCash(victim);
        int stolen = Math.Min(requested, Math.Max(0, victimCash));
        if (stolen <= 0) return;

        ctx.Services.State.SetCash(victim!, -stolen, add: true);
        ctx.Services.State.SetCash(ctx.Owner, stolen, add: true);

        var start = SkillHelpers.CloneOrigin(ctx.Owner);
        var end = SkillHelpers.CloneOrigin(victim);
        if (start != null && end != null)
        {
            ctx.Services.Effects.DrawBeamPoints(
                new Vector(start.X, start.Y, start.Z + 25f),
                new Vector(end.X, end.Y, end.Z + 25f),
                Color.FromArgb(255, 100, 255, 248), 3.0f, 0.7f);
        }

        string attackerMsg = action.GetString("attacker_message");
        if (!string.IsNullOrEmpty(attackerMsg))
            ctx.Services.Effects.PrintChat(ctx.Owner, SkillHelpers.FormatMessage(attackerMsg, ctx, stolen.ToString()));

        string victimMsg = action.GetString("victim_message");
        if (!string.IsNullOrEmpty(victimMsg))
            ctx.Services.Effects.PrintChat(victim!, SkillHelpers.FormatMessage(victimMsg, ctx, stolen.ToString()));
    }
}

public sealed class CashToHealthPrimitive : ISkillPrimitive
{
    public string Type => "cash_to_health";

    public void Execute(SkillContext ctx, SkillActionConfig action)
    {
        var owner = ctx.Owner;
        var pawn = SkillHelpers.GetAlivePawn(owner);
        if (pawn == null)
        {
            ctx.Activated = false;
            return;
        }

        int healAmount = action.GetLevelInt("amount", ctx.SkillLevel, 15);
        int cost = action.GetLevelInt("cost", ctx.SkillLevel, 100);
        int maxHpThreshold = action.GetLevelInt("max_hp_threshold", ctx.SkillLevel, 175);

        if (pawn.Health > maxHpThreshold)
        {
            ctx.Activated = false;
            ctx.Services.Effects.PrintChat(owner, $"You already have over {maxHpThreshold} HP!");
            return;
        }

        int cash = SkillHelpers.GetCash(owner);
        if (cash < cost)
        {
            ctx.Activated = false;
            ctx.Services.Effects.PrintChat(owner, $"Not enough cash! Need ${cost}.");
            return;
        }

        ctx.Services.State.SetCash(owner, -cost, add: true);
        int newHp = pawn.Health + healAmount;
        ctx.Services.State.SetHealth(owner, newHp, add: false, updateMaxHealth: newHp > pawn.MaxHealth);

        ctx.Services.Effects.PrintCenter(owner, $"Curing Ritual healed you for +{healAmount} HP!");
        var origin = SkillHelpers.CloneOrigin(owner);
        if (origin != null)
        {
            ctx.Services.Effects.DrawBeamRing(origin, 50f, Color.FromArgb(220, 255, 255, 220), 2.5f, 1.0f, 16, 25f);
            ctx.Services.Effects.DrawBeamRing(origin, 55f, Color.FromArgb(220, 255, 235, 140), 2.5f, 1.0f, 16, 35f);
        }
    }
}

public sealed class RandomEnemyDrainPrimitive : ISkillPrimitive
{
    public string Type => "random_enemy_drain";

    public void Execute(SkillContext ctx, SkillActionConfig action)
    {
        var owner = ctx.Owner;
        var pawn = SkillHelpers.GetAlivePawn(owner);
        if (pawn == null)
        {
            ctx.Activated = false;
            return;
        }

        bool ffa = ctx.Services.Sessions.IsFriendlyFireAllowed();
        CsTeam? enemyTeam = ffa ? null : SkillHelpers.GetEnemyTeam(owner.Team);
        if (!ffa && enemyTeam == null)
        {
            ctx.Activated = false;
            return;
        }

        var enemies = ctx.Services.Sessions.GetAlivePlayers(enemyTeam)
            .Where(p => SkillHelpers.IsAlive(p) && p.Slot != owner.Slot)
            .ToList();

        if (enemies.Count == 0)
        {
            ctx.Activated = false;
            ctx.Services.Effects.PrintChat(owner, "No living enemies to target.");
            return;
        }

        var target = enemies[ctx.Services.Rng.Next(enemies.Count)];
        if (SkillHelpers.IsUltimateImmune(ctx.Services, target))
        {
            ctx.Activated = false;
            ctx.Services.Effects.PrintChat(target, "Your Ultimate Immunity blocked Locust Swarm!");
            ctx.Services.Effects.PrintChat(owner, $"{target.PlayerName} is immune to ultimates!");
            return;
        }

        float damage = action.GetLevelFloat("damage", ctx.SkillLevel, 35f);
        int heal = action.GetLevelInt("heal", ctx.SkillLevel, 35);
        int healthCap = action.GetLevelInt("health_cap", ctx.SkillLevel, 225);
        int armorBonus = action.GetLevelInt("armor", ctx.SkillLevel, 10);
        int armorCap = action.GetLevelInt("armor_cap", ctx.SkillLevel, 250);

        ctx.Services.Combat.DealDamage(target, owner, damage, "world", ignoreArmor: false, isSkillDamage: true);

        int oldHp = pawn.Health;
        int newHp = Math.Min(oldHp + heal, healthCap);
        if (newHp > oldHp)
        {
            ctx.Services.State.SetHealth(owner, newHp, add: false, updateMaxHealth: newHp > pawn.MaxHealth);
            ctx.Services.Effects.PrintChat(owner, newHp >= healthCap
                ? $"Locust Swarm reached health cap ({healthCap} HP)."
                : $"Locust Swarm drained +{newHp - oldHp} HP!");
        }

        int currentArmor = pawn.ArmorValue;
        int newArmor = Math.Min(currentArmor + armorBonus, armorCap);
        if (newArmor > currentArmor)
        {
            ctx.Services.State.SetArmor(owner, newArmor, add: false, giveHelmet: true);
            ctx.Services.Effects.PrintChat(owner, newArmor >= armorCap
                ? $"Locust Swarm reached armor cap ({armorCap})."
                : $"Locust Swarm granted +{newArmor - currentArmor} armor!");
        }

        ctx.Services.Effects.PrintCenter(owner, $"Locust Swarm hit {target.PlayerName} for {(int)damage} damage!");
        ctx.Services.Effects.PrintCenter(target, $"You were struck by {owner.PlayerName}'s Locust Swarm!");

        var origin = SkillHelpers.CloneOrigin(owner);
        if (origin != null)
        {
            ctx.Services.Effects.DrawBeamRing(origin, 65f, Color.FromArgb(220, 0, 80, 220), 3f, 1.0f, 16, 20f);
            ctx.Services.Effects.DrawBeamRing(origin, 70f, Color.FromArgb(220, 0, 120, 255), 3f, 1.0f, 16, 40f);
        }
    }
}

public sealed class CheatDeathPrimitive : ISkillPrimitive
{
    public string Type => "cheat_death";

    public void Execute(SkillContext ctx, SkillActionConfig action)
    {
        var owner = ctx.Owner;
        var pawn = SkillHelpers.GetAlivePawn(owner);
        if (pawn == null || ctx.DamageInfo == null || pawn.Health <= 0)
        {
            ctx.Activated = false;
            return;
        }

        float rawIncoming = MathF.Max(ctx.DamageDealt, ctx.DamageInfo.Damage);
        float scaledIncoming = rawIncoming * MathF.Max(0f, ctx.DamageMultiplier);
        if (rawIncoming < pawn.Health && scaledIncoming < pawn.Health)
        {
            ctx.Activated = false;
            return;
        }

        int maxPerRound = action.GetLevelInt("max_per_round", ctx.SkillLevel, 1);
        if (maxPerRound > 0 && ctx.Services.State.GetRoundProcCount(owner.Slot, "cheat_death") >= maxPerRound)
        {
            ctx.Activated = false;
            return;
        }

        if (SkillHelpers.HasKey(action, "chance"))
        {
            float extraChance = Math.Clamp(action.GetLevelFloat("chance", ctx.SkillLevel, 100f), 0f, 100f);
            if (!SkillHelpers.RollChance(ctx.Services.Rng, extraChance))
            {
                ctx.Activated = false;
                return;
            }
        }

        int surviveHp = Math.Clamp(action.GetLevelInt("survive_hp", ctx.SkillLevel, 1), 1, Math.Max(1, pawn.MaxHealth));
        float invulnDuration = MathF.Max(0f, action.GetLevelFloat("invuln_duration", ctx.SkillLevel, 0.35f));

        ctx.CancelDamage = true;
        ctx.Services.State.SetHealth(owner, surviveHp, add: false, updateMaxHealth: false);
        if (invulnDuration > 0f)
        {
            ctx.Services.State.SetGodMode(owner, true, invulnDuration);
        }

        ctx.Services.State.IncrementRoundProcCount(owner.Slot, "cheat_death");

        string msg = action.GetString("message", "Cheat Death saved you with {value} HP!");
        if (!string.IsNullOrEmpty(msg))
        {
            string hpStr = surviveHp.ToString();
            ctx.Services.Effects.PrintCenter(owner, SkillHelpers.FormatMessage(msg, ctx, value: hpStr, amount: hpStr));
        }

        var origin = SkillHelpers.CloneOrigin(owner);
        if (origin != null)
        {
            ctx.Services.Effects.DrawBeamRing(origin, 60f, Color.FromArgb(230, 255, 225, 110), 3.0f, 0.8f, 16, 20f);
        }

        ctx.Activated = true;
    }
}

public sealed class SplashDamagePrimitive : ISkillPrimitive
{
    public string Type => "splash_damage";

    public void Execute(SkillContext ctx, SkillActionConfig action)
    {
        if (ctx.IsSkillDamage || ctx.DamageDealt <= 0f || !SkillHelpers.IsAlive(ctx.Owner) || ctx.Victim == null)
            return;

        var victimOrigin = SkillHelpers.CloneOrigin(ctx.Victim);
        if (victimOrigin == null)
            return;

        float percent = MathF.Max(0f, action.GetLevelFloat("percent", ctx.SkillLevel, 35f));
        if (percent <= 0f)
            return;

        float baseSplash = ctx.DamageDealt * (percent / 100f);
        float minDamage = MathF.Max(0f, action.GetLevelFloat("min_damage", ctx.SkillLevel, 1f));
        float maxDamage = MathF.Max(0f, action.GetLevelFloat("max_damage", ctx.SkillLevel, 0f));
        if (maxDamage > 0f)
            baseSplash = MathF.Min(baseSplash, maxDamage);
        if (baseSplash < minDamage)
            baseSplash = minDamage;

        float radius = MathF.Max(1f, action.GetLevelFloat("radius", ctx.SkillLevel, 200f));
        bool falloff = action.GetBool("falloff", false);
        string weaponHint = action.GetString("weapon_hint", string.IsNullOrEmpty(ctx.WeaponName) ? "hegrenade" : ctx.WeaponName);

        bool ffa = ctx.Services.Config.FfaEnabled || ctx.Services.Sessions.IsFriendlyFireAllowed();
        CsTeam? enemyTeam = ffa ? null : SkillHelpers.GetEnemyTeam(ctx.Owner.Team);
        if (!ffa && enemyTeam == null)
            return;

        var candidates = ctx.Services.Combat.GetPlayersInRadius(victimOrigin, radius, teamFilter: enemyTeam, exclude: ctx.Owner, aliveOnly: true);
        int primaryVictimSlot = ctx.Victim.Slot;
        int hitCount = 0;

        foreach (var enemy in candidates)
        {
            if (!SkillHelpers.IsAlive(enemy) || enemy.Slot == primaryVictimSlot || enemy.Slot == ctx.Owner.Slot)
                continue;

            float dmg = baseSplash;
            if (falloff)
            {
                var enemyPos = SkillHelpers.CloneOrigin(enemy);
                if (enemyPos != null)
                {
                    float dx = enemyPos.X - victimOrigin.X;
                    float dy = enemyPos.Y - victimOrigin.Y;
                    float dz = enemyPos.Z - victimOrigin.Z;
                    float dist = MathF.Sqrt(dx * dx + dy * dy + dz * dz);
                    float factor = Math.Clamp(1f - (dist / radius), 0.1f, 1f);
                    dmg = MathF.Max(minDamage, baseSplash * factor);
                }
            }

            if (dmg <= 0f)
                continue;

            ctx.Services.Combat.DealDamage(enemy, ctx.Owner, dmg, weaponHint, ignoreArmor: false, isSkillDamage: true);
            hitCount++;
        }

        if (hitCount > 0 && (action.Params.ContainsKey("ring_color") || action.Params.ContainsKey("r")))
        {
            Color ringColor = SkillHelpers.ReadColor(action, "ring_color", Color.FromArgb(220, 255, 140, 40));
            ctx.Services.Effects.DrawBeamRing(victimOrigin, MathF.Min(radius, 255f), ringColor, 2.5f, 0.6f, 16, 18f);
        }
    }
}

public sealed class AimExplosionPrimitive : ISkillPrimitive
{
    public const string DefaultImpactParticle1 = "particles/survival_fx/gas_cannister_impact.vpcf";
    public const string DefaultImpactParticle2 = "particles/ui/ui_electric_exp_glow.vpcf";
    public const string DefaultSound = "UIPanorama.equip_musicKit";

    public string Type => "aim_explosion";

    public void Execute(SkillContext ctx, SkillActionConfig action)
    {
        var owner = ctx.Owner;
        var pawn = SkillHelpers.GetAlivePawn(owner);
        if (pawn == null || pawn.AbsOrigin == null)
        {
            ctx.Activated = false;
            return;
        }

        bool ffa = ctx.Services.Config.FfaEnabled || ctx.Services.Sessions.IsFriendlyFireAllowed();
        CsTeam? enemyTeam = ffa ? null : SkillHelpers.GetEnemyTeam(owner.Team);
        if (!ffa && enemyTeam == null)
        {
            ctx.Activated = false;
            return;
        }

        float baseDamage = MathF.Max(0f, action.GetLevelFloat("damage", ctx.SkillLevel, 60f));
        float minDamage = MathF.Max(0f, action.GetLevelFloat("min_damage", ctx.SkillLevel, 10f));
        float radius = MathF.Max(1f, action.GetLevelFloat("radius", ctx.SkillLevel, 220f));
        float maxDistance = MathF.Max(50f, action.GetLevelFloat("max_distance", ctx.SkillLevel, 1500f));
        float aimConeDeg = Math.Clamp(action.GetLevelFloat("aim_cone_degrees", ctx.SkillLevel, 8.0f), 0.5f, 45f);
        bool requireLos = action.GetBool("require_los", true);

        float eyeZ = pawn.ViewOffset.Z > 1f ? pawn.ViewOffset.Z : 64f;
        var eye = new Vector(pawn.AbsOrigin.X, pawn.AbsOrigin.Y, pawn.AbsOrigin.Z + eyeZ);

        var eyeAngles = pawn.EyeAngles;
        float pitchRad = eyeAngles.X * (MathF.PI / 180f);
        float yawRad = eyeAngles.Y * (MathF.PI / 180f);
        float cosPitch = MathF.Cos(pitchRad);
        float fwdX = cosPitch * MathF.Cos(yawRad);
        float fwdY = cosPitch * MathF.Sin(yawRad);
        float fwdZ = -MathF.Sin(pitchRad);

        float minDot = MathF.Cos(aimConeDeg * (MathF.PI / 180f));

        CCSPlayerController? bestTarget = null;
        float bestDot = -2f;
        float bestDist = float.MaxValue;

        foreach (var cand in ctx.Services.Sessions.GetAlivePlayers(enemyTeam))
        {
            if (!SkillHelpers.IsAlive(cand) || cand.Slot == owner.Slot)
                continue;
            if (!ffa && cand.Team == owner.Team)
                continue;

            var candOrigin = SkillHelpers.CloneOrigin(cand);
            if (candOrigin == null)
                continue;

            float dx = candOrigin.X - eye.X;
            float dy = candOrigin.Y - eye.Y;
            float dz = (candOrigin.Z + 36f) - eye.Z;
            float dist = MathF.Sqrt(dx * dx + dy * dy + dz * dz);
            if (dist < 1f || dist > maxDistance)
                continue;

            float dot = (dx * fwdX + dy * fwdY + dz * fwdZ) / dist;
            if (dot < minDot)
                continue;

            if (dot > bestDot + 1e-4f || (MathF.Abs(dot - bestDot) <= 1e-4f && dist < bestDist))
            {
                bestDot = dot;
                bestDist = dist;
                bestTarget = cand;
            }
        }

        if (bestTarget != null && requireLos)
        {
            var hit = ctx.Services.Teleport.GetCrosshairPoint(owner, MathF.Min(maxDistance, bestDist + 64f));
            if (hit != null)
            {
                float hdx = hit.X - eye.X;
                float hdy = hit.Y - eye.Y;
                float hdz = hit.Z - eye.Z;
                float hitDist = MathF.Sqrt(hdx * hdx + hdy * hdy + hdz * hdz);
                if (hitDist + 48f < bestDist)
                {
                    bestTarget = null;
                }
            }
        }

        if (bestTarget == null)
        {
            ctx.Activated = false;
            ctx.Services.Effects.PrintChat(owner, "No enemy in your crosshair!");
            return;
        }

        var targetOrigin = SkillHelpers.CloneOrigin(bestTarget);
        if (targetOrigin == null)
        {
            ctx.Activated = false;
            return;
        }

        var enemies = ctx.Services.Combat.GetPlayersInRadius(targetOrigin, radius, teamFilter: enemyTeam, exclude: owner, aliveOnly: true);
        int hitCount = 0;
        foreach (var enemy in enemies)
        {
            if (!SkillHelpers.IsAlive(enemy) || enemy.Slot == owner.Slot)
                continue;
            if (!ffa && enemy.Team == owner.Team)
                continue;

            var enemyOrigin = SkillHelpers.CloneOrigin(enemy);
            if (enemyOrigin == null)
                continue;

            float dx = enemyOrigin.X - targetOrigin.X;
            float dy = enemyOrigin.Y - targetOrigin.Y;
            float dz = enemyOrigin.Z - targetOrigin.Z;
            float dist = MathF.Sqrt(dx * dx + dy * dy + dz * dz);
            float falloff = MathF.Max(0f, 1f - (dist / radius));
            float dmg = MathF.Max(minDamage, MathF.Floor(baseDamage * falloff));

            ctx.Services.Combat.DealDamage(enemy, owner, dmg, "molotov", ignoreArmor: false, isSkillDamage: true);
            hitCount++;
        }

        string impact1 = action.GetString("impact_particle_1");
        if (string.IsNullOrWhiteSpace(impact1)) impact1 = DefaultImpactParticle1;
        string impact2 = action.GetString("impact_particle_2");
        if (string.IsNullOrWhiteSpace(impact2)) impact2 = DefaultImpactParticle2;

        ctx.Services.Effects.SpawnParticle(new Vector(targetOrigin.X, targetOrigin.Y, targetOrigin.Z + 10f), impact1, 3f);
        ctx.Services.Effects.SpawnParticle(new Vector(targetOrigin.X, targetOrigin.Y, targetOrigin.Z), impact2, 3f);

        string sound = action.GetString("sound", DefaultSound);
        if (!string.IsNullOrWhiteSpace(sound))
        {
            ctx.Services.Effects.EmitSound(owner, sound, 0.5f);
        }

        string message = action.GetString("message");
        if (!string.IsNullOrEmpty(message))
        {
            string dmgStr = ((int)Math.Round(baseDamage)).ToString();
            ctx.Services.Effects.PrintCenter(owner, SkillHelpers.FormatMessage(message, ctx, value: dmgStr, damage: dmgStr, count: hitCount.ToString()));
        }

        ctx.Activated = true;
    }
}
