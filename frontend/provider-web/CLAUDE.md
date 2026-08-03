# CLAUDE.md — frontend/provider-web

Scaffold notes for this project, per the global instruction to record which
Claude Code skills were used when starting a new project.

## Skills used

None. This scaffold was built by directly reading and mirroring
`frontend/admin-web` (its `package.json`, config files, `lib/auth.ts`,
`lib/api.ts`, `lib/jwt.ts`, `components/ui.tsx`, `RequireAdminAuth.tsx`, and
several `*-api.ts`/`*-types.ts` + page pairs) as the explicit reference
implementation, plus `docs/PROVIDER.md` for module context and
`docs/CODING-STANDARDS.md` for naming/readability conventions. No Skill tool
invocation added anything beyond what those source files and the task brief
already specified, so none is listed here.

## What this project is

A Next.js 14 provider-facing portal (task 151, docs/PROVIDER.md) for
`backend/provider-api`: OTP-based registration/login, profile & KYC
onboarding, availability management, and job/earnings screens (the latter
two intentionally handle HTTP 501 gracefully - their backends are pending
sibling tasks #147/#148).

Same stack and conventions as `frontend/admin-web`: Next 14.2.35, React 18,
TanStack Query v5 for data fetching, react-hook-form + zod for forms,
Tailwind for styling, sessionStorage-based JWT session (namespaced
`nestly.provider.*`).
