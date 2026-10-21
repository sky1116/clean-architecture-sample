using Competition.Adapters.Rest;
using Competition.Application.UseCases;
using Competition.Domain.Results;
using Competition.Infrastructure.Persistence;
using System.Reflection;
using Xunit;

namespace Competition.Architecture.Tests;

public sealed class DependencyRuleTests
{
    [Fact]
    public void All_production_projects_must_use_the_exact_inward_project_graph()
    {
        var problems = ProjectGraphPolicy.ValidateRepository(LocateRepository());
        Assert.True(problems.Count == 0, string.Join(Environment.NewLine, problems));
    }

    [Fact]
    public void Domain_and_application_compiled_assemblies_cannot_reference_outer_frameworks()
    {
        foreach (var assembly in new[]
                 { typeof(VerifiedResult).Assembly, typeof(SubmitResultInteractor).Assembly })
        {
            var actual = assembly.GetReferencedAssemblies().Select(x => x.Name ?? "").ToArray();
            Assert.DoesNotContain(actual, name =>
                name.StartsWith("Competition.Adapters", StringComparison.Ordinal) ||
                name.StartsWith("Competition.Infrastructure", StringComparison.Ordinal) ||
                name.StartsWith("Competition.Web", StringComparison.Ordinal) ||
                name.StartsWith("Microsoft.EntityFrameworkCore", StringComparison.Ordinal) ||
                name.StartsWith("Microsoft.AspNetCore", StringComparison.Ordinal) ||
                name.StartsWith("Npgsql", StringComparison.Ordinal));
        }
        Assert.DoesNotContain(typeof(VerifiedResult).Assembly.GetReferencedAssemblies(),
            x => x.Name == "Competition.Application");
        Assert.Contains(typeof(SubmitResultInteractor).Assembly.GetReferencedAssemblies(),
            x => x.Name == "Competition.Domain");
    }

    [Fact]
    public void Concrete_gateway_and_presenter_implementations_live_outside_the_use_cases()
    {
        Assert.Equal("Competition.Infrastructure", typeof(PostgresResultGateway).Assembly.GetName().Name);
        Assert.Equal("Competition.Adapters", typeof(RestPresenter).Assembly.GetName().Name);
        Assert.Equal("Competition.Application", typeof(SubmitResultInteractor).Assembly.GetName().Name);
    }

    [Fact]
    public void Negative_control_mutates_real_domain_project_then_fails_the_same_verifier()
    {
        var root = LocateRepository();
        var path = Path.Combine(root, "src", "Competition.Domain", "Competition.Domain.csproj");
        var actual = File.ReadAllText(path);
        Assert.Empty(ProjectGraphPolicy.ValidateProjectXml(root, "Competition.Domain", actual));

        var document = System.Xml.Linq.XDocument.Parse(actual);
        document.Root!.Add(new System.Xml.Linq.XElement("ItemGroup",
            new System.Xml.Linq.XElement("ProjectReference",
                new System.Xml.Linq.XAttribute("Include",
                    "../Competition.Infrastructure/Competition.Infrastructure.csproj"))));
        var mutated = document.ToString();
        Assert.NotEqual(actual, mutated);
        Assert.Contains(ProjectGraphPolicy.ValidateProjectXml(root, "Competition.Domain", mutated),
            problem => problem.Contains("forbidden project reference", StringComparison.Ordinal));
    }

    [Fact]
    public void Negative_control_must_catch_new_framework_package_inside_application()
    {
        var root = LocateRepository();
        var path = Path.Combine(root, "src", "Competition.Application", "Competition.Application.csproj");
        var actual = File.ReadAllText(path);
        Assert.Empty(ProjectGraphPolicy.ValidateProjectXml(root, "Competition.Application", actual));
        var mutated = actual.Replace("</Project>", """
            <ItemGroup><PackageReference Include="Microsoft.EntityFrameworkCore" Version="10.0.0" /></ItemGroup>
            </Project>
            """);
        Assert.NotEqual(actual, mutated);
        Assert.Contains(ProjectGraphPolicy.ValidateProjectXml(root, "Competition.Application", mutated),
            problem => problem.Contains("added package", StringComparison.Ordinal));
    }

    [Fact]
    public void Negative_control_does_not_allow_unregistered_production_projects()
    {
        var root = LocateRepository();
        var failures = ProjectGraphPolicy.ValidateProjectXml(root, "Competition.Sneaky", "<Project />");
        Assert.Contains(failures, x => x.Contains("Unknown project", StringComparison.Ordinal));
    }

    private static string LocateRepository()
    {
        var current = new DirectoryInfo(AppContext.BaseDirectory);
        while (current is not null)
        {
            if (File.Exists(Path.Combine(current.FullName, "Directory.Build.props")))
                return current.FullName;
            current = current.Parent;
        }
        throw new InvalidOperationException("Repository root not found.");
    }
}
