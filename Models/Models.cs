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
        public string CredentialAppKey { get; set; }

        public bool Headless { get; set; }
        public int StepDelayMs { get; set; }
        public int PageLoadTimeoutSec { get; set; }
        public int ElementWaitTimeoutSec { get; set; }
        public int RetryDelayMs { get; set; }
        public int MfaWaitTimeoutSec { get; set; }

        public LoginConfig Login { get; set; }
        public XPathConfig XPaths { get; set; }
        public string TotalPayableLabel { get; set; }
    }

    public class LoginConfig
    {
        public string EnglishLanguageXPath { get; set; }
        public string SignInButtonXPath { get; set; }
        public string UsernameXPath { get; set; }
        public string PasswordXPath { get; set; }
        public string SubmitXPath { get; set; }
        public string MfaDetectXPath { get; set; }
        public string LoginSuccessXPath { get; set; }
        public string LoginSuccessUrl { get; set; }
    }

    public class XPathConfig
    {
        public string SearchClientButton { get; set; }
        public string SearchBody { get; set; }
        public string ResultButton { get; set; }
        public string FirstDiv { get; set; }
        public string DocumentsTab { get; set; }
        public string PremiumPaymentNotices { get; set; }
        public string FirstDocDiv { get; set; }
    }

    // Portal login credentials read from dbo.AutomationCredentials.
    public class PortalCredential
    {
        public string Username { get; set; }
        public string Password { get; set; }
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
        public string PolRefNo { get; set; }
        public string PolCod { get; set; }
        public decimal? Amount { get; set; }
        public string Status { get; set; }     // Success / Disabled / Failed
        public string Message { get; set; }
    }
}
