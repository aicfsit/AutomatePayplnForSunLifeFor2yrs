using System;
using System.Collections.Generic;
using OpenQA.Selenium;
using AutomatePayplnForSunLifeFor2yrs.Models;

namespace AutomatePayplnForSunLifeFor2yrs.Services
{
    public class ExtractionService
    {
        private readonly IWebDriver _driver;
        private readonly SeleniumHelper _helper;
        private readonly AppConfig _config;
        private readonly DatabaseService _db;
        private readonly PdfAmountExtractor _pdf;
        private readonly string _entity;

        public ExtractionService(IWebDriver driver, SeleniumHelper helper,
            AppConfig config, DatabaseService db, PdfAmountExtractor pdf,
            string entity)
        {
            _driver = driver;
            _helper = helper;
            _config = config;
            _db = db;
            _pdf = pdf;
            _entity = entity;
        }

        public List<ProcessResult> Run(List<PolicyItem> policies)
        {
            List<ProcessResult> results = new List<ProcessResult>();
            string mainWindow = _helper.CurrentWindowHandle();

            int counter = 0;
            foreach (PolicyItem policy in policies)
            {
                counter++;
                Console.WriteLine("");
                Console.WriteLine("---- [" + _entity + " " + counter + "/" +
                    policies.Count + "] polcod=" + policy.PolCod +
                    " polrefno=" + policy.PolRefNo + " ----");
                _helper.Delay(5000);
                ProcessResult result = ProcessWithRetry(policy, mainWindow);
                results.Add(result);

                try
                {
                    _db.InsertLog(result);
                }
                catch (Exception logEx)
                {
                    Console.WriteLine("WARNING: could not write log row: " + logEx.Message);
                }

                Console.WriteLine("Result: " + result.Status +
                    (result.Amount.HasValue ? " amount=" + result.Amount.Value : "") +
                    (string.IsNullOrEmpty(result.Message) ? "" : " (" + result.Message + ")"));

                // Always ensure we're back on the main window before the next policy.
                EnsureMainWindow(mainWindow);
                _helper.Delay();
            }

            return results;
        }

        // One retry with a delay, per your instruction.
        private ProcessResult ProcessWithRetry(PolicyItem policy, string mainWindow)
        {

            
            ProcessResult result = ProcessOne(policy, mainWindow);

            if (result.Status == "Failed")
            {
                Console.WriteLine("First attempt failed: " + result.Message +
                    ". Waiting " + _config.RetryDelayMs + "ms then retrying once...");
                _helper.Delay(_config.RetryDelayMs);
                EnsureMainWindow(mainWindow);

                ProcessResult retry = ProcessOne(policy, mainWindow);
                if (retry.Status != "Failed")
                {
                    return retry;
                }
                retry.Message = "Failed after retry: " + retry.Message;
                return retry;
            }

            return result;
        }

        private ProcessResult ProcessOne(PolicyItem policy, string mainWindow)
        {
            ProcessResult result = new ProcessResult();
            result.Entity = _entity;
            result.PolRefNo = policy.PolRefNo;
            result.PolCod = policy.PolCod;

            try
            {
                // a. Click SearchClient. Matched by its label first: the
                // positional path sits next to the Broker Buddy button in the
                // header and can land on that instead, which opens the chatbot
                // in a new tab and leaves the search never having happened.
                _helper.Delay(4000);
                IList<string> tabsBeforeSearch = _helper.WindowHandles();

                if (!_helper.SafeClick(_config.XPaths.SearchClientButton) &&
                    !_helper.SafeClick(_config.XPaths.SearchClientButtonFallback))
                {
                    throw new Exception("Could not click 'Search clients'. " +
                        "Current URL: " + _helper.CurrentUrl());
                }
                _helper.Delay(4000);

                // If that click opened a tab, it hit the wrong control.
                if (_helper.WindowHandles().Count > tabsBeforeSearch.Count)
                {
                    Console.WriteLine("  WARNING: clicking 'Search clients' opened a " +
                        "new tab; the xpath is hitting the wrong control. Closing it.");
                    _helper.CloseTabsExceptUrl(_config.Login.LoginSuccessUrl);
                }
                // b. Switch to the Policies tab. The page opens on Clients, and
                // a polcod searched there matches nothing. Selected before
                // typing, since changing tab can reset the search box.
                if (!string.IsNullOrEmpty(_config.XPaths.SearchPoliciesTab))
                {
                    if (_helper.SafeClick(_config.XPaths.SearchPoliciesTab))
                    {
                        _helper.Delay(2000);
                    }
                    else
                    {
                        Console.WriteLine("  WARNING: could not select the 'Policies' " +
                            "tab; searching under 'Clients' will return no results.");
                    }
                }

                // c. Enter polcod and submit
                _helper.Type(_config.XPaths.SearchBody, policy.PolCod);
                _helper.WaitForVisible(_config.XPaths.SearchBody)
                    .SendKeys(Keys.Enter);
                _helper.Delay(4000);

                // c. Click result button
            
                string resultByText =
                    "//*[@id='mainContent']//div[contains(normalize-space(.),'" +
                    policy.PolCod + "') and not(.//div[contains(normalize-space(.),'" +
                    policy.PolCod + "')])]";
                bool resultClicked = _helper.SafeClick(resultByText);
                if (!resultClicked)
                {
                    resultClicked = _helper.SafeClick(_config.XPaths.ResultButton);
                }
                if (!resultClicked)
                {
                    throw new Exception("Result card not clickable (search results " +
                        "may not have loaded). polcod=" + policy.PolCod +
                        " | Current URL: " + _helper.CurrentUrl());
                }
                
                // d. Click first div
                //_helper.Click(_config.XPaths.FirstDiv);
                _helper.Delay(4000);
                // e. Open Documents tab
                _helper.Click(_config.XPaths.DocumentsTab);
                _helper.Delay(5000);
                // f. Check Premium Payment Notices enabled/disabled
                IWebElement notices =
                    _helper.WaitForElement(_config.XPaths.PremiumPaymentNotices);
                if (!_helper.IsEnabled(notices))
                {
                    result.Status = "Disabled";
                    result.Message = "Premium Payment Notices disabled; skipped.";
                    return result;
                }

                // g. Click first doc div -> new blob tab opens
                _helper.Click(_config.XPaths.PremiumPaymentNotices);
                _helper.Delay(5000);

                // Capture the tabs open right now, so the document tab is
                // identified by being new rather than by the window count.
                IList<string> tabsBeforeOpen = _helper.WindowHandles();
                _helper.Click(_config.XPaths.FirstDocDiv);

                // h. Switch to new tab and read the blob PDF
                string newTab = _helper.SwitchToNewTab(tabsBeforeOpen);
                _helper.Delay(2000);

                string blobUrl = _helper.CurrentUrl();
                Console.WriteLine("Opened document: " + blobUrl);

                decimal? amount = null;
                try
                {
                    amount = _pdf.ExtractTotalPayable(blobUrl);
                }
                finally
                {
                    // Always close the doc tab and return.
                    _helper.CloseTabAndReturn(mainWindow);
                }

                if (!amount.HasValue)
                {
                    result.Status = "Failed";
                    result.Message = "Could not extract amount from PDF.";
                    return result;
                }

                result.Amount = amount;

                // j. Update paypln
                int rows = _db.UpdatePremiumAmount(policy.PolRefNo, amount.Value);
                if (rows == 0)
                {
                    result.Status = "Failed";
                    result.Message = "Amount " + amount.Value +
                        " extracted but no paypln row matched (polrefno/planyr=2).";
                    return result;
                }

                result.Status = "Success";
                result.Message = "Updated " + rows + " row(s).";
                return result;
            }
            catch (Exception ex)
            {
                result.Status = "Failed";
                result.Message = ex.Message;
                return result;
            }
        }

        private void EnsureMainWindow(string mainWindow)
        {
            try
            {
                IList<string> handles = _helper.WindowHandles();
                // Close any stray tabs that aren't the main window.
                foreach (string h in handles)
                {
                    if (h != mainWindow)
                    {
                        try
                        {
                            _driver.SwitchTo().Window(h);
                            _driver.Close();
                        }
                        catch { }
                    }
                }
                _driver.SwitchTo().Window(mainWindow);
            }
            catch (Exception ex)
            {
                Console.WriteLine("WARNING: could not reset to main window: " + ex.Message);
            }
        }
    }
}
