using System.Collections.Generic;

namespace AutomatePayplnForSunLifeFor2yrs.Models
{
    public class AppConfig
    {
        public string Url { get; set; }
        // Username/Password are NOT read from config.json.
        // They are loaded from dbo.AutomationCredentials at startup
        // using ConnectionString + CredentialAppKey, then assigned here.
        public string Username { get; set; }
        public string Password { get; set; }
        public string ConnectionString { get; set; }

        // Entities are NOT configured here. Every active row in
        // dbo.AutomationCredentials is one entity: its AppKey, entity name and
        // StoredProcedure drive the run. config.json only has to know how to
        // reach the database.

        public bool Headless { get; set; }
        public int StepDelayMs { get; set; }
        public int PageLoadTimeoutSec { get; set; }
        public int ElementWaitTimeoutSec { get; set; }
        public int RetryDelayMs { get; set; }
        public int MfaWaitTimeoutSec { get; set; }

        public LoginConfig Login { get; set; }
        public XPathConfig XPaths { get; set; }
        public string TotalPayableLabel { get; set; }
        // Alert settings are NOT here: they live in App.config (appSettings),
        // loaded by EmailSettingsProvider.
    }

    // Failure alerts go through the in-house Email API, not SMTP.
    public class EmailConfig
    {
        public bool Enabled { get; set; }
        public string ApiBaseUrl { get; set; }
        public string EndpointWithAttachment { get; set; }
        public string ClientId { get; set; }
        public string ClientSecret { get; set; }

        public string FromEmail { get; set; }
        public string FromPassword { get; set; }
        public string Host { get; set; }
        public string DisplayName { get; set; }
        public string UserId { get; set; }

        // Semicolon-separated recipient list.
        public string To { get; set; }
        public string Cc { get; set; }
        public int TimeoutSec { get; set; }
    }

    // One active row of dbo.AutomationCredentials: a portal login plus the
    // stored procedure returning that entity's policies.
    public class EntityRegistration
    {
        public int Id { get; set; }
        // AppKey column: SunLifePortalJUVO, SunLifePortalDIFC, ...
        public string AppKey { get; set; }
        // entity column. Labels the run and the PremiumExtractionLog rows.
        public string Name { get; set; }
        // StoredProcedure column: returns "IBS #" and polcod for this entity.
        // The same procedure serves every entity; ACTCOD selects which one.
        public string StoredProcedure { get; set; }
        public string ActCod { get; set; }

        public string Username { get; set; }
        public string Password { get; set; }
    }

    public class LoginConfig
    {
        public string EnglishLanguageXPath { get; set; }
        public string SignInButtonXPath { get; set; }
        public string UsernameXPath { get; set; }
        public string PasswordXPath { get; set; }
        public string SubmitXPath { get; set; }
        public string MfaDetectXPath { get; set; }
        // Matches the red "Username or password is incorrect" banner, so a bad
        // credential is reported as such instead of timing out on MFA.
        public string LoginErrorXPath { get; set; }
        public string LoginSuccessXPath { get; set; }
        public string LoginSuccessUrl { get; set; }
    }

    public class XPathConfig
    {
        public string SearchClientButton { get; set; }
        // Positional path kept as a fallback for when the label changes.
        public string SearchClientButtonFallback { get; set; }
        // The search page opens on "Clients"; polcod only matches under
        // "Policies", so that tab has to be selected before searching.
        public string SearchPoliciesTab { get; set; }
        public string SearchBody { get; set; }
        public string ResultButton { get; set; }
        public string FirstDiv { get; set; }
        public string DocumentsTab { get; set; }
        public string PremiumPaymentNotices { get; set; }
        public string FirstDocDiv { get; set; }
    }

    // One policy to process, sourced from the stored procedure.
    public class PolicyItem
    {
        public string PolRefNo { get; set; }   // IBS #
        public string PolCod { get; set; }     // polcod
    }

    // Result of processing a single policy.
    public class ProcessResult
    {
        public string Entity { get; set; }
        public string PolRefNo { get; set; }
        public string PolCod { get; set; }
        public decimal? Amount { get; set; }
        public string Status { get; set; }     // Success / Disabled / Failed
        public string Message { get; set; }
    }
}
