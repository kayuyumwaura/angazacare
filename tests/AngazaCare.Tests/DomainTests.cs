using AngazaCare.Domain;
using AngazaCare.Services;
using Xunit;

namespace AngazaCare.Tests;

public class FlagTransitionTests
{
    [Fact]
    public void Chw_can_acknowledge_and_assess_but_never_decide()
    {
        Assert.Equal(FlagStates.Acknowledged, FlagTransitions.Target(FlagAction.Acknowledge, FlagStates.Open, Roles.Chw));
        Assert.Equal(FlagStates.Assessed, FlagTransitions.Target(FlagAction.Assess, FlagStates.Acknowledged, Roles.Chw));
        Assert.Equal(FlagStates.Assessed, FlagTransitions.Target(FlagAction.Assess, FlagStates.Escalated, Roles.Chw));
        Assert.Null(FlagTransitions.Target(FlagAction.Approve, FlagStates.Assessed, Roles.Chw));
        Assert.Null(FlagTransitions.Target(FlagAction.Reject, FlagStates.Assessed, Roles.Chw));
    }

    [Theory]
    [InlineData(FlagStates.Open)]
    [InlineData(FlagStates.Acknowledged)]
    [InlineData(FlagStates.Assessed)]
    [InlineData(FlagStates.Escalated)]
    public void Nurse_can_decide_any_live_case(string state)
    {
        Assert.Equal(FlagStates.Approved, FlagTransitions.Target(FlagAction.Approve, state, Roles.Nurse));
        Assert.Equal(FlagStates.Rejected, FlagTransitions.Target(FlagAction.Reject, state, Roles.Nurse));
    }

    [Fact]
    public void The_system_can_only_escalate_and_only_before_a_visit()
    {
        Assert.Equal(FlagStates.Escalated, FlagTransitions.Target(FlagAction.Escalate, FlagStates.Open, null));
        Assert.Equal(FlagStates.Escalated, FlagTransitions.Target(FlagAction.Escalate, FlagStates.Acknowledged, null));
        Assert.Null(FlagTransitions.Target(FlagAction.Escalate, FlagStates.Assessed, null));
        Assert.Null(FlagTransitions.Target(FlagAction.Approve, FlagStates.Assessed, null));
        Assert.Null(FlagTransitions.Target(FlagAction.Reject, FlagStates.Open, null));
    }

    [Theory]
    [InlineData(FlagStates.Approved)]
    [InlineData(FlagStates.Rejected)]
    public void Closed_cases_cannot_move(string state)
    {
        foreach (var action in Enum.GetValues<FlagAction>())
            foreach (var role in new string?[] { Roles.Chw, Roles.Nurse, Roles.Admin, null })
                Assert.Null(FlagTransitions.Target(action, state, role));
    }

    [Fact]
    public void Admin_cannot_approve_referrals()
    {
        Assert.Null(FlagTransitions.Target(FlagAction.Approve, FlagStates.Assessed, Roles.Admin));
    }
}

public class DomainTests
{
    [Theory]
    [InlineData("0712345678", "+254712345678")]
    [InlineData("0112 345 678", "+254112345678")]
    [InlineData("254712345678", "+254712345678")]
    [InlineData("+254 712 345 678", "+254712345678")]
    [InlineData("712345678", "+254712345678")]
    [InlineData("12345", null)]
    [InlineData("", null)]
    public void Kenyan_numbers_normalise_to_e164(string raw, string? expected)
    {
        Assert.Equal(expected, PhoneProtector.Normalize(raw));
    }

    [Fact]
    public void Passwords_verify_and_reject()
    {
        var hash = PasswordHasher.Hash("correct horse");
        Assert.True(PasswordHasher.Verify("correct horse", hash));
        Assert.False(PasswordHasher.Verify("wrong horse", hash));
        Assert.NotEqual(hash, PasswordHasher.Hash("correct horse"));
    }

    [Theory]
    [InlineData(0, 40)]
    [InlineData(7, 39)]
    [InlineData(84, 28)]
    [InlineData(280, 0)]
    public void Gestation_from_due_date(int daysToEdd, int weeks)
    {
        var today = new DateOnly(2026, 10, 16);
        Assert.Equal(weeks, Gestation.WeeksFromEdd(today.AddDays(daysToEdd), today));
    }

    [Fact]
    public void Due_date_is_280_days_after_last_period()
    {
        Assert.Equal(new DateOnly(2027, 7, 23), Gestation.EddFromLmp(new DateOnly(2026, 10, 16)));
    }

    [Fact]
    public void Severity_max_prefers_high()
    {
        Assert.Equal("high", Severity.Max(["low", "high", "medium"]));
        Assert.Equal("low", Severity.Max([]));
    }
}
