// @ts-check
import js from "@eslint/js";
import tseslint from "@typescript-eslint/eslint-plugin";
import tsparser from "@typescript-eslint/parser";
import globals from "globals";

/**
 * Flat ESLint config (ESLint v9) for the Vera monorepo. Lints TypeScript
 * sources across all workspaces; build output and dependencies are ignored.
 */
export default [
  {
    ignores: ["**/dist/**", "**/node_modules/**", "**/*.d.ts"],
  },
  js.configs.recommended,
  {
    files: ["**/*.ts"],
    languageOptions: {
      parser: tsparser,
      parserOptions: {
        ecmaVersion: 2022,
        sourceType: "module",
      },
      globals: {
        // The widget is browser code and the backend tooling is Node; include
        // both global sets. TypeScript's own checker catches genuinely
        // undefined names, so `no-undef` is disabled below (recommended for TS).
        ...globals.node,
        ...globals.browser,
      },
    },
    plugins: {
      "@typescript-eslint": tseslint,
    },
    rules: {
      ...tseslint.configs.recommended.rules,
      // `no-undef` is redundant (and produces false positives) for TypeScript,
      // which already resolves identifiers against lib/DOM types.
      "no-undef": "off",
      "@typescript-eslint/no-unused-vars": [
        "error",
        { argsIgnorePattern: "^_", varsIgnorePattern: "^_" },
      ],
    },
  },
];
