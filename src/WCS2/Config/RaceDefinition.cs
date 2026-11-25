using System.Text.Json;
using System.Text.Json.Serialization;
using WCS2.Skills;

namespace WCS2.Config;

public sealed class RaceDefinition
{
    [JsonPropertyName("key")]
    public string Key { get; set; } = string.Empty;

    [JsonPropertyName("name")]
    public string Name { get; set; } = string.Empty;

    [JsonPropertyName("author")]
    public string Author { get; set; } = string.Empty;

    [JsonPropertyName("description")]
    public string Description { get; set; } = string.Empty;

    [JsonPropertyName("required_level")]
    public int RequiredLevel { get; set; } = 0;

    [JsonPropertyName("max_level")]
    public int MaxLevel { get; set; } = 0;

    [JsonPropertyName("team_limit")]
    public int TeamLimit { get; set; } = 0;

    [JsonPropertyName("restrict_team")]
    public int RestrictTeam { get; set; } = 0;

    [JsonPropertyName("categories")]
    public List<string> Categories { get; set; } = new() { "All races" };

    /// <summary>
    /// If non-empty, restricts ALL weapons except these (e.g. ["weapon_p250"] or ["weapon_knife"]).
    /// </summary>
    [JsonPropertyName("allowed_weapons")]
    public List<string> AllowedWeapons { get; set; } = new();

    /// <summary>
    /// Specific weapons blocked for this race when AllowedWeapons is empty.
    /// </summary>
    [JsonPropertyName("restricted_weapons")]
    public List<string> RestrictedWeapons { get; set; } = new();

    [JsonPropertyName("spawn_actions")]
    public List<SkillActionConfig> SpawnActions { get; set; } = new();

    [JsonPropertyName("death_actions")]
    public List<SkillActionConfig> DeathActions { get; set; } = new();

    [JsonPropertyName("round_start_actions")]
    public List<SkillActionConfig> RoundStartActions { get; set; } = new();

    [JsonPropertyName("round_end_actions")]
    public List<SkillActionConfig> RoundEndActions { get; set; } = new();

    [JsonPropertyName("custom_handler")]
    public string? CustomHandler { get; set; }

    [JsonPropertyName("skills")]
    public List<SkillDefinition> Skills { get; set; } = new();

    [JsonIgnore]
    public int ComputedMaxLevel => MaxLevel > 0 ? MaxLevel : Skills.Sum(s => s.MaxLevel);
}

public sealed class SkillDefinition
{
    [JsonPropertyName("key")]
    public string Key { get; set; } = string.Empty;

    [JsonPropertyName("name")]
    public string Name { get; set; } = string.Empty;

    [JsonPropertyName("description")]
    public string Description { get; set; } = string.Empty;

    [JsonPropertyName("trigger")]
    [JsonConverter(typeof(JsonStringEnumConverter))]
    public SkillTrigger Trigger { get; set; } = SkillTrigger.PlayerSpawn;

    [JsonPropertyName("max_level")]
    public int MaxLevel { get; set; } = 8;

    [JsonPropertyName("required_level")]
    public int RequiredLevel { get; set; } = 0;

    /// <summary>
    /// Cooldown in seconds indexed by skill level (0-based for level 1..MaxLevel). Empty = no cooldown.
    /// </summary>
    [JsonPropertyName("cooldowns")]
    public List<float> Cooldowns { get; set; } = new();

    /// <summary>
    /// Optional proc chance (0-100) indexed by skill level (0-based for level 1..MaxLevel). Empty = 100%.
    /// </summary>
    [JsonPropertyName("chances")]
    public List<float> Chances { get; set; } = new();

    /// <summary>
    /// Optional weapon filter for attacker/victim/kill triggers (e.g. ["weapon_knife"] or ["weapon_hegrenade"]).
    /// </summary>
    [JsonPropertyName("weapons")]
    public List<string> Weapons { get; set; } = new();

    /// <summary>
    /// Optional bespoke C# ISkillHandler key. If set, executed instead of (or before) Actions.
    /// </summary>
    [JsonPropertyName("custom_handler")]
    public string? CustomHandler { get; set; }

    [JsonPropertyName("actions")]
    public List<SkillActionConfig> Actions { get; set; } = new();

    public float GetCooldown(int skillLevel)
    {
        if (Cooldowns.Count == 0 || skillLevel <= 0) return 0f;
        int idx = Math.Clamp(skillLevel - 1, 0, Cooldowns.Count - 1);
        return Cooldowns[idx];
    }

    public float GetChance(int skillLevel)
    {
        if (Chances.Count == 0 || skillLevel <= 0) return 100f;
        int idx = Math.Clamp(skillLevel - 1, 0, Chances.Count - 1);
        return Chances[idx];
    }
}

public sealed class SkillActionConfig
{
    /// <summary>
    /// Primitive type key (e.g. "health", "speed", "gravity", "invisibility", "bonus_damage", "vampiric",
    /// "freeze", "teleport", "beam_ring", "sound", "message", etc.).
    /// </summary>
    [JsonPropertyName("type")]
    public string Type { get; set; } = string.Empty;

    /// <summary>
    /// Target selector: "self", "victim", "attacker", "nearby_enemies", "nearby_allies".
    /// </summary>
    [JsonPropertyName("target")]
    public string Target { get; set; } = "self";

    /// <summary>
    /// Optional delay in seconds before executing this action.
    /// </summary>
    [JsonPropertyName("delay")]
    public float Delay { get; set; } = 0f;

    /// <summary>
    /// Per-skill-level numeric values (index 0 = level 1). Example: { "amount": [10, 20, 30, 40] }.
    /// </summary>
    [JsonPropertyName("level_values")]
    public Dictionary<string, List<double>> LevelValues { get; set; } = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Constant / typed parameters (strings, bools, numbers, color arrays, weapon lists).
    /// </summary>
    [JsonPropertyName("params")]
    public Dictionary<string, JsonElement> Params { get; set; } = new(StringComparer.OrdinalIgnoreCase);

    public double GetLevelDouble(string key, int skillLevel, double defaultValue = 0.0)
    {
        if (LevelValues.TryGetValue(key, out var list) && list.Count > 0)
        {
            int idx = Math.Clamp(Math.Max(1, skillLevel) - 1, 0, list.Count - 1);
            return list[idx];
        }
        if (Params.TryGetValue(key, out var el) && el.ValueKind == JsonValueKind.Number)
        {
            return el.GetDouble();
        }
        return defaultValue;
    }

    public float GetLevelFloat(string key, int skillLevel, float defaultValue = 0f)
        => (float)GetLevelDouble(key, skillLevel, defaultValue);

    public int GetLevelInt(string key, int skillLevel, int defaultValue = 0)
        => (int)Math.Round(GetLevelDouble(key, skillLevel, defaultValue));

    public string GetString(string key, string defaultValue = "")
    {
        if (Params.TryGetValue(key, out var el) && el.ValueKind == JsonValueKind.String)
            return el.GetString() ?? defaultValue;
        return defaultValue;
    }

    public bool GetBool(string key, bool defaultValue = false)
    {
        if (Params.TryGetValue(key, out var el))
        {
            if (el.ValueKind == JsonValueKind.True) return true;
            if (el.ValueKind == JsonValueKind.False) return false;
            if (el.ValueKind == JsonValueKind.Number) return el.GetInt32() != 0;
        }
        return defaultValue;
    }

    public List<string> GetStringList(string key)
    {
        var result = new List<string>();
        if (Params.TryGetValue(key, out var el) && el.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in el.EnumerateArray())
            {
                if (item.ValueKind == JsonValueKind.String && item.GetString() is { Length: > 0 } s)
                    result.Add(s);
            }
        }
        return result;
    }
}
