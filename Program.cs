using System;
using System.Collections.Generic;
using OpenQA.Selenium;
using OpenQA.Selenium.Chrome;
using WebDriverManager;
using WebDriverManager.DriverConfigs.Impl;
using AutomatePayplnForSunLifeFor2yrs.Models;
using AutomatePayplnForSunLifeFor2yrs.Services;

namespace AutomatePayplnForSunLifeFor2yrs
{
    class Program
    {
        static void Main(string[] args)
        {
            string configPath = args.Length > 0 ? args[0] : "config.json";
            IWebDriver driver = null;

            try
            {
                CredentialProvider provider = new CredentialProvider();
                AppConfig config = provider.Load(configPath);

                DatabaseService db = new DatabaseService(config.ConnectionString);

                Console.WriteLine("Ensuring credentials table exists...");
                db.EnsureCredentialTable();

                Console.WriteLine("Loading portal credentials from database...");
                string appKey = string.IsNullOrEmpty(config.CredentialAppKey)
                    ? "SunLifePortal" : config.CredentialAppKey;
                PortalCredential cred = db.GetPortalCredential(appKey);
                if (cred == null ||
                    string.IsNullOrEmpty(cred.Username) ||
                    string.IsNullOrEmpty(cred.Password))
                {
                    Console.WriteLine("ERROR: no active credentials found in " +
                        "dbo.AutomationCredentials for AppKey '" + appKey + "'.");
                    Console.WriteLine("Run sql/AutomationCredentials.sql and set a row first.");
                    return;
                }
                config.Username = cred.Username;
                config.Password = cred.Password;
                Console.WriteLine("Credentials loaded for user: " + config.Username);

                Console.WriteLine("Ensuring log table exists...");
                db.EnsureLogTable();

                Console.WriteLine("Running stored procedure to get policy list...");
                List<PolicyItem> policies = db.GetPoliciesFromStoredProc();
                Console.WriteLine("Found " + policies.Count + " policies to process.");

                if (policies.Count == 0)
                {
                    Console.WriteLine("Nothing to do. Exiting.");
                    return;
                }

                // Auto-download the ChromeDriver matching the installed Chrome.
                Console.WriteLine("Setting up ChromeDriver...");
                new DriverManager().SetUpDriver(new ChromeConfig());

                ChromeOptions options = new ChromeOptions();
                options.AddArgument("--start-maximized");
                options.AddArgument("--disable-notifications");
                options.AddArgument("--disable-popup-blocking");
                if (config.Headless)
                {
                    options.AddArgument("--headless=new");
                    options.AddArgument("--window-size=1920,1080");
                }

                driver = new ChromeDriver(options);
                driver.Manage().Timeouts().PageLoad =
                    TimeSpan.FromSeconds(config.PageLoadTimeoutSec);
                // Required for the blob fetch in PdfAmountExtractor.
                driver.Manage().Timeouts().AsynchronousJavaScript =
                    TimeSpan.FromSeconds(60);

                SeleniumHelper helper = new SeleniumHelper(
                    driver, config.ElementWaitTimeoutSec, config.StepDelayMs);

                LoginHandler login = new LoginHandler(driver, helper, config);
                login.Login();

                PdfAmountExtractor pdf =
                    new PdfAmountExtractor(driver, config.TotalPayableLabel);

                ExtractionService extractor =
                    new ExtractionService(driver, helper, config, db, pdf);

                List<ProcessResult> results = extractor.Run(policies);

                PrintSummary(results);
            }
            catch (Exception ex)
            {
                Console.WriteLine("FATAL ERROR: " + ex.Message);
                Console.WriteLine(ex.StackTrace);
            }
            finally
            {
                if (driver != null)
                {
                    try { driver.Quit(); }
                    catch { }
                }
                Console.WriteLine("");
                Console.WriteLine("Press any key to exit...");
                Console.ReadKey();
            }
        }

        static void PrintSummary(List<ProcessResult> results)
        {
            int success = 0, disabled = 0, failed = 0;
            foreach (ProcessResult r in results)
            {
                if (r.Status == "Success") success++;
                else if (r.Status == "Disabled") disabled++;
                else failed++;
            }

            Console.WriteLine("");
            Console.WriteLine("==================== SUMMARY ====================");
            Console.WriteLine("Total processed : " + results.Count);
            Console.WriteLine("Success         : " + success);
            Console.WriteLine("Disabled/skipped: " + disabled);
            Console.WriteLine("Failed          : " + failed);
            Console.WriteLine("=================================================");
        }
    }
}
