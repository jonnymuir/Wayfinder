import { test, expect, type Browser, type Page } from '@playwright/test';
import { mkdir } from 'node:fs/promises';
import path from 'node:path';
import { LiveAppHost } from '../support/live-app-host';
import { beat, clearBeat, showSlate, clearSlate, moveNarrationTo, startNarrationTimeline, getNarrationTimeline } from './support/narration';
import { humanClick, humanCheck, humanType, humanMoveTo } from './support/human-interactions';

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

    // Sign-out's own POST already redirects to /account/login server-side (see
    // Program.cs's "/account/logout" handler) — an explicit goto() here raced that in-flight
    // navigation and intermittently aborted it (net::ERR_ABORTED). Wait for the page sign-out
    // already sends us to, rather than issuing a second, competing navigation.
    await humanClick(page, page.getByRole('button', { name: 'Sign out' }));
    await page.waitForURL('**/account/login');
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

  test('Act 3 — the blueprint offers only one way forward, and a wait the caseworker never loses sight of', async () => {
    await beat(page, 'intent', 'Reviewing and deciding are separate steps. A risk assessment was attached, so there is only one thing to do here.');

    // Not blocked-with-an-error — simply never offered. The blueprint's own route visibility
    // rule decides which action even appears, before the caseworker could click the wrong one.
    await expect(page.getByRole('button', { name: 'Continue to decision' })).toHaveCount(0);
    const sendToInsurerButton = page.getByRole('button', { name: 'Send risk assessment to insurer' });
    await expect(sendToInsurerButton).toBeVisible();
    // Scroll the one real button into view and lift the narration bar off the bottom of the
    // page — a bottom-anchored bar sits exactly where this button renders, so proving "this is
    // the only option" requires the button to actually be on screen, not just named in the copy.
    await sendToInsurerButton.scrollIntoViewIfNeeded();
    await moveNarrationTo(page, 'top');
    await beat(page, 'recap',
      'No "continue straight to a decision" button exists on this screen at all — that rule lives in the blueprint, not in code. Sending it to the insurer is the only option.',
      { holdMs: 5_500, position: 'top' }
    );

    await humanClick(page, sendToInsurerButton);

    await expect(page.getByRole('heading', { name: 'Caseworker queue' })).toBeVisible();
    await beat(page, 'recap', 'Back on the worklist — and crucially the application is still here, tagged "Waiting". It is out with the insurer, but it has not vanished.');

    const waitingRow = page.locator('tr', { hasText: 'Apply for a licence to hold a juggling event' });
    await expect(waitingRow.getByText('Waiting')).toBeVisible();
    await expect(waitingRow.getByRole('link', { name: 'View' })).toBeVisible();
    await beat(page, 'note', "Every application a caseworker has sent out stays visible and findable here, however long the insurer takes to answer.");

    await humanClick(page, waitingRow.getByRole('link', { name: 'View' }));
    await expect(page.getByText('SafetyNet Underwriting is reviewing the risk assessment.')).toBeVisible();
    await beat(page, 'recap', "The caseworker's own wait screen — the same mechanism the applicant gets, now serving a backstage actor waiting on a support system.");
  });

  test('Act 4 — how easy it is to describe: the same rule, seen in the blueprint', async () => {
    await beat(page, 'intent', "Nothing conjured that button rule out of thin air. It's declared in the blueprint — let's look.");

    await humanClick(page, page.getByRole('link', { name: 'Editor' }));
    const shell = page.locator('[data-wayfinder-component="service-blueprint-editor-shell"]');
    await expect(shell).toHaveAttribute('data-wayfinder-active-service-blueprint', 'juggling-licence', { timeout: 15_000 });
    await page.waitForTimeout(400);

    // Wide establishing shot first: the whole applicant → caseworker → insurer flow, in one
    // frame, before drilling into the one stage this story is actually about.
    await humanClick(page, page.getByRole('button', { name: 'Fit to screen' }));
    await page.waitForTimeout(500);
    await beat(page, 'setup', 'The whole service, laid out end to end — every stage, every gateway, on one canvas.');

    // Zoom in on "Review application" — genuine wheel-zoom, centred on the node, not a jump-cut,
    // so what's on screen stays legible throughout rather than arriving already cropped.
    const reviewNode = page.getByRole('button', { name: /Review application/ }).first();
    const reviewBox = await reviewNode.boundingBox();
    if (reviewBox) {
      await humanMoveTo(page, reviewBox.x + reviewBox.width / 2, reviewBox.y + reviewBox.height / 2);
    }
    for (let i = 0; i < 8; i++) {
      await page.mouse.wheel(0, -120);
      await page.waitForTimeout(90);
    }
    await page.waitForTimeout(400);
    await beat(page, 'setup', 'Here it is — the review stage, with its two exits: send to insurer, or continue.');

    await humanClick(page, reviewNode);
    const inspector = page.locator('wayfinder-step-inspector');
    await expect(inspector.locator('[data-wayfinder-stage-detail="under-review"]')).toBeVisible();
    await inspector.locator('#stage-transitions-heading').scrollIntoViewIfNeeded();
    await page.waitForTimeout(400);

    const sendRoute = inspector.locator('[data-wayfinder-route-target="to-insurer-check"]');
    await expect(sendRoute).toBeVisible();
    const showWhenEditor = sendRoute.locator('wayfinder-calculation-expression-editor');
    await expect(showWhenEditor).toBeVisible();
    await beat(page, 'setup', 'Each route has an "Available when" field — the same expression language, the same editor, as everywhere else in the blueprint.');

    // A genuine, live intellisense demonstration — clear the field, type a recognisable prefix,
    // let the real autocomplete dropdown appear, accept it, then finish the expression back to
    // its actual value. Ends exactly where it started: this is the real rule, not a stand-in.
    const showWhenContent = showWhenEditor.locator('.cm-content');
    await humanClick(page, showWhenContent);
    await showWhenContent.press('ControlOrMeta+A');
    await showWhenContent.press('Backspace');
    await showWhenContent.pressSequentially('riskAss', { delay: 70 });
    await expect(showWhenEditor.locator('.cm-tooltip-autocomplete')).toBeVisible();
    await beat(page, 'note', 'Real autocomplete — every field captured anywhere in the journey, offered as you type.');
    await page.keyboard.press('Enter');
    await showWhenContent.pressSequentially(" <> ''", { delay: 55 });
    await page.waitForTimeout(300);
    await expect(showWhenContent).toHaveText("riskAssessment <> ''");
    await beat(page, 'recap', "That's the whole rule: send to insurer when a risk assessment exists. One line, written by the person who designed the service.");
  });

  test('Act 5 — a genuinely separate insurer system does the work', async () => {
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

  test('Act 6 — the webhook resolves the wait, and the licence is granted', async () => {
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
    // Sign-out's own POST already redirects to /account/login server-side (see
    // Program.cs's "/account/logout" handler) — an explicit goto() here raced that in-flight
    // navigation and intermittently aborted it (net::ERR_ABORTED). Wait for the page sign-out
    // already sends us to, rather than issuing a second, competing navigation.
    await humanClick(page, page.getByRole('button', { name: 'Sign out' }));
    await page.waitForURL('**/account/login');
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
