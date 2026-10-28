namespace AngazaCare.Data;

public sealed record Headline(int Mothers, int ActivePregnancies, int OptedOut, int CheckInsSent, int CheckInsReplied,
    int FlagsTotal, int FlagsLive, int Escalated, int Referrals, int SlaMet, int SlaDecidable,
    double? MedianSecondsToAlert, double? MedianMinutesToAck, double? MedianMinutesToDecision);

public sealed record CountRow(string Key, string? Sub, int Count);

/// <summary>De-identified programme metrics: counts and timings only, no names or numbers.</summary>
public sealed class MetricsRepo(Db db)
{
    public Task<Headline?> HeadlineAsync() => db.SingleAsync("""
        SELECT
          (SELECT count(*) FROM mothers)::int AS mothers,
          (SELECT count(*) FROM pregnancies WHERE status = 'active')::int AS active_pregnancies,
          (SELECT count(*) FROM mothers WHERE opted_out_at IS NOT NULL)::int AS opted_out,
          (SELECT count(*) FROM check_ins WHERE kind = 'check_in')::int AS sent,
          (SELECT count(*) FROM check_ins WHERE kind = 'check_in' AND replied_at IS NOT NULL)::int AS replied,
          (SELECT count(*) FROM flags)::int AS flags_total,
          (SELECT count(*) FROM flags WHERE state IN ('open','acknowledged','assessed','escalated'))::int AS flags_live,
          (SELECT count(*) FROM flags WHERE escalated_at IS NOT NULL)::int AS escalated,
          (SELECT count(*) FROM referrals)::int AS referrals,
          (SELECT count(*) FROM flags WHERE coalesce(acknowledged_at, assessed_at) <= sla_due_at)::int AS sla_met,
          (SELECT count(*) FROM flags WHERE coalesce(acknowledged_at, assessed_at) IS NOT NULL OR escalated_at IS NOT NULL)::int AS sla_decidable,
          (SELECT percentile_cont(0.5) WITHIN GROUP (ORDER BY secs) FROM (
             SELECT extract(epoch FROM (SELECT min(a.created_at) FROM messages a
                    WHERE a.user_id = f.chw_id AND a.purpose = 'alert' AND a.created_at >= m.created_at) - m.created_at) AS secs
             FROM flags f JOIN messages m ON m.id = f.first_message_id) t WHERE secs IS NOT NULL
          ) AS median_seconds_to_alert,
          (SELECT percentile_cont(0.5) WITHIN GROUP (ORDER BY extract(epoch FROM acknowledged_at - created_at) / 60)
             FROM flags WHERE acknowledged_at IS NOT NULL) AS median_minutes_to_ack,
          (SELECT percentile_cont(0.5) WITHIN GROUP (ORDER BY extract(epoch FROM decided_at - created_at) / 60)
             FROM flags WHERE decided_at IS NOT NULL) AS median_minutes_to_decision
        """, r => new Headline((int)r["mothers"], (int)r["active_pregnancies"], (int)r["opted_out"], (int)r["sent"], (int)r["replied"],
            (int)r["flags_total"], (int)r["flags_live"], (int)r["escalated"], (int)r["referrals"], (int)r["sla_met"], (int)r["sla_decidable"],
            r["median_seconds_to_alert"] as double?, r["median_minutes_to_ack"] as double?, r["median_minutes_to_decision"] as double?));

    public Task<List<CountRow>> FlagsBySignAsync() => db.QueryAsync("""
        SELECT d.name_en AS key, d.severity AS sub, count(f.id)::int AS n
        FROM danger_signs d LEFT JOIN flags f ON d.code = ANY(f.signs)
        GROUP BY d.name_en, d.severity, d.sort_order ORDER BY count(f.id) DESC, d.sort_order
        """, r => new CountRow((string)r["key"], r.Str("sub"), (int)r["n"]));

    public Task<List<CountRow>> AssessmentsByMethodAsync() => db.QueryAsync("""
        SELECT method AS key, outcome AS sub, count(*)::int AS n FROM assessments GROUP BY method, outcome ORDER BY method, outcome
        """, r => new CountRow((string)r["key"], r.Str("sub"), (int)r["n"]));

    public Task<List<CountRow>> FlagsByStateAsync() => db.QueryAsync("""
        SELECT state AS key, NULL::text AS sub, count(*)::int AS n FROM flags GROUP BY state
        """, r => new CountRow((string)r["key"], r.Str("sub"), (int)r["n"]));
}
