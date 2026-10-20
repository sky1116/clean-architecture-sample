using System.Reflection;
using System.Xml.Linq;
using Competition.Application.UseCases;
using Competition.Domain.Results;
using Competition.Infrastructure.Persistence;
using Xunit;

namespace Competition.Architecture.Tests;

public sealed class DependencyRuleTests
{
    [Fact]
    public void Entities_must_not_reference_application_adapters_or_drivers()
    {
        var dependencies = typeof(VerifiedResult).Assembly.GetReferencedAssemblies()
            .Select(x => x.Name ?? "").ToArray();
        Assert.DoesNotContain(dependencies, IsOuterFramework);
    }

    [Fact]
    public void Use_cases_must_not_reference_interface_adapters_web_or_persistence_drivers()
    {
        var dependencies = typeof(SubmitResultInteractor).Assembly.GetReferencedAssemblies()
            .Select(x => x.Name ?? "").ToArray();
        Assert.DoesNotContain(dependencies, IsOuterFramework);
        Assert.Contains(dependencies, x => x == "Competition.Domain");
    }

    [Fact]
    public void Gateway_concrete_implementation_must_live_outside_application()
    {
        Assert.Equal("Competition.Infrastructure", typeof(PostgresResultGateway).Assembly.GetName().Name);
        Assert.Equal("Competition.Application", typeof(SubmitResultInteractor).Assembly.GetName().Name);
    }

    [Fact]
    public void Project_graph_enforces_inward_only_references()
    {
        var root = LocateRepository();
        var graph = new Dictionary<string, string[]>
        {
            ["Competition.Domain"] = Array.Empty<string>(),
            ["Competition.Application"] = ["Competition.Domain"],
            ["Competition.Adapters"] = ["Competition.Application"],
            ["Competition.Infrastructure"] = ["Competition.Application"],
            ["Competition.Web"] = ["Competition.Adapters", "Competition.Infrastructure"]
        };
        foreach (var (project, allowed) in graph)
        {
            var file = Path.Combine(root, "src", project, project + ".csproj");
            var actual = XDocument.Load(file).Descendants("ProjectReference")
                .Select(x => Path.GetFileNameWithoutExtension(x.Attribute("Include")?.Value ?? ""))
                .ToArray();
            Assert.All(actual, dependency => Assert.Contains(dependency, allowed));
        }
    }

    [Fact]
    public void Negative_control_detects_an_outward_domain_dependency()
    {
        Assert.False(IsAllowed("Competition.Domain", "Competition.Infrastructure"));
        Assert.False(IsAllowed("Competition.Application", "Competition.Web"));
        Assert.True(IsAllowed("Competition.Adapters", "Competition.Application"));
    }

    private static bool IsOuterFramework(string name) =>
        name.StartsWith("Competition.Application", StringComparison.Ordinal)
        || name.StartsWith("Competition.Adapters", StringComparison.Ordinal)
        || name.StartsWith("Competition.Infrastructure", StringComparison.Ordinal)
        || name.StartsWith("Competition.Web", StringComparison.Ordinal)
        || name.StartsWith("Microsoft.EntityFrameworkCore", StringComparison.Ordinal)
        || name.StartsWith("Microsoft.AspNetCore", StringComparison.Ordinal)
        || name.StartsWith("Npgsql", StringComparison.Ordinal);

    private static bool IsAllowed(string source, string target) => source switch
    {
        "Competition.Domain" => false,
        "Competition.Application" => target == "Competition.Domain",
        "Competition.Adapters" or "Competition.Infrastructure" => target == "Competition.Application",
        "Competition.Web" => target is "Competition.Adapters" or "Competition.Infrastructure",
        _ => false
    };

    private static string LocateRepository()
    {
        var path = new DirectoryInfo(AppContext.BaseDirectory);
        while (path is not null)
        {
            if (File.Exists(Path.Combine(path.FullName, "Directory.Build.props")))
                return path.FullName;
            path = path.Parent;
        }
        throw new InvalidOperationException("Cannot locate repository root for architecture checks.");
    }
}
