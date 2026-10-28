using System.Data.Common;
using AngazaCare.Domain;
using Npgsql;

namespace AngazaCare.Data;

public sealed class UserRepo(Db db)
{
    private const string Cols = "id, full_name, phone, role, facility_id, active, password_hash";
    private static AppUser Map(DbDataReader r) => new(
        (int)r["id"], (string)r["full_name"], (string)r["phone"], (string)r["role"], r.Int("facility_id"), r.Bool("active"), (string)r["password_hash"]);

    public Task<AppUser?> ByPhoneAsync(string phone) =>
        db.SingleAsync($"SELECT {Cols} FROM users WHERE phone = @phone", Map, new { phone });

    public Task<AppUser?> ByIdAsync(int id) =>
        db.SingleAsync($"SELECT {Cols} FROM users WHERE id = @id", Map, new { id });

    public Task<List<AppUser>> AllAsync() =>
        db.QueryAsync($"SELECT {Cols} FROM users ORDER BY role, full_name", Map);

    public Task<List<AppUser>> ByRoleAsync(string role) =>
        db.QueryAsync($"SELECT {Cols} FROM users WHERE role = @role AND active ORDER BY full_name", Map, new { role });

    public Task<int> CountAsync() => db.ScalarAsync<int>("SELECT count(*)::int FROM users");

    public Task<int> InsertAsync(string fullName, string phone, string role, int? facilityId, string passwordHash) =>
        db.ScalarAsync<int>("""
            INSERT INTO users (full_name, phone, role, facility_id, password_hash)
            VALUES (@fullName, @phone, @role, @facilityId, @passwordHash) RETURNING id
            """, new { fullName, phone, role, facilityId, passwordHash });

    public Task SetActiveAsync(int id, bool active) =>
        db.ExecuteAsync("UPDATE users SET active = @active WHERE id = @id", new { id, active });
}

public sealed class ConfigRepo(Db db)
{
    private static DangerSign MapSign(DbDataReader r) => new(
        (string)r["code"], (string)r["name_en"], (string)r["name_sw"], (string)r["severity"], (int)r["sla_minutes"],
        r.Int("menu_option"), (string)r["chw_guidance"], r.Str("advice_sw"), r.Str("advice_en"), (int)r["sort_order"]);

    public Task<List<DangerSign>> DangerSignsAsync() =>
        db.QueryAsync("SELECT * FROM danger_signs ORDER BY sort_order", MapSign);

    public Task UpdateDangerSignAsync(string code, string severity, int slaMinutes, string chwGuidance, string? adviceSw, string? adviceEn) =>
        db.ExecuteAsync("""
            UPDATE danger_signs SET severity = @severity, sla_minutes = @slaMinutes, chw_guidance = @chwGuidance,
                   advice_sw = NULLIF(@adviceSw, ''), advice_en = NULLIF(@adviceEn, '')
            WHERE code = @code
            """, new { code, severity, slaMinutes, chwGuidance, adviceSw, adviceEn });

    public Task<List<LexiconEntry>> LexiconAsync() =>
        db.QueryAsync("SELECT id, kind, sign_code, phrase, language FROM lexicon ORDER BY kind, sign_code, language, phrase",
            r => new LexiconEntry((int)r["id"], (string)r["kind"], r.Str("sign_code"), (string)r["phrase"], (string)r["language"]));

    public Task AddLexiconAsync(string kind, string? signCode, string phrase, string language, int userId) =>
        db.ExecuteAsync("""
            INSERT INTO lexicon (kind, sign_code, phrase, language, added_by)
            VALUES (@kind, @signCode, @phrase, @language, @userId) ON CONFLICT DO NOTHING
            """, new { kind, signCode = kind == "danger" ? signCode : null, phrase, language, userId });

    public Task DeleteLexiconAsync(int id) => db.ExecuteAsync("DELETE FROM lexicon WHERE id = @id", new { id });

    public Task<List<Facility>> FacilitiesAsync() =>
        db.QueryAsync("SELECT id, name, level, county FROM facilities ORDER BY id",
            r => new Facility((int)r["id"], (string)r["name"], (string)r["level"], (string)r["county"]));
}

public sealed class MotherRepo(Db db)
{
    private const string Select = """
        SELECT m.id, m.display_name, m.phone_last3, m.language, m.village, m.chw_id, u.full_name AS chw_name,
               m.discreet, m.consent_version, m.consent_at, m.opted_out_at, m.created_at,
               p.id AS pregnancy_id, p.edd, p.next_anc_date, p.next_check_in_at,
               f.state AS live_state, f.severity AS live_severity,
               (SELECT max(created_at) FROM messages x WHERE x.mother_id = m.id AND x.direction = 'in') AS last_reply_at
        FROM mothers m
        JOIN users u ON u.id = m.chw_id
        LEFT JOIN pregnancies p ON p.mother_id = m.id AND p.status = 'active'
        LEFT JOIN flags f ON f.pregnancy_id = p.id AND f.state IN ('open','acknowledged','assessed','escalated')
        """;

    private static Mother Map(DbDataReader r) => new(
        (int)r["id"], (string)r["display_name"], (string)r["phone_last3"], (string)r["language"], (string)r["village"],
        (int)r["chw_id"], (string)r["chw_name"], r.Bool("discreet"), (string)r["consent_version"], r.Time("consent_at")!.Value,
        r.Time("opted_out_at"), r.Time("created_at")!.Value, r.Int("pregnancy_id"), r.Date("edd"), r.Date("next_anc_date"),
        r.Time("next_check_in_at"), r.Str("live_state"), r.Str("live_severity"), r.Time("last_reply_at"));

    public Task<List<Mother>> ForChwAsync(int chwId) =>
        db.QueryAsync($"{Select} WHERE m.chw_id = @chwId ORDER BY m.display_name", Map, new { chwId });

    public Task<List<Mother>> AllAsync() =>
        db.QueryAsync($"{Select} ORDER BY m.display_name", Map);

    public Task<Mother?> ByIdAsync(int id) =>
        db.SingleAsync($"{Select} WHERE m.id = @id", Map, new { id });

    public Task<int?> IdByPhoneHashAsync(string phoneHash) =>
        db.ScalarAsync<int?>("SELECT id FROM mothers WHERE phone_hash = @phoneHash", new { phoneHash });

    public Task<string?> PhoneEncAsync(int id) =>
        db.ScalarAsync<string>("SELECT phone_enc FROM mothers WHERE id = @id", new { id });

    public Task<int> CountAsync() => db.ScalarAsync<int>("SELECT count(*)::int FROM mothers");

    public Task<int> EnrolAsync(string name, string phoneEnc, string phoneHash, string last3, string language, string village,
        int chwId, bool discreet, string consentVersion, DateOnly edd, DateOnly? lmp, DateOnly? nextAnc, DateTime firstCheckIn) =>
        db.InTransactionAsync(async (conn, tx) =>
        {
            var id = await db.ScalarAsync<int>("""
                INSERT INTO mothers (display_name, phone_enc, phone_hash, phone_last3, language, village, chw_id, discreet, consent_version, consent_at)
                VALUES (@name, @phoneEnc, @phoneHash, @last3, @language, @village, @chwId, @discreet, @consentVersion, now())
                RETURNING id
                """, new { name, phoneEnc, phoneHash, last3, language, village, chwId, discreet, consentVersion }, conn, tx);
            await db.ExecuteAsync("""
                INSERT INTO pregnancies (mother_id, edd, lmp, next_anc_date, next_check_in_at)
                VALUES (@id, @edd, @lmp, @nextAnc, @firstCheckIn)
                """, new { id, edd, lmp, nextAnc, firstCheckIn }, conn, tx);
            return id;
        });

    public Task SetOptOutAsync(int id, bool optedOut) =>
        db.ExecuteAsync("UPDATE mothers SET opted_out_at = CASE WHEN @optedOut THEN now() ELSE NULL END WHERE id = @id", new { id, optedOut });

    public Task UpdateCareAsync(int pregnancyId, DateOnly? nextAnc) =>
        db.ExecuteAsync("UPDATE pregnancies SET next_anc_date = @nextAnc, anc_reminder_sent_for = NULL WHERE id = @pregnancyId", new { pregnancyId, nextAnc });

    public Task CloseAsync(int pregnancyId, string status) =>
        db.ExecuteAsync("UPDATE pregnancies SET status = @status, next_check_in_at = NULL WHERE id = @pregnancyId", new { pregnancyId, status });

    public Task ScheduleCheckInNowAsync(int pregnancyId) =>
        db.ExecuteAsync("UPDATE pregnancies SET next_check_in_at = now() WHERE id = @pregnancyId", new { pregnancyId });
}

public sealed class MessageRepo(Db db)
{
    private static Message Map(DbDataReader r) => new(
        (long)r["id"], r.Int("mother_id"), (string)r["direction"], (string)r["channel"], (string)r["purpose"], (string)r["body"],
        r.Str("gateway_status"), r.Time("created_at")!.Value);

    public Task<List<Message>> ForMotherAsync(int motherId, int limit = 200) =>
        db.QueryAsync("""
            SELECT * FROM (SELECT * FROM messages WHERE mother_id = @motherId ORDER BY created_at DESC, id DESC LIMIT @limit) t
            ORDER BY created_at, id
            """, Map, new { motherId, limit });

    public Task<List<Message>> StaffAlertsAsync(int limit = 50) =>
        db.QueryAsync("SELECT * FROM messages WHERE user_id IS NOT NULL ORDER BY created_at DESC LIMIT @limit", Map, new { limit });

    public Task<long> InsertAsync(int? motherId, int? userId, int? checkInId, string direction, string channel, string purpose, string body,
        NpgsqlConnection? conn = null, NpgsqlTransaction? tx = null) =>
        db.ScalarAsync<long>("""
            INSERT INTO messages (mother_id, user_id, check_in_id, direction, channel, purpose, body)
            VALUES (@motherId, @userId, @checkInId, @direction, @channel, @purpose, @body) RETURNING id
            """, new { motherId, userId, checkInId, direction, channel, purpose, body }, conn, tx);

    public Task SetGatewayResultAsync(long id, string? gatewayId, string status) =>
        db.ExecuteAsync("UPDATE messages SET gateway_id = @gatewayId, gateway_status = @status WHERE id = @id", new { id, gatewayId, status });

    public Task SetStatusByGatewayIdAsync(string gatewayId, string status) =>
        db.ExecuteAsync("UPDATE messages SET gateway_status = @status WHERE gateway_id = @gatewayId", new { gatewayId, status });

    public Task<List<Assessment>> AssessmentsForMotherAsync(int motherId) =>
        db.QueryAsync("""
            SELECT a.* FROM assessments a JOIN messages m ON m.id = a.message_id
            WHERE m.mother_id = @motherId ORDER BY a.created_at
            """, r => new Assessment((long)r["id"], (long)r["message_id"], (string)r["outcome"], (string)r["method"], r.Arr("signs"),
                r.Arr("matched"), r.Dec("confidence"), r.Str("model"), r.Str("prompt_version"), r.Int("latency_ms"), r.Time("created_at")!.Value),
            new { motherId });
}

public sealed class FlagRepo(Db db)
{
    private const string Select = """
        SELECT f.*, m.display_name AS mother_name, m.village, m.phone_last3, u.full_name AS chw_name, p.edd
        FROM flags f
        JOIN mothers m ON m.id = f.mother_id
        JOIN users u ON u.id = f.chw_id
        JOIN pregnancies p ON p.id = f.pregnancy_id
        """;

    // Most urgent first: live before closed, then severity, then the nearest deadline.
    private const string Urgency = """
        ORDER BY (f.state IN ('open','acknowledged','assessed','escalated')) DESC,
                 CASE f.severity WHEN 'high' THEN 3 WHEN 'medium' THEN 2 ELSE 1 END DESC,
                 f.sla_due_at
        """;

    private static Flag Map(DbDataReader r)
    {
        var edd = r.Date("edd");
        return new Flag(
            (int)r["id"], (int)r["pregnancy_id"], (int)r["mother_id"], (string)r["mother_name"], (string)r["village"], (string)r["phone_last3"],
            (int)r["chw_id"], (string)r["chw_name"], (string)r["source"], r.Arr("signs"), (string)r["severity"], (string)r["state"],
            r.Time("sla_due_at")!.Value, r.Time("created_at")!.Value, r.Time("updated_at")!.Value, r.Str("chw_findings"),
            r.Int("bp_systolic"), r.Int("bp_diastolic"), r.Dec("temperature_c"), r.Str("chw_recommendation"),
            r.Time("acknowledged_at"), r.Time("assessed_at"), r.Time("escalated_at"), r.Time("decided_at"),
            edd is null ? null : Gestation.WeeksFromEdd(edd.Value, DateOnly.FromDateTime(DateTime.UtcNow)));
    }

    public Task<Flag?> ByIdAsync(int id, NpgsqlConnection? conn = null, NpgsqlTransaction? tx = null) =>
        db.SingleAsync($"{Select} WHERE f.id = @id", Map, new { id }, conn, tx);

    public Task<List<Flag>> ForChwAsync(int chwId, bool liveOnly) =>
        db.QueryAsync($"{Select} WHERE f.chw_id = @chwId {(liveOnly ? "AND f.state IN ('open','acknowledged','assessed','escalated')" : "")} {Urgency} LIMIT 200",
            Map, new { chwId });

    public Task<List<Flag>> ByStatesAsync(string[] states, int limit = 200) =>
        db.QueryAsync($"{Select} WHERE f.state = ANY(@states) {Urgency} LIMIT @limit", Map, new { states, limit });

    public Task<List<Flag>> ForMotherAsync(int motherId) =>
        db.QueryAsync($"{Select} WHERE f.mother_id = @motherId ORDER BY f.created_at DESC", Map, new { motherId });

    public Task<List<Flag>> RecentlyDecidedAsync(int limit = 20) =>
        db.QueryAsync($"{Select} WHERE f.state IN ('approved','rejected') ORDER BY f.decided_at DESC LIMIT @limit", Map, new { limit });

    public Task<List<FlagEvent>> EventsAsync(int flagId) =>
        db.QueryAsync("""
            SELECT e.*, u.full_name AS actor_name, u.role AS actor_role
            FROM flag_events e LEFT JOIN users u ON u.id = e.actor_id
            WHERE e.flag_id = @flagId ORDER BY e.created_at, e.id
            """, r => new FlagEvent((long)r["id"], (int)r["flag_id"], r.Str("actor_name"), r.Str("actor_role"), r.Str("from_state"),
                (string)r["to_state"], r.Str("note"), r.Time("created_at")!.Value), new { flagId });

    public Task<Referral?> ReferralAsync(int flagId) =>
        db.SingleAsync("""
            SELECT r.*, f.name AS facility_name, u.full_name AS approved_by_name
            FROM referrals r JOIN facilities f ON f.id = r.facility_id JOIN users u ON u.id = r.approved_by
            WHERE r.flag_id = @flagId
            """, r => new Referral((int)r["id"], (int)r["flag_id"], (string)r["facility_name"], (string)r["urgency"],
                (string)r["approved_by_name"], r.Str("nurse_note"), r.Time("sms_sent_at"), r.Time("created_at")!.Value), new { flagId });

    public Task<List<int>> OverdueIdsAsync() =>
        db.QueryAsync("SELECT id FROM flags WHERE state IN ('open','acknowledged') AND sla_due_at < now() ORDER BY sla_due_at",
            r => (int)r["id"]);

    public Task<int> CountLiveForChwAsync(int chwId) =>
        db.ScalarAsync<int>("SELECT count(*)::int FROM flags WHERE chw_id = @chwId AND state IN ('open','acknowledged','escalated')", new { chwId });
}
