namespace AngazaCare.Domain;

public static class Roles
{
    public const string Chw = "chw";
    public const string Nurse = "nurse";
    public const string Admin = "admin";
    public const string Clinical = "nurse,admin";
}

public static class FlagStates
{
    public const string Open = "open";
    public const string Acknowledged = "acknowledged";
    public const string Assessed = "assessed";
    public const string Escalated = "escalated";
    public const string Approved = "approved";
    public const string Rejected = "rejected";

    public static readonly string[] Live = [Open, Acknowledged, Assessed, Escalated];
    public static bool IsLive(string state) => Live.Contains(state);
}

public static class Severity
{
    public static int Rank(string s) => s switch { "high" => 3, "medium" => 2, _ => 1 };
    public static string Max(IEnumerable<string> all) => all.OrderByDescending(Rank).FirstOrDefault() ?? "low";
}

public sealed record Facility(int Id, string Name, string Level, string County);

public sealed record AppUser(int Id, string FullName, string Phone, string Role, int? FacilityId, bool Active, string PasswordHash);

public sealed record DangerSign(
    string Code, string NameEn, string NameSw, string Severity, int SlaMinutes, int? MenuOption,
    string ChwGuidance, string? AdviceSw, string? AdviceEn, int SortOrder);

public sealed record LexiconEntry(int Id, string Kind, string? SignCode, string Phrase, string Language);

public sealed record Mother(
    int Id, string DisplayName, string PhoneLast3, string Language, string Village, int ChwId, string ChwName,
    bool Discreet, string ConsentVersion, DateTime ConsentAt, DateTime? OptedOutAt, DateTime CreatedAt,
    int? PregnancyId, DateOnly? Edd, DateOnly? NextAncDate, DateTime? NextCheckInAt, string? LiveFlagState,
    string? LiveFlagSeverity, DateTime? LastReplyAt)
{
    public int? GestationWeeks => Edd is null ? null : Gestation.WeeksFromEdd(Edd.Value, DateOnly.FromDateTime(DateTime.UtcNow));
}

public sealed record Message(long Id, int? MotherId, string Direction, string Channel, string Purpose, string Body, string? GatewayStatus, DateTime CreatedAt);

public sealed record Assessment(long Id, long MessageId, string Outcome, string Method, string[] Signs, string[] Matched, decimal? Confidence, string? Model, string? PromptVersion, int? LatencyMs, DateTime CreatedAt);

public sealed record Flag(
    int Id, int PregnancyId, int MotherId, string MotherName, string Village, string PhoneLast3, int ChwId, string ChwName,
    string Source, string[] Signs, string Severity, string State, DateTime SlaDueAt, DateTime CreatedAt, DateTime UpdatedAt,
    string? ChwFindings, int? BpSystolic, int? BpDiastolic, decimal? TemperatureC, string? ChwRecommendation,
    DateTime? AcknowledgedAt, DateTime? AssessedAt, DateTime? EscalatedAt, DateTime? DecidedAt, int? GestationWeeks)
{
    public bool IsLive => FlagStates.IsLive(State);
}

public sealed record FlagEvent(long Id, int FlagId, string? ActorName, string? ActorRole, string? FromState, string ToState, string? Note, DateTime CreatedAt);

public sealed record Referral(int Id, int FlagId, string FacilityName, string Urgency, string ApprovedByName, string? NurseNote, DateTime? SmsSentAt, DateTime CreatedAt);

public static class Gestation
{
    /// <summary>Gestational age in completed weeks, from the estimated due date (40 weeks).</summary>
    public static int WeeksFromEdd(DateOnly edd, DateOnly today)
        => Math.Clamp(40 - (int)Math.Ceiling((edd.DayNumber - today.DayNumber) / 7.0), 0, 45);

    /// <summary>Naegele's rule: EDD = LMP + 280 days.</summary>
    public static DateOnly EddFromLmp(DateOnly lmp) => lmp.AddDays(280);
}
