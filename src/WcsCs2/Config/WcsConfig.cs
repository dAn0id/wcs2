using System.Text.Json.Serialization;

namespace WcsCs2.Config;

public sealed class WcsConfig
{
    [JsonPropertyName("default_race")]
    public string DefaultRace { get; set; } = "undead_scourge";

    [JsonPropertyName("database_file")]
    public string DatabaseFile { get; set; } = "wcs_cs2.sqlite";

    [JsonPropertyName("chat_prefix")]
    public string ChatPrefix { get; set; } = "[WCS]";

    // XP & Leveling (matches legacy cfg/source-python/wcs/config.cfg)
    [JsonPropertyName("xp_interval")]
    public int XpInterval { get; set; } = 80;

    [JsonPropertyName("kill_xp")]
    public int KillXp { get; set; } = 20;

    [JsonPropertyName("kill_bot_xp")]
    public int KillBotXp { get; set; } = 20;

    [JsonPropertyName("knife_xp")]
    public int KnifeXp { get; set; } = 40;

    [JsonPropertyName("headshot_xp")]
    public int HeadshotXp { get; set; } = 15;

    [JsonPropertyName("assist_xp")]
    public int AssistXp { get; set; } = 15;

    [JsonPropertyName("bonus_xp_per_level_diff")]
    public int BonusXpPerLevelDiff { get; set; } = 4;

    [JsonPropertyName("bonus_xp_level_cap")]
    public int BonusXpLevelCap { get; set; } = 40;

    [JsonPropertyName("round_survival_xp")]
    public int RoundSurvivalXp { get; set; } = 10;

    [JsonPropertyName("round_win_xp")]
    public int RoundWinXp { get; set; } = 20;

    [JsonPropertyName("bomb_plant_xp")]
    public int BombPlantXp { get; set; } = 10;

    [JsonPropertyName("bomb_explode_xp")]
    public int BombExplodeXp { get; set; } = 10;

    [JsonPropertyName("bomb_defuse_xp")]
    public int BombDefuseXp { get; set; } = 20;

    [JsonPropertyName("hostage_rescue_xp")]
    public int HostageRescueXp { get; set; } = 10;

    // Rested XP & Level Bank
    [JsonPropertyName("rested_xp_enabled")]
    public bool RestedXpEnabled { get; set; } = true;

    [JsonPropertyName("rested_xp_gained_base")]
    public int RestedXpGainedBase { get; set; } = 10;

    [JsonPropertyName("rested_xp_gained_percent")]
    public float RestedXpGainedPercent { get; set; } = 0.01f;

    [JsonPropertyName("rested_xp_online_interval_seconds")]
    public int RestedXpOnlineIntervalSeconds { get; set; } = 60;

    [JsonPropertyName("rested_xp_offline_interval_seconds")]
    public int RestedXpOfflineIntervalSeconds { get; set; } = 120;

    [JsonPropertyName("rested_xp_offline_max_seconds")]
    public int RestedXpOfflineMaxSeconds { get; set; } = 604800;

    [JsonPropertyName("new_player_bank_bonus")]
    public int NewPlayerBankBonus { get; set; } = 15;

    // Gameplay & UX
    [JsonPropertyName("ffa_enabled")]
    public bool FfaEnabled { get; set; } = false;

    [JsonPropertyName("changerace_next_round")]
    public bool ChangeraceNextRound { get; set; } = false;

    [JsonPropertyName("resetskills_next_round")]
    public bool ResetskillsNextRound { get; set; } = false;

    [JsonPropertyName("race_clan_tag")]
    public bool RaceClanTag { get; set; } = true;

    [JsonPropertyName("spawn_text")]
    public bool SpawnText { get; set; } = true;

    [JsonPropertyName("spawn_text_disable_level")]
    public int SpawnTextDisableLevel { get; set; } = 20;

    [JsonPropertyName("level_up_effect")]
    public bool LevelUpEffect { get; set; } = true;

    [JsonPropertyName("top_announcement_enable")]
    public bool TopAnnouncementEnable { get; set; } = true;

    [JsonPropertyName("top_announcement_limit")]
    public int TopAnnouncementLimit { get; set; } = 10;

    [JsonPropertyName("unlock_race_notification")]
    public bool UnlockRaceNotification { get; set; } = true;

    [JsonPropertyName("default_menu_type")]
    public string DefaultMenuType { get; set; } = "wasd";

    [JsonPropertyName("allow_inspect_ultimate_bind")]
    public bool AllowInspectUltimateBind { get; set; } = true;

    // Bots
    [JsonPropertyName("bot_random_race")]
    public bool BotRandomRace { get; set; } = true;

    [JsonPropertyName("bot_ability_chance")]
    public float BotAbilityChance { get; set; } = 0.15f;

    [JsonPropertyName("bot_ignore_level_requirement")]
    public bool BotIgnoreLevelRequirement { get; set; } = false;

    // Admin SteamID64 list — 17-digit SteamID64 integers (e.g. 76561197960265728UL).
    // Also checks CounterStrikeSharp @css/root and @wcs/admin flags from admins.json.
    [JsonPropertyName("admin_steam_ids")]
    public List<ulong> AdminSteamIds { get; set; } = new();

    // Stock CS2 model paths used by prop disguises / mole / spawned props
    [JsonPropertyName("default_barrel_model")]
    public string DefaultBarrelModel { get; set; } = "models/props/containers/barrels/steel_drum_barrel_01a.vmdl";

    [JsonPropertyName("default_t_agent_model")]
    public string DefaultTAgentModel { get; set; } = "characters/models/tm_phoenix/tm_phoenix.vmdl";

    [JsonPropertyName("default_ct_agent_model")]
    public string DefaultCtAgentModel { get; set; } = "characters/models/ctm_sas/ctm_sas.vmdl";
}
