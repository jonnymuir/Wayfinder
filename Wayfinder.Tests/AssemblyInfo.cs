using Xunit;

// ComponentTypeRegistry (Wayfinder.Models.ServiceDesign.Components) is global, process-wide
// static state that freezes the first time anything reads it. xUnit's default parallelization
// runs different test classes concurrently, which races a test that mutates the registry
// (ComponentTypeRegistryTests' ResetForTests/Register calls) against any other test in the
// assembly that merely reads it (directly, or indirectly via ServiceBlueprintJson (de)serialization
// or ComponentPropertyValidator) — the reader can re-freeze the registry mid-mutation. Disabling
// parallelization keeps the whole suite correct without having to track down and tag every test
// that happens to touch this particular piece of shared state.
[assembly: CollectionBehavior(DisableTestParallelization = true)]
