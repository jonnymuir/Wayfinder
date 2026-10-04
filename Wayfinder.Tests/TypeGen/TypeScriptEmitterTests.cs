using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.Json.Serialization.Metadata;
using FluentAssertions;
using Wayfinder.TypeGen;
using Xunit;

namespace Wayfinder.Tests.TypeGen;

public class TypeScriptEmitterTests
{
    private static string Emit(params Type[] roots) =>
        new TypeScriptEmitter(new JsonSerializerOptions(JsonSerializerDefaults.Web) { TypeInfoResolver = new DefaultJsonTypeInfoResolver() }).Emit(roots);

    [Fact]
    public void ANullableProperty_IsOptionalAndANonNullableOneIsRequired()
    {
        var ts = Emit(typeof(Sample));

        ts.Should().Contain("  name: string;");
        ts.Should().Contain("  nickname?: string;");
        ts.Should().Contain("  age?: number;");
    }

    [Fact]
    public void AStringEnum_BecomesAUnionAndAListOfItsValues()
    {
        var ts = Emit(typeof(Sample));

        ts.Should().Contain("export const colourValues = ['Red', 'Green'] as const;");
        ts.Should().Contain("export type Colour = (typeof colourValues)[number];");
        ts.Should().Contain("  colour: Colour;");
    }

    [Fact]
    public void CollectionsAndDictionaries_UseTheirElementTypes()
    {
        var ts = Emit(typeof(Sample));

        ts.Should().Contain("  tags: string[];");
        ts.Should().Contain("  scores: Record<string, number>;");
    }

    [Fact]
    public void APolymorphicBase_BecomesADiscriminatedUnion()
    {
        var ts = Emit(typeof(Shape));

        ts.Should().Contain("export interface Circle {\n  kind: 'circle';\n  radius: number;\n}");
        ts.Should().Contain("export type Shape =\n  | Circle\n  | Square;");
    }

    [Fact]
    public void ANumericEnum_IsRefusedRatherThanGuessed()
    {
        var act = () => Emit(typeof(NumericHolder));

        act.Should().Throw<InvalidOperationException>().WithMessage("*not serialized as a string*");
    }

    [Fact]
    public void TwoTypesWithTheSameName_AreRefused()
    {
        var act = () => Emit(typeof(A.Clash), typeof(B.Clash));

        act.Should().Throw<InvalidOperationException>().WithMessage("*Two C# types are both named Clash*");
    }

    [JsonConverter(typeof(JsonStringEnumConverter<Colour>))]
    public enum Colour { Red, Green }

    public enum Plain { One, Two }

    public sealed record Sample
    {
        public string Name { get; init; } = "";
        public string? Nickname { get; init; }
        public int? Age { get; init; }
        public Colour Colour { get; init; }
        public List<string> Tags { get; init; } = [];
        public Dictionary<string, int> Scores { get; init; } = [];
    }

    public sealed record NumericHolder
    {
        public Plain Value { get; init; }
    }

    [JsonPolymorphic(TypeDiscriminatorPropertyName = "kind")]
    [JsonDerivedType(typeof(Circle), "circle")]
    [JsonDerivedType(typeof(Square), "square")]
    public abstract record Shape;

    public sealed record Circle : Shape
    {
        public double Radius { get; init; }
    }

    public sealed record Square : Shape
    {
        public double Side { get; init; }
    }
}

public static class A
{
    public sealed record Clash(string X);
}

public static class B
{
    public sealed record Clash(string Y);
}
