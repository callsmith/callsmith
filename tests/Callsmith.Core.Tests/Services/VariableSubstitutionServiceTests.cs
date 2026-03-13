using Callsmith.Core.Services;
using FluentAssertions;

namespace Callsmith.Core.Tests.Services;

/// <summary>
/// Tests for <see cref="VariableSubstitutionService"/>.
/// </summary>
public sealed class VariableSubstitutionServiceTests
{
    private static readonly IReadOnlyDictionary<string, string> EmptyVars =
        new Dictionary<string, string>();

    private static IReadOnlyDictionary<string, string> Vars(params (string key, string value)[] pairs) =>
        pairs.ToDictionary(p => p.key, p => p.value);

    // ─── Null / empty inputs ─────────────────────────────────────────────────

    [Fact]
    public void Substitute_NullTemplate_ReturnsNull()
    {
        var result = VariableSubstitutionService.Substitute(null, EmptyVars);
        result.Should().BeNull();
    }

    [Fact]
    public void Substitute_EmptyTemplate_ReturnsEmpty()
    {
        var result = VariableSubstitutionService.Substitute(string.Empty, EmptyVars);
        result.Should().BeEmpty();
    }

    // ─── Basic substitution ──────────────────────────────────────────────────

    [Fact]
    public void Substitute_SingleToken_IsReplaced()
    {
        var result = VariableSubstitutionService.Substitute(
            "https://{{host}}/api",
            Vars(("host", "api.example.com")));

        result.Should().Be("https://api.example.com/api");
    }

    [Fact]
    public void Substitute_MultipleTokens_AllReplaced()
    {
        var result = VariableSubstitutionService.Substitute(
            "{{scheme}}://{{host}}/{{path}}",
            Vars(("scheme", "https"), ("host", "example.com"), ("path", "v1/users")));

        result.Should().Be("https://example.com/v1/users");
    }

    [Fact]
    public void Substitute_SameTokenAppearsMultipleTimes_AllOccurrencesReplaced()
    {
        var result = VariableSubstitutionService.Substitute(
            "{{base}}/a and {{base}}/b",
            Vars(("base", "https://x.com")));

        result.Should().Be("https://x.com/a and https://x.com/b");
    }

    // ─── Unknown token behaviour ─────────────────────────────────────────────

    [Fact]
    public void Substitute_UnknownToken_IsLeftInPlace()
    {
        var result = VariableSubstitutionService.Substitute(
            "https://{{host}}/{{unknown}}",
            Vars(("host", "example.com")));

        result.Should().Be("https://example.com/{{unknown}}");
    }

    [Fact]
    public void Substitute_NoTokensInTemplate_ReturnsTemplateUnchanged()
    {
        const string template = "https://example.com/api/users";
        var result = VariableSubstitutionService.Substitute(template, EmptyVars);
        result.Should().Be(template);
    }

    // ─── Edge cases ──────────────────────────────────────────────────────────

    [Fact]
    public void Substitute_TokenValueContainsSpecialRegexChars_DoesNotThrow()
    {
        var result = VariableSubstitutionService.Substitute(
            "prefix_{{val}}_suffix",
            Vars(("val", "a.b+c*d")));

        result.Should().Be("prefix_a.b+c*d_suffix");
    }

    [Fact]
    public void Substitute_TokenValueIsEmpty_ReplacesWithEmptyString()
    {
        var result = VariableSubstitutionService.Substitute(
            "Bearer {{token}}",
            Vars(("token", string.Empty)));

        result.Should().Be("Bearer ");
    }

    [Fact]
    public void Substitute_TemplateWithNoVariables_IsUnchanged()
    {
        const string template = "no placeholders here";
        var result = VariableSubstitutionService.Substitute(template, EmptyVars);
        result.Should().Be(template);
    }

    [Fact]
    public void Substitute_MalformedBraces_AreLeftUnchanged()
    {
        // Single braces and partial patterns must not be altered
        const string template = "{not a token} and {{also not because no closing}}x";
        var result = VariableSubstitutionService.Substitute(template, EmptyVars);
        // Only {{...}} pattern matches; the partial one doesn't close properly
        result.Should().Be(template);
    }
}
