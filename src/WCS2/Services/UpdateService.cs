using System.IO.Compression;
using System.Net;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.RegularExpressions;
using CounterStrikeSharp.API;
using CounterStrikeSharp.API.Core;
using CounterStrikeSharp.API.Modules.Admin;
using CounterStrikeSharp.API.Modules.Utils;
using Microsoft.Extensions.Logging;
using WCS2.Config;

namespace WCS2.Services;

public sealed record UpdateCheckResult(
    bool Success,
    bool UpdateAvailable,
    string CurrentVersion,
    string? LatestVersion,
    string? TagName,
    string? AssetName,
    string? BrowserDownloadUrl,
    string? ApiAssetUrl,
    string? Sha256AssetBrowserUrl,
    string? Sha256AssetApiUrl,
    string StatusMessage,
    DateTimeOffset CheckedAtUtc);

public sealed class UpdateService : IDisposable
{
    private static readonly TimeSpan CacheDuration = TimeSpan.FromMinutes(15);
    private static readonly TimeSpan CheckTimeout = TimeSpan.FromSeconds(10);
    private static readonly TimeSpan DownloadTimeout = TimeSpan.FromSeconds(60);
    private static readonly TimeSpan ChecksumTimeout = TimeSpan.FromSeconds(15);
    private const long MaxDownloadBytes = 100L * 1024 * 1024; // 100 MiB safety cap
    private static readonly Regex RepoFormatRegex = new(@"^[A-Za-z0-9_.-]+/[A-Za-z0-9_.-]+$", RegexOptions.Compiled);

    private readonly BasePlugin _plugin;
    private readonly ILogger _logger;
    private readonly HttpClient _http;
    private readonly CancellationTokenSource _disposeCts = new();
    private readonly object _cacheLock = new();
    private readonly object _notifyLock = new();
    private readonly HashSet<ulong> _notifiedAdminsThisMap = new();

    private volatile WcsConfig _config;
    private UpdateCheckResult? _cachedResult;
    private DateTimeOffset _lastCheckUtc = DateTimeOffset.MinValue;
    private int _isUpdating;
    private int _disposed;

    public UpdateService(BasePlugin plugin, WcsConfig config, ILogger logger)
    {
        _plugin = plugin;
        _config = config;
        _logger = logger;

        var handler = new HttpClientHandler
        {
            AutomaticDecompression = DecompressionMethods.GZip | DecompressionMethods.Deflate,
            AllowAutoRedirect = true
        };
        _http = new HttpClient(handler, disposeHandler: true)
        {
            Timeout = Timeout.InfiniteTimeSpan
        };
    }

    public string CurrentVersion => _plugin.ModuleVersion;
    public bool IsUpdating => Volatile.Read(ref _isUpdating) != 0;

    public UpdateCheckResult? CachedResult
    {
        get
        {
            lock (_cacheLock)
                return _cachedResult;
        }
    }

    public void UpdateConfig(WcsConfig config)
    {
        var previous = _config;
        _config = config;

        bool repoChanged = !string.Equals(previous.GitHubRepo, config.GitHubRepo, StringComparison.OrdinalIgnoreCase)
                           || !string.Equals(previous.GitHubToken, config.GitHubToken, StringComparison.Ordinal);
        if (repoChanged)
        {
            lock (_cacheLock)
            {
                _cachedResult = null;
                _lastCheckUtc = DateTimeOffset.MinValue;
            }
        }
    }

    public void OnMapStart()
    {
        lock (_notifyLock)
        {
            _notifiedAdminsThisMap.Clear();
        }
    }

    public bool IsRootAdmin(CCSPlayerController? player)
    {
        if (player == null || !player.IsValid || player.IsBot) return false;
        var cfg = _config;
        if (cfg.AdminSteamIds.Contains(player.SteamID)) return true;
        return AdminManager.PlayerHasPermissions(player, "@css/root");
    }

    public void NotifyRootAdminOnTeamJoin(CCSPlayerController player)
    {
        if (Volatile.Read(ref _disposed) != 0) return;
        var cfg = _config;
        if (!cfg.UpdateCheckEnabled) return;
        if (!IsRootAdmin(player)) return;

        ulong steamId = player.SteamID;
        int slot = player.Slot;
        if (steamId == 0) return;

        lock (_notifyLock)
        {
            if (_notifiedAdminsThisMap.Contains(steamId))
                return;
        }

        _ = Task.Run(async () =>
        {
            try
            {
                var result = await CheckLatestAsync(forceRefresh: false);
                if (!result.Success || !result.UpdateAvailable || string.IsNullOrWhiteSpace(result.LatestVersion))
                    return;

                bool shouldNotify;
                lock (_notifyLock)
                {
                    shouldNotify = _notifiedAdminsThisMap.Add(steamId);
                }
                if (!shouldNotify) return;

                string current = result.CurrentVersion;
                string latest = result.LatestVersion;

                MarshalToMainThread(() =>
                {
                    var p = Utilities.GetPlayerFromSlot(slot);
                    if (p == null || !p.IsValid || p.IsBot || p.SteamID != steamId) return;
                    p.PrintToChat($" {ChatColors.Red}[WCS2] Update available! Current WCS2 Version: {current}, New Version: {latest} (use !wcsadmin to update)");
                });
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "[WCS2] Background update check on Root-Admin join failed.");
            }
        });
    }

    public void TriggerManualCheck(CCSPlayerController? requester)
    {
        if (Volatile.Read(ref _disposed) != 0) return;
        if (requester != null && !IsRootAdmin(requester)) return;

        int? slot = requester != null && requester.IsValid ? requester.Slot : null;
        ulong steamId = requester != null && requester.IsValid ? requester.SteamID : 0UL;

        _ = Task.Run(async () =>
        {
            try
            {
                var result = await CheckLatestAsync(forceRefresh: true);
                _logger.LogInformation("[WCS2] Update check: {Status}", result.StatusMessage);

                MarshalToMainThread(() =>
                {
                    if (slot.HasValue)
                    {
                        var p = Utilities.GetPlayerFromSlot(slot.Value);
                        if (p == null || !p.IsValid || p.IsBot || p.SteamID != steamId) return;

                        if (result.Success && result.UpdateAvailable && !string.IsNullOrWhiteSpace(result.LatestVersion))
                        {
                            p.PrintToChat($" {ChatColors.Red}[WCS2] Update available! Current WCS2 Version: {result.CurrentVersion}, New Version: {result.LatestVersion} (use !wcsadmin to update)");
                        }
                        else if (result.Success)
                        {
                            p.PrintToChat($" {ChatColors.Green}[WCS2]{ChatColors.Default} {result.StatusMessage}");
                        }
                        else
                        {
                            p.PrintToChat($" {ChatColors.LightRed}[WCS2]{ChatColors.Default} {result.StatusMessage}");
                        }
                    }
                    else
                    {
                        Server.PrintToConsole($"[WCS2] {result.StatusMessage}");
                    }
                });
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "[WCS2] Manual update check encountered an unexpected error.");
            }
        });
    }

    public void TriggerInstallUpdate(CCSPlayerController? requester)
    {
        if (Volatile.Read(ref _disposed) != 0) return;
        if (requester != null && !IsRootAdmin(requester))
        {
            requester.PrintToChat($" {ChatColors.LightRed}[WCS2]{ChatColors.Default} Only Root Admins can install updates.");
            return;
        }

        int? slot = requester != null && requester.IsValid ? requester.Slot : null;
        ulong steamId = requester != null && requester.IsValid ? requester.SteamID : 0UL;

        _ = Task.Run(() => InstallLatestUpdateCoreAsync(slot, steamId));
    }

    public Task InstallLatestUpdateAsync(CCSPlayerController? requester)
    {
        if (Volatile.Read(ref _disposed) != 0) return Task.CompletedTask;
        if (requester != null && !IsRootAdmin(requester))
        {
            requester.PrintToChat($" {ChatColors.LightRed}[WCS2]{ChatColors.Default} Only Root Admins can install updates.");
            return Task.CompletedTask;
        }

        int? slot = requester != null && requester.IsValid ? requester.Slot : null;
        ulong steamId = requester != null && requester.IsValid ? requester.SteamID : 0UL;

        return Task.Run(() => InstallLatestUpdateCoreAsync(slot, steamId));
    }

    public async Task<UpdateCheckResult> CheckLatestAsync(bool forceRefresh = false)
    {
        var now = DateTimeOffset.UtcNow;
        var cfg = _config;

        if (!forceRefresh)
        {
            lock (_cacheLock)
            {
                if (_cachedResult != null && (now - _lastCheckUtc) < CacheDuration)
                    return _cachedResult;
            }
        }

        if (!cfg.UpdateCheckEnabled && !forceRefresh)
        {
            return CacheResult(new UpdateCheckResult(
                Success: false,
                UpdateAvailable: false,
                CurrentVersion: CurrentVersion,
                LatestVersion: null,
                TagName: null,
                AssetName: null,
                BrowserDownloadUrl: null,
                ApiAssetUrl: null,
                Sha256AssetBrowserUrl: null,
                Sha256AssetApiUrl: null,
                StatusMessage: "Update checks are disabled in configs/wcs.json (update_check_enabled=false).",
                CheckedAtUtc: now));
        }

        string repo = (cfg.GitHubRepo ?? string.Empty).Trim();
        if (!RepoFormatRegex.IsMatch(repo))
        {
            return CacheResult(MakeError($"Invalid github_repo '{repo}' in configs/wcs.json (expected 'owner/repo').", now));
        }

        string url = $"https://api.github.com/repos/{repo}/releases/latest";
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(_disposeCts.Token);
        cts.CancelAfter(CheckTimeout);

        try
        {
            using var req = new HttpRequestMessage(HttpMethod.Get, url);
            req.Headers.UserAgent.ParseAdd($"WCS2-Updater/{CurrentVersion}");
            req.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/vnd.github+json"));
            req.Headers.TryAddWithoutValidation("X-GitHub-Api-Version", "2022-11-28");

            string token = (cfg.GitHubToken ?? string.Empty).Trim();
            if (!string.IsNullOrEmpty(token))
            {
                req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
            }

            using var resp = await _http.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, cts.Token);

            if (resp.StatusCode == HttpStatusCode.NotFound)
            {
                string hint = string.IsNullOrEmpty(token)
                    ? $"GitHub returned 404 for '{repo}/releases/latest' (no published release, or repo is private — set github_token in configs/wcs.json)."
                    : $"GitHub returned 404 for '{repo}/releases/latest' (no published release or github_token lacks repository read access).";
                _logger.LogInformation("[WCS2] {Msg}", hint);
                return CacheResult(MakeError(hint, now));
            }

            if (resp.StatusCode == HttpStatusCode.Unauthorized)
            {
                string msg = $"GitHub returned 401 Unauthorized for '{repo}' (check github_token in configs/wcs.json).";
                _logger.LogWarning("[WCS2] {Msg}", msg);
                return CacheResult(MakeError(msg, now));
            }

            if (resp.StatusCode == HttpStatusCode.Forbidden || resp.StatusCode == (HttpStatusCode)429)
            {
                bool rateLimited = resp.Headers.TryGetValues("X-RateLimit-Remaining", out var rem)
                                   && rem.Any(v => v == "0");
                string msg = rateLimited
                    ? "GitHub API rate limit exceeded (60 req/hr unauthenticated). Retry later or configure github_token."
                    : $"GitHub returned {(int)resp.StatusCode} ({resp.ReasonPhrase}) for '{repo}'.";
                _logger.LogWarning("[WCS2] {Msg}", msg);
                return CacheResult(MakeError(msg, now));
            }

            if (!resp.IsSuccessStatusCode)
            {
                string msg = $"GitHub API returned HTTP {(int)resp.StatusCode} ({resp.ReasonPhrase}).";
                _logger.LogWarning("[WCS2] {Msg}", msg);
                return CacheResult(MakeError(msg, now));
            }

            string json = await resp.Content.ReadAsStringAsync(cts.Token);
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;

            string? tagName = root.TryGetProperty("tag_name", out var tagEl) ? tagEl.GetString() : null;
            if (string.IsNullOrWhiteSpace(tagName))
            {
                return CacheResult(MakeError("GitHub latest release response is missing tag_name.", now));
            }

            string normalizedLatest = NormalizeVersionTag(tagName);
            if (!TryParseSemVer(normalizedLatest, out var latestVer))
            {
                return CacheResult(MakeError($"Could not parse release version tag '{tagName}'.", now));
            }

            if (!TryParseSemVer(CurrentVersion, out var currentVer))
            {
                return CacheResult(MakeError($"Could not parse current plugin version '{CurrentVersion}'.", now));
            }

            string? assetName = null;
            string? browserUrl = null;
            string? apiAssetUrl = null;
            string? sha256BrowserUrl = null;
            string? sha256ApiUrl = null;

            if (root.TryGetProperty("assets", out var assetsEl) && assetsEl.ValueKind == JsonValueKind.Array)
            {
                foreach (var asset in assetsEl.EnumerateArray())
                {
                    string name = asset.TryGetProperty("name", out var n) ? (n.GetString() ?? string.Empty) : string.Empty;
                    string bUrl = asset.TryGetProperty("browser_download_url", out var bu) ? (bu.GetString() ?? string.Empty) : string.Empty;
                    string aUrl = asset.TryGetProperty("url", out var au) ? (au.GetString() ?? string.Empty) : string.Empty;

                    if (assetName == null
                        && name.StartsWith("WCS2-", StringComparison.OrdinalIgnoreCase)
                        && name.EndsWith("-linux-x64.zip", StringComparison.OrdinalIgnoreCase))
                    {
                        assetName = name;
                        browserUrl = bUrl;
                        apiAssetUrl = aUrl;
                    }
                    else if (sha256BrowserUrl == null
                             && name.Equals("SHA256SUMS.txt", StringComparison.OrdinalIgnoreCase))
                    {
                        sha256BrowserUrl = bUrl;
                        sha256ApiUrl = aUrl;
                    }
                }
            }

            bool updateAvailable = latestVer > currentVer;
            string status = updateAvailable
                ? $"Update available: v{CurrentVersion} -> v{normalizedLatest}" + (assetName != null ? $" ({assetName})" : " (no linux-x64 .zip asset found)")
                : $"WCS2 is up to date (current v{CurrentVersion}, latest v{normalizedLatest}).";

            return CacheResult(new UpdateCheckResult(
                Success: true,
                UpdateAvailable: updateAvailable,
                CurrentVersion: CurrentVersion,
                LatestVersion: normalizedLatest,
                TagName: tagName,
                AssetName: assetName,
                BrowserDownloadUrl: browserUrl,
                ApiAssetUrl: apiAssetUrl,
                Sha256AssetBrowserUrl: sha256BrowserUrl,
                Sha256AssetApiUrl: sha256ApiUrl,
                StatusMessage: status,
                CheckedAtUtc: now));
        }
        catch (OperationCanceledException)
        {
            string msg = _disposeCts.IsCancellationRequested
                ? "Update check cancelled (plugin unloading)."
                : $"Update check timed out after {(int)CheckTimeout.TotalSeconds}s.";
            _logger.LogWarning("[WCS2] {Msg}", msg);
            return CacheResult(MakeError(msg, now));
        }
        catch (Exception ex)
        {
            string msg = $"Update check failed: {ex.Message}";
            _logger.LogWarning(ex, "[WCS2] Update check failed for '{Repo}'.", repo);
            return CacheResult(MakeError(msg, now));
        }
    }

    private async Task InstallLatestUpdateCoreAsync(int? requesterSlot, ulong requesterSteamId)
    {
        if (Interlocked.CompareExchange(ref _isUpdating, 1, 0) != 0)
        {
            NotifyRequester(requesterSlot, requesterSteamId, "An update is already in progress.", isError: true);
            return;
        }

        string tmpDir = Path.Combine(_plugin.ModuleDirectory, ".update_tmp");
        try
        {
            NotifyRequester(requesterSlot, requesterSteamId, "Checking GitHub for the latest WCS2 release...");
            var check = await CheckLatestAsync(forceRefresh: true);
            if (!check.Success)
            {
                NotifyRequester(requesterSlot, requesterSteamId, check.StatusMessage, isError: true);
                return;
            }

            if (!check.UpdateAvailable || string.IsNullOrWhiteSpace(check.LatestVersion))
            {
                NotifyRequester(requesterSlot, requesterSteamId, check.StatusMessage);
                return;
            }

            if (string.IsNullOrWhiteSpace(check.AssetName)
                || (string.IsNullOrWhiteSpace(check.BrowserDownloadUrl) && string.IsNullOrWhiteSpace(check.ApiAssetUrl)))
            {
                NotifyRequester(requesterSlot, requesterSteamId,
                    $"Release v{check.LatestVersion} has no matching WCS2-*-linux-x64.zip asset. Update aborted.",
                    isError: true);
                return;
            }

            var cfg = _config;
            string token = (cfg.GitHubToken ?? string.Empty).Trim();

            CleanupDirectory(tmpDir);
            Directory.CreateDirectory(tmpDir);
            string zipPath = Path.Combine(tmpDir, "release.zip");
            string extractDir = Path.Combine(tmpDir, "extracted");
            Directory.CreateDirectory(extractDir);

            NotifyRequester(requesterSlot, requesterSteamId, $"Downloading v{check.LatestVersion} ({check.AssetName})...");
            using (var dlCts = CancellationTokenSource.CreateLinkedTokenSource(_disposeCts.Token))
            {
                dlCts.CancelAfter(DownloadTimeout);
                await DownloadAssetToFileAsync(check.ApiAssetUrl, check.BrowserDownloadUrl, zipPath, token, dlCts.Token);
            }

            // Optional SHA256SUMS.txt verification (strict abort on mismatch!)
            if (!string.IsNullOrWhiteSpace(check.Sha256AssetApiUrl) || !string.IsNullOrWhiteSpace(check.Sha256AssetBrowserUrl))
            {
                NotifyRequester(requesterSlot, requesterSteamId, "Verifying SHA256SUMS.txt checksum...");
                using var sumCts = CancellationTokenSource.CreateLinkedTokenSource(_disposeCts.Token);
                sumCts.CancelAfter(ChecksumTimeout);

                string sumsContent = await DownloadAssetAsStringAsync(
                    check.Sha256AssetApiUrl, check.Sha256AssetBrowserUrl, token, sumCts.Token);

                string? expectedHash = ParseSha256ForAsset(sumsContent, check.AssetName);
                if (string.IsNullOrWhiteSpace(expectedHash))
                {
                    NotifyRequester(requesterSlot, requesterSteamId,
                        $"SHA256SUMS.txt did not contain an entry for '{check.AssetName}'. Update aborted.",
                        isError: true);
                    return;
                }

                string actualHash = await ComputeFileSha256Async(zipPath, sumCts.Token);
                if (!string.Equals(expectedHash, actualHash, StringComparison.OrdinalIgnoreCase))
                {
                    _logger.LogError("[WCS2] SHA256 mismatch for {Asset}: expected {Expected}, got {Actual}.",
                        check.AssetName, expectedHash, actualHash);
                    NotifyRequester(requesterSlot, requesterSteamId,
                        "SHA256 checksum mismatch! Update aborted for safety.",
                        isError: true);
                    return;
                }

                _logger.LogInformation("[WCS2] Verified SHA256 for {Asset}: {Hash}", check.AssetName, actualHash);
            }

            // Extract archive safely with zip-slip protection and strict configs/ + data/ exclusion
            int extractedCount = ExtractAndValidateArchive(zipPath, extractDir, out string? extractError);
            if (extractError != null || extractedCount == 0)
            {
                NotifyRequester(requesterSlot, requesterSteamId,
                    extractError ?? "Release archive contained no installable files. Update aborted.",
                    isError: true);
                return;
            }

            // Atomically replace files in ModuleDirectory via POSIX rename() (never truncates live WCS2.dll!)
            int installedCount = ApplyExtractedFilesAtomically(extractDir, _plugin.ModuleDirectory);
            _logger.LogInformation("[WCS2] Atomically installed {Count} file(s) for v{Version}.",
                installedCount, check.LatestVersion);

            CleanupDirectory(tmpDir);

            string installedVersion = check.LatestVersion;
            MarshalToMainThread(() =>
            {
                if (requesterSlot.HasValue)
                {
                    var p = Utilities.GetPlayerFromSlot(requesterSlot.Value);
                    if (p != null && p.IsValid && !p.IsBot && p.SteamID == requesterSteamId)
                    {
                        p.PrintToChat($" {ChatColors.Green}[WCS2]{ChatColors.Default} Installed v{installedVersion}! Reloading plugin...");
                    }
                }
                else
                {
                    Server.PrintToConsole($"[WCS2] Installed v{installedVersion}! Reloading plugin...");
                }

                Server.ExecuteCommand("css_plugins reload WCS2");
            });
        }
        catch (OperationCanceledException)
        {
            NotifyRequester(requesterSlot, requesterSteamId, "Update timed out or was cancelled.", isError: true);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "[WCS2] Failed to install update.");
            NotifyRequester(requesterSlot, requesterSteamId, $"Update failed: {ex.Message}", isError: true);
        }
        finally
        {
            CleanupDirectory(tmpDir);
            Interlocked.Exchange(ref _isUpdating, 0);
        }
    }

    private async Task DownloadAssetToFileAsync(
        string? apiUrl,
        string? browserUrl,
        string destinationPath,
        string token,
        CancellationToken ct)
    {
        using var req = BuildAssetDownloadRequest(apiUrl, browserUrl, token);
        using var resp = await _http.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, ct);
        resp.EnsureSuccessStatusCode();

        if (resp.Content.Headers.ContentLength is > MaxDownloadBytes)
            throw new InvalidOperationException($"Download exceeds maximum allowed size ({MaxDownloadBytes / (1024 * 1024)} MiB).");

        await using var input = await resp.Content.ReadAsStreamAsync(ct);
        await using var output = new FileStream(destinationPath, FileMode.Create, FileAccess.Write, FileShare.None, 81920, useAsync: true);

        byte[] buffer = new byte[81920];
        long total = 0;
        int read;
        while ((read = await input.ReadAsync(buffer, ct)) > 0)
        {
            total += read;
            if (total > MaxDownloadBytes)
                throw new InvalidOperationException($"Download exceeded maximum allowed size ({MaxDownloadBytes / (1024 * 1024)} MiB).");
            await output.WriteAsync(buffer.AsMemory(0, read), ct);
        }
    }

    private async Task<string> DownloadAssetAsStringAsync(
        string? apiUrl,
        string? browserUrl,
        string token,
        CancellationToken ct)
    {
        using var req = BuildAssetDownloadRequest(apiUrl, browserUrl, token);
        using var resp = await _http.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, ct);
        resp.EnsureSuccessStatusCode();
        return await resp.Content.ReadAsStringAsync(ct);
    }

    private HttpRequestMessage BuildAssetDownloadRequest(string? apiUrl, string? browserUrl, string token)
    {
        bool useApiAuth = !string.IsNullOrEmpty(token) && !string.IsNullOrWhiteSpace(apiUrl);
        string targetUrl = useApiAuth ? apiUrl! : (browserUrl ?? apiUrl)!;

        var req = new HttpRequestMessage(HttpMethod.Get, targetUrl);
        req.Headers.UserAgent.ParseAdd($"WCS2-Updater/{CurrentVersion}");
        req.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/octet-stream"));
        if (useApiAuth)
        {
            req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
            req.Headers.TryAddWithoutValidation("X-GitHub-Api-Version", "2022-11-28");
        }
        return req;
    }

    private int ExtractAndValidateArchive(string zipPath, string extractDir, out string? error)
    {
        error = null;
        string fullExtractRoot = Path.GetFullPath(extractDir);
        if (!fullExtractRoot.EndsWith(Path.DirectorySeparatorChar))
            fullExtractRoot += Path.DirectorySeparatorChar;

        using var archive = ZipFile.OpenRead(zipPath);

        // Detect whether archive wraps files under "WCS2/" (standard release archive) or root
        bool hasWcs2Prefix = false;
        bool hasRootDll = false;
        foreach (var entry in archive.Entries)
        {
            string norm = entry.FullName.Replace('\\', '/').TrimStart('/');
            if (norm.Equals("WCS2/WCS2.dll", StringComparison.OrdinalIgnoreCase))
            {
                hasWcs2Prefix = true;
                break;
            }
            if (norm.Equals("WCS2.dll", StringComparison.OrdinalIgnoreCase))
            {
                hasRootDll = true;
            }
        }

        if (!hasWcs2Prefix && !hasRootDll)
        {
            error = "Release archive does not contain WCS2/WCS2.dll. Update aborted.";
            return 0;
        }

        int extracted = 0;
        foreach (var entry in archive.Entries)
        {
            // Skip directory entries
            if (string.IsNullOrEmpty(entry.Name) || entry.FullName.EndsWith('/') || entry.FullName.EndsWith('\\'))
                continue;

            string norm = entry.FullName.Replace('\\', '/');
            if (Path.IsPathRooted(norm) || norm.StartsWith('/'))
            {
                error = $"Refused unsafe rooted archive entry '{entry.FullName}'.";
                return 0;
            }

            string[] rawParts = norm.Split('/', StringSplitOptions.RemoveEmptyEntries);
            if (rawParts.Any(p => p == ".." || p == "."))
            {
                error = $"Refused unsafe path-traversal archive entry '{entry.FullName}'.";
                return 0;
            }

            string relPath;
            if (hasWcs2Prefix)
            {
                if (rawParts.Length < 2 || !rawParts[0].Equals("WCS2", StringComparison.OrdinalIgnoreCase))
                    continue;
                relPath = string.Join('/', rawParts.Skip(1));
            }
            else
            {
                relPath = string.Join('/', rawParts);
            }

            if (string.IsNullOrWhiteSpace(relPath))
                continue;

            if (IsProtectedOrExcludedRelativePath(relPath))
            {
                _logger.LogWarning("[WCS2] Updater skipped protected/excluded archive entry: {Entry}", relPath);
                continue;
            }

            string destPath = Path.GetFullPath(Path.Combine(extractDir, relPath));
            if (!destPath.StartsWith(fullExtractRoot, StringComparison.Ordinal))
            {
                error = $"Refused zip-slip archive entry '{entry.FullName}'.";
                return 0;
            }

            string? parent = Path.GetDirectoryName(destPath);
            if (!string.IsNullOrEmpty(parent))
                Directory.CreateDirectory(parent);

            entry.ExtractToFile(destPath, overwrite: true);
            extracted++;
        }

        if (!File.Exists(Path.Combine(extractDir, "WCS2.dll")))
        {
            error = "Extracted archive is missing WCS2.dll. Update aborted.";
            return 0;
        }

        return extracted;
    }

    private int ApplyExtractedFilesAtomically(string extractDir, string moduleDir)
    {
        string fullModuleRoot = Path.GetFullPath(moduleDir);
        if (!fullModuleRoot.EndsWith(Path.DirectorySeparatorChar))
            fullModuleRoot += Path.DirectorySeparatorChar;

        int count = 0;
        foreach (string srcFile in Directory.EnumerateFiles(extractDir, "*", SearchOption.AllDirectories))
        {
            string relPath = Path.GetRelativePath(extractDir, srcFile).Replace('\\', '/');
            if (IsProtectedOrExcludedRelativePath(relPath))
                continue;

            string destPath = Path.GetFullPath(Path.Combine(moduleDir, relPath));
            if (!destPath.StartsWith(fullModuleRoot, StringComparison.Ordinal))
                throw new InvalidOperationException($"Refused write outside module directory: '{relPath}'.");

            string? destDir = Path.GetDirectoryName(destPath);
            if (!string.IsNullOrEmpty(destDir))
                Directory.CreateDirectory(destDir);

            // Stage in same directory and atomically rename() so live loaded WCS2.dll inode is never truncated
            string stagePath = $"{destPath}.wcs2upd_{Guid.NewGuid():N}.tmp";
            try
            {
                File.Copy(srcFile, stagePath, overwrite: true);
                if (OperatingSystem.IsLinux() && destPath.EndsWith(".py", StringComparison.OrdinalIgnoreCase))
                {
                    File.SetUnixFileMode(
                        stagePath,
                        UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute |
                        UnixFileMode.GroupRead | UnixFileMode.GroupExecute |
                        UnixFileMode.OtherRead | UnixFileMode.OtherExecute);
                }
                File.Move(stagePath, destPath, overwrite: true);
                count++;
            }
            catch
            {
                try { if (File.Exists(stagePath)) File.Delete(stagePath); } catch { /* best-effort cleanup */ }
                throw;
            }
        }

        return count;
    }

    private static bool IsProtectedOrExcludedRelativePath(string relPath)
    {
        string norm = relPath.Replace('\\', '/').TrimStart('/');
        string firstSegment = norm.Split('/', 2)[0];

        if (firstSegment.Equals("configs", StringComparison.OrdinalIgnoreCase)
            || firstSegment.Equals("data", StringComparison.OrdinalIgnoreCase)
            || firstSegment.Equals(".update_tmp", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        if (norm.EndsWith(".pyc", StringComparison.OrdinalIgnoreCase)
            || norm.Split('/').Any(seg => seg.Equals("__pycache__", StringComparison.OrdinalIgnoreCase)))
        {
            return true;
        }

        return false;
    }

    private static string? ParseSha256ForAsset(string sumsContent, string assetName)
    {
        foreach (string rawLine in sumsContent.Split('\n'))
        {
            string line = rawLine.Trim();
            if (string.IsNullOrEmpty(line) || line.StartsWith('#')) continue;

            // Format: "<64-hex>  <filename>" or "<64-hex> *<filename>"
            string[] parts = line.Split((char[]?)null, 2, StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length != 2) continue;

            string hash = parts[0].Trim();
            string file = parts[1].Trim().TrimStart('*');
            string fileNameOnly = Path.GetFileName(file.Replace('\\', '/'));

            if (hash.Length == 64 && fileNameOnly.Equals(assetName, StringComparison.OrdinalIgnoreCase))
                return hash;
        }
        return null;
    }

    private static async Task<string> ComputeFileSha256Async(string filePath, CancellationToken ct)
    {
        await using var stream = new FileStream(filePath, FileMode.Open, FileAccess.Read, FileShare.Read, 81920, useAsync: true);
        byte[] hash = await SHA256.HashDataAsync(stream, ct);
        return Convert.ToHexString(hash).ToLowerInvariant();
    }

    private static string NormalizeVersionTag(string tag)
    {
        string trimmed = tag.Trim();
        if (trimmed.StartsWith('v') || trimmed.StartsWith('V'))
            trimmed = trimmed[1..];
        return trimmed;
    }

    private static bool TryParseSemVer(string raw, out Version version)
    {
        version = new Version(0, 0, 0, 0);
        if (string.IsNullOrWhiteSpace(raw)) return false;

        string core = NormalizeVersionTag(raw);
        int sepIdx = core.IndexOfAny(new[] { '-', '+' });
        if (sepIdx >= 0)
            core = core[..sepIdx];

        if (!Version.TryParse(core, out var parsed))
            return false;

        version = new Version(
            Math.Max(0, parsed.Major),
            Math.Max(0, parsed.Minor),
            Math.Max(0, parsed.Build),
            Math.Max(0, parsed.Revision));
        return true;
    }

    private UpdateCheckResult MakeError(string message, DateTimeOffset checkedAt)
        => new(
            Success: false,
            UpdateAvailable: false,
            CurrentVersion: CurrentVersion,
            LatestVersion: null,
            TagName: null,
            AssetName: null,
            BrowserDownloadUrl: null,
            ApiAssetUrl: null,
            Sha256AssetBrowserUrl: null,
            Sha256AssetApiUrl: null,
            StatusMessage: message,
            CheckedAtUtc: checkedAt);

    private UpdateCheckResult CacheResult(UpdateCheckResult result)
    {
        lock (_cacheLock)
        {
            _cachedResult = result;
            _lastCheckUtc = result.CheckedAtUtc;
        }
        return result;
    }

    private void NotifyRequester(int? requesterSlot, ulong requesterSteamId, string message, bool isError = false)
    {
        if (isError)
            _logger.LogWarning("[WCS2] Updater: {Msg}", message);
        else
            _logger.LogInformation("[WCS2] Updater: {Msg}", message);

        MarshalToMainThread(() =>
        {
            if (requesterSlot.HasValue)
            {
                var p = Utilities.GetPlayerFromSlot(requesterSlot.Value);
                if (p == null || !p.IsValid || p.IsBot || p.SteamID != requesterSteamId) return;
                char color = isError ? ChatColors.LightRed : ChatColors.Green;
                p.PrintToChat($" {color}[WCS2]{ChatColors.Default} {message}");
            }
            else
            {
                Server.PrintToConsole($"[WCS2] {message}");
            }
        });
    }

    private void MarshalToMainThread(Action action)
    {
        if (Volatile.Read(ref _disposed) != 0) return;
        Server.NextFrame(() =>
        {
            if (Volatile.Read(ref _disposed) != 0) return;
            action();
        });
    }

    private static void CleanupDirectory(string dirPath)
    {
        try
        {
            if (Directory.Exists(dirPath))
                Directory.Delete(dirPath, recursive: true);
        }
        catch { /* best-effort cleanup */ }
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
        try { _disposeCts.Cancel(); } catch { }
        _http.Dispose();
        _disposeCts.Dispose();
    }
}
