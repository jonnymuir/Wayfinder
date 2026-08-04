import { test, expect } from '@playwright/test';
import { DEMO_USERS, loginAs, resetApp } from './fixtures';

// The applicant's frontstage journey through "Apply for a licence to hold a juggling event" —
// GOV.UK Service Manual's own teaching exemplar (see JugglingLicenceBlueprint.cs). This spec
// walks every citizen-queue stage up to handoff, proving the engine's stage/route/queue model
// end to end through the reference app's own hand-rolled HTML forms, not just via API calls.
test.describe('Citizen journey: apply for a juggling licence', () => {
  test.beforeEach(async ({ request }) => resetApp(request));

  test('an applicant can complete every citizen-queue stage up to caseworker handoff', async ({ page }) => {
    await loginAs(page, DEMO_USERS.applicant);

    await test.step('Your details', async () => {
      await expect(page.getByRole('heading', { name: 'Your details' })).toBeVisible();
      await page.getByLabel('Full name').fill('Alex Applicant');
      await page.getByLabel('Email address').fill('alex@example.test');
      await page.getByRole('button', { name: 'Continue' }).click();
    });

    await test.step('About the event', async () => {
      await expect(page.getByRole('heading', { name: 'About the event' })).toBeVisible();
      await page.getByLabel('Name of the event').fill('Big Top Juggling Gala');
      // The real GOV.UK date-input component: three separate day/month/year boxes, not a
      // native date picker — see ComponentHtmlRenderer.RenderDateField.
      await page.getByLabel('Day').fill('1');
      await page.getByLabel('Month').fill('9');
      await page.getByLabel('Year').fill('2026');
      await page.getByLabel('Number of jugglers taking part').fill('12');
      await page.getByRole('button', { name: 'Continue' }).click();
    });

    await test.step('Check your answers and declare', async () => {
      await expect(page.getByRole('heading', { name: 'Check your answers and declare' })).toBeVisible();
      const summary = page.locator('.govuk-summary-list');
      await expect(summary.getByText('Alex Applicant', { exact: true })).toBeVisible();
      await expect(summary.getByText('Big Top Juggling Gala', { exact: true })).toBeVisible();
      await expect(summary.getByText('1 September 2026', { exact: true })).toBeVisible();
      await page.getByLabel('I confirm the details above are correct').check();
      await page.getByRole('button', { name: 'Submit application' }).click();
    });

    await test.step('Handed off to the caseworker queue: the applicant waits at their own join gateway', async () => {
      // CitizenProfile's VisibleQueues doesn't include the caseworker queue — the applicant
      // never gets a read-only peek at the caseworker's own stage content. Instead, the
      // "to-under-review" split parks a citizen-queue cursor directly at a Join gateway
      // (post-review, requiredIncomingQueues: ["citizen", "caseworker"]), so the
      // applicant sees a genuine, first-class "please wait" status via BuildJoinWaitingEnvelope
      // — not ACCESS_DENIED, and not the caseworker's own authored stage content either.
      await expect(page.getByRole('heading', { name: 'Application under review' })).toBeVisible();
      await expect(page.getByText('A caseworker is reviewing your application.')).toBeVisible();
      await expect(page.getByRole('button', { name: 'Approve' })).toHaveCount(0);
    });

    await test.step('Reloading confirms the same waiting response, not a stale render', async () => {
      await page.reload();
      await expect(page.getByText('A caseworker is reviewing your application.')).toBeVisible();
    });
  });

  test('a "Change" link on the check-your-answers page lets the applicant go back and edit an earlier answer', async ({ page }) => {
    await loginAs(page, DEMO_USERS.applicant);
    await page.getByLabel('Full name').fill('Alex Applicant');
    await page.getByLabel('Email address').fill('alex@example.test');
    await page.getByRole('button', { name: 'Continue' }).click();
    await page.getByLabel('Name of the event').fill('Big Top Juggling Gala');
    await page.getByLabel('Day').fill('1');
    await page.getByLabel('Month').fill('9');
    await page.getByLabel('Year').fill('2026');
    await page.getByLabel('Number of jugglers taking part').fill('12');
    await page.getByRole('button', { name: 'Continue' }).click();
    await expect(page.getByRole('heading', { name: 'Check your answers and declare' })).toBeVisible();

    // Deliberately not ticking "I confirm the details above are correct" first: a summary-list
    // Change button must not be blocked by this stage's own required-field validation — its
    // whole point is to let the applicant fix an earlier answer before they're ready to declare.
    // This regressed once already: a plain type="submit" button with no formnovalidate is
    // silently blocked by the browser's own HTML5 constraint validation against the checkbox
    // below, with no server request and no visible error at all.
    await page.getByRole('button', { name: /Change name of the event/i }).click();
    await expect(page.getByRole('heading', { name: 'About the event' })).toBeVisible();
    // The stage re-renders pre-filled with what was already captured, not blanked.
    await expect(page.getByLabel('Name of the event')).toHaveValue('Big Top Juggling Gala');

    await page.getByLabel('Name of the event').fill('Grand Juggling Extravaganza');
    await page.getByRole('button', { name: 'Continue' }).click();

    await expect(page.getByRole('heading', { name: 'Check your answers and declare' })).toBeVisible();
    const summary = page.locator('.govuk-summary-list');
    await expect(summary.getByText('Grand Juggling Extravaganza', { exact: true })).toBeVisible();
    // Untouched fields survive the round trip through the earlier stage.
    await expect(summary.getByText('Alex Applicant', { exact: true })).toBeVisible();
  });

  test('required fields block progress before a value is entered', async ({ page }) => {
    await loginAs(page, DEMO_USERS.applicant);
    await expect(page.getByRole('heading', { name: 'Your details' })).toBeVisible();

    // The browser's own HTML5 `required` validation stops the submit — client-side, mirroring
    // what a caseworker/citizen actually experiences, and cheap to prove without a server round trip.
    await page.getByRole('button', { name: 'Continue' }).click();
    await expect(page.getByRole('heading', { name: 'Your details' })).toBeVisible();
  });

  // The above test proves the client-side guard. This one proves the server doesn't just trust
  // it — posting directly via page.request bypasses the browser entirely (no HTML5 `required`,
  // no form at all involved), the same as a tampered/scripted submission would.
  test('the server rejects a tampered submission even when the browser is bypassed entirely', async ({ page }) => {
    await loginAs(page, DEMO_USERS.applicant);
    await expect(page.getByRole('heading', { name: 'Your details' })).toBeVisible();
    const stateVersion = await page.locator('input[name="stateVersion"]').inputValue();

    await test.step('a missing required field is rejected server-side, not just client-side', async () => {
      const response = await page.request.post('/apply', {
        form: { action: 'continue', stateVersion, 'field:applicantEmail': 'alex@example.test' },
      });
      expect(response.ok()).toBeTruthy();
      const body = await response.text();
      expect(body).toContain('Your details');
      expect(body).toContain('Full name is required.');
    });

    // Posting a value the browser's own type="email" input could never have submitted in the
    // first place — real proof this is checked server-side, not just relying on the browser.
    // (Field-key injection — submitting a value for a field the current stage never declared —
    // is proven directly against the engine in ProcessManagerEngineValidationTests instead:
    // this host's own CoerceFieldValues already only ever reads known declared keys off the
    // form, so an injected key can never reach this endpoint to demonstrate the engine's own
    // allowlist independently.)
    await test.step('a malformed value for a real field is rejected server-side, not just client-side', async () => {
      const response = await page.request.post('/apply', {
        form: {
          action: 'continue',
          stateVersion,
          'field:applicantName': 'Alex Applicant',
          'field:applicantEmail': 'not-an-email-address',
        },
      });
      expect(response.ok()).toBeTruthy();
      const body = await response.text();
      expect(body).toContain('Your details');
      expect(body).toContain('must be a valid email address');
    });

    await test.step('the instance is still on the same stage afterwards, unchanged', async () => {
      await page.goto('/apply');
      await expect(page.getByRole('heading', { name: 'Your details' })).toBeVisible();
    });
  });
});
