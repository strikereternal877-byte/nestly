# FRONTEND.md

Frontend Development Standards & Architecture

## PURPOSE

This document defines the standards, conventions, and architectural guidelines for developing the Nestly frontend application.

It establishes a consistent approach for building scalable, maintainable, reusable, and high-performance user interfaces using React, Next.js, and TypeScript.

This document is the single source of truth for frontend development.

## TECHNOLOGY STACK

### Framework

- Next.js

### UI Library

- React

### Language

- TypeScript

### Styling

- Tailwind CSS

### State Management

- TanStack Query
- React Context (where appropriate)

### Forms

- React Hook Form
- Zod Validation

## FRONTEND ARCHITECTURE

The application follows a component-driven architecture.

Application

↓

Pages / Routes

↓

Feature Modules

↓

Reusable Components

↓

Shared Utilities

Each layer has a clear responsibility.

## PROJECT STRUCTURE

Organize the application into logical feature modules.

Typical structure:

- App / Pages
- Features
- Components
- Layouts
- Services
- Hooks
- Context
- Types
- Utilities
- Assets

Group code by business feature rather than technical type whenever practical.

## COMPONENT DESIGN

Components should be:

- Small
- Reusable
- Independent
- Composable
- Easy to test

Prefer composition over inheritance.

Avoid large, monolithic components.

## COMPONENT RESPONSIBILITIES

A component should have a single responsibility.

Separate:

- UI rendering
- Business logic
- API communication
- State management

Keep presentation components focused on rendering.

## STATE MANAGEMENT

Choose the simplest appropriate solution.

Use:

- Local State for component-specific data
- Context for shared application state
- TanStack Query for server state

Avoid unnecessary global state.

## DATA FETCHING

Server communication should:

- Be centralized
- Be reusable
- Handle loading states
- Handle error states
- Support caching
- Support retry where appropriate

Components should not contain direct API implementation logic.

## ROUTING

Routing should be:

- Predictable
- Feature-oriented
- Easy to navigate

Protect secured routes appropriately.

## FORM MANAGEMENT

Forms should:

- Use React Hook Form
- Validate using Zod
- Display user-friendly validation messages
- Prevent invalid submissions

Separate validation logic from presentation.

## TYPE SAFETY

Use TypeScript throughout the application.

Guidelines:

- Avoid any
- Prefer explicit types
- Use interfaces or type aliases appropriately
- Share common types across features
- Keep types close to the business domain

## ERROR HANDLING

Frontend should gracefully handle:

- API failures
- Validation errors
- Network issues
- Unexpected exceptions

Display meaningful messages to users.

Avoid exposing technical details.

## LOADING STATES

Every asynchronous operation should provide:

- Loading indicators
- Disabled actions where appropriate
- Smooth user experience

Avoid blocking the entire interface unnecessarily.

## PERFORMANCE

Optimize for performance by:

- Lazy loading pages and components
- Code splitting
- Memoization where beneficial
- Avoiding unnecessary re-renders
- Optimizing images and assets

Measure before optimizing.

## REUSABILITY

Prefer reusable:

- Components
- Hooks
- Utilities
- Layouts

Avoid duplicated UI logic.

## ACCESSIBILITY

The application should support:

- Semantic HTML
- Keyboard navigation
- Screen readers
- Proper labels
- Sufficient color contrast

Accessibility should be considered during development.

## RESPONSIVE DESIGN

**Mobile is the primary platform, not one of three equally-weighted targets.**
The large majority of real usage is mobile — design and build for a phone
screen first, then verify the result still holds up on tablet and desktop,
not the other way around.

This applies differently by app, because the three apps have different
real-world users:

- **customer-web** and **provider-web** are mobile-first without
  qualification. Customers browse and book on their phones; providers work
  the entire job lifecycle (accept, navigate, complete, upload proof) from
  the field, usually one-handed, often on a mobile network, sometimes with
  a phone in one hand and a tool in the other. Every screen in these two
  apps must be designed for a phone viewport (~375–430px) first.
- **admin-web** is desk-first — an operations tool typically used at a
  workstation — but must stay usable on a tablet for on-the-go checks
  (approve a provider, look up a booking) without horizontal scrolling or
  broken layout. It does not need bottom-tab navigation or a phone-first
  redesign.

Concretely, "mobile-first" means, at minimum:

- Every interactive element meets a minimum touch target (44×44pt),
  not a size tuned for mouse pointers.
- Primary actions (Continue, Pay Now, Accept Job) stay reachable without
  scrolling to find them — a sticky/fixed action bar on long screens, not
  a button at the bottom of a page.
- Forms use the correct mobile keyboard per field (`inputMode`, `type`,
  `autoComplete`) — numeric for OTP/pincode, `tel` for phone, etc.
- Modals and dialogs use a full-screen or bottom-sheet pattern below a
  breakpoint instead of a small centered desktop dialog.
- Data tables collapse to a card/list layout below a breakpoint instead of
  forcing horizontal scroll.
- Root layouts respect iOS/Android safe areas (notch, home indicator) for
  any fixed header or footer.
- Performance is budgeted for a mobile network (3G/4G), not just desktop
  broadband — this is where a slow page costs the most real users.

Layouts should adapt consistently across supported devices, with mobile as
the baseline every other breakpoint is verified against, not an
afterthought checked once desktop is done.

## WALLET AND RECURRING-PLAN SCREENS (customer-web)

| Route / component | What it does |
| --- | --- |
| `/booking/summary` - "Auto-schedule this service" card | The plan-type tiles (`PlanKindPicker`): **Daily plan** (always every day; each visit paid as it is booked, optionally from the wallet - `DailyPlanPayment` shows balance, per-visit price, low-balance warning and the unpaid-visit rule) or **Prepaid plan** (any cadence, the whole total - `N x price` - paid in one checkout, the default). The choice (`repeatPlanKind`, `planWalletAuto`) is kept in the booking draft so a detour to add money does not lose it. The wallet card offers "Add money to wallet" when top-ups are on |
| `/wallet` | Balance, ledger (a top-up reads "Money added") and the **Add money** panel (`WalletAddMoney`: quick amounts, validated amount, sandbox "Complete" button or the PayU redirect). `?addMoney=1` scrolls to it; `?returnTo=` (validated) is remembered so the result screen can send the customer back |
| `/` (mobile width) - "Enable your location" | One tap on **Allow location** is enough (`LocationPrompt` + `lib/geolocation.ts`). The position is requested only after the browser's own permission question is answered: while it is open the dialog says "Tap Allow on your browser's location prompt" and carries on the moment the customer allows (no short timer runs against them, and a browser that fails the call at once while its prompt is open is not read as a refusal). A real refusal - the permission state actually "denied" - stops at once with "Location is blocked for this site" and a manual city pick. A phone whose location provider is cold gets spaced retries (1s, 2s, 30s overall deadline) instead of an instant second failure |
| admin-web `/payments/wallet-top-ups` | Customers' wallet top-ups, newest first (`WalletTopUpsController`, `payments.read`): a summary strip - pending, **stuck** (pending past 30 minutes) and **need review** (a gateway callback disagreed with the amount; nothing was credited), which doubles as the "needs attention" filter, and the total credited in the last 24 hours to set against the gateway's settlement report - then a table with status, why a row needs attention, and the gateway order id / payment reference. **Reconcile now** (`payments.write`) asks the gateway about a pending or failed top-up and says what happened (credited, marked failed, still pending, nothing to apply). Linked from the Payments tab strip |
| admin-web `/bookings/recurring-plans` | Besides the status tiles: each plan shows how it is paid for (**Prepaid** with "payment due" / "paid through", or **Per visit** with Wallet / Auto-charge) and, when paused, why (the customer / visits went unpaid / auto-charge failed / support); filters for "paused because" and payment type; **Details** shows the customer's contact and wallet balance and the visits the plan has produced (upcoming first). With `bookings.write`: **Pause**, **Resume** (also for a plan the system paused) and **Cancel plan**, each asking for a reason that goes to the audit trail and telling the customer. A plan support paused shows the customer "Paused by our support team" with no Resume button |
| admin-web `/customers/{id}` wallet tab | The wallet history shows, for every entry, where it came from (`WALLET_SOURCE_LABELS`: top-up, refund, used on a booking, late reschedule fee, ...), when, the amount and the **balance after it** |
| `/wallet/topup/{id}/return` | Where PayU returns the browser: polls the top-up, asks the gateway to verify after 20 s, and shows added / failed / still-confirming with the right next step |
| `/recurring-bookings` | Per plan: Pause / Resume, **Skip visits** and **Change time** (`RecurringPlanDialogs`; pay-as-you-go plans only), the reason a plan is paused (you / unpaid visits with an "Add money" link / payment problem), "Skipping visits until", and "Cancel remaining visits" for a prepaid plan. The card also shows the next visit's time window, the price per visit, the visits already booked, the wallet balance and how many visits it covers (with an "Add money" nudge when it covers fewer than 3), and - when a visit is waiting for payment, for example because the wallet ran short - an alert with a **Pay now** link. **Pause asks first** (`Pause this plan?`): it says pausing is free, that visits already booked are not cancelled and are still charged, and the cancellation charge; Skip, Change time and Cancel state the same real numbers (`lib/recurring-plan.ts`) |
| `/bookings/{id}/reschedule` | The rules before confirming, in the customer's own times and amounts (from `GET /bookings/{id}/reschedule/eligibility`, nothing worked out in the browser): until when rescheduling is free and what a late one is recorded as (`freeRescheduleEndsAt`, `lateRescheduleFeePercentage`, `lateFeeIfRescheduledNow`), the last moment a reschedule is allowed (`lastRescheduleAt`), how many reschedules are left, a cancellation fee a late one would lock in (`cancellationFeeLockedIn`) and what happens to the professional already on the booking (`RescheduleRules`). With late-fee collection switched on, a late reschedule's fee is **taken from the wallet** (off, the rules say it is only recorded on the booking and nothing is charged - `lateFeeIsCollected`): the rules say so and show the wallet balance, and when the wallet cannot cover it (`lateFeeShortfall` above 0) Confirm is disabled and a notice says exactly how much to add (with an "Add money" link to `/wallet?addMoney=1` when top-ups are on), or to cancel instead. After confirming the customer stays on the page and reads what happened (`RescheduleResult`): was / now, reschedules left, no fee or the late fee and that it was taken from the wallet (`feeCollected`), and whether the professional stays, was taken off, or none was assigned yet (`professional`). "Back to booking" is a real button |
| `/bookings/{id}/cancel` | The cancellation policy before confirming and the result after, each with the **why**: when free cancellation ends (cut-off time), what a late fee is a percentage of and its amount, and - when it applies - a charge carried over from an earlier late reschedule that set the fee even though the clock alone would have made it free. Built by `CancellationChargesExplainer` from numbers the API sends (`freeCancellationEndsAt`, `feeBasisAmount`, `earlierRescheduleCharge`); the policy as the customer saw it is kept so the result explains against it even if the live policy changes once the booking is cancelled. A late reschedule fee already paid is explained too ("you already paid ... and it counts toward this fee, so you don't pay twice") from `cancellationFeeBeforeCredit` and `rescheduleFeeCredited`; the wallet ledger labels the debit "Late reschedule fee" (and "Late reschedule fee returned" if a reschedule failed after the fee was taken). admin-web's booking detail shows each reschedule's late fee and whether it was taken from the customer's wallet |

Add money is only offered when `GET /wallet/top-up/config` says it is enabled (it is off by
default), so with the feature off none of these controls appear. `submitToPayU`
(`lib/payu-checkout.ts`) is shared by the booking payment page and the top-up panel.

## SECURITY

Frontend should never:

- Store secrets
- Trust client-side validation alone
- Expose sensitive information
- Assume authorization

Treat all client input as untrusted.

## CODE QUALITY

Frontend code should prioritize:

- Readability
- Simplicity
- Maintainability
- Reusability
- Consistency

Follow project coding standards.

## FRONTEND REVIEW CHECKLIST

Before completing a feature, verify:

- Components are reusable.
- TypeScript types are defined.
- Validation is implemented.
- Loading and error states exist.
- Responsive behavior is verified.
- Accessibility requirements are considered.
- No duplicated UI logic exists.
- Performance impact is acceptable.

## OUT OF SCOPE

This document does not define:

- Business requirements
- System architecture
- API design
- Backend implementation
- Database design
- Security implementation
- Testing strategy
- Deployment process

Refer to the corresponding project documents for these topics.
