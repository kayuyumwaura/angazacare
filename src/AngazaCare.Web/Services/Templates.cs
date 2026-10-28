using System.Globalization;
using AngazaCare.Domain;

namespace AngazaCare.Services;

/// <summary>
/// Every SMS the system can send. Kept in one file so the clinical lead and a
/// Swahili reviewer can check all wording in one pass. Discreet mode never names
/// the pregnancy or a symptom, because phones are often shared.
/// </summary>
public static class Templates
{
    private static readonly CultureInfo Sw = new("sw-KE");
    private static readonly CultureInfo En = new("en-KE");

    public static string CheckIn(Mother m, int? weeks)
    {
        if (m.Discreet)
            return m.Language == "sw"
                ? $"Habari {m.DisplayName}, ni ukaguzi wako wa afya wa wiki hii. Jibu 1 kama uko sawa, au tuambie kwa maneno yako unavyojisikia."
                : $"Hi {m.DisplayName}, this is your weekly health check. Reply 1 if you are well, or tell us in your own words how you feel.";

        var wk = weeks is null ? "" : m.Language == "sw" ? $" Una wiki {weeks} za ujauzito." : $" You are {weeks} weeks pregnant.";
        return m.Language == "sw"
            ? $"Habari {m.DisplayName}, ni Angaza Care.{wk} Unajisikiaje leo? Jibu kwa nambari:\n1 Niko sawa\n2 Kutokwa na damu\n3 Kichwa kuuma sana/kuona giza\n4 Mtoto hachezi\n5 Homa\n6 Kuvimba uso/mikono\n7 Maumivu makali ya tumbo\n8 Nyingine (eleza)"
            : $"Hi {m.DisplayName}, this is Angaza Care.{wk} How are you today? Reply with a number:\n1 I am well\n2 Bleeding\n3 Bad headache/blurred vision\n4 Baby not moving\n5 Fever\n6 Swollen face/hands\n7 Severe belly pain\n8 Other (describe)";
    }

    public static string AncReminder(Mother m, DateOnly date)
    {
        var day = date.ToString("dddd d MMMM", m.Language == "sw" ? Sw : En);
        if (m.Discreet)
            return m.Language == "sw" ? $"Habari {m.DisplayName}, kumbuka miadi yako ya kliniki kesho, {day}." : $"Hi {m.DisplayName}, a reminder of your clinic appointment tomorrow, {day}.";
        return m.Language == "sw"
            ? $"Habari {m.DisplayName}, kumbuka kliniki ya ujauzito (ANC) kesho, {day}. Beba kitabu chako cha mama na mtoto."
            : $"Hi {m.DisplayName}, a reminder of your antenatal clinic tomorrow, {day}. Please bring your mother and child booklet.";
    }

    public static string AckWell(Mother m) => m.Language == "sw"
        ? $"Asante {m.DisplayName}! Tutakuuliza tena wiki ijayo. Ukijisikia vibaya wakati wowote, tuma ujumbe hapa."
        : $"Thank you {m.DisplayName}! We will check in again next week. If you feel unwell at any time, send a message here.";

    public static string AckFollowUp(Mother m) => m.Language == "sw"
        ? $"Asante {m.DisplayName}. Mhudumu wako wa afya, {FirstName(m.ChwName)}, atawasiliana nawe hivi karibuni."
        : $"Thank you {m.DisplayName}. Your health worker, {FirstName(m.ChwName)}, will contact you soon.";

    public static string? Advice(Mother m, DangerSign sign) => m.Language == "sw" ? sign.AdviceSw : sign.AdviceEn;

    public static string Referral(Mother m, string facility, string urgency, string chwName)
    {
        var when = (m.Language, urgency) switch
        {
            ("sw", "emergency") => "SASA HIVI",
            ("sw", "same_day") => "leo",
            ("sw", _) => "katika siku chache zijazo",
            (_, "emergency") => "RIGHT NOW",
            (_, "same_day") => "today",
            _ => "in the next few days",
        };
        return m.Language == "sw"
            ? $"Habari {m.DisplayName}. Muuguzi ameomba uende {facility} {when}. {FirstName(chwName)}, mhudumu wako wa afya, atakusaidia na mpango wa kufika."
            : $"Hi {m.DisplayName}. The nurse has asked you to go to {facility} {when}. {FirstName(chwName)}, your health worker, will help you plan how to get there.";
    }

    public static string Closed(Mother m) => m.Language == "sw"
        ? $"Habari {m.DisplayName}. Muuguzi amepitia hali yako na hakuna haja ya kwenda hospitali sasa. Tutaendelea kukuuliza kila wiki."
        : $"Hi {m.DisplayName}. The nurse has reviewed how you are and you do not need to go to a facility now. We will keep checking in every week.";

    public static string OptedOut(string lang) => lang == "sw"
        ? "Umejiondoa kwenye Angaza Care na hutapokea ujumbe zaidi. Tuma START kurudi."
        : "You have left Angaza Care and will get no more messages. Send START to join again.";

    public static string OptedIn(string lang) => lang == "sw"
        ? "Karibu tena kwenye Angaza Care. Tutaendelea na ukaguzi wa kila wiki."
        : "Welcome back to Angaza Care. Weekly check-ins will continue.";

    public const string UnknownNumber =
        "Namba hii haijasajiliwa na Angaza Care. Tafadhali ongea na mhudumu wako wa afya ya jamii. / This number is not enrolled. Please talk to your community health worker.";

    // USSD menus (max 182 characters per screen on most networks).
    public static string UssdMenu(Mother m) => m.Discreet
        ? (m.Language == "sw" ? "CON Angaza Care\n1 Niko sawa\n2 Sijisikii vizuri" : "CON Angaza Care\n1 I am well\n2 I do not feel well")
        : (m.Language == "sw"
            ? "CON Unajisikiaje leo?\n1 Niko sawa\n2 Damu\n3 Kichwa/giza\n4 Mtoto hachezi\n5 Homa\n6 Kuvimba\n7 Tumbo kuuma sana\n8 Nyingine"
            : "CON How are you today?\n1 I am well\n2 Bleeding\n3 Headache/vision\n4 Baby not moving\n5 Fever\n6 Swelling\n7 Bad belly pain\n8 Other");

    public static string UssdDescribe(Mother m) => m.Language == "sw" ? "CON Eleza kwa ufupi unavyojisikia:" : "CON Briefly describe how you feel:";

    // Staff alerts are in English, short enough for one SMS.
    public static string ChwAlert(Flag f, IEnumerable<DangerSign> signs, TimeZoneInfo tz) =>
        $"Angaza: {f.MotherName}, {f.Village} (..{f.PhoneLast3}) reported {string.Join(", ", signs.Select(s => s.NameEn.ToLowerInvariant()))}. " +
        $"{(f.Severity == "high" ? "URGENT. " : "")}Respond by {Local(f.SlaDueAt, tz):HH:mm ddd}.";

    public static string NurseEscalation(Flag f) =>
        $"Angaza: case #{f.Id} for {f.MotherName}, {f.Village} passed its response time without CHW action. It is now in your queue.";

    public static string NurseHighSeverity(Flag f) =>
        $"Angaza: high-severity case #{f.Id} for {f.MotherName}, {f.Village}. CHW {FirstName(f.ChwName)} has been alerted.";

    public static string ChwDecision(Flag f, bool approved, string? facility) => approved
        ? $"Angaza: nurse approved referral for {f.MotherName} to {facility}. She has been sent the details by SMS."
        : $"Angaza: nurse closed case #{f.Id} for {f.MotherName}. She returns to weekly check-ins.";

    public static DateTime Local(DateTime utc, TimeZoneInfo tz) => TimeZoneInfo.ConvertTimeFromUtc(DateTime.SpecifyKind(utc, DateTimeKind.Utc), tz);

    public static string FirstName(string full) => full.Split(' ', StringSplitOptions.RemoveEmptyEntries).FirstOrDefault() ?? full;
}
