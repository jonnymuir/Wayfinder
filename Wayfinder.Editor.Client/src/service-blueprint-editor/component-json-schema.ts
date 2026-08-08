/**
 * Phase 7 of the component-catalog extensibility work — a genuine JSON Schema (2020-12)
 * document generated from the live ComponentDescriptor catalog, exportable for external
 * tooling (an IDE's `$schema` support, a future generic-autocomplete extension) and — the
 * concrete deliverable this editor actually consumes today — a faithful reference for the
 * live linting `lintAuthoredServiceBlueprintDocument` (service-blueprint-lint.ts) performs
 * against the same descriptor data directly (skipping a generic JSON-Schema interpreter
 * deliberately: this editor is the only consumer of that specific validation pass, and
 * ComponentDescriptor is already a strictly richer shape — Editor hints, allowedValues,
 * Required — than a schema round-trip would preserve without loss).
 */

import type { ComponentDescriptor, ComponentPropertyDescriptor } from './types.js';

export type JsonSchemaValue = Record<string, unknown>;

function propertySchema(property: ComponentPropertyDescriptor): JsonSchemaValue {
  const schema: JsonSchemaValue = { description: property.description ?? undefined, title: property.title };

  switch (property.valueKind) {
    case 'String':
      schema.type = 'string';
      break;
    case 'Number':
      schema.type = 'number';
      break;
    case 'Integer':
      schema.type = 'integer';
      break;
    case 'Boolean':
      schema.type = 'boolean';
      break;
    case 'StringArray':
      schema.type = 'array';
      schema.items = { type: 'string' };
      break;
    case 'Object':
      schema.type = 'object';
      if (property.properties) {
        schema.properties = Object.fromEntries(property.properties.map(child => [child.key, propertySchema(child)]));
        const required = property.properties.filter(child => child.required).map(child => child.key);
        if (required.length > 0) {
          schema.required = required;
        }
      }
      break;
    case 'Array':
      schema.type = 'array';
      if (property.items) {
        schema.items = propertySchema(property.items);
      }
      break;
    default:
      break;
  }

  if (property.allowedValues?.length) {
    schema.enum = property.allowedValues;
  }
  if (property.pattern) {
    schema.pattern = property.pattern;
  }
  if (property.format) {
    schema.format = property.format;
  }
  if (property.minLength !== undefined && property.minLength !== null) {
    schema.minLength = property.minLength;
  }
  if (property.maxLength !== undefined && property.maxLength !== null) {
    schema.maxLength = property.maxLength;
  }
  if (property.minimum !== undefined && property.minimum !== null) {
    schema.minimum = property.minimum;
  }
  if (property.maximum !== undefined && property.maximum !== null) {
    schema.maximum = property.maximum;
  }
  if (property.defaultValue !== undefined && property.defaultValue !== null) {
    schema.default = property.defaultValue;
  }

  return schema;
}

function componentSchema(descriptor: ComponentDescriptor): JsonSchemaValue {
  const properties: Record<string, JsonSchemaValue> = {
    type: { const: descriptor.discriminator },
  };
  const required = ['type'];

  for (const property of descriptor.properties) {
    properties[property.key] = propertySchema(property);
    if (property.required) {
      required.push(property.key);
    }
  }

  // The containment slot (a container's children) isn't a declared Property — describe it
  // structurally so the schema stays faithful to what the wire format actually allows, even
  // though the live linter (deliberately, see this file's own module doc comment) doesn't
  // recurse into it via this schema representation.
  if (descriptor.containment.kind === 'ChildList' && descriptor.containment.propertyName) {
    properties[descriptor.containment.propertyName] = { type: 'array', items: { $ref: '#/$defs/component' } };
  } else if (descriptor.containment.kind === 'NamedSections' && descriptor.containment.propertyName) {
    const childrenKey = descriptor.containment.sectionChildrenPropertyName ?? 'children';
    properties[descriptor.containment.propertyName] = {
      type: 'array',
      items: {
        type: 'object',
        properties: {
          heading: { type: 'string' },
          summary: { type: ['string', 'null'] },
          [childrenKey]: { type: 'array', items: { $ref: '#/$defs/component' } },
        },
        required: ['heading', childrenKey],
      },
    };
  } else if (descriptor.containment.kind === 'KeyedChildren' && descriptor.containment.propertyName) {
    properties[descriptor.containment.propertyName] = {
      type: 'object',
      additionalProperties: { type: 'array', items: { $ref: '#/$defs/component' } },
    };
  }

  return {
    title: descriptor.displayName,
    description: descriptor.description ?? undefined,
    type: 'object',
    properties,
    required,
  };
}

/**
 * Builds a JSON Schema (2020-12) document for the `Component` polymorphic union — a `$defs`
 * entry per registered discriminator (built-in and any host-registered custom type, since this
 * is generated from the live catalog fetched at runtime, not a static built-in list), plus a
 * `component` def that's a `oneOf` over all of them, keyed by each variant's own `"type": {"const": ...}`.
 */
export function generateComponentJsonSchema(catalog: ComponentDescriptor[]): JsonSchemaValue {
  const defs: Record<string, JsonSchemaValue> = {};
  for (const descriptor of catalog) {
    defs[descriptor.discriminator] = componentSchema(descriptor);
  }

  defs.component = {
    oneOf: catalog.map(descriptor => ({ $ref: `#/$defs/${descriptor.discriminator}` })),
  };

  return {
    $schema: 'https://json-schema.org/draft/2020-12/schema',
    title: 'Wayfinder Component',
    $defs: defs,
    $ref: '#/$defs/component',
  };
}
