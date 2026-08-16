import { test, expect, type Browser, type Page } from '@playwright/test';
import { mkdir } from 'node:fs/promises';
import path from 'node:path';
import { LiveAppHost } from '../support/live-app-host';
import { beat, clearBeat, showSlate, clearSlate, moveNarrationTo, startNarrationTimeline, getNarrationTimeline } from './support/narration';
import { humanClick, humanCheck, humanType, humanMoveTo } from './support/human-interactions';
import { qrCodeDataUri } from './support/qr';

/**
 * A narrated, single-take walkthrough of Wayfinder itself, for a viewer who has never seen it
 * before: what a service blueprint is (grounded in Nielsen Norman Group's own definition), and
 * then every major thing declaring one buys you — real GOV.UK screens, cross-field validation
 * enforced from a declarative rule, the GDS "Change" pattern, conditional routing, and a genuinely
 * separate third-party system (NN/g's "support processes" lane) all wired from the same blueprint
 * — followed by the authoring side, showing the same rules being written in the visual editor.
 * Not a CI test: this is a recording tool (see playwright.demo.config.ts, which no CI script
 * references). The assertions it does make are load-bearing — a beat that narrates something the
 * app didn't actually do would be a lie on camera, so every claim is checked.
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
const OUTPUT_FILE = path.join(OUTPUT_DIR, 'wayfinder-overview.webm');

const appHost = new LiveAppHost();
let browserRef: Browser;
let page: Page;

const RISK_ASSESSMENT_PDF = Buffer.from(
  '%PDF-1.4\n% Juggling risk assessment — 10 metre exclusion zone, HSE-aligned, 3 performers.\n'
);

// The same URL already cited (and independently verified) throughout this repo's own docs —
// see e.g. docs/guides/support-systems.md and Services/ReferenceActors.cs's own doc comment.
const NNGROUP_URL = 'https://www.nngroup.com/articles/service-blueprints-definition/';
const NNGROUP_QR_DATA_URI = qrCodeDataUri(NNGROUP_URL);

const ORIGINAL_EVENT_NAME = 'Riverside Fire Juggling Gala';
const RENAMED_EVENT_NAME = 'Riverside Community Fire Show';

test.describe('Wayfinder overview — narrated end-to-end demo', () => {
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
      title: 'What is a service blueprint?',
      body:
        'A design tool from Nielsen Norman Group that maps a service across everyone touching it: the ' +
        'customer out front, the staff working behind the scenes, and the external systems neither of them ' +
        'ever sees. Wayfinder is an engine for building real, working services directly from one — declared ' +
        'once, not hand-coded three separate times.',
      link: { url: NNGROUP_URL, qrDataUri: NNGROUP_QR_DATA_URI }
    });
    await clearSlate(page);

    await beat(page, 'setup', 'This film follows one blueprint, start to finish — a licence application — to show what each part of that model looks like as real, working software.');
    await beat(page, 'setup', 'Three actors, two separately running apps: an applicant, a caseworker, and SafetyNet Underwriting — a fictional insurer with its own system.');

    await beat(page, 'intent', 'First, the applicant applies for a licence to hold a juggling event — and attaches the risk assessment the whole story turns on.');

    await humanType(page, page.getByLabel('Email address'), 'applicant@example.test');
    await humanType(page, page.locator('#password'), 'wayfinder-demo');
    await humanClick(page, page.getByRole('button', { name: 'Sign in' }));

    await expect(page.getByLabel('Full name')).toBeVisible();
    await beat(page, 'note', 'Every screen from here on is the real, official GOV.UK Design System — not a lookalike, the actual govuk-frontend package.');
    await humanType(page, page.getByLabel('Full name'), 'Robin Marsh');
    await humanType(page, page.getByLabel('Email address'), 'robin.marsh@example.test');
    await humanClick(page, page.getByRole('button', { name: 'Continue' }));

    await expect(page.getByRole('heading', { name: 'About the event' })).toBeVisible();
    await humanType(page, page.getByLabel('Name of the event'), ORIGINAL_EVENT_NAME);
    await humanType(page, page.getByLabel('Day'), '15');
    await humanType(page, page.getByLabel('Month'), '9');
    await humanType(page, page.getByLabel('Year'), '2026');
    await humanType(page, page.getByLabel('Number of jugglers taking part'), '3');

    await beat(page, 'note', 'Ticking "fire, knives or other dangerous props" is what makes the insurer check worth doing at all.');
    await humanCheck(page, page.getByLabel('This act involves fire, knives, or other dangerous props'));
    await humanClick(page, page.getByRole('button', { name: 'Continue' }));

    await expect(page.getByRole('heading', { name: 'Risk assessment' })).toBeVisible();

    // A declarative, cross-stage business rule — genuinely enforced, not a UI hint — demonstrated
    // by actually tripping it first. hasDangerousProps was captured two stages earlier; this rule
    // reads it from there with no extra wiring, and only applies because of what was ticked then.
    // Neither a document nor a measurable note has been given yet, so the rule catches it.
    await beat(page, 'intent', 'Because dangerous props were ticked, this blueprint requires real mitigation evidence here — not just a promise. A vague answer first, to see the rule actually catch it.');
    await humanType(page, page.getByLabel('How are you mitigating the risk?'), 'We will be careful.');
    await humanClick(page, page.getByRole('button', { name: 'Continue' }));

    await expect(page.getByText('There is a problem')).toBeVisible();
    await beat(page, 'recap', 'Rejected — a real GOV.UK error summary, driven by one declarative rule in the blueprint, not bespoke validation code.', { holdMs: 5_000 });

    await humanType(page, page.getByLabel('How are you mitigating the risk?'), '10 metre exclusion zone, HSE-aligned.');

    // The rule accepts either a measurable note or an actual document — the applicant gives both.
    // This is the real file the whole rest of the story turns on: it's about to travel between
    // two separate apps.
    await beat(page, 'intent', 'The applicant also attaches the risk assessment document itself.');
    await page.getByLabel('Risk assessment or public liability insurance certificate').setInputFiles({
      name: 'riverside-risk-assessment.pdf',
      mimeType: 'application/pdf',
      buffer: RISK_ASSESSMENT_PDF
    });
    await humanClick(page, page.getByRole('button', { name: 'Continue' }));

    await expect(page.getByRole('heading', { name: 'Check your answers and declare' })).toBeVisible();
    await beat(page, 'setup', 'Every answer is shown back before anything is submitted — and any of them can still be changed.');

    await humanClick(page, page.getByRole('button', { name: /Change name of the event/i }));
    await expect(page.getByRole('heading', { name: 'About the event' })).toBeVisible();
    await expect(page.getByLabel('Name of the event')).toHaveValue(ORIGINAL_EVENT_NAME);
    await beat(page, 'note', 'Back on the exact stage that captured it, pre-filled with what was already given — not a blank form to start over on.');
    await humanType(page, page.getByLabel('Name of the event'), RENAMED_EVENT_NAME);
    await humanClick(page, page.getByRole('button', { name: 'Continue' }));

    // Already valid from the first pass — the file and the fixed-up notes both survive untouched,
    // so re-visiting this stage on the way back needs no re-entry at all.
    await expect(page.getByRole('heading', { name: 'Risk assessment' })).toBeVisible();
    await humanClick(page, page.getByRole('button', { name: 'Continue' }));

    await expect(page.getByRole('heading', { name: 'Check your answers and declare' })).toBeVisible();
    const checkAnswersSummary = page.locator('.govuk-summary-list');
    await expect(checkAnswersSummary.getByText(RENAMED_EVENT_NAME, { exact: true })).toBeVisible();
    await expect(checkAnswersSummary.getByText('riverside-risk-assessment.pdf')).toBeVisible();
    await beat(page, 'recap', 'The change stuck, and nothing else was lost — the file and the mitigation notes are both still exactly as they were.');

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

    // The Calculations tab is where the OTHER declarative rule from earlier in the film lives —
    // the one that rejected a vague mitigation answer. Same author, same editor, same language.
    await beat(page, 'intent', 'And the rule that rejected a vague answer earlier — the same tab, the same kind of rule.');
    await humanClick(page, page.getByRole('tab', { name: 'Calculations' }));
    const calcTab = page.locator('wayfinder-calculations-editor');
    await expect(calcTab).toBeVisible({ timeout: 10_000 });

    const validationsSection = calcTab.locator('.calc-section', { hasText: 'Validations' });
    await humanClick(page, validationsSection.locator('summary'));
    const evidenceRule = calcTab.locator('[data-wayfinder-calc-validation="risk-assessment-0"]');
    await evidenceRule.scrollIntoViewIfNeeded();
    await expect(evidenceRule).toBeVisible();
    await page.waitForTimeout(400);

    const whenEditor = evidenceRule.locator('wayfinder-calculation-expression-editor').first();
    const ruleEditor = evidenceRule.locator('wayfinder-calculation-expression-editor').nth(1);
    await expect(whenEditor.locator('.cm-content')).toContainText('hasDangerousProps');
    await expect(ruleEditor.locator('.cm-content')).toBeVisible();
    await beat(page, 'recap',
      'Two fields: "when" — only checked if dangerous props were ticked, two stages earlier — and "rule", which must hold for the stage to continue. Everything this film has shown is this same shape.',
      { holdMs: 5_500 }
    );
  });

  test('Act 5 — a genuinely separate insurer system does the work', async () => {
    await beat(page, 'intent', 'Now the third lane. SafetyNet Underwriting is a different app, on a different port, that knows nothing about Wayfinder.');
    await clearBeat(page);

    await page.goto(`${SAFETYNET}/queue`);
    await expect(page.getByRole('heading', { name: 'SafetyNet Underwriting' })).toBeVisible();
    await beat(page, 'setup', "The insurer's own staff queue. Deliberately not GOV.UK styled — this is somebody else's system, and it should look like it.");

    await expect(page.getByText('Robin Marsh')).toBeVisible();
    await expect(page.getByText(RENAMED_EVENT_NAME)).toBeVisible();

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
      title: 'One blueprint, every lane of the same model',
      body:
        "Frontstage: real GOV.UK screens, cross-field validation, an answer changed after the fact with " +
        'nothing else lost. Backstage: a route offered only when the data calls for it, declared as one ' +
        'expression with the same intellisense throughout. Support process: a genuinely separate app, a real ' +
        'file transfer, and a decision carried back by webhook or poll — whichever the capability declares. ' +
        "None of it is bespoke code. It's one blueprint, describing a service the way Nielsen Norman Group " +
        'already teaches services should be understood.',
      link: { url: NNGROUP_URL, qrDataUri: NNGROUP_QR_DATA_URI },
      holdMs: 9_000
    });
    await clearSlate(page);
  });
});
