using System.Text.RegularExpressions;
using AngazaCare.Domain;

namespace AngazaCare.Tests;

/// <summary>Reads danger signs and lexicon straight from db/002_seed_reference.sql.</summary>
public static partial class SeedData
{
    private static readonly Lazy<(List<DangerSign> Signs, List<LexiconEntry> Lexicon)> Data = new(Load);
    public static List<DangerSign> Signs => Data.Value.Signs;
    public static List<LexiconEntry> Lexicon => Data.Value.Lexicon;

    private static (List<DangerSign>, List<LexiconEntry>) Load()
    {
        var sql = File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "seed.sql"));
        var signs = SignRow().Matches(sql).Select(m => new DangerSign(
            m.Groups["code"].Value, m.Groups["en"].Value, m.Groups["sw"].Value, m.Groups["sev"].Value, int.Parse(m.Groups["sla"].Value),
            m.Groups["menu"].Value == "NULL" ? null : int.Parse(m.Groups["menu"].Value), "", null, null, 0)).ToList();
        var id = 0;
        var lexicon = LexRow().Matches(sql).Select(m => new LexiconEntry(++id, m.Groups["kind"].Value,
            m.Groups["sign"].Value == "NULL" ? null : m.Groups["sign"].Value.Trim('\''), m.Groups["phrase"].Value, m.Groups["lang"].Value)).ToList();
        return (signs, lexicon);
    }

    [GeneratedRegex(@"\('(?<code>[A-Z]+)',\s*'(?<en>[^']+)',\s*'(?<sw>[^']+)',\s*'(?<sev>high|medium|low)',\s*(?<sla>\d+),\s*(?<menu>\d+|NULL)")]
    private static partial Regex SignRow();

    [GeneratedRegex(@"\('(?<kind>danger|well|negation)',\s*(?<sign>NULL|'[A-Z]+'),\s*'(?<phrase>[^']+)',\s*'(?<lang>en|sw|sheng)'\)")]
    private static partial Regex LexRow();
}
