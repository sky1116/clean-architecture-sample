namespace Competition.Domain.Results;

/// <summary>
/// Business revision rule for authoritative score corrections. Database adapters
/// read the last persisted revision but cannot define a different version policy.
/// </summary>
public static class CorrectionRevisionPolicy
{
    /// <returns>Next revision only if the administrator acted on the current version.</returns>
    public static int? NextRevision(int currentRevision, int expectedRevision)
    {
        if (currentRevision < 0 || expectedRevision < 0 ||
            expectedRevision != currentRevision || currentRevision == int.MaxValue)
            return null;
        return currentRevision + 1;
    }
}
