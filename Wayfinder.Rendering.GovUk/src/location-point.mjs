// The value a location-picker stores: "latitude,longitude" in WGS84 decimal degrees. The server
// (LocationValue in the Wayfinder package) is authoritative; this mirrors its rules so the map and the
// text field can agree before anything is posted.

const NUMBER = /^[+-]?(\d+\.?\d*|\.\d+)$/;

function parseWithin(text, limit) {
  const trimmed = text.trim();
  if (!NUMBER.test(trimmed)) return null;
  const number = Number(trimmed);
  return Math.abs(number) <= limit ? number : null;
}

/** Parses "lat,lng", or returns null for anything that is not exactly two in-range numbers. */
export function parsePoint(value) {
  const parts = String(value ?? '').split(',');
  if (parts.length !== 2) return null;
  const latitude = parseWithin(parts[0], 90);
  const longitude = parseWithin(parts[1], 180);
  return latitude === null || longitude === null ? null : { latitude, longitude };
}

/** The canonical stored form: six decimal places (about 0.1 m), no spaces. */
export function formatPoint(latitude, longitude) {
  return `${latitude.toFixed(6)},${longitude.toFixed(6)}`;
}

/** Wraps a longitude into [-180, 180], because a map the user has panned around the world reports values beyond it. */
export function wrapLongitude(longitude) {
  return ((((longitude + 180) % 360) + 360) % 360) - 180;
}
