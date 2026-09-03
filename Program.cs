using System;
using System.Collections.Generic;
using OpenQA.Selenium;
using OpenQA.Selenium.Chrome;
using AutomatePayplnForSunLifeFor2yrs.Models;
using AutomatePayplnForSunLifeFor2yrs.Services;

namespace AutomatePayplnForSunLifeFor2yrs
{
    class Program
    {
        static void Main(string[] args)
        {
            string configPath = "config.json";
            bool driverTestOnly = false;

            foreach (string a in args)
            {
                if (a.Equals("--test-driver", StringComparison.OrdinalIgnoreCase))
                {
                    driverTestOnly = true;
                }
                else
                {
                    configPath = a;
                }
            }

            if (driverTestOnly)
            {
                RunDriverSelfTest();
                return;
            }

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

                ChromeOptions options = new ChromeOptions();
                options.AddArgument("--start-maximized");
                options.AddArgument("--disable-notifications");
                options.AddArgument("--disable-popup-blocking");
                if (config.Headless)
                {
                    options.AddArgument("--headless=new");
                    options.AddArgument("--window-size=1920,1080");
                }

                // Resolves a chromedriver.exe whose major version matches the
                // installed Chrome and starts the service against that exact
                // binary. See ChromeDriverFactory for why the path is explicit.
                Console.WriteLine("Setting up ChromeDriver...");
                driver = ChromeDriverFactory.Create(options, config.PageLoadTimeoutSec);
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
        // Checks Chrome/ChromeDriver version matching on its own, without
        // touching the database or the portal:
        //   AutomatePayplnForSunLifeFor2yrs.exe --test-driver
        static void RunDriverSelfTest()
        {
            IWebDriver driver = null;
            try
            {
                ChromeOptions options = new ChromeOptions();
                options.AddArgument("--headless=new");
                options.AddArgument("--window-size=1920,1080");

                Console.WriteLine("Setting up ChromeDriver...");
                driver = ChromeDriverFactory.Create(options, 60);

                driver.Navigate().GoToUrl("about:blank");
                Console.WriteLine("SUCCESS: Chrome started and accepted a command.");
            }
            catch (Exception ex)
            {
                Console.WriteLine("FAILED: " + ex.Message);
            }
            finally
            {
                if (driver != null)
                {
                    try { driver.Quit(); }
                    catch { }
                }
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
