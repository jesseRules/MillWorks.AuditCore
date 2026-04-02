# Compliance Traceability Matrix

Maps each validator rule to its regulatory citation, what the rule checks, and the test file that proves it.

**Interpretation notes:** Each validator inspects `AuditEvent` records for evidence that the system is logging the activities required by the cited regulation. Rules that check for specific event types (e.g., "Login", "Consent") verify that the *consuming application* emits those events through the audit pipeline. Rules that check for integrity, user identification, or retention verify properties of the audit infrastructure itself.

---

## GDPR

**Source:** `src/MillWorks.AuditCore.Services/GdprValidator.cs`
**Tests:** `tests/MillWorks.AuditCore.Tests/Validators/GdprValidatorTests.cs`

| Rule | Citation | What It Checks | Severity |
|------|----------|----------------|----------|
| Records of Processing | GDPR Article 30 | Processing activities are recorded in audit logs | Critical |
| User Consent Tracking | GDPR Article 7 | Events containing "Consent" exist | High |
| Data Access Logging | GDPR Article 15 | Events with "DataAccess", "PersonalData", or "View" exist (right of access) | High |
| Right to Erasure Tracking | GDPR Article 17 | Events with "Delete", "Erasure", or "Anonymize" exist (right to be forgotten) | Medium |
| Data Portability Tracking | GDPR Article 20 | Events with "Export", "Download", or "Portability" exist | Low |
| Data Retention Compliance | GDPR Article 5(1)(e) | Oldest event is not older than 2555 days (7 years) | Medium |
| User Identification | GDPR Article 32 | All audit events include user identification | High |
| Security Breach Tracking | GDPR Article 33 | Events with "Breach", "Security", or "Incident" exist (72-hour reporting readiness) | Info |
| DPIA Documentation | GDPR Article 35 | Events with "DPIA", "Assessment", or "RiskAssessment" exist | Low |
| Audit Log Integrity Protection | GDPR Article 32 | Events have AuditIntegrity values for tamper detection | High |

---

## HIPAA

**Source:** `src/MillWorks.AuditCore.Services/HipaaValidator.cs`
**Tests:** `tests/MillWorks.AuditCore.Tests/Validators/HipaaValidatorTests.cs`, `tests/MillWorks.AuditCore.Tests/Validators/HipaaValidatorBranchTests.cs`

| Rule | Citation | What It Checks | Severity | Req. Level |
|------|----------|----------------|----------|------------|
| Audit Controls | 45 CFR SS 164.312(b) | Hardware/software/procedural mechanisms for recording and examining activity | Critical | Required |
| Information System Activity Review | 45 CFR SS 164.308(a)(1)(ii)(D) | Events exist in last 30 days (regular review of system activity) | High | Required |
| PHI Access Logging | 45 CFR SS 164.312(a)(1) | Events with "PHI", "Patient", "Medical", "Health", "Record" exist | Critical | Required |
| Unique User Identification | 45 CFR SS 164.312(a)(2)(i) | All events include user identification | Critical | Required |
| Automatic Logoff Tracking | 45 CFR SS 164.312(a)(2)(iii) | Events with "Logoff", "Logout", "SessionEnd", "Timeout" exist | Medium | Addressable |
| Integrity Controls | 45 CFR SS 164.312(c)(1) | Events have AuditIntegrity values (tamper-evident audit logs) | Critical | Required |
| Authentication Mechanism | 45 CFR SS 164.312(c)(2) | Integrity controls as proxy for digital signatures/authentication | Medium | Addressable |
| Log-in Monitoring | 45 CFR SS 164.308(a)(5)(ii)(C) | Events with "Login", "Authentication", "SignIn" exist; counts failed logins | High | Addressable |
| Security Incident Tracking | 45 CFR SS 164.308(a)(6)(ii) | Events with "Security", "Incident", "Breach", "Violation" exist | Info | Required |
| Documentation Retention | 45 CFR SS 164.316(b)(1)(i) | Audit logs span 6+ years (2190 days) | Medium | Addressable |
| Authorization Tracking | 45 CFR SS 164.308(a)(3)(ii)(A) | Events with "Authorization", "Permission", "Role", "Access" exist | Medium | Addressable |
| Emergency Access Procedures | 45 CFR SS 164.312(a)(2)(ii) | Events with "Emergency", "Override", "BreakGlass" exist | Info | Required |

---

## FERPA

**Source:** `src/MillWorks.AuditCore.Services/FerpaValidator.cs`
**Tests:** `tests/MillWorks.AuditCore.Tests/Validators/FerpaValidatorTests.cs`

| Rule | Citation | What It Checks | Severity |
|------|----------|----------------|----------|
| FERPA Entity Configuration | 20 U.S.C. SS 1232g; 34 CFR SS 99.31 | Entity types decorated with [FERPA] attribute for interceptor enforcement | High |
| FERPA Sensitive Data Protection | 20 U.S.C. SS 1232g; 34 CFR SS 99.31 | [SensitiveData] properties include FERPA in ApplicableStandards | High |
| Education Records Access Logging | 20 U.S.C. SS 1232g(b); 34 CFR SS 99.10 | Events with "StudentAccess", "EducationRecord", "Enrollment", "Grade", "Transcript" exist | High |
| Unique User Identification | 20 U.S.C. SS 1232g; 34 CFR SS 99.31(a) | All events include user ID | Critical |
| Prior Written Consent Tracking | 20 U.S.C. SS 1232g(b)(1); 34 CFR SS 99.30 | Events with "Consent", "ParentConsent", "Authorization" exist | Medium |
| Directory Information Opt-Out | 20 U.S.C. SS 1232g(a)(5)(B); 34 CFR SS 99.37 | Events with "DirectoryOptOut", "OptOut", "DirectoryInfo" exist | Medium |
| Legitimate Educational Interest | 20 U.S.C. SS 1232g(b)(1)(A); 34 CFR SS 99.31(a)(1) | Events with "LegitimateInterest", "AccessJustification", "EducationalPurpose" exist | Medium |
| Disclosure Logging | 20 U.S.C. SS 1232g(b)(4)(A); 34 CFR SS 99.32 | Events with "Disclosure", "DataSharing", "ThirdParty" exist | Medium |
| Annual Notification | 34 CFR SS 99.7 | Events with "FerpaNotification", "AnnualNotice", "RightsNotification" exist | Low |
| Retention Compliance | 34 CFR SS 99.32(a)(2) | Audit logs span 5+ years (1825 days) | Info |
| Integrity Controls | 20 U.S.C. SS 1232g | Events have AuditIntegrity (severity depends on SecurityOptions.EnableTamperDetection) | Critical/Info |
| De-identification for Research | 20 U.S.C. SS 1232g(b)(1)(F); 34 CFR SS 99.31(b) | When research events exist: "DeIdentified" or "Anonymized" events also exist | Medium |
| Security Incident Tracking | 20 U.S.C. SS 1232g | Events with "Security", "Breach", "Incident", "Violation" exist | Info |
| Login/Authentication Monitoring | 20 U.S.C. SS 1232g | Events with "Login", "Authentication", "SignIn" exist; counts failed attempts | High |

---

## SOC 2

**Source:** `src/MillWorks.AuditCore.Services/Soc2Validator.cs`
**Tests:** `tests/MillWorks.AuditCore.Tests/Validators/Soc2ValidatorTests.cs`

| Rule | Citation | What It Checks | Severity |
|------|----------|----------------|----------|
| Access Control Logging | CC6.1 | Logical access controls are being logged | Critical |
| Authentication Logging | CC6.2 | Events with "Login", "Authentication", "SignIn" exist | High |
| Account Management Logging | CC6.3 | Events with "User", "Account", "Permission", "Role" exist | High |
| User Identification | CC6.6 | All events include user identification | Critical |
| Security Event Detection | CC7.2 | Events with "Security", "Incident", "Threat", "Breach", "Alert" exist | Info |
| Security Event Evaluation | CC7.3 | Events exist in last 30 days (active monitoring) | Medium |
| Incident Response Tracking | CC7.4 | Events with "Response", "Remediation", "Investigation" exist | Info |
| Change Management Logging | CC8.1 | Events with "Change", "Deploy", "Release", "Configuration" or Action="Modified" exist | Medium |
| Privileged Access Monitoring | CC6.7 | Events with "Admin", "Privileged", "Root" or user containing "admin" | High |
| System Availability Monitoring | A1.2 | Events with "Health", "Performance", "Availability", "Uptime" exist | Low |
| Confidential Data Access | C1.2 | Events with "Data", "Confidential", "Sensitive", "Export" exist | Low |
| Audit Log Retention | CC7.2 | Retention spans 365+ days | Medium |
| Audit Log Integrity Protection | CC7.2 | Events have AuditIntegrity values | High |

---

## ISO 27001

**Source:** `src/MillWorks.AuditCore.Services/Iso27001Validator.cs`
**Tests:** `tests/MillWorks.AuditCore.Tests/Validators/Iso27001ValidatorTests.cs`, `tests/MillWorks.AuditCore.Tests/Validators/Iso27001ValidatorBranchTests.cs`

| Rule | Citation | What It Checks | Severity |
|------|----------|----------------|----------|
| Event Logging | A.12.4.1 | Events are being logged | Critical |
| Log Protection | A.12.4.2 | Events have AuditIntegrity for protection | High |
| Security Event Reporting | A.16.1.2 | Events with "Security", "Incident", "Breach" exist | High |
| User Access Management | A.9.2.1 | Events with "User", "Login", "Access" exist | Medium |
| Administrator Activity Logging | A.12.4.3 | Events with "Admin" in EventType or user field | Medium |
| Clock Synchronization | A.12.4.4 | All events have InsertedDate values | Medium |
| Log Retention Requirements | A.12.4 | Retention spans 90+ days | Low |

---

## PCI DSS

**Source:** `src/MillWorks.AuditCore.Services/PciDssValidator.cs`
**Tests:** `tests/MillWorks.AuditCore.Tests/Validators/PciDssValidatorTests.cs`, `tests/MillWorks.AuditCore.Tests/Validators/PciDssValidatorBranchTests.cs`

| Rule | Citation | What It Checks | Severity |
|------|----------|----------------|----------|
| Audit Logs Implementation | PCI DSS v4.0 Req. 10.2 | Audit logs are collected for all system components | Critical |
| Cardholder Data Access Logging | PCI DSS v4.0 Req. 10.2.1 | Events with "Card", "Payment", "CHD", "PAN" exist | High |
| Privileged User Actions | PCI DSS v4.0 Req. 10.2.2 | Events with "Admin", "Privileged", "Root", "Sudo" exist | High |
| Audit Trail Access Logging | PCI DSS v4.0 Req. 10.2.3 | Events with "Audit" or "Log" exist | Medium |
| Invalid Access Attempts | PCI DSS v4.0 Req. 10.2.4 | Events with "Failed", "Denied", "Unauthorized", "Rejected" exist | Info |
| Authentication Changes | PCI DSS v4.0 Req. 10.2.5 | Events with "Password", "Credential", "Authentication", "User" exist | High |
| Audit Log Initialization | PCI DSS v4.0 Req. 10.2.6 | Logging of initialization/stopping of audit logs | Medium |
| System Object Changes | PCI DSS v4.0 Req. 10.2.7 | Events with Action="Added"/"Deleted" or "Create"/"Delete" EventType | Medium |
| Audit Log Detail Requirements | PCI DSS v4.0 Req. 10.3 | All events include user ID and timestamps | Critical |
| Time Synchronization | PCI DSS v4.0 Req. 10.3.4 | Events exist within last hour (current/synchronized time) | Medium |
| Audit Log Protection | PCI DSS v4.0 Req. 10.4 | Events have AuditIntegrity (prevent unauthorized modification) | Critical |
| Audit Log Retention | PCI DSS v4.0 Req. 10.5 | Retention spans 365+ days; 90+ days immediately available | High |
| Regular Log Review | PCI DSS v4.0 Req. 10.6 | Recent events exist as proxy for active review capability | Info |
| Security Control Failure Detection | PCI DSS v4.0 Req. 10.7 | Events with "Alert", "Failure", "Error" exist | Info |

---

## STIG (DISA)

**Source:** `src/MillWorks.AuditCore.Services/StigValidator.cs`
**Tests:** `tests/MillWorks.AuditCore.Tests/Validators/StigValidatorTests.cs`, `tests/MillWorks.AuditCore.Tests/Validators/StigValidatorBranchTests.cs`

| Rule | Citation | What It Checks | Severity |
|------|----------|----------------|----------|
| Audit Generation | NIST 800-53 AU-12 / V-222582 | Application generates audit records for auditable events | Critical |
| Content of Audit Records | NIST 800-53 AU-3 / V-222576 | Records contain user ID, timestamp, event type | Critical |
| Additional Audit Information | NIST 800-53 AU-3(1) / V-222577 | IP address, component name, and action are included | High |
| Time Stamps | NIST 800-53 AU-8 / V-222578 | All records have timestamps from synchronized time source | High |
| Protection of Audit Information | NIST 800-53 AU-9 / V-222579 | AuditIntegrity for tamper detection / unauthorized modification prevention | Critical |
| Audit Backup to Separate System | NIST 800-53 AU-9(2) / V-222580 | Events with "Archive", "Backup", "Export" exist (Azure Blob satisfies separate system) | Medium |
| Audit Record Retention | NIST 800-53 AU-11 / V-222581 | Retention spans 365+ days (DoD may require 5+ years; check with ISSM) | High |
| System-Wide Audit Trail | NIST 800-53 AU-12(1) / V-222583 | Correlation IDs present for multi-component audit trail compilation | Medium |
| Auditable Events | NIST 800-53 AU-2 / V-222569 | Logon, logoff, privilege use, object access, policy changes detected (minimum: logon + object access) | High |
| Reviews of Auditable Events | NIST 800-53 AU-2(3) / V-222570 | Activity in last 30 days; requires annual review/update | Medium |
| Privileged Function Execution | NIST 800-53 AU-3 / V-222574 | Events with "Admin", "Privileged", "Root", "Sudo", "Elevat" exist | High |
| Account Management | NIST 800-53 AC-2 / V-222534 | Account creation, modification, enabling, disabling, removal events | High |
| Unsuccessful Logon Attempts | NIST 800-53 AC-7 / V-222542 | Events with "FailedLogin", "LoginFailed", "AuthenticationFailed", "Lockout" exist | Medium |
| System Use Notification | NIST 800-53 AC-8 / V-222543 | Events with "Banner", "SystemUse", "Consent", "TermsAccepted" exist | High |
| Remote Access | NIST 800-53 AC-17 / V-222553 | Events with "Remote", "VPN", "SSH", "RDP", "API" exist; or IP address tracking | High |
| Information System Monitoring | NIST 800-53 SI-4 | Events with "Security", "Incident", "Breach", "Threat", "Alert", "Intrusion" exist | Info |
| Authenticator Management | NIST 800-53 IA-5 | Events with "Password", "Credential", "MFA", "Token", "Certificate", "CAC", "PKI" exist | Medium |

---

## Limitations and Interpretation Notes

1. **Event-type detection is keyword-based.** Validators check for specific substrings in `EventType`, `Action`, and `UserId` fields. The consuming application must emit events with recognizable keywords for the rule to pass. This is by design: the audit library validates that the application *is* logging the required activities, not that it *could*.

2. **Retention checks measure log span, not policy enforcement.** Validators check whether enough historical data exists in the audit log, not whether a retention policy is configured. A freshly deployed system will legitimately fail retention rules.

3. **Integrity checks require tamper detection to be enabled.** Rules checking for `AuditIntegrity` values will fail if `SecurityOptions.EnableTamperDetection` is false. The FERPA validator adjusts severity (Critical vs Info) based on this setting; others treat it as unconditionally required.

4. **"Addressable" HIPAA rules** (marked in the Req. Level column) allow organizations to implement alternative measures if the specification is not reasonable and appropriate. The validator flags them at Medium severity rather than Critical.

5. **STIG CAT levels** map to severity as: CAT I = Critical, CAT II = High/Medium (depending on operational impact), CAT III = Low/Info.
