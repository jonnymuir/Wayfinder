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

    // Regression coverage: guidance-checklist renders as a same-name checkbox group posting
    // under "{fieldKey}[]" — same as checkboxlist/checkboxes — but GetSubmittedValue's suffix
    // lookup only recognised those two literal type strings, so a guidance-checklist's own
    // submitted value was never found and its required-completeness check failed unconditionally,
    // regardless of what the visitor actually checked. Confirmed live: a real "Before you apply"
    // guidance page in Umbraco.Prism stuck at "0 of 4 completed" with every item visibly checked.
    private static FieldRenderPayload GuidanceChecklistField(bool required = true) => new()
    {
        FieldKey = "guidance",
        Label = "Guidance",
        FieldType = "guidance-checklist",
        Required = required,
        Options = new[] { "transfer-rules", "international-transfers", "supporting-evidence", "professional-standards" },
    };

    [Fact]
    public void Validate_GuidanceChecklist_AcceptsAllItemsAcknowledgedViaBracketSuffixKey()
    {
        var fields = new[] { GuidanceChecklistField() };
        var submitted = new Dictionary<string, string>
        {
            ["guidance[]"] = "transfer-rules,international-transfers,supporting-evidence,professional-standards",
        };

        var result = FieldValueValidator.Validate(fields, submitted);

        Assert.True(result.IsValid);
    }

    [Fact]
    public void Validate_GuidanceChecklist_RejectsWhenNotEveryItemAcknowledged()
    {
        var fields = new[] { GuidanceChecklistField() };
        var submitted = new Dictionary<string, string>
        {
            ["guidance[]"] = "transfer-rules,international-transfers",
        };

        var result = FieldValueValidator.Validate(fields, submitted);

        Assert.False(result.IsValid);
        Assert.True(result.Errors.ContainsKey("guidance"));
    }
}
