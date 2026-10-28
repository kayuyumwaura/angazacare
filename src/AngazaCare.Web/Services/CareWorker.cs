using AngazaCare.Data;

namespace AngazaCare.Services;

/// <summary>Sends check-ins and ANC reminders when they fall due.</summary>
public sealed class CheckInService(Db db, MotherRepo mothers, Outbox outbox, CareOptions options, ILogger<CheckInService> log)
{
    public async Task<int> SendDueCheckInsAsync()
    {
        var due = await db.QueryAsync("""
            SELECT p.id AS pregnancy_id, p.mother_id FROM pregnancies p JOIN mothers m ON m.id = p.mother_id
            WHERE p.status = 'active' AND m.opted_out_at IS NULL AND p.next_check_in_at <= now()
            ORDER BY p.next_check_in_at LIMIT 200
            """, r => (PregnancyId: (int)r["pregnancy_id"], MotherId: (int)r["mother_id"]));

        foreach (var (pregnancyId, motherId) in due)
        {
            // Claim the slot first so a slow send can never produce a duplicate check-in.
            var claimed = await db.ExecuteAsync("""
                UPDATE pregnancies SET next_check_in_at = now() + @interval
                WHERE id = @pregnancyId AND next_check_in_at <= now()
                """, new { pregnancyId, interval = options.CheckInInterval });
            if (claimed == 0) continue;
            await SendCheckInAsync(motherId, pregnancyId);
        }
        if (due.Count > 0) log.LogInformation("Sent {Count} check-ins", due.Count);
        return due.Count;
    }

    public async Task SendCheckInAsync(int motherId, int pregnancyId)
    {
        var mother = await mothers.ByIdAsync(motherId);
        if (mother is null || mother.OptedOutAt is not null) return;
        var weeks = mother.GestationWeeks;
        var checkInId = await db.ScalarAsync<int>(
            "INSERT INTO check_ins (pregnancy_id, kind, gestation_wk) VALUES (@pregnancyId, 'check_in', @weeks) RETURNING id",
            new { pregnancyId, weeks });
        await outbox.ToMotherAsync(motherId, "check_in", Templates.CheckIn(mother, weeks), checkInId);
    }

    public async Task<int> SendAncRemindersAsync()
    {
        var tomorrow = options.LocalToday.AddDays(1);
        var due = await db.QueryAsync("""
            SELECT p.id, p.mother_id FROM pregnancies p JOIN mothers m ON m.id = p.mother_id
            WHERE p.status = 'active' AND m.opted_out_at IS NULL AND p.next_anc_date = @tomorrow
              AND p.anc_reminder_sent_for IS DISTINCT FROM p.next_anc_date
            """, r => (PregnancyId: (int)r["id"], MotherId: (int)r["mother_id"]), new { tomorrow });

        foreach (var (pregnancyId, motherId) in due)
        {
            var mother = await mothers.ByIdAsync(motherId);
            if (mother is null) continue;
            await db.ExecuteAsync("UPDATE pregnancies SET anc_reminder_sent_for = next_anc_date WHERE id = @pregnancyId", new { pregnancyId });
            var checkInId = await db.ScalarAsync<int>(
                "INSERT INTO check_ins (pregnancy_id, kind, gestation_wk) VALUES (@pregnancyId, 'anc_reminder', @weeks) RETURNING id",
                new { pregnancyId, weeks = mother.GestationWeeks });
            await outbox.ToMotherAsync(motherId, "reminder", Templates.AncReminder(mother, tomorrow), checkInId);
        }
        return due.Count;
    }
}

/// <summary>
/// Background loop: due check-ins, ANC reminders and missed response times.
/// A hosted service keeps the prototype to one process; Hangfire or a queue can replace it at scale.
/// </summary>
public sealed class CareWorker(IServiceScopeFactory scopes, CareOptions options, ILogger<CareWorker> log) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(options.WorkerInterval);
        do
        {
            try
            {
                using var scope = scopes.CreateScope();
                var checkIns = scope.ServiceProvider.GetRequiredService<CheckInService>();
                var workflow = scope.ServiceProvider.GetRequiredService<FlagWorkflow>();
                await checkIns.SendDueCheckInsAsync();
                await checkIns.SendAncRemindersAsync();
                await workflow.EscalateOverdueAsync();
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                log.LogError(ex, "Care worker cycle failed; retrying next cycle");
            }
        } while (await timer.WaitForNextTickAsync(stoppingToken));
    }
}
