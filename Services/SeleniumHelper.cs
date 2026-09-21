using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using OpenQA.Selenium;
using OpenQA.Selenium.Support.UI;

namespace AutomatePayplnForSunLifeFor2yrs.Services
{
    // Small wrapper around common Selenium operations.
    // Avoids the DotNetSeleniumExtras package by defining waits inline.
    public class SeleniumHelper
    {
        private readonly IWebDriver _driver;
        private readonly int _waitSec;
        private readonly int _stepDelayMs;

        public SeleniumHelper(IWebDriver driver, int waitSec, int stepDelayMs)
        {
            _driver = driver;
            _waitSec = waitSec;
            _stepDelayMs = stepDelayMs;
        }

        public void Delay()
        {
            Thread.Sleep(_stepDelayMs);
        }

        public void Delay(int ms)
        {
            Thread.Sleep(ms);
        }

        // Wait until an element is present in the DOM.
        public IWebElement WaitForElement(string xpath)
        {
            WebDriverWait wait = new WebDriverWait(_driver, TimeSpan.FromSeconds(_waitSec));
            return wait.Until(new Func<IWebDriver, IWebElement>(
                delegate(IWebDriver d)
                {
                    try
                    {
                        IWebElement e = d.FindElement(By.XPath(xpath));
                        return e != null ? e : null;
                    }
                    catch (NoSuchElementException)
                    {
                        return null;
                    }
                }));
        }

        // Wait until an element is present AND visible.
        public IWebElement WaitForVisible(string xpath)
        {
            WebDriverWait wait = new WebDriverWait(_driver, TimeSpan.FromSeconds(_waitSec));
            return wait.Until(new Func<IWebDriver, IWebElement>(
                delegate(IWebDriver d)
                {
                    try
                    {
                        IWebElement e = d.FindElement(By.XPath(xpath));
                        return (e != null && e.Displayed) ? e : null;
                    }
                    catch (NoSuchElementException)
                    {
                        return null;
                    }
                    catch (StaleElementReferenceException)
                    {
                        return null;
                    }
                }));
        }

        public void Type(string xpath, string text)
        {
            IWebElement el = WaitForVisible(xpath);

            ClearInput(el);
            el.SendKeys(text);

            // React re-renders can restore the old text, leaving the new text
            // appended to it ("611199467611199467..."), which then matches no
            // policy. Verify what actually landed and retype once if wrong.
            string actual = ReadValue(el);
            if (actual != null && !actual.Equals(text, StringComparison.Ordinal))
            {
                Console.WriteLine("  search box held '" + actual +
                    "' instead of '" + text + "'; clearing and retyping.");

                el = WaitForVisible(xpath);
                ClearInput(el);
                el.SendKeys(text);

                actual = ReadValue(el);
                if (actual != null && !actual.Equals(text, StringComparison.Ordinal))
                {
                    throw new Exception("Could not set the search box to '" + text +
                        "'; it still reads '" + actual + "'.");
                }
            }

            Delay();
        }

        // Clear() sets the DOM value but a React-controlled input does not see
        // that, so the old text comes back. Select-all followed by Delete goes
        // through real key events, which React does process.
        private void ClearInput(IWebElement el)
        {
            try { el.Clear(); }
            catch (Exception) { }

            try
            {
                el.SendKeys(Keys.Control + "a");
                el.SendKeys(Keys.Delete);
            }
            catch (Exception) { }

            // Last resort for inputs that ignore both of the above.
            if (!string.IsNullOrEmpty(ReadValue(el)))
            {
                try
                {
                    for (int i = 0; i < 60; i++)
                    {
                        el.SendKeys(Keys.Backspace);
                    }
                }
                catch (Exception) { }
            }
        }

        private string ReadValue(IWebElement el)
        {
            try { return el.GetAttribute("value"); }
            catch (Exception) { return null; }
        }

        public void Click(string xpath)
        {
            IWebElement el = WaitForVisible(xpath);
            el.Click();
            Delay();
        }

        // Robust click via JavaScript (helps with overlapped / animated SunLife elements).
        public void JsClick(string xpath)
        {
            IWebElement el = WaitForElement(xpath);
            IJavaScriptExecutor js = (IJavaScriptExecutor)_driver;
            js.ExecuteScript("arguments[0].click();", el);
            Delay();
        }
        // Click an element that may be hidden (display:none) by firing the
        // click via JavaScript. Only requires the element to be present in the DOM.
        // Returns false if it never appears.
        public bool ClickHidden(string xpath)
        {
            IWebElement el = null;
            try
            {
                el = WaitForElement(xpath);
            }
            catch (WebDriverTimeoutException)
            {
                return false;
            }

            if (el == null)
            {
                return false;
            }

            try
            {
                IJavaScriptExecutor js = (IJavaScriptExecutor)_driver;
                js.ExecuteScript("arguments[0].click();", el);
                Delay();
                return true;
            }
            catch (Exception)
            {
                return false;
            }
        }
        // Returns null if the element does not exist (no exception thrown).
        public IWebElement TryFind(string xpath)
        {
            try
            {
                return _driver.FindElement(By.XPath(xpath));
            }
            catch (NoSuchElementException)
            {
                return null;
            }
        }

        // Determine whether an element is "enabled" — checks the disabled attribute,
        // aria-disabled, and a disabled CSS class, since SunLife uses all three patterns.
        public bool IsEnabled(IWebElement el)
        {
            if (el == null)
            {
                return false;
            }

            if (!el.Enabled)
            {
                return false;
            }

            string disabledAttr = el.GetAttribute("disabled");
            if (!string.IsNullOrEmpty(disabledAttr))
            {
                return false;
            }

            string aria = el.GetAttribute("aria-disabled");
            if (!string.IsNullOrEmpty(aria) &&
                aria.Equals("true", StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }

            string cls = el.GetAttribute("class");
            if (!string.IsNullOrEmpty(cls) &&
                cls.ToLowerInvariant().Contains("disabled"))
            {
                return false;
            }

            return true;
        }

        // Save a PNG of the current screen for attaching to a failure alert.
        // Returns the file path, or null if the shot could not be taken (for
        // example the browser has already died).
        public string CaptureScreenshot(string folder, string namePrefix)
        {
            try
            {
                ITakesScreenshot shooter = _driver as ITakesScreenshot;
                if (shooter == null)
                {
                    return null;
                }

                if (!Directory.Exists(folder))
                {
                    Directory.CreateDirectory(folder);
                }

                string safePrefix = namePrefix;
                foreach (char c in Path.GetInvalidFileNameChars())
                {
                    safePrefix = safePrefix.Replace(c, '_');
                }

                string path = Path.Combine(folder, safePrefix + "_" +
                    DateTime.Now.ToString("yyyyMMdd_HHmmss") + ".png");

                shooter.GetScreenshot().SaveAsFile(path);
                Console.WriteLine("Screenshot saved: " + path);
                return path;
            }
            catch (Exception ex)
            {
                Console.WriteLine("WARNING: could not capture screenshot: " + ex.Message);
                return null;
            }
        }

        public string CurrentWindowHandle()
        {
            return _driver.CurrentWindowHandle;
        }

        public IList<string> WindowHandles()
        {
            return new List<string>(_driver.WindowHandles);
        }

        // Wait for a tab that was NOT open before the click, and switch to it.
        //
        // Pass the handles captured immediately before the action that opens
        // the tab. Counting windows is not enough: the portal opens tabs of its
        // own after login (the Broker Buddy chatbot), so a "more than one
        // window" test is already true and would latch onto the wrong tab.
        public string SwitchToNewTab(IList<string> knownHandles)
        {
            WebDriverWait wait = new WebDriverWait(_driver, TimeSpan.FromSeconds(_waitSec));

            // WebDriverWait retries while the delegate returns null.
            string opened = wait.Until(new Func<IWebDriver, string>(
                delegate(IWebDriver d)
                {
                    foreach (string handle in d.WindowHandles)
                    {
                        if (!knownHandles.Contains(handle))
                        {
                            return handle;
                        }
                    }
                    return null;
                }));

            _driver.SwitchTo().Window(opened);
            return opened;
        }

        // Close every tab whose URL does not contain the given fragment and
        // return the handle we kept. The portal opens extra tabs after login;
        // this settles the session back onto the policy page.
        public string CloseTabsExceptUrl(string urlFragment)
        {
            string kept = null;

            foreach (string handle in WindowHandles())
            {
                try
                {
                    _driver.SwitchTo().Window(handle);

                    bool isPortal = string.IsNullOrEmpty(urlFragment) ||
                        (_driver.Url != null && _driver.Url.IndexOf(
                            urlFragment, StringComparison.OrdinalIgnoreCase) >= 0);

                    if (isPortal && kept == null)
                    {
                        kept = handle;
                    }
                    else
                    {
                        Console.WriteLine("Closing extra tab: " + _driver.Url);
                        _driver.Close();
                    }
                }
                catch (Exception)
                {
                    // A tab that vanished on its own is fine to ignore.
                }
            }

            // Nothing matched the portal URL: keep whatever is still open.
            if (kept == null)
            {
                IList<string> remaining = WindowHandles();
                if (remaining.Count > 0)
                {
                    kept = remaining[0];
                }
            }

            if (kept != null)
            {
                _driver.SwitchTo().Window(kept);
            }
            return kept;
        }

        public void CloseTabAndReturn(string returnHandle)
        {
            _driver.Close();
            _driver.SwitchTo().Window(returnHandle);
            Delay();
        }

        public string CurrentUrl()
        {
            return _driver.Url;
        }
        // Wait until the browser URL contains the given fragment.
        // Useful for OAuth redirects back to the portal after login.
        public bool WaitForUrlContains(string fragment, int timeoutSec)
        {
            WebDriverWait wait = new WebDriverWait(_driver, TimeSpan.FromSeconds(timeoutSec));
            try
            {
                return wait.Until(new Func<IWebDriver, bool>(
                    delegate (IWebDriver d)
                    {
                        return d.Url != null &&
                            d.Url.IndexOf(fragment, StringComparison.OrdinalIgnoreCase) >= 0;
                    }));
            }
            catch (WebDriverTimeoutException)
            {
                return false;
            }
        }
        // Resilient click: waits for the element to be present (not just visible),
        // scrolls it into view, tries a normal click, then falls back to a JS click.
        // Returns false instead of throwing if the element never appears.
        public bool SafeClick(string xpath)
        {
            IWebElement el = null;
            try
            {
                el = WaitForElement(xpath);
            }
            catch (WebDriverTimeoutException)
            {
                return false;
            }

            if (el == null)
            {
                return false;
            }

            IJavaScriptExecutor js = (IJavaScriptExecutor)_driver;
            try
            {
                js.ExecuteScript(
                    "arguments[0].scrollIntoView({block:'center'});", el);
            }
            catch { }

            try
            {
                el.Click();
                Delay();
                return true;
            }
            catch (Exception)
            {
                // Fall back to a JS click for overlapped/animated elements.
                try
                {
                    js.ExecuteScript("arguments[0].click();", el);
                    Delay();
                    return true;
                }
                catch (Exception)
                {
                    return false;
                }
            }
        }
    }
}
