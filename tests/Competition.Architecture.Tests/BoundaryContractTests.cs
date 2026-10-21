using Competition.Adapters.Rest;
using Competition.Application.UseCases;
using System.Reflection;
using Xunit;

namespace Competition.Architecture.Tests;

/// <summary>
/// Guard the shapes of the input/output seams: no EF entities, ASP.NET DTOs,
/// presenters or business entities pass through application boundary payloads.
/// </summary>
public sealed class BoundaryContractTests
{
    private static readonly Type[] InputBoundaries =
    [
        typeof(ICreateCompetitionInputBoundary),
        typeof(IRegisterParticipantInputBoundary),
        typeof(ISubmitResultInputBoundary),
        typeof(ICorrectResultInputBoundary),
        typeof(IGetLeaderboardInputBoundary),
        typeof(IGetPersonalHistoryInputBoundary),
        typeof(IReceiveResultEventInputBoundary),
        typeof(IRedriveOutboxInputBoundary)
    ];

    private static readonly Type[] OutputBoundaries =
    [
        typeof(ICreateCompetitionOutputBoundary),
        typeof(IRegisterParticipantOutputBoundary),
        typeof(ISubmitResultOutputBoundary),
        typeof(ICorrectResultOutputBoundary),
        typeof(IGetLeaderboardOutputBoundary),
        typeof(IGetPersonalHistoryOutputBoundary),
        typeof(IReceiveResultEventOutputBoundary),
        typeof(IRedriveOutboxOutputBoundary)
    ];

    [Fact]
    public void Every_use_case_has_a_primitive_input_and_output_boundary()
    {
        Assert.Equal(InputBoundaries.Length, OutputBoundaries.Length);
        for (var i = 0; i < InputBoundaries.Length; i++)
        {
            var input = InputBoundaries[i].GetMethods(BindingFlags.Public | BindingFlags.Instance);
            var execute = Assert.Single(input);
            Assert.Equal("ExecuteAsync", execute.Name);
            Assert.Equal(typeof(Task), execute.ReturnType);
            var parameters = execute.GetParameters();
            Assert.Equal(3, parameters.Length);
            Assert.Equal(typeof(CancellationToken), parameters[2].ParameterType);
            Assert.Equal(OutputBoundaries[i], parameters[1].ParameterType);
            AssertOwnedBoundaryData(parameters[0].ParameterType);

            var present = Assert.Single(OutputBoundaries[i].GetMethods());
            Assert.Equal("Present", present.Name);
            Assert.Equal(typeof(void), present.ReturnType);
            AssertOwnedBoundaryData(Assert.Single(present.GetParameters()).ParameterType);
            Assert.Contains(OutputBoundaries[i], typeof(RestPresenter).GetInterfaces());
        }
    }

    [Fact]
    public void All_controller_constructor_dependencies_are_input_boundary_interfaces()
    {
        var constructor = Assert.Single(typeof(CompetitionController).GetConstructors());
        var parameterTypes = constructor.GetParameters().Select(x => x.ParameterType).ToArray();
        Assert.Equal(InputBoundaries.Length, parameterTypes.Length);
        foreach (var type in parameterTypes)
            Assert.Contains(type, InputBoundaries);
        Assert.DoesNotContain(parameterTypes, x => x.Assembly.GetName().Name is
            "Competition.Infrastructure" or "Competition.Web");
    }

    [Fact]
    public void Boundary_type_verifier_rejects_a_domain_entity_or_external_driver()
    {
        Assert.False(IsPlainData(typeof(Competition.Domain.Competitions.Competition)));
        Assert.False(IsPlainData(typeof(Competition.Infrastructure.Persistence.ResultRow)));
        Assert.True(IsPlainData(typeof(SubmitResultInput)));
        Assert.True(IsPlainData(typeof(GetLeaderboardOutput)));
    }

    private static void AssertOwnedBoundaryData(Type type) =>
        Assert.True(IsPlainData(type), $"Boundary contains non-plain data: {type.FullName}");

    private static bool IsPlainData(Type type)
    {
        if (type.IsArray) return IsPlainData(type.GetElementType()!);
        if (Nullable.GetUnderlyingType(type) is { } nullable) return IsPlainData(nullable);
        if (type.IsGenericType && type.GetGenericTypeDefinition() == typeof(IReadOnlyList<>))
            return IsPlainData(type.GetGenericArguments()[0]);
        if (type == typeof(string) || type == typeof(Guid) ||
            type == typeof(DateTimeOffset) || type.IsPrimitive)
            return true;

        if (type.Assembly != typeof(SubmitResultInput).Assembly ||
            type.Namespace != typeof(SubmitResultInput).Namespace ||
            !type.IsClass || !type.IsSealed ||
            type.GetMethod("<Clone>$", BindingFlags.Instance | BindingFlags.NonPublic) is null)
            return false;

        var props = type.GetProperties(BindingFlags.Public | BindingFlags.Instance);
        return props.Length > 0 && props.All(property => IsPlainData(property.PropertyType));
    }
}
