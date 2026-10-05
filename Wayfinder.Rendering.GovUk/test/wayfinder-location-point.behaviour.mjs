#!/usr/bin/env node
// Behavioural tests for the location-picker's point rules (../src/location-point.mjs): the client-side
// mirror of the server's LocationValue, so the map and the text field agree before anything is posted.
// Plain Node, no test framework.
//
// Usage: node test/wayfinder-location-point.behaviour.mjs

import assert from 'node:assert/strict';
import { formatPoint, parsePoint, wrapLongitude } from '../src/location-point.mjs';

const cases = [];
const test = (name, fn) => cases.push([name, fn]);

test('a latitude,longitude pair parses, with or without spaces', () => {
  assert.deepEqual(parsePoint('51.5074,-0.1278'), { latitude: 51.5074, longitude: -0.1278 });
  assert.deepEqual(parsePoint(' 51.5074 , -0.1278 '), { latitude: 51.5074, longitude: -0.1278 });
});

test('the limits themselves are valid', () => {
  assert.deepEqual(parsePoint('90,180'), { latitude: 90, longitude: 180 });
  assert.deepEqual(parsePoint('-90,-180'), { latitude: -90, longitude: -180 });
});

test('anything that is not exactly two in-range plain numbers is rejected', () => {
  for (const bad of ['', '51.5', '1,2,3', 'north,west', '90.0001,0', '0,180.0001', 'NaN,0', '0,Infinity', '1e2,0', ',', '51,5074,-0,1278']) {
    assert.equal(parsePoint(bad), null, `"${bad}" should not parse`);
  }
  assert.equal(parsePoint(null), null);
  assert.equal(parsePoint(undefined), null);
});

test('the stored form is six decimal places with no spaces', () => {
  assert.equal(formatPoint(51.5074, -0.1278), '51.507400,-0.127800');
  assert.deepEqual(parsePoint(formatPoint(51.5074, -0.1278)), { latitude: 51.5074, longitude: -0.1278 });
});

test('a longitude from a map panned around the world wraps back into range', () => {
  assert.equal(wrapLongitude(190), -170);
  assert.equal(wrapLongitude(-190), 170);
  assert.equal(wrapLongitude(180), -180);
  assert.equal(wrapLongitude(0), 0);
  assert.equal(wrapLongitude(540), -180);
});

let failed = 0;
for (const [name, fn] of cases) {
  try {
    fn();
    console.log(`  ok  ${name}`);
  } catch (error) {
    failed += 1;
    console.error(`  FAIL ${name}\n${error.message}`);
  }
}
if (failed > 0) process.exit(1);
console.log('All location point checks passed.');
