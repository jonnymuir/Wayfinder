import { test, expect } from '@playwright/test';
import { DEMO_USERS, loginAs, resetApp } from './fixtures';

// The risk assessment upload is its own dedicated stage between "About the event" and "Check
// your answers and declare" — always shown, always optional. This engine's Split gateways always
// fan out to every one of their routes (there's no conditional single-branch choice), so a stage
// can never be skipped based on an earlier answer; making the field itself unconditionally
// optional, rather than gating the whole stage's visibility on hasDangerousProps, is what keeps
// this a genuine standalone screen instead of a same-page reveal.
async function completeYourDetailsAndEventDetails(page: import('@playwright/test').Page, options: { dangerousProps: boolean }) {
  await loginAs(page, DEMO_USERS.applicant);
  await page.getByLabel('Full name').fill('Alex Applicant');
  await page.getByLabel('Email address').fill('alex@example.test');
  await page.getByRole('button', { name: 'Continue' }).click();

  await expect(page.getByRole('heading', { name: 'About the event' })).toBeVisible();
  await page.getByLabel('Name of the event').fill('Big Top Juggling Gala');
  await page.getByLabel('Day').fill('1');
  await page.getByLabel('Month').fill('9');
  await page.getByLabel('Year').fill('2026');
  await page.getByLabel('Number of jugglers taking part').fill('12');
  if (options.dangerousProps) {
    await page.getByLabel('This act involves fire, knives, or other dangerous props').check();
  }
  await page.getByRole('button', { name: 'Continue' }).click();
  await expect(page.getByRole('heading', { name: 'Risk assessment' })).toBeVisible();
}

test.describe('File upload: risk assessment', () => {
  test.beforeEach(async ({ request }) => resetApp(request));

  test('the upload is optional even when dangerous props are declared, provided mitigation notes cover it instead', async ({ page }) => {
    // The upload itself stays optional (this stage's own field-level `required` never changes) —
    // but juggling-licence.json now carries a cross-stage StageDefinition.Validations rule
    // (StageValidationTests.cs / citizen-journey.spec.ts's dedicated test cover the rule itself):
    // dangerousProps + no upload requires the mitigation notes to say something concrete instead.
    // Skipping BOTH is covered separately; this proves the "upload OR notes" relationship, not
    // "neither is ever required".
    await completeYourDetailsAndEventDetails(page, { dangerousProps: true });
    await page.getByLabel('How are you mitigating the risk?').fill('15 metres exclusion zone maintained throughout.');
    await page.getByRole('button', { name: 'Continue' }).click();

    await expect(page.getByRole('heading', { name: 'Check your answers and declare' })).toBeVisible();
    const summary = page.locator('.govuk-summary-list');
    await expect(summary.getByText('Yes', { exact: true })).toBeVisible(); // hasDangerousProps
    await expect(summary.getByText('Not provided', { exact: true })).toBeVisible();

    await page.getByLabel('I confirm the details above are correct').check();
    await page.getByRole('button', { name: 'Submit application' }).click();
    // The applicant waits at their own Join gateway cursor — see citizen-journey.spec.ts for
    // why this is a genuine "please wait" status, not ACCESS_DENIED or the caseworker's own
    // stage content.
    await expect(page.getByText('A caseworker is reviewing your application.')).toBeVisible();
  });

  test('a valid file completes the journey and shows on check your answers', async ({ page }) => {
    await completeYourDetailsAndEventDetails(page, { dangerousProps: true });

    await page.getByLabel('Risk assessment or public liability insurance certificate').setInputFiles({
      name: 'risk-assessment.pdf',
      mimeType: 'application/pdf',
      buffer: Buffer.from('%PDF-1.4 test risk assessment content'),
    });
    await page.getByRole('button', { name: 'Continue' }).click();

    await expect(page.getByRole('heading', { name: 'Check your answers and declare' })).toBeVisible();
    const summary = page.locator('.govuk-summary-list');
    await expect(summary.getByText('risk-assessment.pdf', { exact: true })).toBeVisible();

    await page.getByLabel('I confirm the details above are correct').check();
    await page.getByRole('button', { name: 'Submit application' }).click();
    await expect(page.getByText('A caseworker is reviewing your application.')).toBeVisible();
  });

  test('an oversized file is rejected server-side, with a field-scoped error', async ({ page }) => {
    await completeYourDetailsAndEventDetails(page, { dangerousProps: false });

    // 6MB — over the blueprint's declared 5MB maxSizeBytes. A real <input type="file"> has no
    // client-side size constraint of its own, so this only ever gets caught server-side.
    await page.getByLabel('Risk assessment or public liability insurance certificate').setInputFiles({
      name: 'oversized.pdf',
      mimeType: 'application/pdf',
      buffer: Buffer.alloc(6 * 1024 * 1024, '0'),
    });
    await page.getByRole('button', { name: 'Continue' }).click();

    await expect(page.getByRole('heading', { name: 'Risk assessment' })).toBeVisible();
    await expect(page.locator('.govuk-error-message', { hasText: 'must be smaller than 5MB' })).toBeVisible();
  });

  test('a disallowed file type is rejected server-side, with a field-scoped error', async ({ page }) => {
    await completeYourDetailsAndEventDetails(page, { dangerousProps: false });

    await page.getByLabel('Risk assessment or public liability insurance certificate').setInputFiles({
      name: 'malware.exe',
      mimeType: 'application/octet-stream',
      buffer: Buffer.from('not a real risk assessment'),
    });
    await page.getByRole('button', { name: 'Continue' }).click();

    await expect(page.getByRole('heading', { name: 'Risk assessment' })).toBeVisible();
    await expect(page.locator('.govuk-error-message', { hasText: 'must be one of' })).toBeVisible();
  });

  test('a caseworker can see and download the uploaded risk assessment', async ({ browser }) => {
    const applicantContext = await browser.newContext();
    const applicantPage = await applicantContext.newPage();
    await completeYourDetailsAndEventDetails(applicantPage, { dangerousProps: true });

    const fileContent = '%PDF-1.4 test risk assessment content';
    await applicantPage.getByLabel('Risk assessment or public liability insurance certificate').setInputFiles({
      name: 'risk-assessment.pdf',
      mimeType: 'application/pdf',
      buffer: Buffer.from(fileContent),
    });
    await applicantPage.getByRole('button', { name: 'Continue' }).click();
    await expect(applicantPage.getByRole('heading', { name: 'Check your answers and declare' })).toBeVisible();
    await applicantPage.getByLabel('I confirm the details above are correct').check();
    await applicantPage.getByRole('button', { name: 'Submit application' }).click();
    await expect(applicantPage.getByText('A caseworker is reviewing your application.')).toBeVisible();

    const caseworkerContext = await browser.newContext();
    const caseworkerPage = await caseworkerContext.newPage();
    await loginAs(caseworkerPage, DEMO_USERS.caseworker);
    await caseworkerPage.getByRole('link', { name: 'Review' }).click();

    // The summary row proves IServiceRequestFileStorage's persisted reference round-trips back
    // to a display value (see ProcessManagerEngine.GetDisplayValue's file-upload branch), not
    // just the storage key/bytes underneath it — and that row's value *is* the download link
    // itself (FieldRenderPayload.FileUrl), rather than a filename in plain text with a separate
    // list of links elsewhere on the page.
    const fileRow = caseworkerPage.locator('.govuk-summary-list__row', {
      hasText: 'Risk assessment / insurance certificate',
    });
    const downloadLink = fileRow.getByRole('link', { name: 'risk-assessment.pdf' });
    await expect(downloadLink).toBeVisible();

    // The download itself exercises OpenReadAsync — the read half of IServiceRequestFileStorage
    // that nothing else in this app ever calls — and proves the bytes served back are exactly
    // what the applicant uploaded, not just that a link with the right filename exists.
    const href = await downloadLink.getAttribute('href');
    const response = await caseworkerPage.request.get(href!);
    expect(response.ok()).toBeTruthy();
    expect(response.headers()['content-type']).toBe('application/pdf');
    expect(await response.text()).toBe(fileContent);

    await applicantContext.close();
    await caseworkerContext.close();
  });
});
