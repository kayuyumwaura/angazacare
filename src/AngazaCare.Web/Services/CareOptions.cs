using AngazaCare.Data;
using AngazaCare.Domain;

namespace AngazaCare.Services;

public sealed class CareOptions
{
    /// <summary>How often each mother gets a check-in. Shorten for demos, e.g. "00:10:00".</summary>
    public TimeSpan CheckInInterval { get; set; } = TimeSpan.FromDays(7);

    /// <summary>Multiplies every danger-sign SLA. 1 = real time; 0.05 turns a 2-hour SLA into 6 minutes for a demo.</summary>
    public double SlaScale { get; set; } = 1.0;

    /// <summary>How often the background worker looks for due check-ins, reminders and missed SLAs.</summary>
    public TimeSpan WorkerInterval { get; set; } = TimeSpan.FromSeconds(30);

    /// <summary>Model signs below this confidence are not trusted; the reply goes to a human instead.</summary>
    public double ModelSignThreshold { get; set; } = 0.6;

    /// <summary>The model may only call a free-text reply "well" above this confidence.</summary>
    public double ModelWellThreshold { get; set; } = 0.85;

    public string TimeZone { get; set; } = "Africa/Nairobi";
    public string ConsentVersion { get; set; } = "2026-10-v1";

    public TimeZoneInfo Tz => TimeZoneInfo.FindSystemTimeZoneById(TimeZone);
    public DateOnly LocalToday => DateOnly.FromDateTime(TimeZoneInfo.ConvertTimeFromUtc(DateTime.UtcNow, Tz));
    public TimeSpan Sla(int minutes) => TimeSpan.FromMinutes(Math.Max(1, minutes * SlaScale));
}

/// <summary>
/// Danger signs and lexicon are read on every reply; this caches them and the
/// compiled rules engine, and is refreshed whenever an admin edits either.
/// </summary>
public sealed class RulesCache(IServiceScopeFactory scopes)
{
    private (RiskEngine Engine, List<DangerSign> Signs)? _current;
    private readonly SemaphoreSlim _lock = new(1, 1);

    public async Task<(RiskEngine Engine, List<DangerSign> Signs)> GetAsync()
    {
        if (_current is { } c) return c;
        await _lock.WaitAsync();
        try
        {
            if (_current is { } again) return again;
            using var scope = scopes.CreateScope();
            var config = scope.ServiceProvider.GetRequiredService<ConfigRepo>();
            var signs = await config.DangerSignsAsync();
            var lexicon = await config.LexiconAsync();
            _current = (new RiskEngine(signs, lexicon), signs);
            return _current.Value;
        }
        finally { _lock.Release(); }
    }

    public void Invalidate() => _current = null;
}
