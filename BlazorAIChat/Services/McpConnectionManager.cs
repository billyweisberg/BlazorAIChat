#pragma warning disable SKEXP0010, SKEXP0001, SKEXP0020, KMEXP00
using BlazorAIChat.Models;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Options;
using Microsoft.SemanticKernel;
using ModelContextProtocol.Client;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Collections.Concurrent;

namespace BlazorAIChat.Services
{
    public sealed record McpServerStatus(string Name, string Source, string Type, string? Command, string? Url, bool Connected, string? ErrorMessage);

    internal sealed class McpConnectionManager : IMcpConnectionManager, IDisposable
    {
        private readonly IOptions<AppSettings> _appSettings;
        private readonly IHttpClientFactory _httpClientFactory;
        private readonly ILogger<McpConnectionManager> _logger;
        private readonly IMemoryCache _cache;
        private readonly Dictionary<string, SemaphoreSlim> _userLocks = new();
        private readonly AIChatDBContext _dbContext;
        private readonly ISecureStorage _secureStorage;

        private record CachedConnection(IMcpClient Client, KernelPlugin Plugin, string ServerName);
        private record CachedStatus(List<McpServerStatus> Statuses);

        private enum SourceRank
        {
            Global = 1,
            Role = 2,
            User = 3
        }

        public McpConnectionManager(IOptions<AppSettings> appSettings,
            IHttpClientFactory httpClientFactory,
            ILogger<McpConnectionManager> logger,
            IMemoryCache memoryCache,
            AIChatDBContext dbContext,
            ISecureStorage secureStorage)
        {
            _appSettings = appSettings;
            _httpClientFactory = httpClientFactory;
            _logger = logger;
            _cache = memoryCache;
            _dbContext = dbContext;
            _secureStorage = secureStorage;
        }

        public async Task EnsurePluginsForUserAsync(string userId, Kernel kernel, CancellationToken cancellationToken = default)
        {
            var key = GetUserCacheKey(userId);
            var list = await GetOrCreateUserConnectionsAsync(userId, key, cancellationToken).ConfigureAwait(false);

            foreach (var conn in list)
            {
                if (!kernel.Plugins.Any(p => string.Equals(p.Name, conn.Plugin.Name, StringComparison.OrdinalIgnoreCase)))
                {
                    _logger.LogInformation("Attaching MCP plugin {Plugin} to kernel for user {UserId}", conn.Plugin.Name, userId);
                    kernel.Plugins.Add(conn.Plugin);
                }
            }
        }

        public async Task DisconnectUserAsync(string userId)
        {
            var key = GetUserCacheKey(userId);

            if (_cache.TryGetValue(key, out List<CachedConnection>? cached) && cached != null)
            {
                await DisposeConnectionsAsync(cached).ConfigureAwait(false);
            }

            _cache.Remove(key);
            _cache.Remove(StatusCacheKey(userId));
        }

        public async Task<IReadOnlyList<McpServerStatus>> GetServerStatusesAsync(string userId, CancellationToken cancellationToken = default)
        {
            // compute and cache alongside connections; short lifetime
            if (_cache.TryGetValue(StatusCacheKey(userId), out CachedStatus? cs) && cs is not null)
            {
                return cs.Statuses;
            }

            var byName = new Dictionary<string, (UserMcpServerConfig Cfg, SourceRank Rank, string SourceLabel)>(StringComparer.OrdinalIgnoreCase);

            // Global
            var servers = _appSettings.Value.Mcp?.Servers;
            if (servers != null)
            {
                foreach (var kvp in servers)
                {
                    var cfg = new UserMcpServerConfig
                    {
                        UserId = userId,
                        Name = kvp.Key,
                        Type = kvp.Value.Type,
                        Command = kvp.Value.Command,
                        ArgsJson = kvp.Value.Args != null ? JsonSerializer.Serialize(kvp.Value.Args) : null,
                        EnvJson = kvp.Value.Env != null ? JsonSerializer.Serialize(kvp.Value.Env) : null,
                        Url = kvp.Value.Url,
                        HeadersJson = kvp.Value.Headers != null ? JsonSerializer.Serialize(kvp.Value.Headers) : null,
                        Enabled = true
                    };
                    UpsertByNameWithLabel(byName, cfg, SourceRank.Global, "Global");
                }
            }

            // Role
            var role = _dbContext.Users.Where(u => u.Id == userId).Select(u => u.Role).FirstOrDefault();
            var roleDefaults = _dbContext.RoleMcpServerConfigs.Where(r => r.Role == role && r.Enabled).ToList();
            foreach (var r in roleDefaults)
            {
                var cfg = new UserMcpServerConfig
                {
                    UserId = userId,
                    Name = r.Name,
                    Type = r.Type,
                    Command = r.Command,
                    ArgsJson = r.ArgsJson,
                    EnvJson = r.EnvJson,
                    Url = r.Url,
                    HeadersJson = r.HeadersJson,
                    Enabled = r.Enabled
                };
                UpsertByNameWithLabel(byName, cfg, SourceRank.Role, "Role");
            }

            // User
            var userConfigs = _dbContext.UserMcpServerConfigs.Where(u => u.UserId == userId && u.Enabled).ToList();
            foreach (var u in userConfigs)
            {
                UpsertByNameWithLabel(byName, u, SourceRank.User, "User");
            }

            // Ordered by precedence, then dedupe by fingerprint to unique candidates
            var ordered = byName.Values
                .OrderByDescending(x => x.Rank)
                .ThenBy(x => x.Cfg.Name, StringComparer.OrdinalIgnoreCase)
                .ToList();

            var seenFingerprints = new HashSet<string>(StringComparer.Ordinal);
            var unique = new List<(UserMcpServerConfig Cfg, string SourceLabel)>();
            foreach (var item in ordered)
            {
                if (!item.Cfg.Enabled) continue;
                var fp = ComputeFingerprint(item.Cfg);
                if (seenFingerprints.Add(fp))
                {
                    unique.Add((item.Cfg, item.SourceLabel));
                }
            }

            // Check statuses in parallel with short per-server timeout for responsiveness
            var results = new ConcurrentBag<McpServerStatus>();
            var tasks = unique.Select(async item =>
            {
                string? error = null; bool connected = false;
                try
                {
                    using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                    linked.CancelAfter(TimeSpan.FromSeconds(3)); // short timeout per server
                    var client = await CreateClientWithRetryAsync(item.Cfg.Name, item.Cfg, userId, linked.Token).ConfigureAwait(false);
                    try
                    {
                        var _ = await client.ListToolsAsync().ConfigureAwait(false);
                        connected = true;
                    }
                    finally
                    {
                        if (client is IAsyncDisposable ad) await ad.DisposeAsync(); else if (client is IDisposable d) d.Dispose();
                    }
                }
                catch (Exception ex)
                {
                    error = ex.Message;
                    connected = false;
                }
                finally
                {
                    results.Add(new McpServerStatus(item.Cfg.Name, item.SourceLabel, item.Cfg.Type, item.Cfg.Command, item.Cfg.Url, connected, error));
                }
            }).ToArray();

            await Task.WhenAll(tasks).ConfigureAwait(false);

            var statuses = results.OrderBy(r => r.Source).ThenBy(r => r.Name, StringComparer.OrdinalIgnoreCase).ToList();

            // Cache briefly so the UI can display without hammering servers
            _cache.Set(StatusCacheKey(userId), new CachedStatus(statuses), new MemoryCacheEntryOptions
            {
                SlidingExpiration = TimeSpan.FromSeconds(30),
                AbsoluteExpirationRelativeToNow = TimeSpan.FromMinutes(2)
            });

            return statuses;
        }

        private static string StatusCacheKey(string userId) => $"mcp:status:{userId}";

        private void UpsertByNameWithLabel(Dictionary<string, (UserMcpServerConfig Cfg, SourceRank Rank, string SourceLabel)> byName, UserMcpServerConfig cfg, SourceRank rank, string sourceLabel)
        {
            if (string.IsNullOrWhiteSpace(cfg.Name)) return;
            if (!cfg.Enabled) return;
            if (!byName.TryGetValue(cfg.Name, out var existing) || rank > existing.Rank)
            {
                byName[cfg.Name] = (cfg, rank, sourceLabel);
            }
        }

        private async Task<List<CachedConnection>> GetOrCreateUserConnectionsAsync(string userId, string cacheKey, CancellationToken ct)
        {
            if (_cache.TryGetValue(cacheKey, out List<CachedConnection>? cached) && cached != null)
            {
                return cached;
            }

            var @lock = GetLockForKey(cacheKey);
            await @lock.WaitAsync(ct).ConfigureAwait(false);
            try
            {
                if (_cache.TryGetValue(cacheKey, out cached) && cached != null)
                {
                    return cached;
                }

                var created = new List<CachedConnection>();
                foreach (var server in GetEffectiveServersForUser(userId))
                {
                    try
                    {
                        var client = await CreateClientWithRetryAsync(serverName: server.Name, cfg: server, userId, ct).ConfigureAwait(false);
                        IList<McpClientTool> tools = await client.ListToolsAsync().ConfigureAwait(false);
                        var plugin = KernelPluginFactory.CreateFromFunctions(server.Name, tools.Select(t => t.AsKernelFunction()));
                        created.Add(new CachedConnection(client, plugin, server.Name));
                    }
                    catch (Exception ex)
                    {
                        _logger.LogError(ex, "Failed to initialize MCP server {Server} for user {UserId}", server.Name, userId);
                    }
                }

                var mcp = _appSettings.Value.Mcp;
                var options = new MemoryCacheEntryOptions
                {
                    SlidingExpiration = TimeSpan.FromMinutes(Math.Max(1, mcp.CacheSlidingExpirationMinutes)),
                    AbsoluteExpirationRelativeToNow = TimeSpan.FromMinutes(Math.Max(1, mcp.CacheAbsoluteExpirationMinutes))
                };

                options.PostEvictionCallbacks.Add(new PostEvictionCallbackRegistration
                {
                    EvictionCallback = (key, value, reason, state) =>
                    {
                        try
                        {
                            if (value is List<CachedConnection> list)
                            {
                                // dispose asynchronously without blocking the cache thread
                                _ = Task.Run(async () =>
                                {
                                    try { await DisposeConnectionsAsync(list).ConfigureAwait(false); }
                                    catch (Exception ex) { _logger.LogWarning(ex, "Error disposing MCP connections after cache eviction"); }
                                });
                            }
                        }
                        catch (Exception ex)
                        {
                            _logger.LogWarning(ex, "Post-eviction disposal threw an exception");
                        }
                    }
                });

                _cache.Set(cacheKey, created, options);
                return created;
            }
            finally
            {
                @lock.Release();
            }
        }

        private IEnumerable<UserMcpServerConfig> GetEffectiveServersForUser(string userId)
        {
            // Collect all candidates with precedence rank
            var byName = new Dictionary<string, (UserMcpServerConfig Cfg, SourceRank Rank)>(StringComparer.OrdinalIgnoreCase);

            // 1) Global from appsettings
            var servers = _appSettings.Value.Mcp?.Servers;
            if (servers != null)
            {
                foreach (var kvp in servers)
                {
                    var cfg = new UserMcpServerConfig
                    {
                        UserId = userId,
                        Name = kvp.Key,
                        Type = kvp.Value.Type,
                        Command = kvp.Value.Command,
                        ArgsJson = kvp.Value.Args != null ? JsonSerializer.Serialize(kvp.Value.Args) : null,
                        EnvJson = kvp.Value.Env != null ? JsonSerializer.Serialize(kvp.Value.Env) : null,
                        Url = kvp.Value.Url,
                        HeadersJson = kvp.Value.Headers != null ? JsonSerializer.Serialize(kvp.Value.Headers) : null,
                        Enabled = true
                    };
                    UpsertByName(byName, cfg, SourceRank.Global);
                }
            }

            // 2) Role defaults
            var role = _dbContext.Users.Where(u => u.Id == userId).Select(u => u.Role).FirstOrDefault();
            var roleDefaults = _dbContext.RoleMcpServerConfigs.Where(r => r.Role == role && r.Enabled).ToList();
            foreach (var r in roleDefaults)
            {
                var cfg = new UserMcpServerConfig
                {
                    UserId = userId,
                    Name = r.Name,
                    Type = r.Type,
                    Command = r.Command,
                    ArgsJson = r.ArgsJson,
                    EnvJson = r.EnvJson,
                    Url = r.Url,
                    HeadersJson = r.HeadersJson,
                    Enabled = r.Enabled
                };
                UpsertByName(byName, cfg, SourceRank.Role);
            }

            // 3) User configs
            var userConfigs = _dbContext.UserMcpServerConfigs.Where(u => u.UserId == userId && u.Enabled).ToList();
            foreach (var u in userConfigs)
            {
                UpsertByName(byName, u, SourceRank.User);
            }

            // Now we have highest-precedence per name. Next, dedupe identical configs across different names
            var listWithRank = byName.Values.ToList();
            var ordered = listWithRank
                .OrderByDescending(x => x.Rank) // precedence: User > Role > Global
                .ThenBy(x => x.Cfg.Name, StringComparer.OrdinalIgnoreCase)
                .ToList();

            var seenFingerprints = new HashSet<string>(StringComparer.Ordinal);
            var result = new List<UserMcpServerConfig>();

            foreach (var item in ordered)
            {
                if (!item.Cfg.Enabled) continue;
                var fp = ComputeFingerprint(item.Cfg);
                if (seenFingerprints.Add(fp))
                {
                    result.Add(item.Cfg);
                }
                else
                {
                    _logger.LogInformation("Skipping MCP server '{Name}' due to identical configuration dedupe.", item.Cfg.Name);
                }
            }

            return result;
        }

        private void UpsertByName(Dictionary<string, (UserMcpServerConfig Cfg, SourceRank Rank)> byName, UserMcpServerConfig cfg, SourceRank rank)
        {
            if (string.IsNullOrWhiteSpace(cfg.Name)) return;
            if (!cfg.Enabled) return; // skip disabled

            if (!byName.TryGetValue(cfg.Name, out var existing))
            {
                byName[cfg.Name] = (cfg, rank);
            }
            else
            {
                // Replace only if higher precedence
                if (rank > existing.Rank)
                {
                    byName[cfg.Name] = (cfg, rank);
                }
            }
        }

        private static string NormalizeJsonArray(string? json)
        {
            if (string.IsNullOrWhiteSpace(json)) return "[]";
            try
            {
                var el = JsonSerializer.Deserialize<JsonElement>(json);
                if (el.ValueKind != JsonValueKind.Array) return "[]";
                return JsonSerializer.Serialize(el, new JsonSerializerOptions { WriteIndented = false });
            }
            catch
            {
                return "[]";
            }
        }

        private static string NormalizeJsonObject(string? json)
        {
            if (string.IsNullOrWhiteSpace(json)) return "{}";
            try
            {
                var dict = JsonSerializer.Deserialize<Dictionary<string, string>>(json) ?? new Dictionary<string, string>();
                var ordered = dict.OrderBy(kvp => kvp.Key, StringComparer.Ordinal);
                // Serialize deterministically
                var sb = new StringBuilder();
                foreach (var kv in ordered)
                {
                    sb.Append(kv.Key).Append('\u0001').Append(kv.Value).Append('\u0002');
                }
                return sb.ToString();
            }
            catch
            {
                return "{}";
            }
        }

        private static string ComputeFingerprint(UserMcpServerConfig cfg)
        {
            var type = (cfg.Type ?? string.Empty).Trim().ToLowerInvariant();
            var key = new StringBuilder();
            key.Append(type).Append('|');
            if (string.Equals(type, "sse", StringComparison.OrdinalIgnoreCase))
            {
                key.Append((cfg.Url ?? string.Empty).Trim());
            }
            else
            {
                key.Append((cfg.Command ?? string.Empty).Trim());
            }
            key.Append('|');
            key.Append(NormalizeJsonArray(cfg.ArgsJson)).Append('|');
            key.Append(NormalizeJsonObject(cfg.EnvJson)).Append('|');
            key.Append(NormalizeJsonObject(cfg.HeadersJson));

            // Hash for compactness and to avoid leaking details in logs
            using var sha = SHA256.Create();
            var bytes = sha.ComputeHash(Encoding.UTF8.GetBytes(key.ToString()));
            return Convert.ToHexString(bytes);
        }

        private async Task<IMcpClient> CreateClientWithRetryAsync(string serverName, UserMcpServerConfig cfg, string userId, CancellationToken ct)
        {
            var mcp = _appSettings.Value.Mcp;
            var retries = Math.Max(0, mcp.ConnectRetryCount);
            var backoffMs = Math.Max(0, mcp.ConnectRetryBackoffMs);

            Exception? last = null;
            for (int attempt = 0; attempt <= retries; attempt++)
            {
                try
                {
                    return await CreateClientAsync(serverName, cfg, userId).ConfigureAwait(false);
                }
                catch (Exception ex)
                {
                    last = ex;
                    _logger.LogWarning(ex, "CreateClient attempt {Attempt} failed for server {Server}", attempt + 1, serverName);
                    if (attempt < retries)
                    {
                        await Task.Delay(backoffMs, ct).ConfigureAwait(false);
                    }
                }
            }
            throw last ?? new InvalidOperationException("CreateClient failed with unknown error");
        }

        private async Task<IMcpClient> CreateClientAsync(string serverName, UserMcpServerConfig cfg, string userId)
        {
            if (string.Equals(cfg.Type, "sse", StringComparison.OrdinalIgnoreCase))
            {
                var httpClient = _httpClientFactory.CreateClient("defaultHttpClient");
                var headers = MergeHeadersWithInputs(cfg.HeadersJson, userId);
                return await McpClientFactory.CreateAsync(
                    new SseClientTransport(httpClient: httpClient, transportOptions: new SseClientTransportOptions
                    {
                        Endpoint = new Uri(cfg.Url ?? string.Empty),
                        AdditionalHeaders = headers
                    }),
                    new McpClientOptions
                    {
                        ClientInfo = new() { Name = serverName, Version = "1.0.0.0" }
                    });
            }

            var args = MergeArgsWithInputs(cfg.ArgsJson, userId);
            var env = MergeEnvWithInputs(cfg.EnvJson, userId);
            return await McpClientFactory.CreateAsync(new StdioClientTransport(new()
            {
                Name = serverName,
                Command = cfg.Command ?? string.Empty,
                Arguments = args,
                EnvironmentVariables = env
            }));
        }

        private List<string> MergeArgsWithInputs(string? argsJson, string userId)
        {
            var list = string.IsNullOrWhiteSpace(argsJson) ? new List<string>() : (JsonSerializer.Deserialize<List<string>>(argsJson!) ?? new());
            return list.Select(v => SubstituteInputs(v, userId)).ToList();
        }

        private Dictionary<string, string> MergeEnvWithInputs(string? envJson, string userId)
        {
            var dict = string.IsNullOrWhiteSpace(envJson) ? new Dictionary<string, string>() : (JsonSerializer.Deserialize<Dictionary<string, string>>(envJson!) ?? new());
            return dict.ToDictionary(k => k.Key, v => SubstituteInputs(v.Value, userId));
        }

        private Dictionary<string, string> MergeHeadersWithInputs(string? headersJson, string userId)
        {
            var dict = string.IsNullOrWhiteSpace(headersJson) ? new Dictionary<string, string>() : (JsonSerializer.Deserialize<Dictionary<string, string>>(headersJson!) ?? new());
            return dict.ToDictionary(k => k.Key, v => SubstituteInputs(v.Value, userId));
        }

        private string SubstituteInputs(string value, string userId)
        {
            if (string.IsNullOrWhiteSpace(value)) return value;
            // format: ${input:input_id}
            const string prefix = "${input:";
            const string suffix = "}";

            int idx = 0;
            var sb = new System.Text.StringBuilder(value.Length);
            while (idx < value.Length)
            {
                int start = value.IndexOf(prefix, idx, StringComparison.Ordinal);
                if (start < 0)
                {
                    sb.Append(value.AsSpan(idx));
                    break;
                }
                sb.Append(value.AsSpan(idx, start - idx));

                int end = value.IndexOf(suffix, start + prefix.Length, StringComparison.Ordinal);
                if (end < 0)
                {
                    // malformed, append rest
                    sb.Append(value.AsSpan(start));
                    break;
                }

                var inputId = value.Substring(start + prefix.Length, end - (start + prefix.Length));
                var protectedVal = _dbContext.UserMcpInputValues.Where(x => x.UserId == userId && x.InputId == inputId).Select(x => x.ProtectedValue).FirstOrDefault();
                var raw = _secureStorage.UnprotectOrNull(protectedVal) ?? string.Empty;
                sb.Append(raw);
                idx = end + suffix.Length;
            }
            return sb.ToString();
        }

        private SemaphoreSlim GetLockForKey(string key)
        {
            lock (_userLocks)
            {
                if (!_userLocks.TryGetValue(key, out var sem))
                {
                    sem = new SemaphoreSlim(1, 1);
                    _userLocks[key] = sem;
                }
                return sem;
            }
        }

        private static string GetUserCacheKey(string userId) => $"mcp:{userId}";

        private static async Task DisposeConnectionsAsync(IEnumerable<CachedConnection> list)
        {
            foreach (var c in list)
            {
                try
                {
                    if (c.Client is IAsyncDisposable asyncDisposable)
                    {
                        await asyncDisposable.DisposeAsync().ConfigureAwait(false);
                    }
                    else if (c.Client is IDisposable disposable)
                    {
                        disposable.Dispose();
                    }
                }
                catch
                {
                    // swallow; best effort
                }
            }
        }

        public void Dispose() { }
    }
}
