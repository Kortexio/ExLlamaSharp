using System.Text.RegularExpressions;
using ExLlamaSharp.Server.Data;
using ExLlamaSharp.Server.Data.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace ExLlamaSharp.Server.Services;

public sealed class ContentModerationService
{
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly SettingsService _settings;
    private readonly object _gate = new();
    private List<CompiledRule>? _rules;
    private DateTime _loadedAt = DateTime.MinValue;

    public ContentModerationService(IServiceScopeFactory scopeFactory, SettingsService settings)
    {
        _scopeFactory = scopeFactory;
        _settings = settings;
    }

    public async Task<ModerationResult> EvaluateAsync(string text, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(text);

        var settings = await _settings.GetAsync(cancellationToken).ConfigureAwait(false);
        if (!settings.ContentModerationEnabled)
        {
            return ModerationResult.Allow();
        }

        var rules = await GetRulesAsync(cancellationToken).ConfigureAwait(false);
        foreach (var rule in rules)
        {
            if (rule.Regex.IsMatch(text))
            {
                return new ModerationResult
                {
                    Allowed = rule.Action is not "block",
                    Matched = true,
                    Action = rule.Action,
                    Category = rule.Category,
                    RuleId = rule.Id,
                    Message = $"Content matched moderation rule ({rule.Category})",
                };
            }
        }

        return ModerationResult.Allow();
    }

    public void InvalidateCache()
    {
        lock (_gate)
        {
            _rules = null;
            _loadedAt = DateTime.MinValue;
        }
    }

    private async Task<IReadOnlyList<CompiledRule>> GetRulesAsync(CancellationToken cancellationToken)
    {
        lock (_gate)
        {
            if (_rules is not null && DateTime.UtcNow - _loadedAt < TimeSpan.FromSeconds(30))
            {
                return _rules;
            }
        }

        await using var scope = _scopeFactory.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var entities = await db.ModerationRules.AsNoTracking()
            .Where(r => r.Enabled)
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);

        var compiled = new List<CompiledRule>(entities.Count);
        foreach (var entity in entities)
        {
            try
            {
                compiled.Add(new CompiledRule(
                    entity.Id,
                    entity.Action,
                    entity.Category,
                    new Regex(entity.Pattern, RegexOptions.IgnoreCase | RegexOptions.Compiled | RegexOptions.CultureInvariant, TimeSpan.FromMilliseconds(250))));
            }
            catch (ArgumentException)
            {
                // skip invalid patterns
            }
        }

        lock (_gate)
        {
            _rules = compiled;
            _loadedAt = DateTime.UtcNow;
            return _rules;
        }
    }

    private sealed record CompiledRule(Guid Id, string Action, string Category, Regex Regex);
}

public sealed class ModerationResult
{
    public bool Allowed { get; init; } = true;
    public bool Matched { get; init; }
    public string? Action { get; init; }
    public string? Category { get; init; }
    public Guid? RuleId { get; init; }
    public string? Message { get; init; }

    public static ModerationResult Allow() => new() { Allowed = true, Matched = false };
}
