import { defineConfig } from "vitest/config";

/**
 * Root Vitest configuration for all workspaces. Tests are co-located with
 * source using the `.test.ts` suffix. Property-based tests use fast-check
 * with pragmatic iteration counts (see design.md -> Testing Strategy).
 *
 * `root` is pinned to the config directory (canonically cased by the OS) so
 * the collector and task lookup always agree on the Windows drive-letter case.
 * Without this, invoking from a lowercase `d:\` cwd made Vitest register tasks
 * under one casing and look them up under another, leaving every `describe`
 * callback un-run during collection ("No test suite found").
 */
export default defineConfig({
  root: import.meta.dirname,
  test: {
    // Vitest covers only the TypeScript workspaces (widget, shared frontend
    // contracts, and the CDK infra). The backend is C#/.NET 10 and is tested
    // separately with xUnit + FsCheck (see backend/Vera.slnx).
    include: ["**/src/**/*.test.ts"],
    exclude: ["**/node_modules/**", "**/dist/**"],
    environment: "node",
    globals: false,
  },
});
