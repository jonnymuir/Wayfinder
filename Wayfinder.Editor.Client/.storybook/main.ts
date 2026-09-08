import type { StorybookConfig } from '@storybook/web-components-vite';

const config: StorybookConfig = {
  stories: ['../src/**/*.stories.@(ts|tsx)'],
  addons: ['@storybook/addon-a11y', '@storybook/addon-docs', '@storybook/addon-vitest'],

  framework: {
    name: '@storybook/web-components-vite',
    options: {}
  },

  viteFinal: async (config) => {
    config.resolve ??= {};
    config.resolve.dedupe = Array.from(
      new Set([...(config.resolve.dedupe ?? []), 'react', 'react-dom'])
    );
    return config;
  }
};

export default config;
