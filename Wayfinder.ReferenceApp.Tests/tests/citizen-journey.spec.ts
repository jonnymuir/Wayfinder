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

    await test.step('Risk assessment: optional, skipped here', async () => {
      await expect(page.getByRole('heading', { name: 'Risk assessment' })).toBeVisible();
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

  test('dangerous-props cross-stage validation is demonstrated live and actually enforced through the real HTML journey', async ({ page }) => {
    // Proves both halves of what juggling-licence.json's "Wayfinder demo note" on the Risk
    // assessment stage tells a visitor: the rule exists (the demo note itself renders), and it's
    // genuinely enforced (StageDefinition.Validations, not just a UI hint) — driven through the
    // real citizen-facing HTML forms, not the engine API directly.
    await loginAs(page, DEMO_USERS.applicant);
    await page.getByLabel('Full name').fill('Alex Applicant');
    await page.getByLabel('Email address').fill('alex@example.test');
    await page.getByRole('button', { name: 'Continue' }).click();

    await page.getByLabel('Name of the event').fill('Fire and Blades Spectacular');
    await page.getByLabel('Day').fill('1');
    await page.getByLabel('Month').fill('9');
    await page.getByLabel('Year').fill('2026');
    await page.getByLabel('Number of jugglers taking part').fill('4');
    await page.getByLabel('This act involves fire, knives, or other dangerous props').check();
    await page.getByRole('button', { name: 'Continue' }).click();

    await expect(page.getByRole('heading', { name: 'Risk assessment' })).toBeVisible();
    // The demo note is a collapsed GDS <details> — expand it to prove it's genuinely there for a
    // curious visitor to read, not just present in the DOM.
    await page.getByText("Wayfinder demo note: what's being shown on this stage").click();
    await expect(page.getByText(/demonstrates cross-stage validation/)).toBeVisible();

    await test.step('vague mitigation notes are rejected by the cross-stage rule', async () => {
      await page.getByLabel('How are you mitigating the risk?').fill('We will be careful.');
      await page.getByRole('button', { name: 'Continue' }).click();

      await expect(page.getByText('There is a problem')).toBeVisible();
      await expect(
        page.locator('.govuk-error-message', { hasText: /measurable detail/ })
      ).toBeVisible();
      // Rejected before advancing — still on Risk assessment.
      await expect(page.getByRole('heading', { name: 'Risk assessment' })).toBeVisible();
    });

    await test.step('a measurable detail satisfies the rule and the journey continues', async () => {
      await page.getByLabel('How are you mitigating the risk?').fill('10 metres safety distance maintained throughout.');
      await page.getByRole('button', { name: 'Continue' }).click();

      await expect(page.getByRole('heading', { name: 'Check your answers and declare' })).toBeVisible();
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
    await expect(page.getByRole('heading', { name: 'Risk assessment' })).toBeVisible();
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
    await expect(page.getByRole('heading', { name: 'Risk assessment' })).toBeVisible();
    await page.getByRole('button', { name: 'Continue' }).click();

    await expect(page.getByRole('heading', { name: 'Check your answers and declare' })).toBeVisible();
    const summary = page.locator('.govuk-summary-list');
    await expect(summary.getByText('Grand Juggling Extravaganza', { exact: true })).toBeVisible();
    // Untouched fields survive the round trip through the earlier stage.
    await expect(summary.getByText('Alex Applicant', { exact: true })).toBeVisible();
  });

  // Regression: an uploaded file's reference used to be silently wiped by revisiting an EARLIER
  // stage via a "Change" link and continuing back through the file-upload stage without
  // reselecting anything (browsers can never pre-fill a file input, so there's nothing to
  // reselect). CoerceFieldValues didn't skip "file-upload" fields, and a browser's empty
  // <input type="file"> still posts a real (zero-byte, empty-filename) multipart section for its
  // field name — enough to satisfy form.TryGetValue — so the generic text-field branch stamped an
  // explicit "" over the field before ApplyFileUploadsAsync ever got a chance to leave it alone.
  // See Program.cs's CoerceFieldValues.
  test('an uploaded file survives a "Change" round trip through an earlier, unrelated stage', async ({ page }) => {
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

    await expect(page.getByRole('heading', { name: 'Risk assessment' })).toBeVisible();
    await page.getByLabel('Risk assessment or public liability insurance certificate').setInputFiles({
      name: 'risk-assessment.pdf',
      mimeType: 'application/pdf',
      buffer: Buffer.from('%PDF-1.4\n% test risk assessment\n')
    });
    await page.getByRole('button', { name: 'Continue' }).click();

    await expect(page.getByRole('heading', { name: 'Check your answers and declare' })).toBeVisible();
    const summary = page.locator('.govuk-summary-list');
    await expect(summary.getByText('risk-assessment.pdf')).toBeVisible();

    // Change an EARLIER, unrelated stage — never touches the file input at all.
    await page.getByRole('button', { name: /Change name of the event/i }).click();
    await expect(page.getByRole('heading', { name: 'About the event' })).toBeVisible();
    await page.getByLabel('Name of the event').fill('Big Top Juggling Gala 2');
    await page.getByRole('button', { name: 'Continue' }).click();

    // Back through Risk assessment without reselecting a file — Continue straight through.
    await expect(page.getByRole('heading', { name: 'Risk assessment' })).toBeVisible();
    await expect(page.getByText('Currently uploaded: risk-assessment.pdf')).toBeVisible();
    await page.getByRole('button', { name: 'Continue' }).click();

    await expect(page.getByRole('heading', { name: 'Check your answers and declare' })).toBeVisible();
    await expect(summary.getByText('Big Top Juggling Gala 2', { exact: true })).toBeVisible();
    // The file survived a stage resubmission it was never part of.
    await expect(summary.getByText('risk-assessment.pdf')).toBeVisible();
  });

  // Regression: a boolean shown read-only on a *later* stage's summary-list used to be reset to
  // false when that stage was submitted — CoerceFieldValues treated every rendered field as a form
  // field, and an unchecked checkbox is indistinguishable from an absent one. Submitting "check
  // your answers" (whose summary displays hasDangerousProps) therefore wiped the applicant's own
  // "yes", and the caseworker reviewing a fire act read "No". The applicant's own summary was
  // right, so this only ever showed up one stage later — see Program.cs's CoerceFieldValues.
  test('a boolean answered "yes" survives a later stage that only displays it read-only', async ({ browser }) => {
    const applicantContext = await browser.newContext();
    const applicantPage = await applicantContext.newPage();

    await loginAs(applicantPage, DEMO_USERS.applicant);
    await applicantPage.getByLabel('Full name').fill('Bool Survivor');
    await applicantPage.getByLabel('Email address').fill('bool@example.test');
    await applicantPage.getByRole('button', { name: 'Continue' }).click();

    await applicantPage.getByLabel('Name of the event').fill('Boolean Survival Gala');
    await applicantPage.getByLabel('Day').fill('1');
    await applicantPage.getByLabel('Month').fill('9');
    await applicantPage.getByLabel('Year').fill('2026');
    await applicantPage.getByLabel('Number of jugglers taking part').fill('2');
    await applicantPage.getByLabel('This act involves fire, knives, or other dangerous props').check();
    await applicantPage.getByRole('button', { name: 'Continue' }).click();

    await applicantPage.getByLabel('How are you mitigating the risk?').fill('12 metre exclusion zone, HSE-aligned.');
    await applicantPage.getByRole('button', { name: 'Continue' }).click();

    // Correct here even before the fix — the damage happened on *submitting* this stage.
    await expect(applicantPage.getByRole('heading', { name: 'Check your answers and declare' })).toBeVisible();
    await expect(
      applicantPage.locator('.govuk-summary-list__row', { hasText: 'Fire, knives or other dangerous props' })
    ).toContainText('Yes');

    await applicantPage.getByLabel('I confirm the details above are correct').check();
    await applicantPage.getByRole('button', { name: 'Submit application' }).click();
    await expect(applicantPage.getByText('A caseworker is reviewing your application.')).toBeVisible();

    const caseworkerContext = await browser.newContext();
    const caseworkerPage = await caseworkerContext.newPage();
    await loginAs(caseworkerPage, DEMO_USERS.caseworker);
    await caseworkerPage.getByRole('button', { name: 'Pick up' }).click();
    await expect(caseworkerPage.getByText('With you')).toBeVisible();
    await caseworkerPage.getByRole('link', { name: 'Review' }).click();

    await expect(
      caseworkerPage.locator('.govuk-summary-list__row', { hasText: 'Fire, knives or other dangerous props' })
    ).toContainText('Yes');

    await applicantContext.close();
    await caseworkerContext.close();
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
