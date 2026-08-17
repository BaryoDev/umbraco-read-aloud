using System.Reflection;
using Shouldly;
using Xunit;

namespace BaryoDev.Umbraco.ReadAloud.Tests;

/// <summary>
/// C# copies the value of a <c>const</c> into every assembly that reads it, so a public const in a
/// published package can never be changed again: consumers keep their inlined copy until they
/// rebuild, and nothing tells them the value moved. These tests pin which public strings are
/// <c>static readonly</c>, which is resolved at run time and therefore changeable, and which one is
/// deliberately not.
///
/// Reflection can see the difference. A const field reports <c>IsLiteral</c>; a static readonly
/// field reports <c>IsInitOnly</c>.
/// </summary>
public class PublicConstantTests
{
    public static TheoryData<Type, string> ChangeableStrings => new()
    {
        { typeof(ReadAloudOptions), nameof(ReadAloudOptions.SectionName) },
        { typeof(ReadAloudOptions), nameof(ReadAloudOptions.EdgeProvider) },
    };

    [Theory]
    [MemberData(nameof(ChangeableStrings))]
    public void Public_strings_are_not_inlined_into_consumers(Type declaring, string name)
    {
        var field = declaring.GetField(name, BindingFlags.Public | BindingFlags.Static);

        field.ShouldNotBeNull($"{declaring.Name}.{name} should be a public static field.");
        field.IsLiteral.ShouldBeFalse(
            $"{declaring.Name}.{name} is a const, so its value is baked into every consuming "
            + "assembly at compile time and can never be changed for anyone who already built. "
            + "Make it static readonly unless it has to satisfy an attribute argument.");
        field.IsInitOnly.ShouldBeTrue($"{declaring.Name}.{name} should be static readonly.");
    }

    /// <summary>
    /// The one deliberate exception, pinned so nobody "fixes" it to match the others and finds out
    /// from a compiler error. An attribute argument must be a compile-time constant, and this is
    /// used as <c>[EnableRateLimiting(ReadAloudRateLimiting.PolicyName)]</c> on the controller.
    /// </summary>
    [Fact]
    public void The_rate_limit_policy_name_stays_const_because_an_attribute_needs_it()
    {
        var field = typeof(ReadAloudRateLimiting)
            .GetField(nameof(ReadAloudRateLimiting.PolicyName), BindingFlags.Public | BindingFlags.Static);

        field.ShouldNotBeNull();
        field.IsLiteral.ShouldBeTrue(
            "PolicyName has to stay const: it is an attribute argument on ReadAloudController, and "
            + "attribute arguments must be compile-time constants. Treat its value as permanent.");
    }

    /// <summary>
    /// Catches a public const added later. Without this, the rule above only covers the two fields
    /// someone remembered to list, and the next one lands unnoticed.
    /// </summary>
    [Fact]
    public void No_other_public_const_is_added_without_a_decision()
    {
        var allowed = new[] { nameof(ReadAloudRateLimiting.PolicyName) };

        var consts = typeof(ReadAloudOptions).Assembly
            .GetExportedTypes()
            .SelectMany(t => t.GetFields(BindingFlags.Public | BindingFlags.Static))
            .Where(f => f.IsLiteral && !allowed.Contains(f.Name))
            .Select(f => $"{f.DeclaringType!.Name}.{f.Name}")
            .OrderBy(x => x)
            .ToArray();

        consts.ShouldBeEmpty(
            "a new public const inlines into consumers and cannot be changed after publish. Make it "
            + "static readonly, or add it to the allowed list here with the reason it must be const.");
    }
}
