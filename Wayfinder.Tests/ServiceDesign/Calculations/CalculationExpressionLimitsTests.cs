using FluentAssertions;
using Wayfinder.Models.ServiceDesign.Calculations;
using Wayfinder.Services.Calculations;

namespace Wayfinder.Tests.ServiceDesign.Calculations;

/// <summary>
/// A stack overflow cannot be caught in .NET: it ends the host process. Blueprints are untrusted
/// input, so a hostile expression must be rejected with a <see cref="CalculationException"/>, never
/// recurse until the process dies. 2,000 nested parentheses used to be enough to kill it.
/// </summary>
public class CalculationExpressionLimitsTests
{
    [Theory]
    [InlineData(2_000)]
    [InlineData(50_000)]
    [InlineData(500_000)]
    public void DeeplyNestedParentheses_AreRejected_NotAStackOverflow(int depth)
    {
        var expression = new string('(', depth) + "1" + new string(')', depth);

        var act = () => CalculationExpressionParser.Parse(expression);

        act.Should().Throw<CalculationException>();
    }

    [Fact]
    public void ALongRunOfPrefixOperators_IsRejected()
    {
        var act = () => CalculationExpressionParser.Parse(string.Concat(Enumerable.Repeat("not ", 100_000)) + "true");

        act.Should().Throw<CalculationException>();
    }

    [Fact]
    public void TheLongestChainTheLimitsAllow_ParsesAndEvaluatesWithoutOverflowing()
    {
        // 500 terms = 999 tokens, just under MaxTokens; a left-nested tree 499 levels deep.
        var expression = string.Join("+", Enumerable.Repeat("1", (CalculationExpressionParser.MaxTokens + 1) / 2));
        var calculations = new ServiceBlueprintCalculationSet
        {
            Fields = new Dictionary<string, ServiceBlueprintCalculationField> { ["total"] = new() { Expr = expression } }
        };

        var result = new CalculationEvaluator().Evaluate(calculations, new Dictionary<string, object?>());

        result.Fields["total"].Should().Be(500m);
    }

    [Fact]
    public void ExpressionsAtTheNestingLimit_StillParse()
    {
        var depth = CalculationExpressionParser.MaxNestingDepth;
        var node = CalculationExpressionParser.Parse(new string('(', depth) + "1" + new string(')', depth));

        node.Should().BeOfType<CalcNode.Number>();
    }
}
