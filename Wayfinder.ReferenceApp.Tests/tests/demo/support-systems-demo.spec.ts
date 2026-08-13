import { test, expect, type Browser, type Page } from '@playwright/test';
import { mkdir } from 'node:fs/promises';
import path from 'node:path';
import { LiveAppHost } from '../support/live-app-host';
import { beat, clearBeat, showSlate, clearSlate, moveNarrationTo, startNarrationTimeline, getNarrationTimeline } from './support/narration';
import { humanClick, humanCheck, humanType } from './support/human-interactions';

/**
 * A narrated, single-take recording of the Support Systems feature — NN/g's third
 * service-blueprint lane — working end to end across three real actors and two genuinely separate
 * running apps. Not a CI test: this is a recording tool (see playwright.demo.config.ts, which no
 * CI script references). The assertions it does make are load-bearing — a beat that narrates
 * something the app didn't actually do would be a lie on camera, so every claim is checked.
 *
 * One continuous video: Playwright records one video per *Page*, so every act navigates the same
 * page rather than opening new ones, and afterAll saves it explicitly (a page shared across tests
 * silently loses its auto-attached video). Identity changes are real sign-outs/sign-ins on that
 * one page — the insurer's own app needs no auth at all, and lives on a different origin, so its
 * cookies never collide with the reference app's.
 */

const REFERENCE_APP = 'https://localhost:7286';
const SAFETYNET = 'https://localhost:7301';
const OUTPUT_DIR = path.resolve(process.cwd(), '../docs/demos');
const OUTPUT_FILE = path.join(OUTPUT_DIR, 'support-systems-end-to-end.webm');

const appHost = new LiveAppHost();
let browserRef: Browser;
let page: Page;

const RISK_ASSESSMENT_PDF = Buffer.from(
  '%PDF-1.4\n% Juggling risk assessment — 10 metre exclusion zone, HSE-aligned, 3 performers.\n'
);

test.describe('Support systems — narrated end-to-end demo', () => {
  test.describe.configure({ mode: 'serial' });
  test.setTimeout(12 * 60_000);

  test.beforeAll(async ({ browser }) => {
    browserRef = browser;
    await appHost.start();

    const context = await browserRef.newContext({
      baseURL: REFERENCE_APP,
      ignoreHTTPSErrors: true,
      viewport: { width: 1440, height: 900 },
      // Explicit size is not optional — omitting it scales the recording down to fit inside
      // 800x800, which is what actually makes a "grainy" take, not the encode.
      recordVideo: { dir: path.join(process.cwd(), 'test-results', 'demo-video'), size: { width: 1440, height: 900 } }
    });
    page = await context.newPage();
    startNarrationTimeline();

    // Clean slate on both apps so the demo always tells the same story.
    await page.request.delete(`${REFERENCE_APP}/api/test/reset`);
    await page.request.delete(`${SAFETYNET}/api/test/reset`);
  });

  test.afterAll(async () => {
    const video = page.video();
    const context = page.context();
    await page.close();
    if (video) {
      await mkdir(OUTPUT_DIR, { recursive: true });
      await video.saveAs(OUTPUT_FILE);
      // saveAs copies — delete the auto-hash-named original or every run leaves a duplicate.
      await video.delete();
      console.log(`\nDemo video: ${OUTPUT_FILE}`);
      console.log('Narration timeline:');
      for (const entry of getNarrationTimeline()) {
        const seconds = (entry.atMs / 1000).toFixed(1).padStart(6);
        console.log(`  ${seconds}s  [${entry.kind}] ${entry.text}`);
      }
    }
    await context.close();
    await appHost.stop();
  });

  test('Act 1 — the citizen applies, and uploads a risk assessment', async () => {
    await page.goto('/account/login');

    await showSlate(page, {
      eyebrow: 'WAYFINDER — SERVICE BLUEPRINTS',
      title: 'Support systems: the third lane',
      body:
        "Nielsen Norman Group's service blueprint has three actor layers. Wayfinder already modelled two — " +
        'the citizen out front, the caseworker behind the line of visibility. This is the third: a real ' +
        'external system, doing real work, that the service has to wait on.'
    });
    await clearSlate(page);

    await beat(page, 'setup', 'Three actors, two separately running apps: an applicant, a caseworker, and SafetyNet Underwriting — a fictional insurer with its own system.');

    await beat(page, 'intent', 'First, the applicant applies for a licence to hold a juggling event — and attaches the risk assessment the whole story turns on.');

    await humanType(page, page.getByLabel('Email address'), 'applicant@example.test');
    await humanType(page, page.locator('#password'), 'wayfinder-demo');
    await humanClick(page, page.getByRole('button', { name: 'Sign in' }));

    await expect(page.getByLabel('Full name')).toBeVisible();
    await humanType(page, page.getByLabel('Full name'), 'Robin Marsh');
    await humanType(page, page.getByLabel('Email address'), 'robin.marsh@example.test');
    await humanClick(page, page.getByRole('button', { name: 'Continue' }));

    await expect(page.getByRole('heading', { name: 'About the event' })).toBeVisible();
    await humanType(page, page.getByLabel('Name of the event'), 'Riverside Fire Juggling Gala');
    await humanType(page, page.getByLabel('Day'), '15');
    await humanType(page, page.getByLabel('Month'), '9');
    await humanType(page, page.getByLabel('Year'), '2026');
    await humanType(page, page.getByLabel('Number of jugglers taking part'), '3');

    await beat(page, 'note', 'Ticking "fire, knives or other dangerous props" is what makes the insurer check worth doing at all.');
    await humanCheck(page, page.getByLabel('This act involves fire, knives, or other dangerous props'));
    await humanClick(page, page.getByRole('button', { name: 'Continue' }));

    await expect(page.getByRole('heading', { name: 'Risk assessment' })).toBeVisible();
    await beat(page, 'intent', 'The applicant attaches their risk assessment. This is a real file — it is about to travel between two separate apps.');
    await page.getByLabel('Risk assessment or public liability insurance certificate').setInputFiles({
      name: 'riverside-risk-assessment.pdf',
      mimeType: 'application/pdf',
      buffer: RISK_ASSESSMENT_PDF
    });
    await humanType(page, page.getByLabel('How are you mitigating the risk?'), '10 metre exclusion zone, HSE-aligned.');
    await humanClick(page, page.getByRole('button', { name: 'Continue' }));

    await expect(page.getByRole('heading', { name: 'Check your answers and declare' })).toBeVisible();
    await humanCheck(page, page.getByLabel('I confirm the details above are correct'));
    await humanClick(page, page.getByRole('button', { name: 'Submit application' }));

    await expect(page.getByText('A caseworker is reviewing your application.')).toBeVisible();
    await beat(page, 'recap', 'Submitted. The applicant now waits behind the line of visibility — the same join-gateway wait screen Wayfinder has always had.');
  });

  test('Act 2 — the caseworker reviews it, and can open the actual file', async () => {
    await beat(page, 'intent', 'Now we become the caseworker: backstage, reviewing what just arrived.');
    await clearBeat(page);

    await humanClick(page, page.getByRole('button', { name: 'Sign out' }));
    await page.goto('/account/login');
    await humanType(page, page.getByLabel('Email address'), 'caseworker@example.test');
    await humanType(page, page.locator('#password'), 'wayfinder-demo');
    await humanClick(page, page.getByRole('button', { name: 'Sign in' }));

    await humanClick(page, page.getByRole('link', { name: 'Caseworker queue' }));
    await expect(page.getByRole('heading', { name: 'Caseworker queue' })).toBeVisible();
    await beat(page, 'setup', "The caseworker's worklist. One application waiting for a decision.");

    await humanClick(page, page.getByRole('link', { name: 'Review' }));
    await expect(page.getByRole('heading', { name: 'Review application' })).toBeVisible();

    // The uploaded file is a real link on its own summary row — not a filename in plain text, and
    // not a stray list of links at the bottom of the page.
    // The narration in Act 1 claims the applicant ticked "dangerous props" — assert the summary
    // actually agrees. A recorded take once narrated that tick while the caseworker's summary read
    // "No" (a govuk checkbox's hidden input swallowed a coordinate click), which is precisely the
    // kind of quiet contradiction a demo must never ship with.
    const summaryList = page.locator('.govuk-summary-list');
    await expect(
      summaryList.locator('.govuk-summary-list__row', { hasText: 'Fire, knives or other dangerous props' })
    ).toContainText('Yes');

    const fileLink = page.getByRole('link', { name: 'riverside-risk-assessment.pdf' });
    await expect(fileLink).toBeVisible();
    await beat(page, 'setup', 'Every answer, including the uploaded file — a real link on its own row, so the caseworker can open exactly what was submitted.');

    const fileHref = await fileLink.getAttribute('href');
    const fileResponse = await page.request.get(`${REFERENCE_APP}${fileHref}`);
    expect(fileResponse.ok()).toBeTruthy();
    expect(fileResponse.headers()['content-type']).toBe('application/pdf');
    await beat(page, 'note', 'That link serves the real bytes the applicant uploaded — the engine itself never touches them; the host owns file storage.');
  });

  test('Act 3 — the blueprint refuses to skip the insurer, and the wait that used to be invisible', async () => {
    await beat(page, 'intent', 'Reviewing and deciding are separate steps. The caseworker tries to go straight to a decision.');

    await humanClick(page, page.getByRole('button', { name: 'Continue to decision' }));
    await expect(page.locator('.govuk-error-summary')).toContainText('SafetyNet Underwriting');
    await beat(page, 'recap', 'Refused. A risk assessment was attached, so it must go to the insurer first — and that rule lives in the blueprint, not in code.');

    await beat(page, 'intent', 'So the caseworker sends it, which is the only way forward.');
    await humanClick(page, page.getByRole('button', { name: 'Send risk assessment to insurer' }));

    await expect(page.getByRole('heading', { name: 'Caseworker queue' })).toBeVisible();
    await beat(page, 'recap', 'Back on the worklist — and crucially the application is still here, tagged "Waiting". It is out with the insurer, but it has not vanished.');

    const waitingRow = page.locator('tr', { hasText: 'Apply for a licence to hold a juggling event' });
    await expect(waitingRow.getByText('Waiting')).toBeVisible();
    await expect(waitingRow.getByRole('link', { name: 'View' })).toBeVisible();
    await beat(page, 'note', 'This is the fix that made the feature real: before it, an application sent to a support system disappeared from the queue entirely.');

    await humanClick(page, waitingRow.getByRole('link', { name: 'View' }));
    await expect(page.getByText('SafetyNet Underwriting is reviewing the risk assessment.')).toBeVisible();
    await beat(page, 'recap', "The caseworker's own wait screen — the same mechanism the applicant gets, now serving a backstage actor waiting on a support system.");
  });

  test('Act 4 — a genuinely separate insurer system does the work', async () => {
    await beat(page, 'intent', 'Now the third lane. SafetyNet Underwriting is a different app, on a different port, that knows nothing about Wayfinder.');
    await clearBeat(page);

    await page.goto(`${SAFETYNET}/queue`);
    await expect(page.getByRole('heading', { name: 'SafetyNet Underwriting' })).toBeVisible();
    await beat(page, 'setup', "The insurer's own staff queue. Deliberately not GOV.UK styled — this is somebody else's system, and it should look like it.");

    await expect(page.getByText('Robin Marsh')).toBeVisible();
    await expect(page.getByText('Riverside Fire Juggling Gala')).toBeVisible();

    // The file genuinely arrived here over HTTP — the insurer can open it, not just see its name.
    const insurerFileLink = page.getByRole('link', { name: 'riverside-risk-assessment.pdf' });
    await expect(insurerFileLink).toBeVisible();
    const insurerHref = await insurerFileLink.getAttribute('href');
    const insurerFile = await page.request.get(`${SAFETYNET}${insurerHref}`);
    expect(insurerFile.ok()).toBeTruthy();
    expect(await insurerFile.text()).toContain('exclusion zone');
    await beat(page, 'recap', "The applicant's actual file travelled server to server and is downloadable here. The underwriter can read the thing they are being asked to judge.");

    await beat(page, 'intent', 'The underwriter makes a real decision, in their own system, on their own terms.');
    const row = page.locator('tr', { hasText: 'Robin Marsh' });
    await humanType(page, row.getByLabel('Decision notes'), 'Exclusion zone adequate. Cover approved.');
    await humanClick(page, row.getByRole('button', { name: 'Approve' }));

    await expect(page.locator('tr', { hasText: 'Robin Marsh' }).getByText('approved').first()).toBeVisible();
    await beat(page, 'recap', 'Approved — and that click fired a real webhook back into Wayfinder.');
  });

  test('Act 5 — the webhook resolves the wait, and the licence is granted', async () => {
    await beat(page, 'intent', "Back to the caseworker. Nothing was polled or refreshed by hand — the insurer's decision was pushed to Wayfinder.");
    await clearBeat(page);

    await page.goto(`${REFERENCE_APP}/caseworker/queue`);
    await expect(page.getByRole('heading', { name: 'Caseworker queue' })).toBeVisible();

    const row = page.locator('tr', { hasText: 'Apply for a licence to hold a juggling event' });
    await expect(row.getByText('Waiting')).toHaveCount(0);
    await beat(page, 'recap', 'The "Waiting" tag is gone. The join released the moment the webhook arrived, and it is actionable again.');

    await humanClick(page, row.getByRole('link', { name: 'Review' }));
    await expect(page.getByRole('heading', { name: 'Record your decision' })).toBeVisible();

    const summary = page.locator('.govuk-summary-list');
    await expect(summary.getByText('approved', { exact: true })).toBeVisible();
    await expect(summary.getByText('Exclusion zone adequate. Cover approved.')).toBeVisible();

    // Scroll the insurer's own rows into view and lift the narration off the bottom — a
    // bottom-anchored bar sits exactly on top of the two rows this beat is about.
    await summary.getByText('Exclusion zone adequate. Cover approved.').scrollIntoViewIfNeeded();
    await moveNarrationTo(page, 'top');
    await beat(page, 'setup', "The insurer's decision and their notes are now part of the application — carried back by the webhook and shown to the caseworker.", { position: 'top' });

    await beat(page, 'intent', 'The caseworker still makes the final call. The support system informed the decision; it did not make it.');
    await humanClick(page, page.getByRole('button', { name: 'Approve' }));
    await expect(page.getByRole('heading', { name: 'Caseworker queue' })).toBeVisible();

    await beat(page, 'intent', 'And back to the applicant, who has been waiting this whole time.');
    await clearBeat(page);
    await humanClick(page, page.getByRole('button', { name: 'Sign out' }));
    await page.goto('/account/login');
    await humanType(page, page.getByLabel('Email address'), 'applicant@example.test');
    await humanType(page, page.locator('#password'), 'wayfinder-demo');
    await humanClick(page, page.getByRole('button', { name: 'Sign in' }));

    await expect(page.getByText('Licence granted')).toBeVisible();
    await beat(page, 'recap', 'Licence granted. The applicant never saw the insurer, never knew there was a wait inside a wait — which is exactly the point.');

    await showSlate(page, {
      eyebrow: 'WHAT YOU JUST WATCHED',
      title: 'One blueprint, three lanes, two real systems',
      body:
        'The insurer call is declared in the blueprint as an action, not written as bespoke code. The engine ' +
        'offers both polling and webhooks; each capability declares which it uses. And the caseworker waits ' +
        'on a support system using the very same gateway Wayfinder already used to make a citizen wait.'
    });
    await clearSlate(page);
  });
});
