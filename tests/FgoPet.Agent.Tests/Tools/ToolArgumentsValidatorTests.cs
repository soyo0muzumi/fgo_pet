using System.Text.Json;
using FgoPet.Kernel.Agent;
using Xunit;

namespace FgoPet.Agent.Tests.Tools;

public sealed class ToolArgumentsValidatorTests
{
    [Theory]
    [InlineData("{\"type\":\"object\",\"required\":[\"id\"]}", "{}", false)]
    [InlineData("{\"type\":\"object\",\"properties\":{\"id\":{\"type\":\"integer\",\"minimum\":1}},\"required\":[\"id\"],\"additionalProperties\":false}", "{\"id\":1}", true)]
    [InlineData("{\"type\":\"object\",\"properties\":{\"id\":{\"type\":\"integer\",\"minimum\":1}},\"additionalProperties\":false}", "{\"id\":0}", false)]
    [InlineData("{\"type\":\"object\",\"properties\":{\"id\":{\"type\":\"integer\"}}}", "{\"id\":1.5}", false)]
    [InlineData("{\"type\":\"object\",\"additionalProperties\":false}", "{\"extra\":true}", false)]
    [InlineData("{\"type\":\"object\",\"properties\":{\"mode\":{\"enum\":[\"a\",\"b\"]}}}", "{\"mode\":\"c\"}", false)]
    [InlineData("{\"type\":\"object\",\"properties\":{\"items\":{\"type\":\"array\",\"items\":{\"type\":\"string\",\"minLength\":2},\"maxItems\":2}}}", "{\"items\":[\"ok\",\"yes\"]}", true)]
    [InlineData("{\"type\":\"object\",\"properties\":{\"items\":{\"type\":\"array\",\"items\":{\"type\":\"string\",\"minLength\":2}}}}", "{\"items\":[\"x\"]}", false)]
    [InlineData("{\"type\":\"object\",\"properties\":{\"nullable\":{\"type\":[\"string\",\"null\"]}}}", "{\"nullable\":null}", true)]
    [InlineData("{\"type\":\"object\"}", "{\"id\":1,\"id\":2}", false)]
    [InlineData("{\"type\":\"object\",\"properties\":{\"child\":{\"type\":\"object\"}}}", "{\"child\":{\"id\":1,\"id\":2}}", false)]
    public void Supported_schema_validates_nested_arguments(string schema, string arguments, bool valid)
    {
        using var document = JsonDocument.Parse(schema);
        using var value = JsonDocument.Parse(arguments);
        Assert.Equal(valid, ToolArgumentsValidator.Validate(document.RootElement, value.RootElement, out _));
    }

    [Theory]
    [InlineData("{\"type\":\"object\",\"$ref\":\"#/definitions/x\"}")]
    [InlineData("{\"type\":\"object\",\"oneOf\":[{}]}")]
    [InlineData("{\"type\":\"object\",\"properties\":{\"unused\":{\"pattern\":\"secret\"}}}")]
    [InlineData("{\"type\":\"imaginary\"}")]
    [InlineData("{\"type\":\"object\",\"required\":\"id\"}")]
    [InlineData("{\"type\":\"object\",\"additionalProperties\":0}")]
    [InlineData("{\"type\":\"object\",\"properties\":{\"unused\":{\"minLength\":-1}}}")]
    public void Unsupported_or_malformed_schema_fails_closed_even_in_unused_branches(string schema)
    {
        using var document = JsonDocument.Parse(schema);
        using var value = JsonDocument.Parse("{}");
        Assert.False(ToolArgumentsValidator.Validate(document.RootElement, value.RootElement, out var error));
        Assert.Equal("TOOL_UNSUPPORTED_SCHEMA", error);
    }
}
