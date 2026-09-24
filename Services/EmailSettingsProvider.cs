using System;
using System.Configuration;
using AutomatePayplnForSunLifeFor2yrs.Models;

namespace AutomatePayplnForSunLifeFor2yrs.Services
{
    // Reads the failure-alert settings from App.config (appSettings).
    //
    // Recipients are hard-defaulted: if failure_email_to is missing, blank, or
    // App.config itself is absent, alerts still reach the two people who own
    // this automation. Losing the config must never mean losing the alerts.
    public static class EmailSettingsProvider
    {
        public const string DefaultRecipients =
            "techsupport2@cfsgroup.com;nandan.hegde@cfsgroup.com";

        public static EmailConfig Load()
        {
            EmailConfig cfg = new EmailConfig();

            cfg.To = Get("failure_email_to", DefaultRecipients);
            if (string.IsNullOrEmpty(cfg.To.Trim()))
            {
                cfg.To = DefaultRecipients;
            }
            cfg.Cc = Get("failure_email_cc", "");

            cfg.Enabled = GetBool("email_enabled", true);

            cfg.ApiBaseUrl = Get("email_api_base_url", "http://172.16.10.2:8081/");
            cfg.EndpointWithAttachment =
                Get("email_endpoint_attachment", "/SendEmailSMS/SendEmailwithBytes");
            cfg.ClientId = Get("email_client_id", "");
            cfg.ClientSecret = Get("email_client_secret", "");

            cfg.FromEmail = Get("email_from", "DoNotReply@cfsgroup.com");
            cfg.Host = Get("email_host", "");
            cfg.DisplayName = Get("email_display_name", "Paypln Automation");
            cfg.UserId = Get("email_userid", "PayplnAutomation");
            cfg.TimeoutSec = GetInt("email_timeout_sec", 120);

            return cfg;
        }

        private static string Get(string key, string fallback)
        {
            try
            {
                string v = ConfigurationManager.AppSettings[key];
                return string.IsNullOrEmpty(v) ? fallback : v;
            }
            catch (Exception)
            {
                return fallback;
            }
        }

        private static bool GetBool(string key, bool fallback)
        {
            bool parsed;
            return bool.TryParse(Get(key, ""), out parsed) ? parsed : fallback;
        }

        private static int GetInt(string key, int fallback)
        {
            int parsed;
            return int.TryParse(Get(key, ""), out parsed) ? parsed : fallback;
        }
    }
}
