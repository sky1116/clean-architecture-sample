using System.Xml.Linq;

namespace Competition.Architecture.Tests;

/// <summary>
/// One testable verifier for the real project files AND deliberate negative controls.
/// Project graph checks use exact resolved paths so spoofed file names cannot bypass policy.
/// </summary>
internal static class ProjectGraphPolicy
{
    private static readonly IReadOnlyDictionary<string, string[]> Expected =
        new Dictionary<string, string[]>(StringComparer.Ordinal)
        {
            ["Competition.Domain"] = [],
            ["Competition.Application"] = ["Competition.Domain"],
            ["Competition.Adapters"] = ["Competition.Application"],
            ["Competition.Infrastructure"] = ["Competition.Application"],
            ["Competition.Web"] = ["Competition.Adapters", "Competition.Infrastructure"]
        };

    internal static IReadOnlyList<string> ValidateRepository(string root)
    {
        var errors = new List<string>();
        var source = Path.Combine(root, "src");
        var directories = Directory.GetDirectories(source, "Competition.*")
            .Select(Path.GetFileName).Where(x => x is not null).Select(x => x!).ToArray();
        foreach (var extra in directories.Except(Expected.Keys, StringComparer.Ordinal))
            errors.Add("Unexpected source project: " + extra);
        foreach (var project in Expected.Keys)
        {
            var path = Path.Combine(source, project, project + ".csproj");
            if (!File.Exists(path))
            {
                errors.Add(project + ": missing project file");
                continue;
            }
            errors.AddRange(ValidateProjectXml(root, project, File.ReadAllText(path)));
        }
        return errors;
    }

    internal static IReadOnlyList<string> ValidateProjectXml(string root, string project, string xml)
    {
        var failures = new List<string>();
        if (!Expected.TryGetValue(project, out var allowed))
            return ["Unknown project: " + project];

        XDocument document;
        try { document = XDocument.Parse(xml); }
        catch (System.Xml.XmlException) { return [project + ": malformed project XML"]; }

        var projectDir = Path.Combine(root, "src", project);
        var actual = new HashSet<string>(StringComparer.Ordinal);
        foreach (var reference in document.Descendants("ProjectReference"))
        {
            var include = (string?)reference.Attribute("Include") ?? "";
            if (string.IsNullOrWhiteSpace(include))
            {
                failures.Add(project + ": empty project reference");
                continue;
            }
            var normalized = include.Replace('\\', Path.DirectorySeparatorChar)
                .Replace('/', Path.DirectorySeparatorChar);
            var resolved = Path.GetFullPath(Path.Combine(projectDir, normalized));
            var target = Path.GetFileNameWithoutExtension(resolved);
            var expectedPath = Path.GetFullPath(Path.Combine(root, "src", target, target + ".csproj"));
            if (!Expected.ContainsKey(target) || resolved != expectedPath || !allowed.Contains(target))
                failures.Add(project + ": forbidden project reference " + include);
            actual.Add(target);
        }

        foreach (var missing in allowed.Except(actual, StringComparer.Ordinal))
            failures.Add(project + ": missing inward dependency " + missing);

        if (project is "Competition.Domain" or "Competition.Application" or "Competition.Adapters")
        {
            foreach (var package in document.Descendants("PackageReference"))
                failures.Add(project + ": dependency-free inner layer added package " +
                    ((string?)package.Attribute("Include") ?? "<unnamed>"));
        }
        return failures;
    }
}
