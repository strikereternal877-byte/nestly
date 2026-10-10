import { defineConfig, globalIgnores } from "eslint/config";
import nextVitals from "eslint-config-next/core-web-vitals";
import nextTs from "eslint-config-next/typescript";

const eslintConfig = defineConfig([
  ...nextVitals,
  ...nextTs,
  // Default ignores of eslint-config-next - next lint used to apply these
  // implicitly; the flat-config CLI does not, so they're explicit here.
  // e2e/** added on top: next lint never linted it either (its own default
  // scope was pages/app/components/lib/src, never a sibling e2e/ directory),
  // and eslint-plugin-react's bundled copy in eslint-config-next@16.3.5
  // throws ("contextOrFilename.getFilename is not a function") when a React
  // rule is applied to a non-JSX file - see frontend/admin-web's identical
  // config for the fuller writeup of that crash.
  globalIgnores([".next/**", "out/**", "build/**", "next-env.d.ts", "e2e/**"]),
]);

export default eslintConfig;
