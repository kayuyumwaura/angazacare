using System.ComponentModel.DataAnnotations;
using AngazaCare.Data;
using AngazaCare.Domain;

namespace AngazaCare.Models;

public sealed class LoginInput
{
    [Required(ErrorMessage = "Enter your phone number.")] public string Phone { get; set; } = "";
    [Required(ErrorMessage = "Enter your password.")] public string Password { get; set; } = "";
    public string? ReturnUrl { get; set; }
}

public sealed class EnrolInput
{
    [Required(ErrorMessage = "Enter her first name."), StringLength(40)] public string DisplayName { get; set; } = "";
    [Required(ErrorMessage = "Enter her phone number.")] public string Phone { get; set; } = "";
    [Required] public string Language { get; set; } = "sw";
    [Required(ErrorMessage = "Enter her village or area."), StringLength(80)] public string Village { get; set; } = "";
    public string DateBasis { get; set; } = "lmp";          // lmp or edd
    public DateOnly? Lmp { get; set; }
    public DateOnly? Edd { get; set; }
    public DateOnly? NextAnc { get; set; }
    public bool Discreet { get; set; }
    public bool ConsentRead { get; set; }
    public bool ConsentGiven { get; set; }
}

public sealed class AssessInput
{
    [Required(ErrorMessage = "Write what you found.")] public string Findings { get; set; } = "";
    [Range(60, 260)] public int? BpSystolic { get; set; }
    [Range(30, 160)] public int? BpDiastolic { get; set; }
    [Range(34, 43)] public decimal? TemperatureC { get; set; }
    [Required(ErrorMessage = "Choose refer or close.")] public string Recommendation { get; set; } = "";
}

public sealed class DecisionInput
{
    public string Decision { get; set; } = "";              // approve or close
    public int? FacilityId { get; set; }
    public string Urgency { get; set; } = "same_day";
    public string? Note { get; set; }
    public bool TellMother { get; set; } = true;
}

public sealed record CaseListVm(string Title, string Lead, List<Flag> Live, List<Flag> Other, List<DangerSign> Signs, string EmptyMessage);

public sealed record NurseQueueVm(List<Flag> NeedsDecision, List<Flag> Watching, List<Flag> RecentlyDecided, List<DangerSign> Signs, Headline? Headline);

public sealed record CaseVm(
    Flag Flag, Mother Mother, List<FlagEvent> Events, List<Message> Messages, List<Assessment> Assessments,
    List<DangerSign> Signs, List<Facility> Facilities, Referral? Referral, string Role,
    bool CanAcknowledge, bool CanAssess, bool CanDecide);

public sealed record MotherListVm(List<Mother> Mothers, bool ShowChw, string? Filter);

public sealed record MotherVm(Mother Mother, List<Message> Messages, List<Assessment> Assessments, List<Flag> Flags, List<DangerSign> Signs, string Role, bool ShowSimulator);

public sealed record MetricsVm(Headline Headline, List<CountRow> BySign, List<CountRow> ByMethod, List<CountRow> ByState);

public sealed record RulesVm(List<DangerSign> Signs, List<LexiconEntry> Lexicon);

public sealed record SimulatorVm(List<Mother> Mothers, Mother? Selected, List<Message> Messages, List<Message> StaffAlerts, string Gateway);
