using Competition.Domain;
using Competition.Domain.Results;
using Xunit;

namespace Competition.Domain.Tests;

public sealed class ReceivedResultPolicyTests
{
    [Fact]
    public void Original_result_and_corrections_follow_explicit_revision_order()
    {
        var player = Guid.NewGuid();
        var first = ReceivedResultPolicy.Decide(null, true, player, 80, 0);
        Assert.Equal(ReceivedResultPolicy.DecisionKind.Applied, first.Kind);
        Assert.NotNull(first.Next);
        Assert.Equal(80, first.Next.EffectivePoints);
        Assert.Equal(0, first.Next.Revision);

        var corrected = ReceivedResultPolicy.Decide(first.Next, false, null, 45, 1);
        Assert.Equal(ReceivedResultPolicy.DecisionKind.Applied, corrected.Kind);
        Assert.Equal(player, corrected.Next?.ParticipantId);
        Assert.Equal(45, corrected.Next?.EffectivePoints);
        Assert.Equal(1, corrected.Next?.Revision);
    }

    [Fact]
    public void Missing_initial_result_or_revision_is_not_an_accepted_transition()
    {
        Assert.Equal(ReceivedResultPolicy.DecisionKind.DependencyMissing,
            ReceivedResultPolicy.Decide(null, false, null, 50, 1).Kind);
        var first = ReceivedResultPolicy.Decide(null, true, Guid.NewGuid(), 80, 0).Next;
        Assert.Equal(ReceivedResultPolicy.DecisionKind.DependencyMissing,
            ReceivedResultPolicy.Decide(first, false, null, 30, 2).Kind);
        Assert.Equal(ReceivedResultPolicy.DecisionKind.Conflict,
            ReceivedResultPolicy.Decide(first, true, Guid.NewGuid(), 80, 0).Kind);
        var revision1 = ReceivedResultPolicy.Decide(first, false, null, 50, 1).Next;
        Assert.Equal(ReceivedResultPolicy.DecisionKind.Conflict,
            ReceivedResultPolicy.Decide(revision1, false, null, 30, 1).Kind);
    }

    [Fact]
    public void Invalid_points_and_identity_are_rejected_in_the_domain()
    {
        Assert.Equal("result.points_out_of_range", Assert.Throws<DomainRuleException>(() =>
            ReceivedResultPolicy.Decide(null, true, Guid.NewGuid(), -1, 0)).Code);
        Assert.Equal("result.invalid_initial_transition", Assert.Throws<DomainRuleException>(() =>
            ReceivedResultPolicy.Decide(null, true, Guid.Empty, 40, 0)).Code);
        Assert.Equal("result.invalid_correction_transition", Assert.Throws<DomainRuleException>(() =>
            ReceivedResultPolicy.Decide(null, false, Guid.NewGuid(), 40, 1)).Code);
    }
}
