using Nestly.BuildingBlocks.Primitives;
using System;
using System.Collections.Generic;

namespace Nestly.Application
{
    public class Customer : Entity<Guid>
    {
        public string Mobile { get; private set; }
        public string? Email { get; private set; }
        public string Name { get; private set; }
        public DateTime? DateOfBirth { get; private set; }
        public string? Address { get; private set; }
        public string? City { get; private set; }
        public string? State { get; private set; }
        public string? Pincode { get; private set; }
        public string? Country { get; private set; }
        public DateTime CreatedAt { get; private set; }
        public DateTime UpdatedAt { get; private set; }
        public CustomerStatus Status { get; private set; }

        /// <summary>This customer's own shareable referral code (REFERRAL.md, task 162). Null until first requested - generated lazily, not at signup, since most customers never share it.</summary>
        public string? ReferralCode { get; private set; }

        // Mobile+OTP is the always-available registration path (SRS 11.2.1)
        // and collects only mobile/name/email at signup; date of birth and
        // address are optional and, for address, superseded entirely by the
        // separate address book (CustomerAddress / SRS 11.3) rather than
        // living on the customer record.
        public Customer(
            Guid id,
            string mobile,
            string name,
            CustomerStatus status,
            string? email = null,
            DateTime? dateOfBirth = null,
            string? address = null,
            string? city = null,
            string? state = null,
            string? pincode = null,
            string? country = null)
        {
            Id = id;
            Mobile = mobile;
            Email = email;
            Name = name;
            DateOfBirth = dateOfBirth;
            Address = address;
            City = city;
            State = state;
            Pincode = pincode;
            Country = country;
            Status = status;
            CreatedAt = DateTime.UtcNow;
            UpdatedAt = DateTime.UtcNow;
        }

        public void UpdateStatus(CustomerStatus newStatus)
        {
            if (newStatus != Status)
            {
                Status = newStatus;
                UpdatedAt = DateTime.UtcNow;
            }
        }

        /// <summary>
        /// Right-to-erasure account deletion: terminal and irreversible,
        /// unlike <see cref="UpdateStatus"/>'s other transitions - there is no
        /// "undelete". Personally-identifying fields are overwritten with
        /// anonymized placeholders (derived from this customer's own id, so
        /// they satisfy the unique-mobile/unique-email constraints without
        /// colliding across deletions) rather than left in place, since
        /// financial/booking history is retained (this row and its id stay;
        /// each booking already carries its own point-in-time snapshot of
        /// name/mobile/address, so nothing downstream depends on these fields
        /// remaining real). Login is blocked the moment <see cref="Status"/>
        /// leaves <see cref="CustomerStatus.Active"/> -
        /// <c>CustomerLoginService.IssueSessionAsync</c> already gates on
        /// that, so no separate "kill switch" is needed here.
        /// </summary>
        public void SoftDelete()
        {
            if (Status == CustomerStatus.SoftDeleted)
            {
                return;
            }

            Name = "Deleted User";
            Email = $"deleted+{Id:N}@deleted.glavyx.invalid";
            // Mobile is varchar(20) (CustomerConfiguration) - see Provider.SoftDelete's
            // matching fix/comment: the full "deleted-{32 hex chars}" form is 40
            // characters, which a real Postgres column rejects (22001: value too
            // long) even though a lenient test database let it through silently.
            Mobile = $"deleted-{Id:N}"[..20];
            DateOfBirth = null;
            Address = null;
            City = null;
            State = null;
            Pincode = null;
            Country = null;
            Status = CustomerStatus.SoftDeleted;
            UpdatedAt = DateTime.UtcNow;
        }

        public void UpdateProfile(string name, string? email)
        {
            Name = name ?? throw new ArgumentNullException(nameof(name));
            Email = email;
            UpdatedAt = DateTime.UtcNow;
        }

        /// <summary>
        /// Self-service profile edit (SRS 11.2.3: "Edit name, email, optional
        /// profile data"). Email is deliberately absent: changing it goes
        /// through <see cref="ChangeEmail"/> after OTP re-verification, so it
        /// cannot be swapped by an unverified PUT.
        /// </summary>
        public void UpdateProfileDetails(
            string name,
            DateTime? dateOfBirth,
            string? city,
            string? state,
            string? pincode,
            string? country)
        {
            Name = string.IsNullOrWhiteSpace(name)
                ? throw new ArgumentException("Name is required.", nameof(name))
                : name;
            DateOfBirth = dateOfBirth;
            City = city;
            State = state;
            Pincode = pincode;
            Country = country;
            UpdatedAt = DateTime.UtcNow;
        }

        /// <summary>Applied only after an OTP proved control of the new number (SRS 11.2.3).</summary>
        public void ChangeMobile(string newMobile)
        {
            Mobile = string.IsNullOrWhiteSpace(newMobile)
                ? throw new ArgumentException("Mobile is required.", nameof(newMobile))
                : newMobile;
            UpdatedAt = DateTime.UtcNow;
        }

        /// <summary>Applied only after an OTP proved control of the new address (SRS 11.2.3).</summary>
        public void ChangeEmail(string newEmail)
        {
            Email = string.IsNullOrWhiteSpace(newEmail)
                ? throw new ArgumentException("Email is required.", nameof(newEmail))
                : newEmail;
            UpdatedAt = DateTime.UtcNow;
        }

        /// <summary>
        /// Assigns this customer's referral code (REFERRAL.md, task 162).
        /// Set-once: a customer's code is stable for life once generated, so
        /// links already shared never break. Uniqueness is the caller's
        /// responsibility (the generating service checks against
        /// <c>ICustomerRepository</c> before calling this) - the entity only
        /// enforces its own invariant (idempotent identity, not global
        /// uniqueness, which needs a repository read this entity can't do).
        /// </summary>
        public void SetReferralCode(string code)
        {
            if (ReferralCode is not null)
            {
                throw new InvalidOperationException("Referral code is already assigned and cannot be changed.");
            }

            ReferralCode = string.IsNullOrWhiteSpace(code)
                ? throw new ArgumentException("Referral code is required.", nameof(code))
                : code;
            UpdatedAt = DateTime.UtcNow;
        }

        public override bool Equals(object? obj)
        {
            return obj is Customer other && Id == other.Id;
        }

        public override int GetHashCode()
        {
            return HashCode.Combine(Id);
        }
    }

    public enum CustomerStatus
    {
        Active,
        Blocked,
        Unverified,
        SoftDeleted
    }
}
