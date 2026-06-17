using System;
using OpenQA.Selenium;
using AutomatePayplnForSunLifeFor2yrs.Models;

namespace AutomatePayplnForSunLifeFor2yrs.Services
{
    public class LoginHandler
    {
        private readonly IWebDriver _driver;
        private readonly SeleniumHelper _helper;
        private readonly AppConfig _config;

        public LoginHandler(IWebDriver driver, SeleniumHelper helper, AppConfig config)
        {
            _driver = driver;
            _helper = helper;
            _config = config;
        }

        public void Login()
        {
            Console.WriteLine("Navigating to portal...");
            _driver.Navigate().GoToUrl(_config.Url);
            _helper.Delay();

            // Step 0: switch the portal to English. This must happen before the
            // Okta login page is shown. If the toggle isn't present (already English),
            // we just continue.
            if (!string.IsNullOrEmpty(_config.Login.EnglishLanguageXPath))
            {
                try
                {
                    Console.WriteLine("Switching language to English...");
                    _helper.Click(_config.Login.EnglishLanguageXPath);
                    _helper.Delay(2000);
                }
                catch (Exception)
                {
                    Console.WriteLine("Language toggle not found; assuming English already.");
                }
            }

            // Step 1: click the sign-in button to open the Okta login page.
            // Step 1: click the sign-in button to open the Okta login page.
            // The button has style="display:none" (SSO pattern), so a normal click
            // won't work — we fire it via JavaScript with ClickHidden.
            if (!string.IsNullOrEmpty(_config.Login.SignInButtonXPath))
            {
                Console.WriteLine("Clicking sign-in button...");
                bool clicked = _helper.ClickHidden(_config.Login.SignInButtonXPath);
                if (!clicked)
                {
                    Console.WriteLine("  Sign-in button not found. xpath=" +
                        _config.Login.SignInButtonXPath);
                    Console.WriteLine("  Current URL: " + _helper.CurrentUrl());
                    if (_helper.TryFind(_config.Login.UsernameXPath) == null)
                    {
                        throw new Exception(
                            "Could not click sign-in button and the login form is " +
                            "not present. Current URL: " + _helper.CurrentUrl());
                    }
                    Console.WriteLine("  Login form already present; continuing.");
                }
                // After clicking, wait for the OAuth redirect to Okta to start.
                _helper.Delay(3000);
            }

            Console.WriteLine("Entering credentials...");
            _helper.Type(_config.Login.UsernameXPath, _config.Username);
            _helper.Type(_config.Login.PasswordXPath, _config.Password);
            _helper.Click(_config.Login.SubmitXPath);

            // Give Okta a moment to either log in or present an MFA challenge.
            _helper.Delay(3000);

            HandleMfaIfPresent();

            // Confirm we landed back on the portal. The OAuth flow redirects to
            // id.sunlife.com.hk and then back to mysunlife.../awb, so a URL check
            // is more reliable than an element that exists on every page.
            if (!string.IsNullOrEmpty(_config.Login.LoginSuccessUrl))
            {
                bool landed = _helper.WaitForUrlContains(
                    _config.Login.LoginSuccessUrl, _config.ElementWaitTimeoutSec);
                if (!landed)
                {
                    throw new Exception(
                        "Login did not redirect back to the portal (" +
                        _config.Login.LoginSuccessUrl + "). " +
                        "Current URL: " + _helper.CurrentUrl());
                }
                Console.WriteLine("Login successful (redirected to portal).");
            }
            else
            {
                try
                {
                    _helper.WaitForElement(_config.Login.LoginSuccessXPath);
                    Console.WriteLine("Login successful.");
                }
                catch (WebDriverTimeoutException)
                {
                    throw new Exception(
                        "Login did not reach the expected landing page. " +
                        "Check credentials or the loginSuccessXPath in config.json.");
                }
            }

            // Confirm we landed on the app.
            try
            {
                _helper.WaitForElement(_config.Login.LoginSuccessXPath);
                Console.WriteLine("Login successful.");
            }
            catch (WebDriverTimeoutException)
            {
                throw new Exception(
                    "Login did not reach the expected landing page. " +
                    "Check credentials or the loginSuccessXPath in config.json.");
            }
        }

        // If an MFA screen is detected, pause and let the operator complete it manually.
        
        private void HandleMfaIfPresent()
        {
            // If we've already redirected back to the portal, login is done.
            if (HasLanded())
            {
                return;
            }

            IWebElement mfa = null;
            if (!string.IsNullOrEmpty(_config.Login.MfaDetectXPath))
            {
                mfa = _helper.TryFind(_config.Login.MfaDetectXPath);
            }

            if (mfa == null)
            {
                // No explicit MFA element yet, but we also haven't landed.
                // Give the redirect a bit more time before assuming MFA.
                _helper.Delay(3000);
                if (HasLanded())
                {
                    return;
                }
            }

            Console.WriteLine("");
            Console.WriteLine("========================================================");
            Console.WriteLine(" MFA / additional verification may be required.");
            Console.WriteLine(" Complete it in the browser window now.");
            Console.WriteLine(" Waiting up to " + _config.MfaWaitTimeoutSec + " seconds...");
            Console.WriteLine("========================================================");

            int waited = 0;
            int interval = 3000;
            while (waited < _config.MfaWaitTimeoutSec * 1000)
            {
                if (HasLanded())
                {
                    Console.WriteLine("Verification complete, continuing.");
                    return;
                }
                _helper.Delay(interval);
                waited += interval;
            }

            Console.WriteLine("MFA wait timed out. Attempting to continue anyway.");
        }

        // True once the browser has redirected back to the portal URL.
        private bool HasLanded()
        {
            if (!string.IsNullOrEmpty(_config.Login.LoginSuccessUrl))
            {
                string url = _helper.CurrentUrl();
                return url != null &&
                    url.IndexOf(_config.Login.LoginSuccessUrl,
                        StringComparison.OrdinalIgnoreCase) >= 0;
            }
            return _helper.TryFind(_config.Login.LoginSuccessXPath) != null;
        }

    }
}