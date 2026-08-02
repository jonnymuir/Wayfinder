using Wayfinder.Models.ServiceDesign;
using Wayfinder.Services.Validation;

namespace Wayfinder.Tests.Services.Validation;

public class FieldValueValidatorTests
{
    private static FieldRenderPayload TextField(string key = "name", bool required = true) => new()
    {
        FieldKey = key,
        Label = "Name",
        FieldType = "text",
        Required = required,
    };

    [Fact]
    public void Validate_AcceptsWellFormedSubmission()
    {
        var fields = new[] { TextField() };
        var submitted = new Dictionary<string, string> { ["name"] = "Alice" };

        var result = FieldValueValidator.Validate(fields, submitted);

        Assert.True(result.IsValid);
    }

    [Fact]
    public void Validate_RejectsUnknownFieldKey()
    {
        var fields = new[] { TextField() };
        var submitted = new Dictionary<string, string>
        {
            ["name"] = "Alice",
            ["injected"] = "malicious value",
        };

        var result = FieldValueValidator.Validate(fields, submitted);

        Assert.False(result.IsValid);
        Assert.True(result.Errors.ContainsKey("injected"));
    }

    [Fact]
    public void Validate_RejectsMissingRequiredField()
    {
        var fields = new[] { TextField(required: true) };
        var submitted = new Dictionary<string, string>();

        var result = FieldValueValidator.Validate(fields, submitted);

        Assert.False(result.IsValid);
        Assert.True(result.Errors.ContainsKey("name"));
    }

    [Fact]
    public void Validate_SkipsRequiredCheckWhenFieldIsHidden()
    {
        var fields = new[] { TextField(required: true) };
        var submitted = new Dictionary<string, string>();
        var hidden = new HashSet<string> { "name" };

        var result = FieldValueValidator.Validate(fields, submitted, hidden);

        Assert.True(result.IsValid);
    }

    [Fact]
    public void Validate_RejectsOptionOutsideDeclaredAllowlist()
    {
        var fields = new[]
        {
            new FieldRenderPayload
            {
                FieldKey = "colour",
                Label = "Colour",
                FieldType = "radio",
                Required = true,
                Options = new[] { "red", "blue" },
            },
        };
        var submitted = new Dictionary<string, string> { ["colour"] = "purple" };

        var result = FieldValueValidator.Validate(fields, submitted);

        Assert.False(result.IsValid);
        Assert.True(result.Errors.ContainsKey("colour"));
    }

    [Fact]
    public void Validate_RejectsValueBelowDeclaredMinimum()
    {
        var fields = new[]
        {
            new FieldRenderPayload
            {
                FieldKey = "amount",
                Label = "Amount",
                FieldType = "number",
                Required = true,
                Min = 1,
                Max = 100,
            },
        };
        var submitted = new Dictionary<string, string> { ["amount"] = "0" };

        var result = FieldValueValidator.Validate(fields, submitted);

        Assert.False(result.IsValid);
        Assert.True(result.Errors.ContainsKey("amount"));
    }

    [Fact]
    public void Validate_RejectsValueExceedingDeclaredMaxLength()
    {
        var fields = new[]
        {
            new FieldRenderPayload
            {
                FieldKey = "notes",
                Label = "Notes",
                FieldType = "textarea",
                Required = true,
                MaxLength = 5,
            },
        };
        var submitted = new Dictionary<string, string> { ["notes"] = "this is far too long" };

        var result = FieldValueValidator.Validate(fields, submitted);

        Assert.False(result.IsValid);
        Assert.True(result.Errors.ContainsKey("notes"));
    }

    [Fact]
    public void Validate_SkipsFieldHiddenByConditionalOn()
    {
        var fields = new[]
        {
            new FieldRenderPayload
            {
                FieldKey = "trigger",
                Label = "Trigger",
                FieldType = "radio",
                Required = false,
                Options = new[] { "yes", "no" },
            },
            new FieldRenderPayload
            {
                FieldKey = "detail",
                Label = "Detail",
                FieldType = "text",
                Required = true,
                ConditionalOn = "trigger",
                VisibleWhen = "yes",
            },
        };
        var submitted = new Dictionary<string, string> { ["trigger"] = "no" };

        var result = FieldValueValidator.Validate(fields, submitted);

        Assert.True(result.IsValid);
    }
}
