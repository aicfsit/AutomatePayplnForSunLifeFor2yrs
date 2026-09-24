using System;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Text;
using Newtonsoft.Json;
using AutomatePayplnForSunLifeFor2yrs.Models;

namespace AutomatePayplnForSunLifeFor2yrs.Services
{
    // Sends failure alerts through the in-house Email API on 172.16.10.2:8081,
    // the same service the other schedulers use. Deliberately NOT SmtpClient:
    // Office 365 no longer accepts basic authentication, which is why the
    // SmtpClient blocks in those projects are commented out.
    //
    // Nothing here is allowed to throw. An alert that fails must never be the
    // reason the run dies, so every failure is reported to the console only.
    public class EmailNotifier
    {
        private readonly EmailConfig _cfg;

        public EmailNotifier(EmailConfig cfg)
        {
            _cfg = cfg;
        }

        public bool IsEnabled
        {
            get
            {
                return _cfg != null && _cfg.Enabled &&
                    !string.IsNullOrEmpty(_cfg.ApiBaseUrl) &&
                    !string.IsNullOrEmpty(_cfg.To);
            }
        }

        // subject/body are plain text; body is wrapped into simple HTML.
        // attachmentPath may be null (for example when the browser never
        // started, so there is no screenshot to take).
        public void SendAlert(string subject, string body, string attachmentPath)
        {
            if (!IsEnabled)
            {
                Console.WriteLine("Email alerts are disabled; not sending: " + subject);
                return;
            }

            try
            {
                string url = CombineUrl(_cfg.ApiBaseUrl, _cfg.EndpointWithAttachment);

                Dictionary<string, object> model = new Dictionary<string, object>();
                string to = NormalizeRecipients(_cfg.To);

                model["strToEmail"] = to;
                model["strCC"] = NormalizeRecipients(_cfg.Cc);
                model["strBcc"] = "";
                model["strSubject"] = subject;
                model["strBody"] = BuildHtmlBody(body);
                model["IsBodyHTML"] = true;
                model["strFromEmailId"] = _cfg.FromEmail;
                model["strHost"] = _cfg.Host ?? "";
                model["strDisplayName"] = _cfg.DisplayName ?? "";
                model["userid"] = _cfg.UserId ?? "PayplnAutomation";
                model["source"] = "PayplnAutomation";
                model["replyTo"] = "";
                model["emailPriority"] = "";
                model["moduleName"] = "EmailNotifier";
                model["application"] = "Payplan Updater";

                // The API expects one flat dictionary with these two keys, the
                // file content base64-encoded. Matches the working caller.
                if (!string.IsNullOrEmpty(attachmentPath) && File.Exists(attachmentPath))
                {
                    byte[] bytes = File.ReadAllBytes(attachmentPath);
                    Dictionary<string, object> attachment =
                        new Dictionary<string, object>();
                    attachment["FileName"] = Path.GetFileName(attachmentPath);
                    attachment["FileStreamArray"] = Convert.ToBase64String(bytes);
                    model["attachmentBytes"] = attachment;
                }
                else
                {
                    model["attachmentBytes"] = null;
                }

                Post(url, JsonConvert.SerializeObject(model));
                Console.WriteLine("Alert email sent to " + to + ": " + subject);
            }
            catch (Exception ex)
            {
                Console.WriteLine("WARNING: could not send alert email: " + ex.Message);
            }
        }

        private void Post(string url, string json)
        {
            HttpWebRequest request = (HttpWebRequest)WebRequest.Create(url);
            request.Method = "POST";
            request.ContentType = "application/json";
            request.Accept = "application/json";
            request.Headers.Add("ClientId", _cfg.ClientId ?? "");
            request.Headers.Add("ClientSecret", _cfg.ClientSecret ?? "");
            request.Timeout = (_cfg.TimeoutSec > 0 ? _cfg.TimeoutSec : 120) * 1000;

            byte[] payload = Encoding.UTF8.GetBytes(json);
            request.ContentLength = payload.Length;
            using (Stream stream = request.GetRequestStream())
            {
                stream.Write(payload, 0, payload.Length);
            }

            using (HttpWebResponse response = (HttpWebResponse)request.GetResponse())
            using (StreamReader reader =
                new StreamReader(response.GetResponseStream()))
            {
                string body = reader.ReadToEnd();
                if ((int)response.StatusCode >= 300)
                {
                    Console.WriteLine("  email API returned " +
                        (int)response.StatusCode + ": " + body);
                }
            }
        }

        // The API passes this string straight to MailMessage, which accepts
        // ONLY commas between addresses. A ';' (the Outlook convention, and
        // what App.config uses) fails the whole send with:
        //   "An invalid character was found in the mail header: ';'."
        // Both separators are accepted here and normalised to commas.
        private static string NormalizeRecipients(string list)
        {
            if (string.IsNullOrEmpty(list))
            {
                return "";
            }

            string[] parts = list.Split(
                new char[] { ';', ',' }, StringSplitOptions.RemoveEmptyEntries);

            List<string> cleaned = new List<string>();
            foreach (string part in parts)
            {
                string trimmed = part.Trim();
                if (trimmed.Length > 0)
                {
                    cleaned.Add(trimmed);
                }
            }

            return string.Join(",", cleaned.ToArray());
        }

        private static string BuildHtmlBody(string text)
        {
            string encoded = (text ?? "")
                .Replace("&", "&amp;")
                .Replace("<", "&lt;")
                .Replace(">", "&gt;");

            return "<div style=\"font-family:Segoe UI,Arial,sans-serif;font-size:13px\">" +
                "<pre style=\"font-family:Consolas,monospace;font-size:12px;" +
                "white-space:pre-wrap\">" + encoded + "</pre></div>";
        }

        private static string CombineUrl(string baseUrl, string endpoint)
        {
            string b = (baseUrl ?? "").TrimEnd('/');
            string e = (endpoint ?? "").TrimStart('/');
            return b + "/" + e;
        }
    }
}
