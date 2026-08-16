// A genuinely scannable QR code for the demo's cold-open slate — not a placeholder graphic.
// qrcode-generator is a small, dependency-free, pure-JS encoder (no network call, no external
// service, the matrix is computed locally from the given text) — appropriate for a devDependency
// that only ever runs inside this recording tool.
import qrcode from 'qrcode-generator';

/**
 * Renders `text` as a QR code and returns it as a `data:image/svg+xml;base64,...` URI, ready to
 * drop straight into an `<img src>` inside a `page.evaluate` callback (a data URI is a plain
 * string, so it survives Playwright's arg serialisation with no extra plumbing).
 */
export function qrCodeDataUri(text: string): string {
  // Version 0 = "auto-size to fit the data"; error-correction 'M' (~15% recovery) is the usual
  // default for a URL — enough resilience for a screen-captured video without bloating the matrix.
  const qr = qrcode(0, 'M');
  qr.addData(text);
  qr.make();
  const svg = qr.createSvgTag(8, 2); // cellSize, margin
  return `data:image/svg+xml;base64,${Buffer.from(svg).toString('base64')}`;
}
