using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Logging;

namespace WCS2.Config;

public sealed class GamedataConfig
{
    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        PropertyNameCaseInsensitive = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true
    };

    /// <summary>
    /// Resolution mode:
    /// - "auto": try dynamic ELF64 resolution first, fall back per-symbol to linux_signatures.
    /// - "dynamic": dynamic ELF64 resolution only.
    /// - "gamedata": linux_signatures from gamedata.json only (admin override).
    /// </summary>
    [JsonPropertyName("resolution_mode")]
    public string ResolutionMode { get; set; } = "auto";

    [JsonPropertyName("dynamic")]
    public DynamicGamedataConfig Dynamic { get; set; } = new();

    [JsonPropertyName("linux_signatures")]
    public LinuxSignaturesConfig LinuxSignatures { get; set; } = new();

    public static GamedataConfig Load(string configDir, ILogger logger)
    {
        string path = Path.Combine(configDir, "gamedata.json");
        if (!File.Exists(path))
        {
            logger.LogWarning("[WCS2] gamedata.json not found at {Path}; using built-in defaults (resolution_mode=auto).", path);
            return new GamedataConfig();
        }

        try
        {
            string json = File.ReadAllText(path);
            var cfg = JsonSerializer.Deserialize<GamedataConfig>(json, JsonOpts) ?? new GamedataConfig();
            cfg.Dynamic ??= new DynamicGamedataConfig();
            cfg.LinuxSignatures ??= new LinuxSignaturesConfig();
            logger.LogInformation("[WCS2] Loaded gamedata.json (resolution_mode={Mode}).", cfg.ResolutionMode);
            return cfg;
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "[WCS2] Failed to parse {Path}; falling back to built-in defaults.", path);
            return new GamedataConfig();
        }
    }
}

public sealed class DynamicGamedataConfig
{
    [JsonPropertyName("trace_shape_vprof_string")]
    public string TraceShapeVprofString { get; set; } = "Physics/TraceShape (Server)";

    [JsonPropertyName("trace_filter_rtti_name")]
    public string TraceFilterRttiName { get; set; } = "12CTraceFilter";
}

public sealed class LinuxSignaturesConfig
{
    [JsonPropertyName("trace_shape")]
    public SignatureEntry TraceShape { get; set; } = new()
    {
        Pattern = "55 48 8D 05 ? ? ? ? 48 89 E5 41 57 49 89 F7 41 56 48 8D 35 ? ? ? ? 49 89 CE"
    };

    [JsonPropertyName("game_trace_manager")]
    public SignatureEntry GameTraceManager { get; set; } = new()
    {
        Pattern = "48 8D 05 ? ? ? ? 4C 89 EA 4C 8B",
        RipOffset = 3,
        InstrSize = 7
    };

    [JsonPropertyName("trace_filter_vtable")]
    public SignatureEntry TraceFilterVtable { get; set; } = new()
    {
        Pattern = "48 8D 05 ? ? ? ? 48 89 85 D0 FE FF FF 48 B8 41 00 18 00",
        RipOffset = 3,
        InstrSize = 7
    };
}

public sealed class SignatureEntry
{
    [JsonPropertyName("pattern")]
    public string Pattern { get; set; } = string.Empty;

    [JsonPropertyName("rip_offset")]
    public int RipOffset { get; set; } = 0;

    [JsonPropertyName("instr_size")]
    public int InstrSize { get; set; } = 0;
}
