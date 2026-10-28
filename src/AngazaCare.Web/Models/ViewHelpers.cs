using AngazaCare.Domain;
using Microsoft.AspNetCore.Html;

namespace AngazaCare.Models;

public static class Ui
{
    private static readonly TimeZoneInfo Tz = TimeZoneInfo.FindSystemTimeZoneById("Africa/Nairobi");

    public static DateTime Local(DateTime utc) => TimeZoneInfo.ConvertTimeFromUtc(DateTime.SpecifyKind(utc, DateTimeKind.Utc), Tz);

    /// <summary>"14:05" today, "Yesterday 14:05", or "Tue 14 Oct, 14:05".</summary>
    public static string When(DateTime utc)
    {
        var local = Local(utc);
        var today = Local(DateTime.UtcNow).Date;
        if (local.Date == today) return local.ToString("HH:mm");
        if (local.Date == today.AddDays(-1)) return "Yesterday " + local.ToString("HH:mm");
        return local.ToString("ddd d MMM, HH:mm");
    }

    public static string Ago(DateTime utc)
    {
        var d = DateTime.UtcNow - utc;
        if (d.TotalMinutes < 1) return "just now";
        if (d.TotalMinutes < 60) return $"{(int)d.TotalMinutes} min ago";
        if (d.TotalHours < 24) return $"{(int)d.TotalHours} h ago";
        return $"{(int)d.TotalDays} days ago";
    }

    public static string Iso(DateTime utc) => DateTime.SpecifyKind(utc, DateTimeKind.Utc).ToString("o");

    public static string SignNames(IEnumerable<string> codes, IEnumerable<DangerSign> signs) =>
        string.Join(", ", codes.Select(c => signs.FirstOrDefault(s => s.Code == c)?.NameEn ?? c));

    public static string State(string state) => state switch
    {
        FlagStates.Open => "New",
        FlagStates.Acknowledged => "Seen by CHW",
        FlagStates.Assessed => "Waiting for nurse",
        FlagStates.Escalated => "Escalated to nurse",
        FlagStates.Approved => "Referred",
        FlagStates.Rejected => "Closed",
        _ => state,
    };

    public static string Severity(string s) => s switch { "high" => "Urgent", "medium" => "Soon", _ => "Check" };

    public static string Method(string m) => m switch
    {
        "menu" => "menu number",
        "lexicon" => "known phrase",
        "llm" => "language model",
        "fallback" => "sent to a person",
        _ => m,
    };

    public static string Urgency(string u) => u switch { "emergency" => "Emergency, now", "same_day" => "Today", _ => "Within days" };

    /// <summary>' ("damu", "naona giza")' for showing what the rules matched.</summary>
    public static string Quoted(IReadOnlyCollection<string> parts) =>
        parts.Count == 0 ? "" : " (" + string.Join(", ", parts.Select(p => "“" + p + "”")) + ")";

    public static string Lang(string l) => l switch { "sw" => "Swahili", "en" => "English", "sheng" => "Sheng", _ => l };

    private static readonly Dictionary<string, string> Icons = new()
    {
        ["cases"] = "<path d='M3 12h4l3-8 4 16 3-8h4'/>",
        ["mothers"] = "<circle cx='9' cy='8' r='3.5'/><path d='M2.5 20c.8-3.6 3.4-5.5 6.5-5.5s5.7 1.9 6.5 5.5'/><circle cx='17.5' cy='9' r='2.5'/><path d='M17 14.6c2.4.3 4 1.9 4.5 4.9'/>",
        ["enrol"] = "<circle cx='12' cy='12' r='9'/><path d='M12 8v8M8 12h8'/>",
        ["queue"] = "<path d='M4 6h16M4 12h16M4 18h10'/>",
        ["numbers"] = "<path d='M5 20V11M12 20V5M19 20v-6'/>",
        ["rules"] = "<path d='M4 7h10M18 7h2M4 17h4M12 17h8'/><circle cx='16' cy='7' r='2'/><circle cx='10' cy='17' r='2'/>",
        ["phone"] = "<rect x='7' y='2.5' width='10' height='19' rx='2.5'/><path d='M11 18h2'/>",
        ["staff"] = "<rect x='4' y='5' width='16' height='14' rx='2'/><circle cx='9.5' cy='11' r='2'/><path d='M6.5 16c.6-1.6 1.7-2.4 3-2.4s2.4.8 3 2.4M14.5 10h3M14.5 13h3'/>",
        ["back"] = "<path d='M15 5l-7 7 7 7'/>",
    };

    public static HtmlString Icon(string name, int size = 20) => new(
        $"<svg width='{size}' height='{size}' viewBox='0 0 24 24' fill='none' stroke='currentColor' stroke-width='1.9' stroke-linecap='round' stroke-linejoin='round' aria-hidden='true'>{Icons[name]}</svg>");

    public static HtmlString Mark(int size = 34) => new(
        $"<svg width='{size}' height='{size}' viewBox='0 0 64 64' aria-hidden='true'><path d='M14 42a18 18 0 0 1 36 0z' fill='#e5a00d'/><g stroke='#e5a00d' stroke-width='3.5' stroke-linecap='round'><path d='M32 14v6'/><path d='M15.5 21.5l4.2 4.2'/><path d='M48.5 21.5l-4.2 4.2'/></g><path d='M10 47h44' stroke='#e3efea' stroke-width='3.5' stroke-linecap='round'/></svg>");
}
