using Encomm.Browser.Engine.Abstractions;

namespace Encomm.Browser.Shield;

/// <summary>
/// A simple domain/URL matching rule. We intentionally do not implement
/// the full EasyList/uBlock rule grammar in Phase 1; the format here is
/// deliberately small and license-clean.
/// </summary>
public sealed class ShieldRule
{
    public required string Pattern { get; init; }
    public required ResourceCategory Category { get; init; }
    public string? Reason { get; init; }
}

/// <summary>Provider of rules. Local, hand-written by default.</summary>
public interface IFilterRuleProvider
{
    string ProviderId { get; }
    IReadOnlyList<ShieldRule> Rules { get; }
}

/// <summary>Pipeline that decides whether a request should be blocked.</summary>
public interface IRequestBlocker
{
    BlockDecision ShouldBlock(string url, ResourceCategory category);
    ShieldStats Stats { get; }
}

public sealed class ShieldStats
{
    public long TotalRequests { get; private set; }
    public long BlockedRequests { get; private set; }
    public long BlockedAds { get; private set; }
    public long BlockedTrackers { get; private set; }

    internal void RecordRequest() => TotalRequests++;
    internal void RecordBlock(ShieldRule? rule)
    {
        BlockedRequests++;
        if (rule is null) return;
        if (rule.Category == ResourceCategory.Other) BlockedTrackers++;
        else if (rule.Category == ResourceCategory.Script || rule.Category == ResourceCategory.Image) BlockedAds++;
    }
}

public sealed class FilterRuleProvider : IFilterRuleProvider
{
    public string ProviderId => "built-in-safe";
    public IReadOnlyList<ShieldRule> Rules { get; }

    public FilterRuleProvider(IEnumerable<ShieldRule>? extra = null)
    {
        var list = new List<ShieldRule>(BuiltInShieldRules.SafeDefaults);
        if (extra is not null) list.AddRange(extra);
        Rules = list;
    }
}

public sealed class RequestBlocker : IRequestBlocker
{
    private readonly List<ShieldRule> _rules;
    public ShieldStats Stats { get; } = new();
    public bool Enabled { get; set; } = true;

    public RequestBlocker(IFilterRuleProvider provider)
    {
        _rules = provider.Rules.ToList();
    }

    public BlockDecision ShouldBlock(string url, ResourceCategory category)
    {
        if (!Enabled) return new BlockDecision(true);
        Stats.RecordRequest();
        if (string.IsNullOrEmpty(url)) return new BlockDecision(true);
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri))
            return new BlockDecision(true);

        foreach (var rule in _rules)
        {
            if (rule.Category != ResourceCategory.Other && rule.Category != category) continue;
            if (Matches(rule.Pattern, uri)) { Stats.RecordBlock(rule); return new BlockDecision(false, rule.Reason ?? "blocked by rule"); }
        }
        return new BlockDecision(true);
    }

    private static bool Matches(string pattern, Uri uri)
    {
        if (string.IsNullOrEmpty(pattern)) return false;
        // Two supported patterns:
        //   "host" — exact host
        //   "host/path-prefix" — host + path prefix
        //   "*://*.example.com/*" — wildcard suffix
        if (pattern.StartsWith("*://", StringComparison.Ordinal))
        {
            var body = pattern.Substring(4);
            var slashIdx = body.IndexOf("/*", StringComparison.Ordinal);
            var hostTpl = slashIdx >= 0 ? body.Substring(0, slashIdx) : body;
            if (hostTpl.StartsWith("*.", StringComparison.Ordinal))
            {
                var suffix = hostTpl.Substring(1); // ".example.com"
                if (uri.Host.EndsWith(suffix, StringComparison.OrdinalIgnoreCase)) return true;
            }
            else
            {
                if (uri.Host.Equals(hostTpl, StringComparison.OrdinalIgnoreCase)) return true;
            }
            return false;
        }
        var idx = pattern.IndexOf('/', StringComparison.Ordinal);
        if (idx < 0) return string.Equals(uri.Host, pattern, StringComparison.OrdinalIgnoreCase);
        var host = pattern.Substring(0, idx);
        var path = pattern.Substring(idx);
        return string.Equals(uri.Host, host, StringComparison.OrdinalIgnoreCase) &&
               uri.AbsolutePath.StartsWith(path, StringComparison.OrdinalIgnoreCase);
    }
}

/// <summary>
/// Hand-written safe default rules. These are NOT derived from
/// EasyList / uBlock / Brave. They exist to exercise the blocking
/// pipeline end-to-end during Phase 1.
/// </summary>
public static class BuiltInShieldRules
{
    public static readonly IReadOnlyList<ShieldRule> SafeDefaults = new List<ShieldRule>
    {
        new ShieldRule { Pattern = "doubleclick.net", Category = ResourceCategory.Other, Reason = "tracking" },
        new ShieldRule { Pattern = "googlesyndication.com", Category = ResourceCategory.Other, Reason = "ads" },
        new ShieldRule { Pattern = "googleadservices.com", Category = ResourceCategory.Other, Reason = "ads" },
        new ShieldRule { Pattern = "scorecardresearch.com", Category = ResourceCategory.Other, Reason = "tracking" },
        new ShieldRule { Pattern = "adnxs.com", Category = ResourceCategory.Other, Reason = "ads" },
        new ShieldRule { Pattern = "adservice.google.com", Category = ResourceCategory.Other, Reason = "ads" },
        new ShieldRule { Pattern = "*://*.ads.example.test/*", Category = ResourceCategory.Other, Reason = "test-rule" }
    };
}