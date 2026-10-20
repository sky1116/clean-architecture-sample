namespace Competition.Domain;

/// <summary>A stable rule code; no HTTP, database or presentation terminology enters the domain.</summary>
public sealed class DomainRuleException(string code) : Exception(code)
{
    public string Code { get; } = code;
}
