# SOFTWARE REQUIREMENTS SPECIFICATION (SRS) – VERSION 2

Home Services Marketplace Platform — Customer Web Platform + Admin Panel

- **Document Version:** 2.0
- **Prepared Date:** 09 July 2026
- **Document Type:** Detailed Functional, Technical, and Operational Requirements Specification
- **Intended Use:** Product Design, Architecture, Database Design, API Design, UI/UX, Development, QA, UAT, DevOps, Operations

## TABLE OF CONTENTS

1. Document Control
1. Purpose and Scope
1. Product Vision and Business Objectives
1. Platform Scope and Boundaries
1. Stakeholders and User Personas
1. Assumptions, Constraints, and Dependencies
1. Product Modules Overview
1. Business Domain Model
1. End-to-End Customer Journey
1. End-to-End Admin Journey
1. Customer UI Functional Requirements
1. Admin Panel Functional Requirements
1. Booking Lifecycle Specification
1. Pricing, Coupon, Payment, Refund, Wallet Rules
1. Serviceability and Slot Engine Rules
1. Customer Support and Issue Management
1. Review and Rating Management
1. CMS / Content / Banner Management
1. Notification and Communication Framework
1. Role-Based Access Control (RBAC)
1. Audit, Logs, and Traceability Requirements
1. Master Data and Configuration Requirements
1. Data Model Requirements and Conceptual Entities
1. API Requirements and Module-Wise Endpoint Inventory
1. UI Screen Inventory and Screen-Level Requirements
1. Validation Rules and Error Handling Matrix
1. Reports, Dashboards, and Exports
1. Security Requirements
1. Non-Functional Requirements
1. Integration Requirements
1. Status Transition Matrix
1. Sequence Flow Specifications
1. Acceptance Criteria
1. Open Decisions / Implementation Clarifications
1. Next Deliverables

## 1. DOCUMENT CONTROL

### 1.1 Document Information

- **Document Name:** Software Requirements Specification (SRS) – Home Services Marketplace Platform
- **Version:** 2.0
- **Document Type:** Detailed Product + Functional + Technical Requirement Specification
- **Prepared For:** Product, Engineering, QA, Architecture, Delivery
- **Prepared On:** 09-Jul-2026
- **Prepared By:** Product / Solution Design
- **Status:** Detailed Draft for Architecture and Engineering

### 1.2 Revision History

| Version | Date | Description |
|---|---|---|
| 1.0 | 09-Jul-2026 | Initial functional SRS baseline |
| 2.0 | 09-Jul-2026 | Detailed implementation-oriented SRS with workflow, validation, RBAC, screen, API, and operational requirements |

### 1.3 Intended Audience

- Product Owner / Business Owner
- Solution Architect
- Backend Engineers
- Frontend Engineers
- QA / Test Engineers
- UI/UX Team
- DevOps / SRE
- Operations / Customer Support / Finance / Marketing Admin Teams

## 2. PURPOSE AND SCOPE

### 2.1 Purpose

This document defines the **complete functional, operational, system, validation, workflow, and integration requirements** for a **production-grade home services marketplace platform** that allows customers to book home services and enables internal teams to manage service catalog, bookings, pricing, serviceability, customer issues, promotions, and operational controls through an admin panel.

This SRS is intended to be the **single source of truth** for:

- System architecture design
- Database schema design
- API specification
- Frontend screen design
- Workflow automation
- Role-based admin design
- Test case creation
- Release planning and UAT

### 2.2 Scope Covered in SRS v2

This SRS covers:

- Customer-facing web application
- Admin panel
- Core booking, payment, refund, coupon, slot, and support workflows
- Role-based admin management
- Reporting, audit, and operational controls
- Conceptual data model and API inventory

### 2.3 Out of Scope for Current Phase (but architecture should remain extensible)

- Dedicated service provider mobile app / panel
- Real-time professional dispatch optimization
- Advanced AI recommendation engine
- Loyalty program and subscription model
- Franchise/branch operations
- B2B enterprise booking workflows
- Multi-country taxation and compliance variations

## 3. PRODUCT VISION AND BUSINESS OBJECTIVES

### 3.1 Product Vision

To build a scalable, trusted, and operationally efficient digital marketplace for at-home services where customers can browse, book, pay, reschedule, and review services, while internal business teams can manage the full service commerce lifecycle through a secure and configurable administrative platform.

### 3.2 Business Objectives

The platform must: 1. Allow customers to discover and book services with minimal friction. 2. Support service categories, packages, add-ons, pricing, and city-wise serviceability. 3. Provide robust booking lifecycle management including payment, cancellation, reschedule, refund, and issue handling. 4. Enable internal teams to manage categories, pricing, coupons, bookings, support, and reporting. 5. Be scalable for multi-city, multi-category operations. 6. Preserve transaction history and auditability for all critical events.

## 4. PLATFORM SCOPE AND BOUNDARIES

### 4.1 Included Platforms

- Customer Website / Web UI
- Admin Panel / Backoffice UI
- Backend services and business logic layer
- Database and master data
- Integrations with payment, communication, and optional map services

### 4.2 Excluded Direct End-User Interfaces in Current Scope

- Native customer mobile apps
- Provider / professional app
- Third-party vendor portal

### 4.3 Supported Service Models

The platform shall support:

- Fixed-price service packages
- Add-on based service customization
- Slot-based service booking
- City / pincode / locality-based serviceability
- Prepaid and optional pay-later / COD models based on business configuration

## 5. STAKEHOLDERS AND USER PERSONAS

### 5.1 Customer Personas

- New visitor browsing categories
- Registered customer booking services
- Repeat customer with saved addresses and prior bookings
- Customer raising support issues
- Customer cancelling/rescheduling and expecting refund visibility

### 5.2 Internal Business Personas

- Super Admin
- Operations Admin
- Booking Admin
- Category / Catalog Admin
- Pricing Admin
- Customer Support Agent
- Marketing / Coupon Admin
- Finance / Refund Admin
- Analyst / Reporting User

## 6. ASSUMPTIONS, CONSTRAINTS, AND DEPENDENCIES

### 6.1 Assumptions

- Services are delivered at customer location.
- A service may be restricted by city, pincode, locality, or zone.
- Slot availability is centrally managed in Phase 1 and may later integrate with professional capacity planning.
- Payment gateway and communication integrations are available from external vendors.

### 6.2 Constraints

- Historical bookings must remain immutable from a pricing snapshot perspective.
- All critical financial and admin actions must be auditable.
- Public website must remain performant during traffic spikes.
- Admin operations must be permission-controlled.

### 6.3 External Dependencies

- Payment gateway
- SMS/Email/WhatsApp providers
- Optional geolocation/maps provider
- Hosting / cloud infrastructure
- CDN / media storage if needed

## 7. PRODUCT MODULES OVERVIEW

### 7.1 Customer-Facing Modules

1. Public website and content pages
1. Authentication and account management
1. Location / address / serviceability selection
1. Category and service discovery
1. Service detail and package selection
1. Add-ons and booking summary
1. Slot selection
1. Coupon application
1. Payment and booking confirmation
1. Order history and order detail
1. Cancellation and reschedule
1. Review and rating
1. Wallet / refund visibility
1. Support and issue reporting
1. Notification and profile preferences

### 7.2 Admin Modules

1. Admin login and RBAC
1. Dashboard and analytics
1. Customer management
1. Category management
1. Service/package management
1. Add-on management
1. Pricing management
1. Serviceability management
1. Slot configuration
1. Booking management
1. Cancellation / reschedule / refund handling
1. Coupon and campaign management
1. Support ticket management
1. Review moderation
1. CMS and banners
1. Notification templates
1. Reports and exports
1. Audit logs
1. Settings / configuration
1. Admin user management

## 8. BUSINESS DOMAIN MODEL

### 8.1 Core Business Objects

- Customer
- Customer Address
- City / Zone / Pincode / Locality
- Category
- Service / Package
- Add-on
- Price Rule / Service Price
- Slot / Slot Rule / Availability Rule
- Booking
- Booking Item / Booking Snapshot
- Payment Transaction
- Refund Transaction
- Coupon
- Wallet Ledger / Credit Ledger
- Review / Rating
- Support Ticket
- CMS Content / Banner
- Notification Template / Notification Event
- Admin User / Role / Permission
- Audit Event

### 8.2 Transactional Principle

All customer-facing bookings and financial records shall be stored with **snapshot integrity**. This means the booking must preserve the exact catalog, price, tax, slot, address, and discount context at the time of booking even if catalog data changes later.

## 9. END-TO-END CUSTOMER JOURNEY

### 9.1 Discovery to Booking Flow

1. Customer lands on website.
1. Customer selects city / service location.
1. Customer browses categories and services.
1. Customer opens service detail page.
1. Customer selects package/add-ons.
1. Customer selects address and slot.
1. Customer logs in/registers if required.
1. Customer reviews price breakdown and applies coupon if eligible.
1. Customer completes payment / confirms booking.
1. System creates booking and sends confirmation.
1. Customer tracks booking in account.
1. Customer may cancel or reschedule subject to policy.
1. After completion, customer can rate/review or raise support issues.

### 9.2 Customer Journey Variants

- Guest browse → login only at checkout
- Repeat customer with saved address and coupon
- Failed payment → retry flow
- Booking created but payment pending (if business supports deferred confirmation)
- Customer cancels and receives wallet or gateway refund
- Customer reschedules to a future slot

## 10. END-TO-END ADMIN JOURNEY

### 10.1 Catalog and Service Operations

1. Admin creates categories.
1. Admin creates services/packages and add-ons.
1. Admin configures pricing and serviceability.
1. Admin configures slots and blackout dates.
1. Services go live on customer website.

### 10.2 Booking Operations

1. Customer booking appears in admin booking list.
1. Admin views booking details and status.
1. Admin may update operational status, assist customer, cancel/reschedule if authorized.
1. Admin may initiate refund if required.
1. Admin monitors support tickets and escalations.

### 10.3 Marketing / Support / Reporting Operations

1. Admin creates coupons and banners.
1. Admin manages public content and FAQs.
1. Admin views dashboards and exports reports.
1. Admin reviews ratings and service performance.

## 11. CUSTOMER UI FUNCTIONAL REQUIREMENTS

## 11.1 Public Website / Home Page

### 11.1.1 Objective

Provide a high-conversion landing experience where customers can quickly identify service categories, select their location, and start a booking journey.

### 11.1.2 Components

- Header with logo, location selector, category navigation, login/account entry
- Hero banner / primary CTA
- Search bar
- Category listing tiles
- Featured services / promotions
- Trust markers / ratings / benefits
- FAQs / testimonials
- Footer with static links and support information

### 11.1.3 Functional Requirements

- Homepage shall load without authentication.
- Homepage shall display service categories filtered by selected city/serviceability where applicable.
- Banner visibility, order, and content shall be admin-configurable.
- Search should be available globally from homepage.
- Customer should be able to change city from homepage.

## 11.2 Authentication and Account Management

### 11.2.1 Registration

The system shall support registration via:

- Mobile number + OTP
- Email + OTP/password (configurable)
- Social login (future optional)

#### Data Fields

- First name
- Last name or full name
- Mobile number
- Email
- Password if password-based auth is enabled
- Consent checkbox for Terms & Privacy
- Marketing opt-in (optional)

#### Business Rules

- Mobile number uniqueness validation required.
- Email uniqueness configurable.
- OTP expiration and retry limits must be enforced.
- Account status should be stored (active, blocked, unverified, deleted/soft-deleted).

### 11.2.2 Login

Supported modes:

- Mobile OTP
- Email/password
- Mobile/password (optional)
- Social auth (future)

#### Functional Rules

- Session/token shall be securely managed.
- Login attempts shall be throttled.
- Forgot password / reset password flow shall be supported if password login exists.
- Logout shall invalidate active session/token as per architecture design.

### 11.2.3 Profile Management

Customer shall be able to:

- View profile
- Edit name, email, optional profile data
- Change mobile/email subject to verification
- Manage communication preferences

## 11.3 Address Management

### 11.3.1 Address Book Features

Customer shall be able to:

- Add address
- Edit address
- Delete address
- Mark one address as default
- Select an address during booking

### 11.3.2 Address Data Fields

- Address label
- Address line 1
- Address line 2
- Landmark
- Pincode
- City
- State
- Latitude
- Longitude
- Contact person name
- Contact mobile
- Default flag

### 11.3.3 Business Rules

- Address must be serviceability-validated before booking confirmation.
- Customer can store multiple addresses.
- Deleted addresses should not remove address snapshots already used in bookings.

## 11.4 Location Selection and Serviceability

### 11.4.1 Location Inputs

Customer may select location by:

- City selection
- Pincode selection
- Address selection
- Optional map pin (future)

### 11.4.2 Serviceability Rules

The system shall validate serviceability using one or more dimensions:

- City
- Pincode
- Zone
- Locality
- Category-level serviceability
- Service-level serviceability

### 11.4.3 Customer Experience Rules

- Non-serviceable categories/services should not be bookable.
- If customer selects a non-serviceable address during checkout, the system shall block booking and show a clear message.
- The system may allow browsing of non-serviceable services but must block booking; this should be configurable.

## 11.5 Category Discovery and Service Catalog

### 11.5.1 Category Listing Page

Each category card may include:

- Category name
- Image/icon
- Short description
- Starting price if applicable
- CTA to view services

### 11.5.2 Category Detail Page

Each category page shall support:

- Category banner
- Category title and description
- Service list under the category
- FAQs
- Related services
- Category-specific testimonials / reviews if configured

### 11.5.3 Service Listing Within Category

Each service/package card shall support:

- Service name
- Short description
- Estimated duration
- Base or starting price
- Discount display if applicable
- Rating summary if enabled
- Add / View Details CTA

## 11.6 Service Detail Page

### 11.6.1 Service Detail Content

A service detail page shall include:

- Service/package title
- Detailed description
- Inclusions
- Exclusions
- Add-ons available
- Estimated duration
- Pricing information
- FAQs
- Terms / preparation notes
- Cancellation/reschedule summary
- Reviews and rating summary

### 11.6.2 Booking Readiness Rules

Each service may define:

- Is login mandatory before add/checkout?
- Is address mandatory before slot?
- Is slot mandatory?
- Is quantity allowed?
- Are add-ons mandatory/optional?
- Is custom note allowed?
- Are images/uploads allowed in future?

## 11.7 Cart / Booking Summary

### 11.7.1 Cart Capability

The system should support one of the following models, configurable by business:

- Single service booking only
- Multiple service booking within same category
- Multiple service booking across categories (future optional)

### 11.7.2 Booking Summary Data

Booking summary shall display:

- Selected service/package(s)
- Add-ons
- Address
- Slot
- Quantity if applicable
- Base price
- Add-on price
- Taxes
- Coupon discount
- Wallet credit used
- Final payable amount
- Cancellation policy summary

### 11.7.3 Actions Available

Customer shall be able to:

- Remove service/add-on
- Change address
- Change slot
- Apply/remove coupon
- Change quantity if allowed
- Proceed to payment

## 11.8 Slot Selection and Availability

### 11.8.1 Slot Engine Inputs

Slot availability may depend on:

- Service
- Category
- City / pincode / locality
- Date
- Cutoff rules
- Holiday / blackout dates
- Capacity rules
- Advance booking days

### 11.8.2 Slot Selection UI

The system shall display:

- Available dates
- Available time windows for selected date
- Disabled slots with reason optional
- Earliest available slot suggestion optional

### 11.8.3 Slot Validation Rules

- Slot must be revalidated at booking confirmation.
- Slot selection must fail gracefully if the slot is no longer available.
- Reschedule flow shall use the same slot validation engine.

## 11.9 Pricing and Checkout Calculation

### 11.9.1 Supported Pricing Components

- Base service price
- Add-on price
- Quantity-based price
- Visit charge / inspection charge
- Tax / GST
- Convenience fee / platform fee (optional)
- Coupon discount
- Wallet / credit deduction
- Cancellation / reschedule fee if applicable

### 11.9.2 Price Calculation Rules

- Final price must be calculated server-side.
- Frontend price shown is indicative until server validation at checkout.
- Booking creation shall use a final price snapshot.
- If any price component changes before booking confirmation, customer must be shown updated price before proceeding.

## 11.10 Coupon Application

### 11.10.1 Coupon Inputs

Customer enters a coupon code at checkout.

### 11.10.2 Coupon Validation Dimensions

Coupon applicability may depend on:

- Validity dates
- Active status
- Customer segment
- First order / repeat order rule
- Min order amount
- Category / service / city applicability
- Per-user usage count
- Overall campaign usage count
- Max discount amount

### 11.10.3 Customer Experience

- Coupon application shall show discount value and success message.
- Invalid coupon shall show a meaningful error message.
- Coupon removal shall recompute final payable amount.

## 11.11 Payment Flow

### 11.11.1 Supported Payment Modes

- Online payment gateway
- Wallet/credit
- Cash after service / COD (configurable)
- Partial payment / advance payment (future optional)

### 11.11.2 Payment Business Modes

The system shall support one of the following booking patterns: 1. **Pay first, then booking confirm** 2. **Create pending booking, confirm after payment success** 3. **Create booking and mark payment pending** (if business permits)

### 11.11.3 Payment Functional Requirements

- Payment initiation request shall be generated securely.
- Payment callback/webhook shall be verified.
- Duplicate callback handling shall be idempotent.
- Payment failure shall allow retry.
- Booking-payment mapping shall be preserved.
- Payment transaction history shall be available to admin.

## 11.12 Booking Creation and Confirmation

### 11.12.1 Booking Creation Preconditions

Booking creation shall require:

- Valid customer identity/session where required
- Valid service and active catalog state
- Serviceable address
- Valid slot
- Valid pricing snapshot
- Payment status satisfying booking policy
- Coupon validation success if applied

### 11.12.2 Booking Snapshot Fields

At minimum, booking shall capture:

- Booking ID
- Customer snapshot
- Address snapshot
- Service snapshot
- Add-on snapshot
- Slot snapshot
- Price breakdown snapshot
- Coupon snapshot
- Tax snapshot
- Payment summary snapshot
- Booking status
- Notes / customer instructions
- Channel/source
- Created timestamp

### 11.12.3 Confirmation Experience

Customer shall see:

- Booking success page
- Booking ID
- Service summary
- Slot and address
- Amount paid / payable
- Cancellation/reschedule guidance
- Link to booking detail page

## 11.13 Order History and Booking Detail

### 11.13.1 Booking List

Customer account shall show:

- Upcoming bookings
- Completed bookings
- Cancelled bookings
- Payment pending/failed bookings if applicable

#### List Fields

- Booking ID
- Service summary
- Date/slot
- Booking status
- Payment status
- Amount
- Address summary

### 11.13.2 Booking Detail View

Booking detail page shall display:

- Full service summary
- Address snapshot
- Slot
- Status timeline
- Price and payment details
- Coupon applied
- Refund details if any
- Cancellation/reschedule CTA if eligible
- Review CTA if completed
- Support CTA

## 11.14 Cancellation

### 11.14.1 Cancellation Eligibility Rules

Cancellation eligibility may depend on:

- Booking status
- Time remaining before slot
- Category/service policy
- Whether service has started
- Whether payment was made
- Whether cancellation fee applies

### 11.14.2 Cancellation Data Captured

- Booking ID
- Cancellation actor (customer/admin/system)
- Cancellation reason
- Timestamp
- Refund amount / charge
- Refund mode
- Internal notes if admin

### 11.14.3 Customer Experience

- Customer sees cancellation policy summary.
- Customer confirms cancellation.
- System shows refund outcome or wallet credit outcome.
- Confirmation notification is sent.

## 11.15 Reschedule

### 11.15.1 Reschedule Eligibility Rules

Reschedule shall be allowed only if:

- Booking is in an eligible status
- Reschedule window has not expired
- New slot is available
- Reschedule count limit has not been exceeded if configured

### 11.15.2 Reschedule Data Captured

- Original slot
- New slot
- Reschedule actor
- Timestamp
- Reason
- Any fee impact

### 11.15.3 Customer Experience

- Eligible future slots shown
- Customer confirms new slot
- Booking detail updates immediately
- Confirmation notification sent

## 11.16 Ratings and Reviews

### 11.16.1 Review Eligibility

Only completed bookings shall be reviewable.

### 11.16.2 Review Data

- Rating value
- Review text
- Optional issue tags
- Submission timestamp
- Booking reference
- Customer reference

### 11.16.3 Review Rules

- One booking should have one primary review record unless edit/re-review rules are enabled.
- Reviews may be hidden/moderated by admin.
- Review submission may be time-limited if business wants post-service review windows.

## 11.17 Wallet / Credit / Refund Visibility

### 11.17.1 Wallet Screen

If wallet/credit is enabled, customer shall be able to view:

- Current balance
- Credit/debit entries
- Booking references
- Expiry if applicable

### 11.17.2 Refund Tracking

Refund status should be visible against booking:

- Initiated
- Processed
- Failed
- Credited to wallet
- Gateway refund reference if available

## 11.18 Support and Issue Raising

### 11.18.1 Support Categories

Customer shall be able to raise issues related to:

- Payment
- Refund
- Cancellation/reschedule
- Service quality
- Professional behavior
- Wrong charge / pricing dispute
- General support

### 11.18.2 Ticket Submission Data

- Booking reference (optional for generic issues, mandatory for booking issues)
- Issue category
- Subject / summary
- Description
- Attachment optional
- Contact preference optional

### 11.18.3 Ticket Tracking

Customer may view ticket status and ticket history if exposed in UI.

## 12. ADMIN PANEL FUNCTIONAL REQUIREMENTS

## 12.1 Admin Authentication and Session Management

### 12.1.1 Admin Login

Admin panel shall require secure authentication.

#### Required Features

- Username/email login
- Strong password policy
- Optional MFA
- Login throttling
- Account lockout after repeated failure
- Session timeout
- Forced logout on password reset if desired

### 12.1.2 Admin Session Security

- Token/session expiry rules
- Device/IP metadata logging
- Audit of login/logout/failure events

## 12.2 Admin User, Role, and Permission Management

### 12.2.1 Admin User Management

Super Admin shall be able to:

- Create admin users
- Edit admin user profile
- Assign role(s)
- Activate/deactivate users
- Reset password / send reset link

### 12.2.2 Role Management

Roles may include:

- Super Admin
- Operations Admin
- Booking Admin
- Support Admin
- Catalog Admin
- Pricing Admin
- Marketing Admin
- Finance Admin
- Read-only Analyst

### 12.2.3 Permission Matrix Capability

Permissions should be configurable at least by module/action:

- View / Create / Edit / Delete / Export / Approve / Refund / Cancel / Reschedule / Publish

## 12.3 Admin Dashboard

### 12.3.1 KPI Widgets

Dashboard may show:

- Bookings today / this week / this month
- Revenue today / period
- Upcoming bookings
- Cancellation count
- Refund amount
- Coupon redemptions
- Support tickets open
- Top categories / cities
- Repeat customer ratio

### 12.3.2 Dashboard Filters

- Date range
- City
- Category
- Booking status
- Payment status

## 12.4 Customer Management

### 12.4.1 Customer List

Admin shall be able to search and filter customers by:

- Name
- Mobile
- Email
- Registration date
- Booking count
- City
- Account status

### 12.4.2 Customer Detail View

Admin view shall include:

- Profile
- Addresses
- Booking history
- Wallet/refund history
- Coupons used
- Support tickets
- Internal notes

### 12.4.3 Customer Actions

Authorized admins may:

- Edit selected fields
- Block/unblock customer
- Add notes/tags
- Assist with booking support actions

## 12.5 Category Management

### 12.5.1 Category CRUD

Admin shall be able to:

- Create category
- Edit category
- Activate/deactivate category
- Control display order
- Upload category image/banner
- Manage category description and SEO content

### 12.5.2 Category Fields

- Category name
- Slug
- Description
- Icon/image
- Banner image
- Active flag
- Featured flag
- Sort order
- SEO title/meta/description
- Category-level FAQs (optional)

## 12.6 Service / Package Management

### 12.6.1 Service CRUD

Admin shall be able to create and manage services under a category.

### 12.6.2 Service Fields

- Service name
- Category
- Slug
- Short description
- Long description
- Inclusions
- Exclusions
- Duration
- Pricing type
- Base price
- Tax applicable flag
- Add-on allowed flag
- Quantity allowed flag
- Cancellation policy mapping
- Reschedule policy mapping
- Featured flag
- Active flag
- Sort order
- SEO metadata
- Gallery images

### 12.6.3 Service Options

The system should support service-level configuration such as:

- Fixed price package
- Variable/add-on price
- Inspection-based flag
- Slot required flag
- Address required flag
- Customer note allowed flag

## 12.7 Add-On Management

### 12.7.1 Add-On CRUD

Admin shall be able to create add-ons and map them to services.

### 12.7.2 Add-On Fields

- Add-on name
- Description
- Price
- Service mapping
- Quantity allowed
- Mandatory/optional
- Active status
- Sort order

## 12.8 Pricing Management

### 12.8.1 Pricing Rules

Admin shall be able to configure:

- Base service price
- Add-on price
- City-wise price
- Promotional price
- Tax configuration
- Visit charge
- Convenience fee
- Cancellation fee
- Reschedule fee

### 12.8.2 Pricing Governance

- Effective date support recommended
- Price change audit required
- Historical bookings must not be altered

## 12.9 Serviceability Management

### 12.9.1 Geography Master

Admin shall manage:

- City
- State
- Pincode
- Zone / locality (if required)

### 12.9.2 Serviceability Mapping

Admin shall be able to define:

- Which categories are active in which city
- Which services are active in which pincode/locality
- Service blackout in selected areas
- Temporary service suspension

## 12.10 Slot and Availability Management

### 12.10.1 Slot Configuration

Admin shall configure:

- Slot windows
- Day-of-week availability
- Holiday blackout
- Cutoff rules
- Advance booking limit
- Max bookings per slot if capacity is used
- City/category/service applicability

### 12.10.2 Availability Override

Admin shall be able to block:

- Entire day
- Selected slot
- Selected city/category/service/date combination

## 12.11 Booking Management

### 12.11.1 Booking List

Filters should include:

- Booking ID
- Customer name/mobile
- Category/service
- Date range
- Slot date
- Booking status
- Payment status
- Coupon
- City / pincode
- Source

### 12.11.2 Booking Detail View

Admin booking detail shall show:

- Booking header summary
- Customer snapshot
- Address snapshot
- Service snapshot
- Price and payment details
- Status timeline
- Cancellation / reschedule history
- Refund history
- Support tickets linked
- Internal notes
- Audit trail summary

### 12.11.3 Booking Actions

Authorized admins may:

- Cancel booking
- Reschedule booking
- Update selected operational statuses
- Re-send confirmation
- Trigger refund flow
- Add internal note
- Mark issue/escalation

## 12.12 Coupon and Campaign Management

### 12.12.1 Coupon Creation Fields

- Coupon code
- Coupon name
- Start date
- End date
- Discount type
- Discount amount / percentage
- Max discount cap
- Min order amount
- Applicable cities/categories/services
- New user / repeat user rule
- Usage limits
- Per-user limits
- Active status

### 12.12.2 Coupon Reporting

Admin should be able to see:

- Redemptions
- Discount total
- Booking count using coupon
- Conversion and abuse indicators

## 12.13 Payment, Refund, and Financial Operations

### 12.13.1 Payment Transaction View

Admin shall see:

- Booking reference
- Transaction reference
- Payment mode
- Amount
- Status
- Gateway status / reference
- Created/updated timestamps

### 12.13.2 Refund Operations

Authorized admins shall be able to:

- Initiate full refund
- Initiate partial refund
- Credit wallet instead of gateway refund where policy allows
- Record refund reason
- Track refund status

### 12.13.3 Refund Rules

Refund amount calculation must respect:

- Cancellation policy
- Service completion status
- Partial fulfilment cases
- Payment captured amount
- Manual admin override rules if permitted

## 12.14 Support Ticket Management

### 12.14.1 Ticket Listing

Filters:

- Ticket ID
- Booking ID
- Customer
- Category
- Priority
- Status
- Assigned agent
- Date range

### 12.14.2 Ticket Workflow

Support/admin should be able to:

- View ticket details
- Add response / note
- Assign to team/user
- Change status
- Mark escalated
- Link refund / cancellation / booking action
- Mark resolved / closed

## 12.15 Review Moderation

Admin shall be able to:

- View reviews by service, category, date, rating
- Hide/unhide reviews
- Flag abusive content
- Export reviews

## 12.16 CMS / Banner / Static Content Management

### 12.16.1 CMS Scope

Admin shall manage:

- Home banners
- Category banners
- Promotional blocks
- FAQ entries
- About / policy pages
- Footer links
- SEO content for key public pages

### 12.16.2 Content Features

- Draft / publish status
- Publish start/end date optional
- Media upload support
- Sort order

## 12.17 Notification Template Management

### 12.17.1 Template Types

Templates shall be manageable for:

- OTP
- Registration welcome
- Booking confirmation
- Payment success/failure
- Cancellation
- Reschedule
- Refund
- Support ticket acknowledgement/update

### 12.17.2 Template Requirements

- Channel-specific templates
- Variable placeholders
- Preview/test capability recommended
- Audit of template changes

## 12.18 Reports and Exports

### 12.18.1 Standard Reports

- Booking report
- Revenue report
- Refund report
- Cancellation report
- Coupon usage report
- Customer report
- Category performance report
- City performance report
- Support ticket report
- Review report

### 12.18.2 Export Requirements

- CSV / Excel export
- Filter-based export
- Permission-based access
- Large report export may be asynchronous

## 12.19 System Configuration

Admin-configurable settings should include:

- Booking rules
- Slot rules
- Cancellation rules
- Reschedule rules
- Tax settings
- Wallet settings
- Coupon settings
- Communication provider settings (or references)
- Public contact details
- Feature flags

## 13. BOOKING LIFECYCLE SPECIFICATION

## 13.1 Booking Lifecycle States

Recommended base lifecycle: 1. Initiated 2. Payment Pending 3. Payment Failed 4. Confirmed 5. Awaiting Fulfilment / Awaiting Assignment 6. Assigned / Scheduled 7. In Progress 8. Completed 9. Cancelled by Customer 10. Cancelled by Admin/System 11. Rescheduled 12. Refund Pending 13. Refunded / Partially Refunded

### 13.2 Status Governance

- Every booking shall have one current status.
- Status history shall be stored separately.
- Customer-visible status label may differ from internal operational status.
- Invalid transitions must be blocked.

## 14. PRICING, COUPON, PAYMENT, REFUND, WALLET RULES

## 14.1 Pricing Rules

- Final price = service base + add-ons + quantity + applicable charges + tax – discounts – wallet.
- Server is source of truth for final price.
- Booking stores full price snapshot.

## 14.2 Coupon Rules

- Coupon validation must happen server-side.
- Coupon discount must not exceed cap.
- Coupon usage must be linked to customer and booking.

## 14.3 Payment Rules

- Every payment attempt must have a transaction record.
- Payment callback handling must be idempotent.
- Payment and booking reconciliation should be possible.

## 14.4 Refund Rules

- Refund may be gateway refund, wallet credit, or mixed model.
- Refund must reference booking and original payment where applicable.
- Refund status lifecycle should be trackable.

## 14.5 Wallet Rules

- Wallet ledger must be append-only or traceable.
- Every credit/debit must reference source event.
- Wallet usage should be reflected in booking price summary.

## 15. SERVICEABILITY AND SLOT ENGINE RULES

## 15.1 Serviceability Dimensions

The system should support serviceability by:

- City
- Pincode
- Zone
- Locality
- Category
- Service
- Date blackout if needed

## 15.2 Slot Engine Rules

- Slot availability should be generated or fetched based on serviceability and configured slot rules.
- Same-day cutoff rules must be supported.
- Advance booking days must be configurable.
- Slot capacity model should be future-ready even if initial implementation is simple.

## 16. CUSTOMER SUPPORT AND ISSUE MANAGEMENT

### 16.1 Ticket Categories

- Booking issue
- Payment issue
- Refund issue
- Service quality complaint
- Professional conduct complaint
- Pricing issue
- Technical issue
- General inquiry

### 16.2 Ticket Lifecycle

Suggested statuses:

- Open
- In Progress
- Waiting for Customer
- Escalated
- Resolved
- Closed

### 16.3 Ticket Entity Requirements

Ticket shall capture:

- Ticket ID
- Customer ID
- Booking ID if linked
- Category
- Priority
- Description
- Current status
- Assigned admin/support user
- Resolution summary
- Created/updated timestamps

## 17. REVIEW AND RATING MANAGEMENT

### 17.1 Review Data Requirements

- Booking ID
- Customer ID
- Service/category reference
- Rating
- Review text
- Review status (visible/hidden/flagged)
- Submitted date

### 17.2 Moderation Rules

- Admin may hide abusive or policy-violating reviews.
- Original review record should be retained for audit.

## 18. CMS / CONTENT / BANNER MANAGEMENT

### 18.1 CMS Entities

- Page
- Banner
- FAQ
- SEO metadata block
- Content section / block

### 18.2 Content Rules

- Public content should support publish status.
- Content versioning is recommended for policy pages.
- Banner visibility by page/placement should be configurable.

## 19. NOTIFICATION AND COMMUNICATION FRAMEWORK

### 19.1 Trigger Events

Notifications may be triggered for:

- OTP
- Welcome / registration
- Booking confirmed
- Payment failed / success
- Reschedule
- Cancellation
- Refund
- Support ticket updates

### 19.2 Notification Record Requirements

Each notification event log should store:

- Event type
- Recipient
- Channel
- Template used
- Payload / variables reference
- Delivery status
- Error reason if failed

## 20. ROLE-BASED ACCESS CONTROL (RBAC)

### 20.1 Role Matrix Concept

Each admin role should have permission sets for:

- Dashboard
- Customer module
- Catalog module
- Pricing module
- Booking module
- Refund module
- Coupon module
- CMS module
- Support module
- Reports / export
- Admin user management

### 20.2 Permission Action Types

At minimum:

- View
- Create
- Edit
- Delete / Archive
- Approve
- Cancel
- Reschedule
- Refund
- Export
- Publish
- Configure

## 21. AUDIT, LOGS, AND TRACEABILITY REQUIREMENTS

### 21.1 Audit Scope

Audit records shall exist for:

- Customer account events
- Booking creation and lifecycle changes
- Cancellation and reschedule actions
- Coupon usage
- Payment and refund updates
- Admin login/logout
- Category/service/pricing changes
- Serviceability changes
- CMS changes
- Role/permission changes

### 21.2 Audit Event Minimum Fields

- Event ID
- Actor type (customer/admin/system)
- Actor ID
- Entity type
- Entity ID
- Action type
- Old value
- New value
- Timestamp
- Source IP / metadata if available
- Correlation ID if implemented

## 22. MASTER DATA AND CONFIGURATION REQUIREMENTS

### 22.1 Master Data Domains

Expected master/configuration domains:

- Cities / states / pincodes / localities
- Categories
- Services
- Add-ons
- Tax slabs / fee settings
- Slot definitions
- Cancellation reason master
- Support category master
- Review tag master
- Notification template master
- Admin role master
- Status master / enum references
- Feature flags / config values

## 23. DATA MODEL REQUIREMENTS AND CONCEPTUAL ENTITIES

### 23.1 Customer Domain

- customer
- customer_auth_identity / session / otp
- customer_address

### 23.2 Catalog Domain

- category
- service
- service_addon
- service_media
- service_faq
- service_price
- service_city_mapping / serviceability mapping

### 23.3 Booking Domain

- booking
- booking_item
- booking_addon_item
- booking_status_history
- booking_reschedule_history
- booking_cancellation
- booking_note / admin note

### 23.4 Financial Domain

- payment_transaction
- payment_attempt
- refund_transaction
- wallet_ledger
- coupon
- coupon_redemption

### 23.5 Support and Experience Domain

- review
- support_ticket
- support_ticket_comment
- notification_event

### 23.6 Admin / Control Domain

- admin_user
- admin_role
- admin_permission
- role_permission_mapping
- cms_page
- banner
- faq
- audit_log
- system_config

## 24. API REQUIREMENTS AND MODULE-WISE ENDPOINT INVENTORY

This section is an API inventory, not the full API contract. Full request/response specification will be produced in the API document.

## 24.1 Customer Auth APIs

- Register
- Send OTP
- Verify OTP
- Login
- Refresh token/session
- Logout
- Forgot password
- Reset password

## 24.2 Customer Profile APIs

- Get profile
- Update profile
- Get addresses
- Add address
- Edit address
- Delete address
- Set default address

## 24.3 Catalog APIs

- Get home page content
- Get categories
- Get category detail
- Get services by category
- Get service detail
- Search services/categories
- Get FAQs/content blocks if separate

## 24.4 Serviceability and Slot APIs

- Validate serviceability for address/pincode
- Get available slots for service + address + date
- Revalidate slot

## 24.5 Pricing / Coupon APIs

- Calculate booking price
- Validate/apply coupon
- Remove coupon / recompute
- Fetch wallet balance / eligible credits

## 24.6 Booking APIs

- Create booking
- Get booking list
- Get booking detail
- Cancel booking
- Get reschedule slots
- Reschedule booking

## 24.7 Payment APIs

- Create payment order
- Verify payment
- Payment webhook callback endpoint
- Retry payment
- Get payment status if needed

## 24.8 Review and Support APIs

- Submit review
- Get eligible review booking
- Raise support ticket
- Get ticket list
- Get ticket detail / updates

## 24.9 Admin Auth APIs

- Admin login
- Admin logout
- Change/reset password

## 24.10 Admin Catalog APIs

- Category CRUD
- Service CRUD
- Add-on CRUD
- Pricing CRUD
- Serviceability CRUD
- Slot config CRUD

## 24.11 Admin Booking APIs

- Get bookings
- Get booking detail
- Cancel booking
- Reschedule booking
- Update booking status
- Add admin note

## 24.12 Admin Financial APIs

- Get payments
- Initiate refund
- Get refund status
- Wallet adjustment if allowed

## 24.13 Admin Coupon / CMS / Support APIs

- Coupon CRUD
- Banner/CMS CRUD
- Support ticket management
- Review moderation
- Dashboard/report APIs
- Admin user / role / permission APIs

## 25. UI SCREEN INVENTORY AND SCREEN-LEVEL REQUIREMENTS

## 25.1 Customer UI Screens

1. Home page
1. Category listing page
1. Category detail page
1. Service detail page
1. Login / registration / OTP screens
1. Profile page
1. Address list / add/edit address
1. Cart / booking summary page
1. Slot selection modal/page
1. Checkout / payment page
1. Booking success page
1. Booking history page
1. Booking detail page
1. Cancel booking flow
1. Reschedule booking flow
1. Wallet/refund page
1. Review submission page
1. Support ticket page
1. Static CMS pages

### 25.1.1 Minimum Screen Requirements

Each screen specification in UI design should define:

- Purpose
- Data shown
- Actions allowed
- Validation messages
- Empty state
- Error state
- Loading state
- Responsive behavior
- Permission/visibility rules where relevant

## 25.2 Admin Screens

1. Admin login
1. Dashboard
1. Customer list / detail
1. Category list / create / edit
1. Service list / create / edit
1. Add-on list / create / edit
1. Pricing list / edit
1. Serviceability configuration screens
1. Slot configuration screens
1. Booking list / detail
1. Refund / payment view
1. Coupon list / create / edit
1. Support ticket list / detail
1. Review moderation screen
1. CMS / banner management
1. Notification template management
1. Reports screen
1. Admin user / role management
1. Audit log screen
1. Settings/config screen

## 26. VALIDATION RULES AND ERROR HANDLING MATRIX

## 26.1 Customer Validation Categories

- Registration validation
- Login validation
- Address validation
- Serviceability validation
- Slot validation
- Coupon validation
- Payment validation
- Cancellation/reschedule eligibility validation
- Review eligibility validation

### 26.2 Admin Validation Categories

- Category/service field validation
- Pricing validation
- Coupon rule validation
- Refund validation
- Permission validation
- Booking status transition validation
- Content publishing validation

### 26.3 Error Handling Principles

- User-facing errors must be concise and actionable.
- Internal errors must be logged with diagnostic context.
- Payment and booking failures must be recoverable where possible.
- Idempotency and retry safety should be built for financial and callback flows.

## 27. REPORTS, DASHBOARDS, AND EXPORTS

### 27.1 Booking Reports

Fields may include:

- Booking ID
- Date
- Customer
- Service/category
- City
- Status
- Payment status
- Amount
- Coupon used
- Refund amount if any

### 27.2 Financial Reports

- Revenue by date/category/city
- Refund by date/reason
- Payment success/failure rate
- Coupon discount cost

### 27.3 Customer Reports

- New registrations
- Repeat customer bookings
- Average order value
- Customer lifetime metrics (future)

### 27.4 Support Reports

- Ticket volume by category
- Resolution time
- Escalation rate

### 27.5 Export Controls

- Permission-based
- Filter-based
- Audit of exports recommended

## 28. SECURITY REQUIREMENTS

### 28.1 Customer Security

- Secure auth/session handling
- OTP expiry and abuse control
- PII protection
- Rate limiting where needed

### 28.2 Admin Security

- Strong password policy
- MFA optional/recommended
- RBAC enforcement
- Sensitive action audit
- Secure session timeout

### 28.3 Application Security

System should be designed against:

- SQL injection
- XSS
- CSRF where relevant
- insecure direct object reference
- broken access control
- payment callback abuse
- replay / OTP brute force

### 28.4 Data Security

- Encrypt secrets and credentials
- Do not store prohibited payment card data
- Protect logs from leaking sensitive information

## 29. NON-FUNCTIONAL REQUIREMENTS

## 29.1 Performance

- Customer catalog pages must be optimized for quick browsing.
- Booking checkout and price calculation should be low-latency.
- Admin booking list and reports should support pagination, filters, and efficient query execution.

## 29.2 Scalability

The architecture must support:

- Multi-city growth
- Large booking volume
- Multiple categories and services
- Concurrent checkout traffic during promotions

## 29.3 Reliability

- Booking and payment flows must be transactionally safe.
- Duplicate payment or booking risk must be controlled.
- Critical workflows should support reconciliation.

## 29.4 Availability

- Customer booking flows should be highly available.
- Monitoring and alerting should exist for payment, booking, and notification failures.

## 29.5 Maintainability

- Modular service/domain separation preferred.
- Config-driven business rules preferred.
- Clear logging and auditability required.

## 29.6 Observability

- Structured application logs
- Error logs
- Audit logs
- Metrics and health checks
- Alerting for critical failures

## 30. INTEGRATION REQUIREMENTS

### 30.1 Payment Gateway

Capabilities required:

- Create payment order
- Verify payment result
- Refund API
- Webhook/callback support

### 30.2 Communication Providers

- SMS
- Email
- WhatsApp
- Push (future)

### 30.3 Maps / Geolocation

Optional for:

- Address autocomplete
- Lat/long capture
- Pincode mapping

## 31. STATUS TRANSITION MATRIX

### 31.1 Booking Transition Examples

- Initiated → Payment Pending
- Payment Pending → Confirmed
- Payment Pending → Payment Failed
- Confirmed → Cancelled
- Confirmed → Rescheduled
- Confirmed → Awaiting Fulfilment / Assigned
- Assigned → In Progress
- In Progress → Completed
- Cancelled → Refund Pending / Refunded where applicable

### 31.2 Ticket Transition Examples

- Open → In Progress
- In Progress → Waiting for Customer
- In Progress → Resolved
- Resolved → Closed
- Any open state → Escalated

### 31.3 Refund Transition Examples

- Initiated → Processing
- Processing → Refunded
- Processing → Failed

## 32. SEQUENCE FLOW SPECIFICATIONS

## 32.1 Booking Creation Sequence

1. Customer selects service and slot.
1. Customer selects address.
1. System validates serviceability.
1. System calculates price.
1. Customer applies coupon.
1. System validates coupon and recalculates amount.
1. Customer initiates payment.
1. Payment success is confirmed.
1. System revalidates slot/price/coupon state.
1. Booking is created with snapshots.
1. Booking confirmation event is triggered.
1. Notification is sent.

## 32.2 Cancellation Sequence

1. Customer/admin requests cancellation.
1. System validates cancellation policy and current status.
1. Refund amount / fee is computed.
1. Booking status changes to cancelled.
1. Refund transaction or wallet credit record is created.
1. Notification is sent.
1. Audit log is stored.

## 32.3 Reschedule Sequence

1. Customer/admin requests reschedule.
1. System validates eligibility.
1. System returns available slots.
1. New slot selected.
1. Slot is revalidated and booking updated.
1. Reschedule history is recorded.
1. Notification is sent.

## 33. ACCEPTANCE CRITERIA

### 33.1 Customer Acceptance

- Customer can register/login and manage profile.
- Customer can add address and book only serviceable services.
- Customer can browse category/service catalog and view pricing.
- Customer can select slot, apply coupon, pay, and create booking.
- Customer can see booking history and booking detail.
- Customer can cancel/reschedule eligible bookings.
- Customer can see refund outcome and raise support issues.
- Customer can submit review after completion.

### 33.2 Admin Acceptance

- Admin can manage categories, services, pricing, serviceability, slots, coupons, and CMS.
- Admin can manage bookings end-to-end.
- Admin can initiate refund, cancel/reschedule, and manage support tickets based on role.
- Admin actions are permission-controlled and audited.
- Reports and dashboards are available.

### 33.3 Platform Acceptance

- Booking, payment, refund, and notification flows are traceable and reliable.
- Historical booking data remains consistent.
- Security, logging, and audit requirements are implemented.

## 34. OPEN DECISIONS / IMPLEMENTATION CLARIFICATIONS (CLOSED)

Closed 2026-08-01, task 143. Each item below reflects the decision actually
embodied in the shipped implementation (Phases 1-7), not a new choice made
in isolation — resolving via "what the code already does" rather than
re-litigating settled ground, consistent with how ambiguity has been
handled throughout this backlog.

1. **Multi-service bookings.** A booking is not a single-service package: it
   holds a `BookingItem` collection (one per service line) plus
   `BookingAddOnItem`s per line (`Booking`, `BookingItem`,
   `BookingAddOnItem` in `Nestly.Domain`). Multiple services per booking is
   supported.
1. **COD.** Not implemented — no `PaymentMethod`/gateway code path exists
   for cash/offline collection anywhere in `backend/`. Payment is
   online-gateway-only (Razorpay-style) for all categories.
1. **Slot inventory.** Capacity-linked, not rule-based-only:
   `SlotWindow.MaxBookingsPerSlot` is enforced via an atomic
   conditional-update reservation (`SlotCapacityRepository`, task 135c),
   on top of the existing cutoff/blackout rule checks.
1. **Wallet/promotional credits.** Enabled, not deferred: `WalletLedgerEntry`
   (append-only, `WalletSourceType`-tagged) and `WalletEntryType` shipped in
   Phase 4 (tasks 67, 74), with a customer-facing balance/ledger API and
   frontend page (task 78).
1. **Partial refunds.** Both. `IRefundService.InitiatePartialRefundAsync`
   is used by admin-initiated refunds (task 117) and refund status
   (including partial `RefundType`) is exposed read-only to the customer
   via `RefundsController` per booking (SRS 11.17.2, task 78c) — full
   transaction detail stays admin-only, status/amount is customer-visible.
1. **Coupon stacking.** Configurable, default off:
   `SettingsContracts.AllowCouponStacking` (Coupon settings group, task
   131) — platform-wide toggle rather than a fixed rule, defaulting to one
   coupon per booking.
1. **Tax display.** Configurable, default inclusive:
   `SettingsContracts.TaxInclusivePricing` (System Configuration, task 131)
   — avoids hardcoding a choice that varies by market/category.
1. **Reschedule fee.** Category-specific and policy-driven, not a flat
   free/paid switch: `ReschedulePolicyOptions` + `RescheduleFeeCalculator`
   (Phase 5, task 82) compute the fee from the configured policy window and
   category.
1. **Provider assignment.** Deferred out of the Phase 1 admin workflow, then
   delivered as its own phase: reordered 2026-07-31 to run as Phase 7
   (`docs/PROVIDER.md`), landed 2026-08-01 — `BookingProviderAssignment`,
   admin assignment/reassignment (task 159), provider-api.
1. **Support ticket visibility.** Full timeline, not summary-only:
   `SupportTicketComment` history is returned to the customer via
   `SupportTicketsController` (consumer-api), matching the same
   assign/respond/escalate/resolve trail admin sees (task 120).
1. **Review publish.** Immediate, with post-publish moderation: `Review`
   defaults to `ReviewStatus.Visible` on submission; admins can `Hide` /
   `MakeVisible` / `Flag` afterward (`ReviewModerationService`, task 122) —
   pre-publish moderation queue was not built.
1. **Multilingual content.** Not implemented — no culture/locale
   infrastructure (`IStringLocalizer`, resource files, `Accept-Language`
   handling) exists anywhere in `backend/` or `frontend/`. English-only for
   the scope delivered through Phase 10.

## 35. NEXT DELIVERABLES

This SRS v2 is the foundation for the next project documents. The next recommended outputs are:

### 35.1 Database Design Specification

Should include:

- final entity list
- table-by-table schema
- columns and data types
- primary/foreign keys
- indexes
- status enums
- audit columns
- booking snapshot design
- payment/refund/wallet design
- coupon and CMS tables
- RBAC schema

### 35.2 API Specification

Should include:

- endpoint list
- request/response payloads
- auth requirements
- validation rules
- error codes
- idempotency expectations
- pagination/filtering format

### 35.3 Screen Specification Document

Should include:

- screen wireframes / component inventory
- field-level validation
- empty/error/loading states
- responsive behavior
- screen-to-API mapping

### 35.4 QA Test Matrix

Should include:

- functional scenarios
- negative scenarios
- booking/coupon/payment edge cases
- cancellation/refund cases
- admin RBAC test cases
- regression coverage

## FINAL SUMMARY

This **SRS v2** defines a **production-grade home services marketplace platform** with:

- **Customer Web UI**
- **Admin Panel**
- **booking lifecycle**
- **pricing / coupon / payment / refund / wallet**
- **serviceability and slot logic**
- **support and review workflows**
- **CMS, notification, reporting, RBAC, audit, and security requirements**
- **conceptual data model and API inventory**

This document is intended to be the **authoritative base** for the next engineering stages: 1. **System Architecture** 2. **Database Schema / ERD** 3. **API Specification** 4. **Screen/UI Specification** 5. **Development Planning** 6. **QA and UAT End of SRS v2**
