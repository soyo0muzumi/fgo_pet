using Xunit;
using System.Collections.Immutable;
using System.Text.Json;

namespace FgoPet.Architecture.Tests;

public sealed class ArchitecturePolicyReaderTests
{
    [Fact]
    public void Valid_policy_preserves_document_order_and_all_fields()
    {
        var policy = Read(ValidPolicy);

        Assert.Equal(1, policy.SchemaVersion);
        Assert.Collection(policy.Projects,
            project =>
            {
                Assert.Equal("modules/example/src/Example.Contracts/Example.Contracts.csproj", project.Path);
                Assert.Equal("example", project.Module);
                Assert.Equal(ArchitectureLayer.Contract, project.Layer);
                Assert.Equal(ArchitectureRole.Production, project.Role);
            },
            project => Assert.Equal("tests/Example.Architecture.Tests/Example.Architecture.Tests.csproj", project.Path));
        var rule = Assert.Single(policy.DependencyRules);
        Assert.Equal("example-implementation-to-contract", rule.Id);
        Assert.Equal(new[] { "example", "shared" }, rule.FromModules);
        Assert.Equal(new[] { ArchitectureLayer.Implementation }, rule.FromLayers);
        Assert.Equal(new[] { "example" }, rule.ToModules);
        Assert.Equal(new[] { ArchitectureLayer.Contract }, rule.ToLayers);
        Assert.Equal(new[] { ArchitectureDependencyKind.Compile, ArchitectureDependencyKind.Companion }, rule.Kinds);
    }

    [Theory]
    [InlineData("root", "unknown")]
    [InlineData("project", "unknown")]
    [InlineData("rule", "unknown")]
    public void Unknown_properties_fail_at_each_object_level(string level, string property)
    {
        var json = level switch
        {
            "root" => ValidPolicy.Replace("\"projects\"", "\"unknown\":true,\"projects\""),
            "project" => ValidPolicy.Replace("\"module\":\"example\"", "\"unknown\":true,\"module\":\"example\""),
            _ => ValidPolicy.Replace("\"id\":\"example-implementation-to-contract\"", "\"unknown\":true,\"id\":\"example-implementation-to-contract\""),
        };

        var error = Assert.Throws<ArchitecturePolicyException>(() => Read(json));

        Assert.Contains(property, error.Message);
        Assert.Contains("source.json", error.Message);
    }

    [Fact]
    public void Duplicate_json_property_names_fail()
    {
        var json = ValidPolicy.Replace("\"schemaVersion\": 1", "\"schemaVersion\": 1,\"schemaVersion\": 1");

        Assert.Throws<ArchitecturePolicyException>(() => Read(json));
    }

    [Theory]
    [InlineData("{\"schemaVersion\":1,\"projects\":[],\"dependencyRules\":[]}", "projects")]
    [InlineData("{\"schemaVersion\":1,\"projects\":null,\"dependencyRules\":[]}", "projects")]
    [InlineData("{\"schemaVersion\":1,\"projects\":[{\"path\":\"x.csproj\",\"module\":\"x\",\"layer\":\"contract\"}],\"dependencyRules\":[]}", "role")]
    public void Missing_null_or_empty_required_values_fail(string json, string field)
    {
        var error = Assert.Throws<ArchitecturePolicyException>(() => Read(json));

        Assert.Contains(field, error.Message);
    }

    [Theory]
    [InlineData("schemaVersion", "\"schemaVersion\":2")]
    [InlineData("layer", "\"layer\":\"wrong\"")]
    [InlineData("role", "\"role\":\"wrong\"")]
    [InlineData("kinds", "\"kinds\":[\"wildcard\"]")]
    [InlineData("module", "\"module\":\"Bad_Module\"")]
    [InlineData("id", "\"id\":\"bad_id\"")]
    public void Invalid_schema_values_fail(string field, string replacement)
    {
        var original = field switch
        {
            "schemaVersion" => "\"schemaVersion\": 1",
            "layer" => "\"layer\":\"contract\"",
            "role" => "\"role\":\"production\"",
            "module" => "\"module\":\"example\"",
            "id" => "\"id\":\"example-implementation-to-contract\"",
            _ => "\"kinds\":[\"compile\",\"companion\"]",
        };
        var json = ValidPolicy.Replace(original, replacement);

        var error = Assert.Throws<ArchitecturePolicyException>(() => Read(json));

        Assert.Contains(field, error.Message);
    }

    [Fact]
    public void Invalid_path_forms_fail()
    {
        foreach (var path in new[] { "C:/absolute.csproj", @"modules\example.csproj", "modules//example.csproj", "modules/../example.csproj" })
        {
            var encodedPath = path.Replace("\\", "\\\\", StringComparison.Ordinal);
            var json = ValidPolicy.Replace(
                "\"path\":\"modules/example/src/Example.Contracts/Example.Contracts.csproj\"",
                $"\"path\":\"{encodedPath}\"");

            var error = Assert.Throws<ArchitecturePolicyException>(() => Read(json));
            Assert.Contains("path", error.Message);
        }
    }

    [Theory]
    [InlineData("projects")]
    [InlineData("rule-id")]
    [InlineData("fromModules")]
    [InlineData("fromLayers")]
    [InlineData("toModules")]
    [InlineData("toLayers")]
    [InlineData("kinds")]
    public void Case_insensitive_duplicates_fail(string _)
    {
        var json = _ == "projects"
            ? ValidPolicy.Replace(
                "\"path\":\"tests/Example.Architecture.Tests/Example.Architecture.Tests.csproj\"",
                "\"path\":\"modules/example/src/Example.Contracts/Example.Contracts.csproj\"")
            : _ == "rule-id"
                ? ValidPolicy.Replace(
                    "{" + FullRuleJson("example-implementation-to-contract") + "}",
                    "{" + FullRuleJson("example-implementation-to-contract") + "},{" + FullRuleJson("EXAMPLE-IMPLEMENTATION-TO-CONTRACT") + "}")
                : ReplaceDuplicateRuleEntry(ValidPolicy, _);

        Assert.Throws<ArchitecturePolicyException>(() => Read(json));
    }

    [Fact]
    public void Exception_identifies_source_and_location_without_document_sentinel()
    {
        var json = ValidPolicy.Replace("\"schemaVersion\": 1", "\"schemaVersion\": 9")
            .Replace("modules/example/src/Example.Contracts/Example.Contracts.csproj", "PRIVATE-SENTINEL.csproj");

        var error = Assert.Throws<ArchitecturePolicyException>(() => Read(json));

        Assert.Contains("source.json", error.Message);
        Assert.Contains("schemaVersion", error.Message);
        Assert.DoesNotContain("PRIVATE-SENTINEL", error.Message);
    }

    [Fact]
    public void Parsed_collections_are_immutable_at_every_policy_level()
    {
        var policy = Read(ValidPolicy);

        Assert.IsType<ImmutableArray<ArchitectureProject>>(policy.Projects);
        Assert.IsType<ImmutableArray<ArchitectureDependencyRule>>(policy.DependencyRules);
        var rule = Assert.Single(policy.DependencyRules);
        Assert.IsType<ImmutableArray<string>>(rule.FromModules);
        Assert.IsType<ImmutableArray<ArchitectureLayer>>(rule.FromLayers);
        Assert.IsType<ImmutableArray<string>>(rule.ToModules);
        Assert.IsType<ImmutableArray<ArchitectureLayer>>(rule.ToLayers);
        Assert.IsType<ImmutableArray<ArchitectureDependencyKind>>(rule.Kinds);
    }

    [Theory]
    [InlineData("module", "example\n")]
    [InlineData("id", "example-implementation-to-contract\n")]
    public void Identifiers_reject_trailing_control_characters(string field, string value)
    {
        var original = field == "module" ? "\"module\":\"example\"" : "\"id\":\"example-implementation-to-contract\"";
        var encoded = JsonSerializer.Serialize(value);
        var json = ValidPolicy.Replace(original, $"\"{field}\":{encoded}");

        var error = Assert.Throws<ArchitecturePolicyException>(() => Read(json));

        Assert.Contains(field, error.Message);
        Assert.DoesNotContain("malformed JSON", error.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Project_paths_differing_only_by_case_are_duplicate_ownership()
    {
        var json = ValidPolicy.Replace(
            "tests/Example.Architecture.Tests/Example.Architecture.Tests.csproj",
            "MODULES/EXAMPLE/SRC/EXAMPLE.CONTRACTS/EXAMPLE.CONTRACTS.csproj");

        var error = Assert.Throws<ArchitecturePolicyException>(() => Read(json));

        Assert.Contains("duplicate project paths", error.Message);
    }

    [Fact]
    public void Project_path_requires_exact_lowercase_csproj_suffix()
    {
        var json = ValidPolicy.Replace("Example.Contracts.csproj", "Example.Contracts.CSPROJ");

        var error = Assert.Throws<ArchitecturePolicyException>(() => Read(json));

        Assert.Contains("path", error.Message);
    }

    private static ArchitecturePolicy Read(string json) => new StrictJsonArchitecturePolicyReader().Read(json, "source.json");

    private static string ReplaceDuplicateRuleEntry(string json, string field)
    {
        return field switch
        {
            "fromModules" => json.Replace("\"fromModules\":[\"example\",\"shared\"]", "\"fromModules\":[\"example\",\"EXAMPLE\"]"),
            "fromLayers" => json.Replace("\"fromLayers\":[\"implementation\"]", "\"fromLayers\":[\"implementation\",\"IMPLEMENTATION\"]"),
            "toModules" => json.Replace("\"toModules\":[\"example\"]", "\"toModules\":[\"example\",\"EXAMPLE\"]"),
            "toLayers" => json.Replace("\"toLayers\":[\"contract\"]", "\"toLayers\":[\"contract\",\"CONTRACT\"]"),
            _ => json.Replace("\"kinds\":[\"compile\",\"companion\"]", "\"kinds\":[\"compile\",\"COMPILE\"]"),
        };
    }

    private static string FullRuleJson(string id) => $"\"id\":\"{id}\",\"fromModules\":[\"example\",\"shared\"],\"fromLayers\":[\"implementation\"],\"toModules\":[\"example\"],\"toLayers\":[\"contract\"],\"kinds\":[\"compile\",\"companion\"]";

    private const string ValidPolicy = """
        {
          "schemaVersion": 1,
          "projects": [
            {"path":"modules/example/src/Example.Contracts/Example.Contracts.csproj","module":"example","layer":"contract","role":"production"},
            {"path":"tests/Example.Architecture.Tests/Example.Architecture.Tests.csproj","module":"example","layer":"architecture-test","role":"test"}
          ],
          "dependencyRules": [
            {"id":"example-implementation-to-contract","fromModules":["example","shared"],"fromLayers":["implementation"],"toModules":["example"],"toLayers":["contract"],"kinds":["compile","companion"]}
          ]
        }
        """;
}
