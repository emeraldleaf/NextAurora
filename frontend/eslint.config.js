import js from '@eslint/js'
import importPlugin from 'eslint-plugin-import'
import reactHooks from 'eslint-plugin-react-hooks'
import reactRefresh from 'eslint-plugin-react-refresh'
import tseslint from 'typescript-eslint'

// Boundary enforcement per frontend/CLAUDE.md "Architecture rules": features never import
// another feature's internals (only its index.ts public API), and shared/ never imports
// from features. Violations are build errors, not review comments — the same
// enforcement-spectrum thinking as the backend's architecture tests.
export default tseslint.config(
  { ignores: ['dist', 'coverage'] },
  {
    files: ['**/*.{ts,tsx}'],
    extends: [js.configs.recommended, ...tseslint.configs.strictTypeChecked],
    languageOptions: {
      parserOptions: {
        projectService: {
          allowDefaultProject: ['*.config.ts'],
        },
        tsconfigRootDir: import.meta.dirname,
      },
    },
    plugins: {
      'react-hooks': reactHooks,
      'react-refresh': reactRefresh,
      import: importPlugin,
    },
    settings: {
      'import/resolver': { typescript: true },
    },
    rules: {
      ...reactHooks.configs.recommended.rules,
      // Effect deps are facts, not knobs — see frontend/CLAUDE.md "Effects discipline".
      'react-hooks/exhaustive-deps': 'error',
      'react-refresh/only-export-components': ['warn', { allowConstantExport: true }],
      'import/no-restricted-paths': [
        'error',
        {
          zones: [
            {
              target: './src/features/!(catalog)/**/*',
              from: './src/features/catalog/!(index.ts)',
              message:
                'Import from the feature public API (features/catalog) — not its internals. See frontend/CLAUDE.md.',
            },
            {
              target: './src/shared/**/*',
              from: './src/features/**/*',
              message: 'shared/ must stay domain-agnostic — it never imports from features. See frontend/CLAUDE.md.',
            },
            {
              target: './src/core/**/*',
              from: './src/features/**/*',
              message: 'core/ holds singletons only — it never imports from features. See frontend/CLAUDE.md.',
            },
          ],
        },
      ],
    },
  },
  {
    // Test files: relax type-aware strictness that fights test idioms.
    files: ['**/*.test.{ts,tsx}', 'src/test/**/*'],
    rules: {
      '@typescript-eslint/no-unsafe-assignment': 'off',
      '@typescript-eslint/no-floating-promises': 'off',
    },
  },
)
