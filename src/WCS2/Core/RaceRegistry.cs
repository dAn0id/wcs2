using System.Text.Json;
using Microsoft.Extensions.Logging;
using WCS2.Config;

namespace WCS2.Core;

public sealed class RaceRegistry
{
    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        PropertyNameCaseInsensitive = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true
    };

    private readonly ILogger _logger;
    private readonly Dictionary<string, RaceDefinition> _races = new(StringComparer.OrdinalIgnoreCase);
    private readonly List<RaceDefinition> _orderedRaces = new();
    private readonly Dictionary<string, List<RaceDefinition>> _categories = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, ItemDefinition> _items = new(StringComparer.OrdinalIgnoreCase);
    private readonly List<ItemDefinition> _orderedItems = new();

    public RaceRegistry(ILogger logger)
    {
        _logger = logger;
    }

    public IReadOnlyList<RaceDefinition> Races => _orderedRaces;
    public IReadOnlyList<ItemDefinition> Items => _orderedItems;
    public IReadOnlyDictionary<string, List<RaceDefinition>> Categories => _categories;

    /// <summary>
    /// Copies missing default config files from defaults/configs/ into configs/ without
    /// ever overwriting existing admin-customized .json/config files, while automatically
    /// keeping bundled .html/.py tooling (e.g. wcs2-race-builder.html, wcs2-race-builder.py) up to date.
    /// </summary>
    public void EnsureConfigsSeeded(string moduleDirectory)
    {
        string defaultsDir = Path.Combine(moduleDirectory, "defaults", "configs");
        if (!Directory.Exists(defaultsDir))
            return;

        string targetDir = Path.Combine(moduleDirectory, "configs");
        int seeded = 0;
        int updatedTools = 0;

        try
        {
            foreach (string srcPath in Directory.EnumerateFiles(defaultsDir, "*", SearchOption.AllDirectories))
            {
                string relPath = Path.GetRelativePath(defaultsDir, srcPath);
                string destPath = Path.Combine(targetDir, relPath);
                bool isPythonScript = destPath.EndsWith(".py", StringComparison.OrdinalIgnoreCase);
                bool isHtmlTool = destPath.EndsWith(".html", StringComparison.OrdinalIgnoreCase);
                bool isBundledTool = isPythonScript || isHtmlTool;
                bool exists = File.Exists(destPath);

                if (exists)
                {
                    if (!isBundledTool)
                        continue;

                    if (File.ReadAllBytes(srcPath).AsSpan().SequenceEqual(File.ReadAllBytes(destPath)))
                        continue;
                }

                string? destParent = Path.GetDirectoryName(destPath);
                if (!string.IsNullOrEmpty(destParent))
                    Directory.CreateDirectory(destParent);

                File.Copy(srcPath, destPath, overwrite: exists);
                if (OperatingSystem.IsLinux())
                {
                    if (isPythonScript)
                    {
                        File.SetUnixFileMode(
                            destPath,
                            UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute |
                            UnixFileMode.GroupRead | UnixFileMode.GroupExecute |
                            UnixFileMode.OtherRead | UnixFileMode.OtherExecute);
                    }
                    else if (isHtmlTool)
                    {
                        File.SetUnixFileMode(
                            destPath,
                            UnixFileMode.UserRead | UnixFileMode.UserWrite |
                            UnixFileMode.GroupRead |
                            UnixFileMode.OtherRead);
                    }
                }

                if (exists)
                    updatedTools++;
                else
                    seeded++;
            }

            if (seeded > 0)
            {
                _logger.LogInformation("[WCS2] Seeded {Count} missing default config file(s) into configs/.", seeded);
            }

            if (updatedTools > 0)
            {
                _logger.LogInformation("[WCS2] Updated {Count} bundled tool file(s) in configs/.", updatedTools);
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "[WCS2] Error seeding default config files from {DefaultsDir}.", defaultsDir);
        }
    }

    public WcsConfig LoadConfig(string configDir)
    {
        var path = Path.Combine(configDir, "wcs.json");
        if (!File.Exists(path))
        {
            _logger.LogWarning("[WCS2] wcs.json not found at {Path}, using defaults.", path);
            return new WcsConfig();
        }

        try
        {
            var json = File.ReadAllText(path);
            var cfg = JsonSerializer.Deserialize<WcsConfig>(json, JsonOpts) ?? new WcsConfig();
            _logger.LogInformation("[WCS2] Loaded wcs.json (default_race={Race}, xp_interval={Interval}).",
                cfg.DefaultRace, cfg.XpInterval);
            return cfg;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "[WCS2] Failed to parse {Path}; falling back to defaults.", path);
            return new WcsConfig();
        }
    }

    public void LoadAll(string configDir)
    {
        _races.Clear();
        _orderedRaces.Clear();
        _categories.Clear();
        _items.Clear();
        _orderedItems.Clear();

        LoadRaces(Path.Combine(configDir, "races"));
        LoadItems(Path.Combine(configDir, "items.json"));
    }

    private void LoadRaces(string racesDir)
    {
        if (!Directory.Exists(racesDir))
        {
            _logger.LogWarning("[WCS2] Races directory not found: {Dir}", racesDir);
            return;
        }

        foreach (var file in Directory.GetFiles(racesDir, "*.json").OrderBy(f => f, StringComparer.OrdinalIgnoreCase))
        {
            try
            {
                var json = File.ReadAllText(file);
                var race = JsonSerializer.Deserialize<RaceDefinition>(json, JsonOpts);
                if (race == null || string.IsNullOrWhiteSpace(race.Key))
                {
                    _logger.LogWarning("[WCS2] Skipping invalid race JSON: {File}", file);
                    continue;
                }

                race.Key = race.Key.Trim().ToLowerInvariant();
                if (string.IsNullOrWhiteSpace(race.Name))
                    race.Name = race.Key;

                foreach (var skill in race.Skills)
                {
                    if (string.IsNullOrWhiteSpace(skill.Key))
                        skill.Key = skill.Name.Trim().ToLowerInvariant().Replace(' ', '_');
                }

                _races[race.Key] = race;
                _orderedRaces.Add(race);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "[WCS2] Error loading race file {File}", file);
            }
        }

        _orderedRaces.Sort((a, b) =>
        {
            int cmp = a.RequiredLevel.CompareTo(b.RequiredLevel);
            return cmp != 0 ? cmp : string.Compare(a.Name, b.Name, StringComparison.OrdinalIgnoreCase);
        });

        foreach (var race in _orderedRaces)
        {
            var cats = race.Categories.Count > 0 ? race.Categories : new List<string> { "All races" };
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var rawCat in cats)
            {
                var cat = rawCat.Trim().Trim('[', ']');
                if (string.IsNullOrEmpty(cat) || !seen.Add(cat)) continue;
                if (!_categories.TryGetValue(cat, out var list))
                {
                    list = new List<RaceDefinition>();
                    _categories[cat] = list;
                }
                list.Add(race);
            }
            if (seen.Add("All races"))
            {
                if (!_categories.TryGetValue("All races", out var allList))
                {
                    allList = new List<RaceDefinition>();
                    _categories["All races"] = allList;
                }
                allList.Add(race);
            }
        }

        _logger.LogInformation("[WCS2] Loaded {Count} races across {Cats} categories.",
            _orderedRaces.Count, _categories.Count);
    }

    private void LoadItems(string itemsPath)
    {
        if (!File.Exists(itemsPath))
        {
            _logger.LogWarning("[WCS2] items.json not found at {Path}", itemsPath);
            return;
        }

        try
        {
            var json = File.ReadAllText(itemsPath);
            var list = JsonSerializer.Deserialize<List<ItemDefinition>>(json, JsonOpts) ?? new List<ItemDefinition>();
            foreach (var item in list)
            {
                if (string.IsNullOrWhiteSpace(item.Key)) continue;
                item.Key = item.Key.Trim().ToLowerInvariant();
                _items[item.Key] = item;
                _orderedItems.Add(item);
            }
            _logger.LogInformation("[WCS2] Loaded {Count} shop items.", _orderedItems.Count);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "[WCS2] Error loading {Path}", itemsPath);
        }
    }

    public RaceDefinition? GetRace(string? key)
    {
        if (string.IsNullOrWhiteSpace(key)) return null;
        return _races.TryGetValue(key.Trim(), out var r) ? r : null;
    }

    public RaceDefinition? GetDefaultRace(string defaultKey)
    {
        return GetRace(defaultKey) ?? _orderedRaces.FirstOrDefault();
    }

    public ItemDefinition? GetItem(string? key)
    {
        if (string.IsNullOrWhiteSpace(key)) return null;
        return _items.TryGetValue(key.Trim(), out var i) ? i : null;
    }
}
