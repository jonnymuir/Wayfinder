# Extending the component catalog

How a toolkit user — a human integrator, or an AI agent working on their behalf —
registers a genuinely new `Component` type, beyond the ~27 Wayfinder ships out of the box.
This is for whoever is *building* a Wayfinder host, not whoever is *authoring* a
service blueprint against one; if that's you, see
[Reference Service Blueprint Contract](./reference-service-blueprint-contract.md) instead.

This document is also exposed as an MCP resource
(`service-blueprint-docs://extending-the-component-catalog`) so an agent can fetch it
directly without repo access.

---

## The three separate things a component type is

Registering a new type touches three independent pieces — you can supply any subset,
though in practice you almost always want all three:

1. **A CLR record** — your own type deriving from `Component` (or `InputComponent`, if
   it captures a value). Plain data, same shape as any built-in component.
2. **A `ComponentDescriptor`** — "what it is": display name, category, its property
   schema (for validation and, eventually, a generic editor UI), and how it contains
   other components, if it does. Registered via `ComponentTypeRegistry.Register<T>()`.
3. **A renderer override** — "how it renders": a plain delegate registered via
   `GovUkComponentRenderer.RegisterComponent`/`RegisterField`
   (`Wayfinder.Rendering.GovUk`), independent of the descriptor.

The descriptor and the renderer are genuinely separate registrations. A type with a
descriptor but no renderer override falls back to whatever generic rendering Wayfinder
already does for its category (an unrecognised `InputComponent` renders as a plain text
field, for instance) — useful as a starting point, but you'll usually want your own
renderer for anything that isn't just a differently-labelled text box.

## Worked example: a five-point confidence rating

`Wayfinder.ReferenceApp/Services/CustomComponents.cs` in this repo is a complete, real,
tested example — not a hypothetical. It defines `RatingComponent`, a five-point
confidence scale, entirely outside Wayfinder's own assembly, and proves it end to end
(`Wayfinder.ReferenceApp.Tests/tests/custom-component.spec.ts`: authors it into a live
blueprint via the real authoring API, then drives it through the actual browser-rendered
citizen journey). The walkthrough below follows that example; read the real source
alongside this doc.

### 1. Define the CLR record

```csharp
public sealed record RatingComponent : InputComponent;
```

Deriving from `InputComponent` (rather than `Component` directly) gets you `FieldKey`,
`Label`, `Hint`, `Required`, `ConditionalOn`, `VisibleWhen`, `Default`, `DefaultFrom`,
`ChangeStateKey` for free, and participation in the calculation scope. Add your own
properties as ordinary C# properties — `RatingComponent` deliberately adds none (see
[a known limitation](#known-limitation-a-renderfield-override-only-sees-fieldrenderpayload)
below for why that's a deliberate, not accidental, choice here).

### 2. Describe it

```csharp
ComponentTypeRegistry.Register<RatingComponent>(new ComponentDescriptor
{
    Discriminator = "rating",
    DisplayName = "Confidence rating",
    Category = ComponentCategory.Input,
    Description = "A five-point confidence rating, from \"Very unconfident\" to \"Very confident\".",
    ClrType = typeof(RatingComponent),
    IsInput = true,
    Properties =
    [
        new() { Key = nameof(InputComponent.FieldKey), Title = "Field key", ValueKind = ComponentPropertyValueKind.String, Required = true },
        new() { Key = nameof(InputComponent.Label), Title = "Label", ValueKind = ComponentPropertyValueKind.String, Required = true },
        new() { Key = nameof(InputComponent.Hint), Title = "Hint", ValueKind = ComponentPropertyValueKind.String },
        new() { Key = nameof(InputComponent.Required), Title = "Required", ValueKind = ComponentPropertyValueKind.Boolean, Editor = "toggle" },
    ],
});
```

Call this **once, at host startup, before any `ServiceBlueprint` is read or written** —
see [Registration timing](#registration-timing-the-registry-freezes) below; this is not
optional, it's the single most common way this goes wrong.

### 3. Render it

```csharp
renderer.RegisterField("rating", (field, errors) => RenderRating(field, errors));
```

where `RenderRating` builds real `govuk-frontend`-styled HTML by hand (a `<fieldset>`
with a `<legend>`, five `<input type="radio">` options, hint/error markup matching every
other field's accessibility pattern) using nothing but the public `GovUk.Esc`/
`GovUk.FieldName` helpers Wayfinder itself ships. See the real source for the full
markup — it's a genuinely accessible (WCAG AA) radios group, not a placeholder.

### 4. Declare it as a queue capability (optional, but recommended)

If the host uses `IQueueCapabilitiesProvider`, add the new discriminator to whichever
queue's declaration should support it:

```csharp
private static readonly IReadOnlyList<string> CitizenComponentTypes =
    [..., CustomComponents.RatingDiscriminator];
```

A typo here is caught immediately (`QUEUE_CAPABILITY_UNKNOWN_COMPONENT_TYPE`) the next
time any blueprint is validated — see
[Reference Service Blueprint Contract § Queue render capabilities](./reference-service-blueprint-contract.md#queue-render-capabilities-host-declared).

That's it — a service blueprint can now use `{"type": "rating", "fieldKey": "...", ...}`
anywhere a component is expected, validated, saved, and rendered exactly like a
built-in type.

## `ComponentDescriptor` reference

| Field | Meaning |
|---|---|
| `Discriminator` | The `"type"` JSON value, e.g. `"rating"`. Must be unique across the whole process — a duplicate throws at registration time. |
| `DisplayName` | Human-readable name, e.g. "Confidence rating" — for editor UI and docs. |
| `Category` | `Input` \| `Content` \| `Container` \| `DataDisplay` \| `FlowControl` — see [the contract doc](./reference-service-blueprint-contract.md#components) for what each means. |
| `Description` | Longer help text — editor tooltip / AI-agent-readable prose. |
| `ClrType` | The `Component`-derived CLR type backing this discriminator. |
| `IsInput` | `true` for anything deriving from `InputComponent` — declares a `fieldKey`, participates in the calculation scope. |
| `Properties` | `IReadOnlyList<ComponentPropertyDescriptor>` — see below. Drives [descriptor-driven validation](#validation-comes-for-free). |
| `Containment` | How (if at all) this type holds other components — see below. Defaults to `ComponentContainment.None`. |

## `ComponentPropertyDescriptor` reference

Describes one property — deliberately the same shape as `AuthoredParameterDefinition`,
the editor's own proven schema for action parameters, so a component's property schema
and an action's parameter schema are the same shape a host or editor UI only has to
understand once.

| Field | Meaning |
|---|---|
| `Key` | The CLR property name, e.g. `"FieldKey"` — reflected against the component instance directly, so use `nameof(YourComponent.YourProperty)`, never a raw string. |
| `Title` | Human-readable label, e.g. "Field key". |
| `Description` | Longer help text. |
| `ValueKind` | `String` \| `Number` \| `Integer` \| `Boolean` \| `StringArray` \| `Object` \| `Array`. |
| `Format` | Semantic hint, e.g. `"email"`, `"date"`, `"color"`. |
| `Editor` | Explicit editor widget hint, e.g. `"textarea"`, `"select"`, `"toggle"`. `null` infers from `ValueKind`/`AllowedValues`. |
| `AllowedValues` | Closed set of legal string values, if any. |
| `Required` | Whether this property must have a real (non-null, non-empty) value — see [Validation](#validation-comes-for-free). |
| `DefaultValue` | Suggested default for an editor UI. |
| `Minimum` / `Maximum` | Numeric bounds, checked for `Integer`/`Number` properties. |
| `MinLength` / `MaxLength` | String length bounds. |
| `Pattern` | Regex a string value must match. |
| `Properties` | Nested property schema when `ValueKind` is `Object`. |
| `Items` | Element schema when `ValueKind` is `Array` — see `ChartComponent.Bands`/`StatGroupComponent.Items` in `BuiltInComponentDescriptors.cs` for a real recursive example. |

## Containment shapes

Only three shapes exist across the whole built-in catalog (verified) — pick whichever
matches your own container type, or `None` if it's a leaf:

- **`None`** — a leaf. Most types, including `RatingComponent`.
- **`ChildList(propertyName)`** — a single flat `IReadOnlyList<Component>` property, e.g.
  `FieldsetComponent.Children`.
- **`NamedSections(propertyName, sectionChildrenPropertyName)`** — a list of named
  sections, each with its own children, e.g. `AccordionComponent.Sections[].Children`.
- **`KeyedChildren(propertyName, keySourceProperty)`** — an
  `IReadOnlyDictionary<string, IReadOnlyList<Component>>` keyed by a value that should be
  a subset of another property on the *same* component, e.g.
  `RadiosComponent.ConditionalChildren` keyed against `Options`. Declaring
  `keySourceProperty` gets you a real, automatic check for free: a key that doesn't
  match any declared option is flagged
  (`COMPONENT_CONDITIONAL_CHILD_KEY_MISMATCH`) as a branch that can never be reached.

Whichever shape you declare, `ComponentExtensions.Flatten`/`FlattenWithPaths` (the tree
walker every validation/calculation-scope/rendering pass in the toolkit is built on)
automatically descends into it — no engine change needed for a new container type to
work correctly.

## Registration timing: the registry freezes

`ComponentTypeRegistry` is global, process-wide state. It **freezes the first time
anything actually reads it** — the first `(de)serialization`, `list_component_types`
call, or direct `.All`/`.Find`/`.DescriptorFor` — and `Register` throws after that,
loudly, rather than silently doing nothing:

> `ComponentTypeRegistry is frozen — a component has already been read/(de)serialized,
> so 'x' can't be registered now.`

Call every `Register<T>()` at host startup, before anything else touches a
`ServiceBlueprint`. `Wayfinder.ReferenceApp/Program.cs` calls
`CustomComponents.Register()` as the very first statement in `Main`, before
`WebApplication.CreateBuilder` even runs, precisely to avoid any ordering risk from
DI singletons resolving in an order you don't fully control.

If you're using the REST authoring API (`Wayfinder.Engine.Api`), also call
`services.AddServiceBlueprintAuthoringApi()` alongside `AddServiceBlueprintAuthoring()`
at startup — ASP.NET Core's own minimal-API `[FromBody]` binding uses a *separate*
`JsonSerializerOptions` to `ServiceBlueprintJson`'s by default, and won't otherwise pick
up your registered type at all (a built-in type still works either way, since those are
also seeded onto `Component` via `[JsonDerivedType]` as a fallback — only a
custom-registered type is affected). The MCP tools don't need this; they already
deserialize with `ServiceBlueprintJson.ReadOptions` directly.

## Validation comes for free

Once registered, `ServiceBlueprintAuthoringService.Validate` (and therefore
`validate_service_blueprint`/`save_service_blueprint`) automatically checks every
instance of your type against its own `ComponentPropertyDescriptor`s — required
properties present, `AllowedValues` respected, `Pattern`/`MinLength`/`MaxLength`/
`Minimum`/`Maximum` satisfied, recursing into any `Object`/`Array`-shaped nested
properties — with no extra code. See `Wayfinder.Engine.Services.ComponentPropertyValidator`
for the implementation if you want the exact diagnostic codes
(`COMPONENT_PROPERTY_REQUIRED`, `COMPONENT_PROPERTY_INVALID_VALUE`,
`COMPONENT_PROPERTY_PATTERN_MISMATCH`, `COMPONENT_PROPERTY_TOO_SHORT`/`_TOO_LONG`/
`_TOO_SMALL`/`_TOO_LARGE`, `COMPONENT_CONDITIONAL_CHILD_KEY_MISMATCH`).

## Known limitation: a `RegisterField` override only sees `FieldRenderPayload`

`GovUkComponentRenderer.RegisterField`'s delegate receives a `FieldRenderPayload` — the
same generic rendering DTO every built-in field type shares (`FieldKey`, `Label`,
`Hint`, `Required`, `Value`, plus a handful of type-specific extras like `Options` or
`Min`/`Max` that only populate for the built-in types that declare them) — **not** the
original `Component` instance. If your new type's own properties beyond that base set
(FieldKey/Label/Hint/Required/Value, which all thread through generically for any
`InputComponent`) need to reach your renderer, there's no mechanism for that today.
This is exactly why `RatingComponent` deliberately declares no properties beyond
`InputComponent`'s own — its five-point scale is fixed data the renderer hardcodes, not
threaded through the payload. A component needing genuinely dynamic extra data (e.g. a
configurable number of rating points) isn't fully supported by the render pipeline yet;
treat this as a real, current boundary, not an oversight to design around.

## Related documentation

- [Reference Service Blueprint Contract](./reference-service-blueprint-contract.md) — the
  full `ServiceBlueprint` JSON shape, including the built-in component catalog table.
- [AI-Ready Blueprint Authoring — Integrator Guide](./ai-service-blueprint-authoring.md) —
  wiring the MCP/REST authoring surface into a host's own pipeline.
