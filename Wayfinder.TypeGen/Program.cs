using Wayfinder.Engine.Services;
using Wayfinder.Models.ServiceDesign;
using Wayfinder.Models.ServiceDesign.Components;
using Wayfinder.Models.ServiceDesign.SupportSystems;
using Wayfinder.TypeGen;

// Generates the editor's TypeScript model from the C# model it edits, so the two cannot drift.
//   dotnet run --project Wayfinder.TypeGen -- write <output.ts>   regenerate the file
//   dotnet run --project Wayfinder.TypeGen -- check <output.ts>   fail if the file is stale (CI)
if (args is not [var mode and ("write" or "check"), var path])
{
    Console.Error.WriteLine("usage: Wayfinder.TypeGen (write|check) <output.ts>");
    return 2;
}

var generated = new TypeScriptEmitter(ServiceBlueprintJson.WriteOptions).Emit(
[
    typeof(ServiceBlueprint),
    typeof(ComponentDescriptor),
    typeof(SupportSystemDescriptor),
    typeof(ServiceBlueprintValidationOutcome),
    typeof(ServiceBlueprintSaveOutcome),
]);

if (mode == "write")
{
    await File.WriteAllTextAsync(path, generated);
    return 0;
}

var current = File.Exists(path) ? await File.ReadAllTextAsync(path) : "";
if (current == generated)
{
    return 0;
}

Console.Error.WriteLine(
    $"{path} is out of date with the C# model. Regenerate it:\n" +
    "  dotnet run --project Wayfinder.TypeGen -- write " + path);
return 1;
