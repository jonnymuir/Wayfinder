import type { Preview } from '@storybook/web-components-vite';

const preview: Preview = {
  parameters: {
    controls: {
      matchers: {
        color: /(background|color)$/i,
        date: /Date$/
      }
    },
    a11y: {
      // Fail the Vitest run on any violation — the equivalent of the old
      // .storybook/test-runner.js postVisit hook throwing out of axe-playwright's checkA11y.
      test: 'error',
      // Same rule scope that test-runner.js configured: WCAG 2.0 / 2.1, levels A and AA only.
      options: {
        runOnly: {
          type: 'tag',
          values: ['wcag2a', 'wcag2aa', 'wcag21a', 'wcag21aa']
        }
      }
    }
  }
};

export default preview;
