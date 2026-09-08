using System.Runtime.Versioning;
using System.Text.Json;
using Microsoft.Data.Sqlite;
using Encomm.Browser.Core;

namespace Encomm.Browser.Settings;

/// <summary>
/// Persisted user settings. Secrets live in the secret store, not here.
/// </summary>
public sealed class BrowserSettings
{
    public string Mode { get; set; } = "Everyday";          // Everyday | Developer
    public string SearchProviderUrl { get; set; } = "https://duckduckgo.com/?q={q}";
    public string NewTabUrl { get; set; } = "encomm://newtab";
    public bool MemorySaverEnabled { get; set; } = true;
    public string MemoryPreset { get; set; } = "Balanced"; // Balanced | Aggressive | Adaptive | NeverSleep
    public int WarmTimeoutMinutes { get; set; } = 5;
    public int GhostTimeoutMinutes { get; set; } = 20;
    /// <summary>
    /// Process-tree threshold in MB for the Adaptive preset. When the
    /// total (host + WebView2 children) exceeds this, unprotected tabs
    /// ghost at half the configured Ghost timeout. 0 disables the check.
    /// </summary>
    public int MemoryPressureThresholdMB { get; set; } = 2048;
    public bool ShieldEnabled { get; set; } = true;
    public bool ShieldSafeBuiltInOnly { get; set; } = true;
    public bool RestoreLastSession { get; set; } = true;
    public bool DeveloperDiagnostics { get; set; } = false;
    public string Theme { get; set; } = "System";           // System | Light | Dark
    public AIProviderSettings AI { get; set; } = new();
}

public sealed class AIProviderSettings
{
    public string ProviderId { get; set; } = "mock"; // mock | openai-compatible
    public string? BaseUrl { get; set; }
    public string? Model { get; set; }
    public IReadOnlyDictionary<string, string> CustomHeaders { get; set; } = new Dictionary<string, string>();
    public bool HasSecret { get; set; }
}

/// <summary>
/// Loads / saves settings to the SQLite kv table. Secrets are stored via
/// <see cref="ISecretStore"/>.
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class SettingsStore
{
    private const string SettingsKey = "settings.v1";
    private readonly SqliteStore _store;
    private readonly ISecretStore _secrets;

    public SettingsStore(SqliteStore store, ISecretStore secrets)
    {
        _store = store;
        _secrets = secrets;
    }

    public BrowserSettings Load()
    {
        using var conn = _store.OpenConnection();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT value FROM kv WHERE key=$k;";
        cmd.Parameters.AddWithValue("$k", SettingsKey);
        var result = cmd.ExecuteScalar();
        if (result is string s)
        {
            try
            {
                var loaded = JsonSerializer.Deserialize<BrowserSettings>(s);
                if (loaded is not null) return loaded;
            }
            catch { /* corrupt; fall through to defaults */ }
        }
        return new BrowserSettings();
    }

    public void Save(BrowserSettings settings)
    {
        var json = JsonSerializer.Serialize(settings);
        using var conn = _store.OpenConnection();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "INSERT INTO kv(key,value) VALUES($k,$v) " +
                          "ON CONFLICT(key) DO UPDATE SET value=$v;";
        cmd.Parameters.AddWithValue("$k", SettingsKey);
        cmd.Parameters.AddWithValue("$v", json);
        cmd.ExecuteNonQuery();
    }
}

public interface ISecretStore
{
    void SetSecret(string name, string value);
    string? GetSecret(string name);
    void DeleteSecret(string name);
}

/// <summary>
/// DPAPI-backed secret store. Secrets are encrypted at rest with the
/// current user scope and persisted in a small table inside the SQLite
/// database; they are NEVER stored in plaintext, never in appsettings,
/// never logged.
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class DpapiSqliteSecretStore : ISecretStore
{
    private readonly SqliteStore _store;

    public DpapiSqliteSecretStore(SqliteStore store)
    {
        _store = store;
        EnsureTable();
    }

    private void EnsureTable()
    {
        using var conn = _store.OpenConnection();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = @"
CREATE TABLE IF NOT EXISTS secrets(
    name TEXT PRIMARY KEY,
    ciphertext_base64 TEXT NOT NULL,
    created_utc TEXT NOT NULL
);";
        cmd.ExecuteNonQuery();
    }

    public void SetSecret(string name, string value)
    {
        if (string.IsNullOrEmpty(name)) throw new ArgumentException("name required", nameof(name));
        if (value is null) throw new ArgumentNullException(nameof(value));
        var cipher = DpapiSecretCipher.ProtectString(value);
        using var conn = _store.OpenConnection();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "INSERT INTO secrets(name, ciphertext_base64, created_utc) VALUES($n,$c,$t) " +
                          "ON CONFLICT(name) DO UPDATE SET ciphertext_base64=$c, created_utc=$t;";
        cmd.Parameters.AddWithValue("$n", name);
        cmd.Parameters.AddWithValue("$c", cipher);
        cmd.Parameters.AddWithValue("$t", DateTimeOffset.UtcNow.ToString("o"));
        cmd.ExecuteNonQuery();
    }

    public string? GetSecret(string name)
    {
        using var conn = _store.OpenConnection();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT ciphertext_base64 FROM secrets WHERE name=$n;";
        cmd.Parameters.AddWithValue("$n", name);
        var result = cmd.ExecuteScalar();
        if (result is not string s) return null;
        try { return DpapiSecretCipher.UnprotectString(s); }
        catch { return null; }
    }

    public void DeleteSecret(string name)
    {
        using var conn = _store.OpenConnection();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "DELETE FROM secrets WHERE name=$n;";
        cmd.Parameters.AddWithValue("$n", name);
        cmd.ExecuteNonQuery();
    }
}