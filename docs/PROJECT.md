# PROJECT.md

Master project overview for AI agents and contributors.

## PROJECT

Enterprise-grade, production-ready Urban Services Platform built using a modular monolith architecture. The platform provides customers with a seamless service booking experience while enabling administrators and service providers to manage operations efficiently.

The project prioritizes scalability, maintainability, security, performance, and long-term evolution.

## BUSINESS OBJECTIVE

Build a highly scalable digital platform where customers can:

- Discover services
- Search by location
- View pricing
- Book appointments
- Make payments
- Track bookings
- Manage profiles
- Receive notifications
- Review services

Administrators manage the complete business lifecycle through a dedicated admin portal.

## PRIMARY GOALS

- Production-first development
- Enterprise architecture
- Modular design
- Clean code
- High performance
- Strong security
- Excellent developer experience
- Easy maintenance
- Future scalability

## TARGET USERS

- Customers
- Administrators
- Service Providers
- Operations Team
- Customer Support
- Finance Team
- Super Administrators

## CORE MODULES

- Identity
- Customer
- Provider
- Catalog
- Categories
- Services
- Pricing
- Availability
- Booking
- Payments
- Notifications
- Reviews
- Referral
- Subscription
- Chat
- Dashboard
- Reports
- Settings
- Audit
- Administration

## ARCHITECTURE

Use a modular monolith with clear module boundaries.

Keep business logic independent from infrastructure.

Design modules to allow future extraction into microservices if required.

## TECHNOLOGY STACK

Backend:

- .NET 8
- ASP.NET Core Web API
- Entity Framework Core
- PostgreSQL

Frontend:

- Next.js
- React
- TypeScript

Infrastructure:

- Docker
- REST APIs
- JWT Authentication

## DESIGN PRINCIPLES

Always prefer:

- SOLID
- DRY
- KISS
- Clean Architecture
- Feature-based organization
- Dependency Injection
- Separation of Concerns
- Reusable components

## DEVELOPMENT PRINCIPLES

Every implementation should be:

- Modular
- Reusable
- Testable
- Secure
- Performant
- Maintainable
- Extensible
- Production-ready

## DATA PRINCIPLES

Data must be:

- Accurate
- Consistent
- Auditable
- Secure
- Optimized

Avoid duplication whenever possible.

## SECURITY

Security is mandatory.

Protect:

- Authentication
- Authorization
- Personal data
- Payment information
- Business data
- Administrative functions

Never expose sensitive information.

## PERFORMANCE

Optimize for:

- Low latency
- Fast API response
- Efficient database queries
- Minimal resource usage
- Horizontal scalability

Performance is part of every feature.

## API PHILOSOPHY

APIs should be:

- Consistent
- Predictable
- Versioned
- Well validated
- Properly documented

## USER EXPERIENCE

Prioritize:

- Fast interactions
- Responsive UI
- Accessibility
- Clear workflows
- Simple navigation
- Minimal friction

## CODE QUALITY

Every change must improve or preserve:

- Readability
- Maintainability
- Reliability
- Testability
- Consistency

Avoid unnecessary complexity.

## AI EXPECTATIONS

Before making any change:

1. Understand the requirement.
1. Understand the existing implementation.
1. Identify impacted modules.
1. Consider architectural impact.
1. Preserve project conventions.
1. Minimize breaking changes.
1. Deliver production-ready solutions.

Never guess requirements.

Never invent project behavior.

Ask questions whenever requirements are unclear.

## DEFINITION OF SUCCESS

The project is successful when it remains:

- Stable
- Secure
- Performant
- Easy to extend
- Easy to maintain
- Easy to test
- Easy to deploy
- Ready for enterprise-scale production

Every contribution should move the project closer to this goal.
