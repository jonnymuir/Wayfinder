/**
 * Curated, hand-written regex patterns for the Pattern (regex) field's "insert a common pattern"
 * quick-insert (see component-property-editor.ts's `renderPatternField`) — deliberately not a
 * visual regex-construction UI. That's a large, fragile undertaking that still bottoms out in
 * "type a pattern" for anything non-trivial; GOV.UK Design System's own guidance favours simple,
 * well-tested patterns and clear error messages over cleverness here. A preset is a starting
 * point the designer can still hand-edit afterwards, not a locked choice.
 */
export interface RegexPreset {
  label: string;
  pattern: string;
}

export const REGEX_PRESETS: RegexPreset[] = [
  { label: 'UK postcode', pattern: '^[A-Za-z]{1,2}\\d[A-Za-z\\d]?\\s?\\d[A-Za-z]{2}$' },
  { label: 'National Insurance number', pattern: '^[A-CEGHJ-PR-TW-Za-ceghj-pr-tw-z]{2}\\d{6}[A-Da-d]$' },
  { label: 'UK phone number', pattern: '^(\\+44\\s?|0)7\\d{3}\\s?\\d{6}$' },
  { label: 'Letters only', pattern: '^[A-Za-z]+$' },
  { label: 'Digits only', pattern: '^\\d+$' },
  { label: 'Alphanumeric', pattern: '^[A-Za-z0-9]+$' },
  { label: 'No leading/trailing whitespace', pattern: '^\\S(.*\\S)?$' },
];
