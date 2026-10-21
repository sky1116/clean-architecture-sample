using Competition.Domain.Results;
using Xunit;

namespace Competition.Domain.Tests;

public sealed class CorrectionRevisionPolicyTests
{
    [Theory]
    [InlineData(0, 0, 1)]
    [InlineData(1, 1, 2)]
    [InlineData(12, 12, 13)]
    public void Current_expected_revision_is_eligible_for_the_next_version(
        int current, int expected, int next)
    {
        Assert.Equal(next, CorrectionRevisionPolicy.NextRevision(current, expected));
    }

    [Theory]
    [InlineData(0, 1)]
    [InlineData(2, 1)]
    [InlineData(-1, -1)]
    [InlineData(1, -1)]
    [InlineData(int.MaxValue, int.MaxValue)]
    public void Stale_future_or_invalid_revisions_must_not_advance(int current, int expected)
    {
        Assert.Null(CorrectionRevisionPolicy.NextRevision(current, expected));
    }
}
