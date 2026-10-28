using System.Globalization;
using System.Text;
using AngazaCare.Domain;

namespace AngazaCare.Services;

public enum Outcome { Well, Danger, Unclear }

/// <param name="ModelMayClear">False when the reply has a "but", or denies a symptom: then the model may add a
/// sign but may not call her well, and a person reads it.</param>
public sealed record EngineResult(Outcome Outcome, string Method, IReadOnlyList<string> Signs, IReadOnlyList<string> Matched, bool NeedsModel, bool ModelMayClear = true);

/// <summary>
/// Rules-first danger-sign detection. Deterministic and explainable: every sign it
/// returns names the menu option or phrase that produced it. When the rules cannot
/// decide, it says so (NeedsModel) instead of guessing; the caller then asks the
/// language model or, failing that, raises a flag for a human.
///
/// Safety properties (covered by unit tests):
///  - A danger phrase always produces a flag, whatever else the reply says.
///  - "Well" is only returned for short replies made of well-words, with no
///    negated well-word ("si sawa") and no "but" / "lakini". "sina damu, niko poa" is well;
///    "sina damu" alone is not (it goes to the model or a person).
///  - Nothing here can remove a sign; the model is only consulted when the rules found none.
/// </summary>
public sealed class RiskEngine
{
    public const string WellOption = "1";
    public const string Unclear = "UNCLEAR";

    private readonly Dictionary<int, string> _menu;
    private readonly List<(string[] Tokens, string Kind, string? Sign, string Phrase)> _phrases;
    private readonly HashSet<string> _negations;

    // Words that carry no health meaning on their own (greetings, fillers, pronouns).
    private static readonly HashSet<string> Filler = new(StringComparer.Ordinal)
    {
        "hi","hello","hey","habari","jambo","sasa","mambo","vipi","asante","asanti","thanks","thank","you","u","sana","very","so",
        "mimi","mi","niko","i","im","am","is","are","the","a","and","na","pia","also","too","leo","today","yes","ndio","ndiyo","yeah",
        "daktari","doctor","dr","nurse","mama","madam","sister","please","tafadhali","bado","still","now","sasahivi","we","our","tuko",
        "kabisa","totally","really","nimeshukuru","shukran","mungu","god","bless","amen","ok","k"
    };

    private static readonly HashSet<string> Contrast = new(StringComparer.Ordinal) { "but", "lakini", "ila", "except", "however", "though" };

    public RiskEngine(IEnumerable<DangerSign> signs, IEnumerable<LexiconEntry> lexicon)
    {
        _menu = signs.Where(s => s.MenuOption is not null).ToDictionary(s => s.MenuOption!.Value, s => s.Code);
        _negations = lexicon.Where(l => l.Kind == "negation").Select(l => Normalize(l.Phrase)).ToHashSet();
        _phrases = lexicon.Where(l => l.Kind is "danger" or "well")
            .Select(l => (Tokens: Tokenize(l.Phrase), l.Kind, Sign: l.SignCode, l.Phrase))
            .Where(p => p.Tokens.Length > 0)
            // Longest phrases first so "sina shida" (well) wins over "shida", and
            // "maumivu makali ya tumbo" wins over "maumivu makali".
            .OrderByDescending(p => p.Tokens.Length).ThenByDescending(p => p.Kind == "well")
            .ToList();
    }

    public EngineResult Evaluate(string? raw)
    {
        var tokens = Tokenize(raw ?? "");
        if (tokens.Length == 0)
            return new(Outcome.Unclear, "fallback", [Unclear], ["(empty reply)"], NeedsModel: false);

        var signs = new List<string>();
        var matched = new List<string>();
        var used = new bool[tokens.Length];
        var wellCount = 0;
        var negatedDanger = false;
        var negatedWell = false;
        var menuUsed = false;

        // 1. Leading menu numbers: "2", "2 5", "2,5", "3 kichwa inauma".
        var i = 0;
        while (i < tokens.Length && int.TryParse(tokens[i], NumberStyles.None, CultureInfo.InvariantCulture, out var n) && tokens[i].Length <= 2)
        {
            used[i] = true;
            menuUsed = true;
            if (n == 1) { wellCount++; matched.Add("menu 1"); }
            else if (_menu.TryGetValue(n, out var code)) { signs.Add(code); matched.Add($"menu {n}"); }
            else { signs.Add(Unclear); matched.Add($"menu {n} (not on the menu)"); }
            i++;
        }

        // 2. Phrase matching, longest first, each token used at most once.
        foreach (var (pt, kind, sign, phrase) in _phrases)
        {
            for (var start = 0; start + pt.Length <= tokens.Length; start++)
            {
                if (!SpanMatches(tokens, used, start, pt)) continue;
                for (var k = 0; k < pt.Length; k++) used[start + k] = true;

                if (kind == "well")
                {
                    // "not good", "si sawa": a negated well-word is not well.
                    if (IsNegated(tokens, used, start, window: 1)) { negatedWell = true; MarkNegator(tokens, used, start); }
                    else wellCount++;
                    continue;
                }

                if (IsNegated(tokens, used, start, window: 2)) { negatedDanger = true; MarkNegator(tokens, used, start); continue; }
                if (!signs.Contains(sign!)) signs.Add(sign!);
                matched.Add(phrase);
            }
        }

        var hasContrast = tokens.Any(Contrast.Contains);
        var leftover = tokens.Where((t, idx) => !used[idx] && !Filler.Contains(t) && !Contrast.Contains(t)).ToList();

        // A real danger sign (not UNCLEAR) is final. UNCLEAR from the menu (8 = "other")
        // still lets the model read any text that came with it.
        var realSigns = signs.Where(s => s != Unclear).ToList();
        if (realSigns.Count > 0)
            return new(Outcome.Danger, matched.Any(m => !m.StartsWith("menu")) ? "lexicon" : "menu", realSigns, matched, NeedsModel: false);

        if (signs.Contains(Unclear))
            return new(Outcome.Unclear, "menu", [Unclear], matched, NeedsModel: leftover.Count > 0);

        // "sina damu, niko poa" (no bleeding, I'm fine): a negated danger word next to a clear well-word is well.
        if (wellCount > 0 && !negatedWell && !hasContrast && leftover.Count <= 1)
            return new(Outcome.Well, menuUsed ? "menu" : "lexicon", [], matched, NeedsModel: false);

        // Only filler ("asante sana") with no health content: treat as well, as a thank-you.
        if (wellCount == 0 && !negatedDanger && !negatedWell && !hasContrast && leftover.Count == 0)
            return new(Outcome.Well, "lexicon", [], ["(acknowledgement only)"], NeedsModel: false);

        return new(Outcome.Unclear, "fallback", [Unclear], matched, NeedsModel: true,
            ModelMayClear: !hasContrast && !negatedDanger && !negatedWell);
    }

    /// <summary>True if a negation word sits just before the phrase ("sina damu", "sina tena damu"),
    /// without another matched phrase in between ("sina damu, niko poa" does not negate "niko poa").</summary>
    private bool IsNegated(string[] tokens, bool[] used, int start, int window)
    {
        for (var back = 1; back <= window && start - back >= 0; back++)
        {
            if (_negations.Contains(tokens[start - back])) return true;
            if (used[start - back]) return false;
        }
        return false;
    }

    private void MarkNegator(string[] tokens, bool[] used, int start)
    {
        for (var back = 1; back <= 2 && start - back >= 0; back++)
            if (_negations.Contains(tokens[start - back])) used[start - back] = true;
    }

    private static bool SpanMatches(string[] tokens, bool[] used, int start, string[] phrase)
    {
        for (var k = 0; k < phrase.Length; k++)
            if (used[start + k] || tokens[start + k] != phrase[k]) return false;
        return true;
    }

    public static string[] Tokenize(string text) =>
        Normalize(text).Split(' ', StringSplitOptions.RemoveEmptyEntries);

    /// <summary>Lower-case, strip accents and apostrophes, turn punctuation into spaces.</summary>
    public static string Normalize(string text)
    {
        var decomposed = text.ToLowerInvariant().Normalize(NormalizationForm.FormD);
        var sb = new StringBuilder(decomposed.Length);
        foreach (var c in decomposed)
        {
            if (CharUnicodeInfo.GetUnicodeCategory(c) == UnicodeCategory.NonSpacingMark) continue;
            if (c is '\'' or '’' or '`') continue;                       // can't -> cant
            sb.Append(char.IsLetterOrDigit(c) ? c : ' ');
        }
        // Letters repeated 3+ times collapse to one ("damuuu" -> "damu", "sawaaa" -> "sawa").
        // Runs of two are kept: Swahili has real double vowels ("kaa", "ndoo").
        var collapsed = new StringBuilder(sb.Length);
        var k = 0;
        while (k < sb.Length)
        {
            var run = 1;
            while (k + run < sb.Length && sb[k + run] == sb[k]) run++;
            collapsed.Append(sb[k], run >= 3 && char.IsLetter(sb[k]) ? 1 : run);
            k += run;
        }
        return string.Join(' ', collapsed.ToString().Split(' ', StringSplitOptions.RemoveEmptyEntries));
    }
}
