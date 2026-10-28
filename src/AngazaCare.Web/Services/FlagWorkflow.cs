using AngazaCare.Data;
using AngazaCare.Domain;
using Npgsql;

namespace AngazaCare.Services;

public enum FlagAction { Acknowledge, Assess, Escalate, Approve, Reject }

/// <summary>
/// The one place that decides who may move a flag from which state to which.
/// The risk engine can only create flags; only people move them to a decision.
/// </summary>
public static class FlagTransitions
{
    private static readonly Dictionary<FlagAction, (string[] From, string To, string[] Roles)> Rules = new()
    {
        [FlagAction.Acknowledge] = ([FlagStates.Open], FlagStates.Acknowledged, [Roles.Chw]),
        [FlagAction.Assess] = ([FlagStates.Open, FlagStates.Acknowledged, FlagStates.Escalated], FlagStates.Assessed, [Roles.Chw, Roles.Nurse]),
        [FlagAction.Escalate] = ([FlagStates.Open, FlagStates.Acknowledged], FlagStates.Escalated, []),   // system only, on a missed SLA
        [FlagAction.Approve] = (FlagStates.Live, FlagStates.Approved, [Roles.Nurse]),
        [FlagAction.Reject] = (FlagStates.Live, FlagStates.Rejected, [Roles.Nurse]),
    };

    /// <summary>Returns the target state, or null if this role may not do this from this state. A null role means the system.</summary>
    public static string? Target(FlagAction action, string fromState, string? role)
    {
        var rule = Rules[action];
        if (!rule.From.Contains(fromState)) return null;
        if (role is null) return action == FlagAction.Escalate ? rule.To : null;
        return rule.Roles.Contains(role) ? rule.To : null;
    }

    public static bool Can(FlagAction action, string fromState, string? role) => Target(action, fromState, role) is not null;
}

public sealed class FlagConflictException(string message) : Exception(message);

public sealed record Actor(int UserId, string Role);

public sealed class FlagWorkflow(Db db, FlagRepo flags, MotherRepo mothers, UserRepo users, Outbox outbox, RulesCache rules, CareOptions options, ILogger<FlagWorkflow> log)
{
    /// <summary>
    /// Opens a flag for these signs, or folds them into the pregnancy's live flag
    /// (raising severity and tightening the deadline if needed). Then alerts people.
    /// </summary>
    public async Task<int> RaiseAsync(int motherId, int pregnancyId, IReadOnlyList<string> signCodes, long? messageId, string source, Actor? by, string? note = null)
    {
        var (_, allSigns) = await rules.GetAsync();
        var signs = allSigns.Where(s => signCodes.Contains(s.Code)).ToList();
        if (signs.Count == 0) signs = allSigns.Where(s => s.Code == RiskEngine.Unclear).ToList();
        var severity = Severity.Max(signs.Select(s => s.Severity));
        var due = DateTime.UtcNow + options.Sla(signs.Min(s => s.SlaMinutes));
        var mother = await mothers.ByIdAsync(motherId) ?? throw new InvalidOperationException($"Mother {motherId} not found");
        var codes = signs.Select(s => s.Code).ToArray();
        var label = string.Join(", ", signs.Select(s => s.NameEn));

        var (flagId, isNew, escalatedSeverity) = await db.InTransactionAsync(async (conn, tx) =>
        {
            var live = await db.SingleAsync("""
                SELECT id, signs, severity, state, sla_due_at FROM flags
                WHERE pregnancy_id = @pregnancyId AND state IN ('open','acknowledged','assessed','escalated') FOR UPDATE
                """, r => new LiveFlag((int)r["id"], r.Arr("signs"), (string)r["severity"], (string)r["state"], r.Time("sla_due_at")!.Value),
                new { pregnancyId }, conn, tx);

            if (live is null)
            {
                var id = await db.ScalarAsync<int>("""
                    INSERT INTO flags (pregnancy_id, mother_id, chw_id, source, first_message_id, signs, severity, state, sla_due_at)
                    VALUES (@pregnancyId, @motherId, @chwId, @source, @messageId, @codes, @severity, 'open', @due) RETURNING id
                    """, new { pregnancyId, motherId, chwId = mother.ChwId, source, messageId, codes, severity, due }, conn, tx);
                await AddEventAsync(conn, tx, id, by?.UserId, null, FlagStates.Open,
                    note ?? (source == "engine" ? $"Raised from her reply: {label}" : $"Opened by staff: {label}"));
                return (id, true, false);
            }

            // Merge into the live case. Never lower severity or loosen the deadline.
            var mergedSigns = live.Signs.Union(codes).ToArray();
            var mergedSeverity = Severity.Max([live.Severity, severity]);
            var raised = Severity.Rank(mergedSeverity) > Severity.Rank(live.Severity);
            var mergedDue = due < live.Due ? due : live.Due;
            // A worse sign after the CHW assessed reopens the case for the CHW.
            var state = raised && live.State == FlagStates.Assessed ? FlagStates.Open : live.State;
            await db.ExecuteAsync("""
                UPDATE flags SET signs = @mergedSigns, severity = @mergedSeverity, sla_due_at = @mergedDue, state = @state, updated_at = now()
                WHERE id = @id
                """, new { id = live.Id, mergedSigns, mergedSeverity, mergedDue, state }, conn, tx);
            await AddEventAsync(conn, tx, live.Id, by?.UserId, live.State, state,
                $"New report added: {label}{(raised ? $". Severity raised to {mergedSeverity}." : ".")}");
            return (live.Id, false, raised);
        });

        var flag = await flags.ByIdAsync(flagId);
        if (flag is null) return flagId;

        if (isNew || escalatedSeverity)
        {
            var flagSigns = allSigns.Where(s => flag.Signs.Contains(s.Code));
            await outbox.ToStaffAsync(flag.ChwId, "alert", Templates.ChwAlert(flag, flagSigns, options.Tz));
            if (flag.Severity == "high")
                foreach (var nurse in await users.ByRoleAsync(Roles.Nurse))
                    await outbox.ToStaffAsync(nurse.Id, "alert", Templates.NurseHighSeverity(flag));
        }
        return flagId;
    }

    public Task AcknowledgeAsync(int flagId, Actor by) =>
        MoveAsync(flagId, FlagAction.Acknowledge, by, null, null);

    public Task AssessAsync(int flagId, Actor by, string findings, int? bpSys, int? bpDia, decimal? tempC, string recommendation)
    {
        if (recommendation is not ("refer" or "close")) throw new ArgumentException("Recommendation must be refer or close.");
        var note = $"Findings: {findings.Trim()}{Vitals(bpSys, bpDia, tempC)}. Recommends {(recommendation == "refer" ? "referral" : "closing")}.";
        return MoveAsync(flagId, FlagAction.Assess, by, note, async (conn, tx) =>
            await db.ExecuteAsync("""
                UPDATE flags SET chw_findings = @findings, bp_systolic = @bpSys, bp_diastolic = @bpDia, temperature_c = @tempC,
                       chw_recommendation = @recommendation, assessed_at = now()
                WHERE id = @flagId
                """, new { flagId, findings = findings.Trim(), bpSys, bpDia, tempC, recommendation }, conn, tx));
    }

    public async Task ApproveAsync(int flagId, Actor by, int facilityId, string urgency, string? note)
    {
        if (urgency is not ("emergency" or "same_day" or "routine")) throw new ArgumentException("Unknown urgency.");
        await MoveAsync(flagId, FlagAction.Approve, by, $"Referral approved ({urgency.Replace('_', ' ')}){(string.IsNullOrWhiteSpace(note) ? "" : ": " + note.Trim())}",
            async (conn, tx) =>
            {
                await db.ExecuteAsync("UPDATE flags SET decided_at = now() WHERE id = @flagId", new { flagId }, conn, tx);
                await db.ExecuteAsync("""
                    INSERT INTO referrals (flag_id, facility_id, urgency, approved_by, nurse_note) VALUES (@flagId, @facilityId, @urgency, @userId, @note)
                    """, new { flagId, facilityId, urgency, userId = by.UserId, note }, conn, tx);
            });

        var flag = (await flags.ByIdAsync(flagId))!;
        var referral = (await flags.ReferralAsync(flagId))!;
        var mother = (await mothers.ByIdAsync(flag.MotherId))!;
        await outbox.ToMotherAsync(mother.Id, "referral", Templates.Referral(mother, referral.FacilityName, urgency, flag.ChwName));
        await db.ExecuteAsync("UPDATE referrals SET sms_sent_at = now() WHERE flag_id = @flagId", new { flagId });
        await outbox.ToStaffAsync(flag.ChwId, "alert", Templates.ChwDecision(flag, true, referral.FacilityName));
    }

    public async Task RejectAsync(int flagId, Actor by, string note, bool tellMother)
    {
        if (string.IsNullOrWhiteSpace(note)) throw new ArgumentException("A note is required to close a case.");
        await MoveAsync(flagId, FlagAction.Reject, by, $"Closed without referral: {note.Trim()}",
            (conn, tx) => db.ExecuteAsync("UPDATE flags SET decided_at = now() WHERE id = @flagId", new { flagId }, conn, tx));
        var flag = (await flags.ByIdAsync(flagId))!;
        if (tellMother)
        {
            var mother = (await mothers.ByIdAsync(flag.MotherId))!;
            await outbox.ToMotherAsync(mother.Id, "system", Templates.Closed(mother));
        }
        await outbox.ToStaffAsync(flag.ChwId, "alert", Templates.ChwDecision(flag, false, null));
    }

    /// <summary>Moves every flag whose deadline passed without CHW action into the nurse queue.</summary>
    public async Task<int> EscalateOverdueAsync()
    {
        var count = 0;
        foreach (var id in await flags.OverdueIdsAsync())
        {
            try
            {
                await MoveAsync(id, FlagAction.Escalate, null, "Response time passed without CHW action. Moved to nurse queue.",
                    (conn, tx) => db.ExecuteAsync("UPDATE flags SET escalated_at = now() WHERE id = @id", new { id }, conn, tx));
                var flag = (await flags.ByIdAsync(id))!;
                foreach (var nurse in await users.ByRoleAsync(Roles.Nurse))
                    await outbox.ToStaffAsync(nurse.Id, "alert", Templates.NurseEscalation(flag));
                count++;
            }
            catch (FlagConflictException) { /* acted on just now; nothing to escalate */ }
        }
        if (count > 0) log.LogInformation("Escalated {Count} overdue flags", count);
        return count;
    }

    private async Task MoveAsync(int flagId, FlagAction action, Actor? by, string? note, Func<NpgsqlConnection, NpgsqlTransaction, Task>? extra)
    {
        await db.InTransactionAsync(async (conn, tx) =>
        {
            var current = await db.ScalarAsync<string>("SELECT state FROM flags WHERE id = @flagId FOR UPDATE", new { flagId }, conn, tx)
                ?? throw new FlagConflictException("This case no longer exists.");
            var to = FlagTransitions.Target(action, current, by?.Role)
                ?? throw new FlagConflictException($"This case is now '{current}', so it cannot be {Past(action)}. Refresh to see the latest.");

            var stamp = action == FlagAction.Acknowledge ? ", acknowledged_at = now()" : "";
            await db.ExecuteAsync($"UPDATE flags SET state = @to, updated_at = now(){stamp} WHERE id = @flagId", new { flagId, to }, conn, tx);
            if (extra is not null) await extra(conn, tx);
            await AddEventAsync(conn, tx, flagId, by?.UserId, current, to, note);
            return 0;
        });
    }

    private Task AddEventAsync(NpgsqlConnection conn, NpgsqlTransaction tx, int flagId, int? actorId, string? from, string to, string? note) =>
        db.ExecuteAsync("""
            INSERT INTO flag_events (flag_id, actor_id, from_state, to_state, note) VALUES (@flagId, @actorId, @from, @to, @note)
            """, new { flagId, actorId, from, to, note }, conn, tx);

    private sealed record LiveFlag(int Id, string[] Signs, string Severity, string State, DateTime Due);

    private static string Vitals(int? sys, int? dia, decimal? temp)
    {
        var parts = new List<string>();
        if (sys is not null && dia is not null) parts.Add($"BP {sys}/{dia}");
        if (temp is not null) parts.Add($"temp {temp:0.0}°C");
        return parts.Count == 0 ? "" : $" ({string.Join(", ", parts)})";
    }

    private static string Past(FlagAction a) => a switch
    {
        FlagAction.Acknowledge => "acknowledged",
        FlagAction.Assess => "assessed",
        FlagAction.Escalate => "escalated",
        FlagAction.Approve => "approved",
        _ => "closed",
    };
}
