using System.Runtime.CompilerServices;

// Only for SupportSystemRegistry.ResetForTests / ComponentTypeRegistry.ResetForTests: both are
// process-wide static registries that tests must be able to empty. Replace with instance-scoped
// registries and delete this file.
[assembly: InternalsVisibleTo("Wayfinder.Tests")]
