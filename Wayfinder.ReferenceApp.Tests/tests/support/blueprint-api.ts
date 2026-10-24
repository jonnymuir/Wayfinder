import { type APIRequestContext, expect } from '@playwright/test';
import { parse, stringify } from 'yaml';

// The authoring API carries a whole blueprint as YAML, both ways.
const BLUEPRINTS = '/wayfinder/service-blueprint-authoring/blueprints';

// biome-ignore lint/suspicious/noExplicitAny: specs reach into whatever part of the blueprint they test.
export async function readBlueprint(request: APIRequestContext, key: string): Promise<any> {
  const response = await request.get(`${BLUEPRINTS}/${key}`);
  expect(response.ok()).toBeTruthy();
  return parse(await response.text());
}

export function saveBlueprint(request: APIRequestContext, key: string, blueprint: unknown) {
  return request.put(`${BLUEPRINTS}/${key}`, {
    data: stringify(blueprint),
    headers: { 'Content-Type': 'application/yaml' },
  });
}
