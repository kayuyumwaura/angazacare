using AngazaCare.Services;
using Xunit;

namespace AngazaCare.Tests;

public class RiskEngineTests
{
    private static readonly RiskEngine Engine = new(SeedData.Signs, SeedData.Lexicon);

    [Fact]
    public void Seed_file_loads()
    {
        Assert.Equal(10, SeedData.Signs.Count);
        Assert.True(SeedData.Lexicon.Count > 100);
    }

    [Theory]
    [InlineData("1")]
    [InlineData("niko sawa")]
    [InlineData("Niko poa sana, asante!")]
    [InlineData("fiti tu")]
    [InlineData("I'm fine thank you")]
    [InlineData("sawaaaa")]
    [InlineData("sina damu, niko poa")]
    [InlineData("asante sana")]
    public void Well_replies_raise_no_flag(string reply)
    {
        var r = Engine.Evaluate(reply);
        Assert.Equal(Outcome.Well, r.Outcome);
        Assert.Empty(r.Signs);
    }

    [Theory]
    [InlineData("2", "BLEED", "menu")]
    [InlineData("4", "FETALMOV", "menu")]
    [InlineData("Natoka damu kidogo", "BLEED", "lexicon")]
    [InlineData("damuuu", "BLEED", "lexicon")]
    [InlineData("kichwa inaniuma sana", "HEADVIS", "lexicon")]
    [InlineData("my head is paining", "HEADVIS", "lexicon")]
    [InlineData("baby hachezi tangu jana", "FETALMOV", "lexicon")]
    [InlineData("mwili inachemka", "FEVER", "lexicon")]
    [InlineData("chupa imepasuka", "FLUID", "lexicon")]
    [InlineData("alipata degedege", "CONVULS", "lexicon")]
    [InlineData("tumbo inaniua", "ABDPAIN", "lexicon")]
    [InlineData("face imeswell", "SWELL", "lexicon")]
    public void Danger_replies_raise_the_right_sign(string reply, string sign, string method)
    {
        var r = Engine.Evaluate(reply);
        Assert.Equal(Outcome.Danger, r.Outcome);
        Assert.Contains(sign, r.Signs);
        Assert.Equal(method, r.Method);
        Assert.False(r.NeedsModel);
    }

    [Theory]
    [InlineData("niko sawa lakini damu inatoka")]
    [InlineData("1 2")]
    [InlineData("fine but bleeding")]
    [InlineData("poa, ila mtoto hachezi")]
    public void A_danger_sign_always_wins_over_well_words(string reply)
    {
        Assert.Equal(Outcome.Danger, Engine.Evaluate(reply).Outcome);
    }

    [Fact]
    public void Several_menu_numbers_give_several_signs()
    {
        var r = Engine.Evaluate("2, 5");
        Assert.Equal(["BLEED", "FEVER"], r.Signs.OrderBy(s => s));
    }

    [Fact]
    public void Menu_number_plus_text_reads_both()
    {
        var r = Engine.Evaluate("3 na homa pia");
        Assert.Contains("HEADVIS", r.Signs);
        Assert.Contains("FEVER", r.Signs);
    }

    [Theory]
    [InlineData("sina damu")]          // negated danger, nothing else: a person or the model checks
    [InlineData("si sawa")]            // negated well-word
    [InlineData("not good")]
    [InlineData("najisikia vibaya tu")]
    [InlineData("niko sawa lakini nimechoka sana")]
    public void Unclear_replies_go_to_the_model_or_a_person(string reply)
    {
        var r = Engine.Evaluate(reply);
        Assert.Equal(Outcome.Unclear, r.Outcome);
        Assert.True(r.NeedsModel);
        Assert.Equal([RiskEngine.Unclear], r.Signs);
    }

    [Fact]
    public void Menu_other_without_text_is_unclear_and_needs_no_model()
    {
        var r = Engine.Evaluate("8");
        Assert.Equal(Outcome.Unclear, r.Outcome);
        Assert.False(r.NeedsModel);
    }

    [Fact]
    public void Menu_other_with_text_lets_the_model_read_the_text()
    {
        var r = Engine.Evaluate("8 najisikia vibaya");
        Assert.Equal(Outcome.Unclear, r.Outcome);
        Assert.True(r.NeedsModel);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("?!")]
    public void Empty_replies_are_unclear(string reply)
    {
        var r = Engine.Evaluate(reply);
        Assert.Equal(Outcome.Unclear, r.Outcome);
        Assert.False(r.NeedsModel);
    }

    [Fact]
    public void Number_not_on_the_menu_is_unclear()
    {
        Assert.Equal(Outcome.Unclear, Engine.Evaluate("9").Outcome);
    }

    [Theory]
    [InlineData("Can't  SEE well!!", "cant see well")]
    [InlineData("Damu—nyingi", "damu nyingi")]
    [InlineData("kaa", "kaa")]
    public void Normalize_handles_case_punctuation_and_repeats(string input, string expected)
    {
        Assert.Equal(expected, RiskEngine.Normalize(input));
    }
}

public class ModelGuardTests
{
    private static readonly RiskEngine Engine = new(SeedData.Signs, SeedData.Lexicon);

    [Theory]
    [InlineData("niko safi kabisa ila nimechoka", false)]
    [InlineData("sina damu", false)]
    [InlineData("najisikia vibaya tu", true)]
    public void Model_may_only_clear_replies_without_a_but_or_a_denial(string reply, bool mayClear)
    {
        var r = Engine.Evaluate(reply);
        Assert.True(r.NeedsModel);
        Assert.Equal(mayClear, r.ModelMayClear);
    }
}
