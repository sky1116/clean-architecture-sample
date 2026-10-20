namespace Competition.Domain.Results;

/// <summary>
/// Business transition policy for a consumer-side score projection.
/// Infrastructure must not invent its own revision or correction rules.
/// </summary>
public static class ReceivedResultPolicy
{
    public sealed record State(Guid ParticipantId, int EffectivePoints, int Revision);
    public enum DecisionKind { Applied, DependencyMissing, Conflict }
    public sealed record Decision(DecisionKind Kind, State? Next);

    public static Decision Decide(State? current, bool isInitialResult,
        Guid? participantId, int points, int revision)
    {
        if (points is < 0 or > 1_000_000)
            throw new DomainRuleException("result.points_out_of_range");

        if (isInitialResult)
        {
            if (revision != 0 || participantId is null || participantId == Guid.Empty)
                throw new DomainRuleException("result.invalid_initial_transition");
            return current is null
                ? new(DecisionKind.Applied, new State(participantId.Value, points, 0))
                : new(DecisionKind.Conflict, null);
        }

        if (revision < 1 || participantId is not null)
            throw new DomainRuleException("result.invalid_correction_transition");

        if (current is null || current.Revision < revision - 1)
            return new(DecisionKind.DependencyMissing, null);
        if (current.Revision >= revision)
            return new(DecisionKind.Conflict, null);

        return new(DecisionKind.Applied,
            new State(current.ParticipantId, points, revision));
    }
}
