using System.Text.Json.Serialization;
using WCS2.Skills;

namespace WCS2.Config;

public enum ItemBuyMode
{
    DeadOnly = 0,
    AliveOnly = 1,
    Both = 2
}

public enum ItemDuration
{
    Instant = 0,
    UntilDeath = 1,
    Round = 2
}

public sealed class ItemDefinition
{
    [JsonPropertyName("key")]
    public string Key { get; set; } = string.Empty;

    [JsonPropertyName("name")]
    public string Name { get; set; } = string.Empty;

    [JsonPropertyName("description")]
    public string Description { get; set; } = string.Empty;

    [JsonPropertyName("category")]
    public string Category { get; set; } = "Shop";

    [JsonPropertyName("cost")]
    public int Cost { get; set; } = 0;

    [JsonPropertyName("required_level")]
    public int RequiredLevel { get; set; } = 0;

    [JsonPropertyName("buy_mode")]
    public ItemBuyMode BuyMode { get; set; } = ItemBuyMode.AliveOnly;

    [JsonPropertyName("duration")]
    public ItemDuration Duration { get; set; } = ItemDuration.UntilDeath;

    [JsonPropertyName("max_count")]
    public int MaxCount { get; set; } = 1;

    [JsonPropertyName("trigger")]
    [JsonConverter(typeof(JsonStringEnumConverter))]
    public SkillTrigger Trigger { get; set; } = SkillTrigger.PlayerSpawn;

    [JsonPropertyName("actions")]
    public List<SkillActionConfig> Actions { get; set; } = new();
}
