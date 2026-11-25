using CounterStrikeSharp.API;
using CounterStrikeSharp.API.Core;
using CounterStrikeSharp.API.Modules.Utils;
using WcsCs2.Config;
using WcsCs2.Skills;

namespace WcsCs2.Core;

public sealed class ShopService
{
    private readonly RaceRegistry _registry;
    private readonly ProgressionService _progression;
    private readonly IEffectsService _effects;
    private ISkillExecutionEngine? _engine;

    public ShopService(RaceRegistry registry, ProgressionService progression, IEffectsService effects)
    {
        _registry = registry;
        _progression = progression;
        _effects = effects;
    }

    public void BindEngine(ISkillExecutionEngine engine) => _engine = engine;

    public bool TryBuyItem(CCSPlayerController player, string itemKey, out string feedback)
    {
        if (!player.IsValid)
        {
            feedback = "Invalid player.";
            return false;
        }

        var session = _progression.GetTypedSession(player);
        var item = _registry.GetItem(itemKey);
        if (session == null || item == null)
        {
            feedback = "Item not found.";
            return false;
        }

        if (session.TotalLevel < item.RequiredLevel)
        {
            feedback = $"{item.Name} requires Total Level {item.RequiredLevel}.";
            return false;
        }

        bool isAlive = player.PawnIsAlive;
        if (item.BuyMode == ItemBuyMode.AliveOnly && !isAlive)
        {
            feedback = $"You must be alive to buy {item.Name}.";
            return false;
        }
        if (item.BuyMode == ItemBuyMode.DeadOnly && isAlive)
        {
            feedback = $"You must be dead to buy {item.Name}.";
            return false;
        }

        int owned = session.GetItemCount(item.Key);
        if (item.MaxCount > 0 && owned >= item.MaxCount)
        {
            feedback = $"You already own the maximum ({item.MaxCount}) of {item.Name}.";
            return false;
        }

        var moneyServices = player.InGameMoneyServices;
        if (moneyServices == null || moneyServices.Account < item.Cost)
        {
            int have = moneyServices?.Account ?? 0;
            feedback = $"Not enough money for {item.Name} (need ${item.Cost}, have ${have}).";
            return false;
        }

        moneyServices.Account -= item.Cost;
        Utilities.SetStateChanged(player, "CCSPlayerController", "m_pInGameMoneyServices");

        if (item.Duration != ItemDuration.Instant)
        {
            session.AddItemCount(item.Key, 1);
        }

        // Apply item actions immediately (for PlayerBuy instant items AND alive PlayerSpawn items)
        _engine?.ExecuteItemActions(player, session, item);

        feedback = $"Purchased {ChatColors.Green}{item.Name}{ChatColors.Default} for ${item.Cost}!";
        return true;
    }

    public void ReapplySpawnItems(CCSPlayerController player, PlayerSession session)
    {
        if (_engine == null || session.ActiveItems.Count == 0) return;

        foreach (var (itemKey, count) in session.ActiveItems)
        {
            if (count <= 0) continue;
            var item = _registry.GetItem(itemKey);
            if (item == null || item.Trigger != SkillTrigger.PlayerSpawn) continue;

            for (int i = 0; i < count; i++)
            {
                _engine.ExecuteItemActions(player, session, item);
            }
        }
    }

    public void OnPlayerDeath(PlayerSession session)
    {
        session.ClearItems(key =>
        {
            var item = _registry.GetItem(key);
            return item == null || item.Duration == ItemDuration.UntilDeath;
        });
    }

    public void OnRoundEnd(IEnumerable<PlayerSession> sessions)
    {
        foreach (var s in sessions)
        {
            s.ClearItems(key =>
            {
                var item = _registry.GetItem(key);
                return item == null || item.Duration == ItemDuration.Round;
            });
        }
    }
}
