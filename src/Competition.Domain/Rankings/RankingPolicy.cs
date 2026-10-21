using Competition.Domain.Results;

namespace Competition.Domain.Rankings;

public sealed record Standing(Guid ParticipantId, long TotalPoints, int Rank, int ResultCount);

/// <summary>Competition ranking: ties share a rank and cause gaps (1, 1, 3), stable by participant ID.</summary>
public static class RankingPolicy
{
    public static IReadOnlyList<Standing> Calculate(IEnumerable<VerifiedResult> verifiedResults)
    {
        ArgumentNullException.ThrowIfNull(verifiedResults);
        var totals = verifiedResults.GroupBy(x => x.ParticipantId)
            .Select(group => new
            {
                ParticipantId = group.Key,
                Points = group.Aggregate(0L, (total, result) => checked(total + result.Points)),
                Count = group.Count()
            })
            .OrderByDescending(x => x.Points)
            .ThenBy(x => x.ParticipantId)
            .ToArray();

        var standings = new List<Standing>(totals.Length);
        long? previous = null;
        int rank = 0;
        for (var index = 0; index < totals.Length; index++)
        {
            var row = totals[index];
            if (previous != row.Points) rank = index + 1;
            standings.Add(new Standing(row.ParticipantId, row.Points, rank, row.Count));
            previous = row.Points;
        }
        return standings;
    }
}
