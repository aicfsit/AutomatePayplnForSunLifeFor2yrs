using System;
using System.Collections.Generic;
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
            el.Clear();
            el.SendKeys(text);
            Delay();
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

        public string CurrentWindowHandle()
        {
            return _driver.CurrentWindowHandle;
        }

        public IList<string> WindowHandles()
        {
            return new List<string>(_driver.WindowHandles);
        }

        // Wait for a new tab to appear and switch to it. Returns the new handle.
        public string SwitchToNewTab(string originalHandle)
        {
            WebDriverWait wait = new WebDriverWait(_driver, TimeSpan.FromSeconds(_waitSec));
            wait.Until(new Func<IWebDriver, bool>(
                delegate(IWebDriver d)
                {
                    return d.WindowHandles.Count > 1;
                }));

            foreach (string handle in _driver.WindowHandles)
            {
                if (handle != originalHandle)
                {
                    _driver.SwitchTo().Window(handle);
                    return handle;
                }
            }
            return originalHandle;
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
