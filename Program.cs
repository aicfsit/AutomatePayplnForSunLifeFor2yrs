using System;
using System.Collections.Generic;
using System.IO;
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
            bool emailTestOnly = false;
            string onlyEntity = null;
            bool expectEntityName = false;

            // Usage: [config.json] [--test-driver] [--test-email]
            //        [--entity JUVO | --entity=JUVO]
            foreach (string a in args)
            {
                if (expectEntityName)
                {
                    onlyEntity = a;
                    expectEntityName = false;
                }
                else if (a.Equals("--test-driver", StringComparison.OrdinalIgnoreCase))
                {
                    driverTestOnly = true;
                }
                else if (a.Equals("--test-email", StringComparison.OrdinalIgnoreCase))
                {
                    emailTestOnly = true;
                }
                else if (a.StartsWith("--entity=", StringComparison.OrdinalIgnoreCase))
                {
                    onlyEntity = a.Substring("--entity=".Length);
                }
                else if (a.Equals("--entity", StringComparison.OrdinalIgnoreCase))
                {
                    expectEntityName = true;
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

            if (emailTestOnly)
            {
                RunEmailSelfTest();
                return;
            }

            EmailNotifier notifier = null;

            try
            {
                CredentialProvider provider = new CredentialProvider();
                AppConfig config = provider.Load(configPath);

                notifier = new EmailNotifier(EmailSettingsProvider.Load());

                DatabaseService db = new DatabaseService(config.ConnectionString);

                Console.WriteLine("Ensuring credentials table exists...");
                db.EnsureCredentialTable();

                Console.WriteLine("Ensuring log table exists...");
                db.EnsureLogTable();

                Console.WriteLine("Loading entities from dbo.AutomationCredentials...");
                List<EntityRegistration> entities = ResolveEntities(db, onlyEntity);
                if (entities.Count == 0)
                {
                    Console.WriteLine("ERROR: no active rows in " +
                        "dbo.AutomationCredentials" +
                        (onlyEntity == null
                            ? "." : " matching --entity " + onlyEntity + "."));
                    return;
                }

                // Validate everything against the database BEFORE opening a
                // browser, so a bad AppKey or procedure name does not surface
                // only after a manual MFA prompt.
                List<string> prepareProblems = new List<string>();
                List<EntityRun> runs = PrepareRuns(db, entities, prepareProblems);

                // An entity that could not even be prepared never reaches the
                // browser, so this is the only place it gets reported.
                if (prepareProblems.Count > 0 && notifier != null)
                {
                    notifier.SendAlert(
                        "Paypln automation: " + prepareProblems.Count +
                            " entity/entities could not start",
                        "The following entities were skipped before the browser " +
                        "opened:" + Environment.NewLine + Environment.NewLine +
                        string.Join(Environment.NewLine, prepareProblems.ToArray()),
                        null);
                }

                if (runs.Count == 0)
                {
                    Console.WriteLine("Nothing to do. Exiting.");
                    return;
                }

                List<ProcessResult> all = new List<ProcessResult>();
                foreach (EntityRun run in runs)
                {
                    Console.WriteLine("");
                    Console.WriteLine("################################################");
                    Console.WriteLine(" ENTITY: " + run.Entity.Name +
                        "   ACTCOD: " + run.Entity.ActCod +
                        "   user: " + run.Entity.Username +
                        "   policies: " + run.Policies.Count);
                    Console.WriteLine("################################################");

                    all.AddRange(RunEntity(config, db, run, notifier));
                }

                PrintSummary(all);
            }
            catch (Exception ex)
            {
                Console.WriteLine("FATAL ERROR: " + ex.Message);
                Console.WriteLine(ex.StackTrace);

                if (notifier != null)
                {
                    notifier.SendAlert(
                        "Paypln automation FAILED to run",
                        "The automation stopped before completing." +
                        Environment.NewLine + Environment.NewLine +
                        "Machine : " + Environment.MachineName + Environment.NewLine +
                        "Time    : " + DateTime.Now + Environment.NewLine +
                        "Config  : " + configPath + Environment.NewLine +
                        Environment.NewLine +
                        "Error:" + Environment.NewLine + ex.Message +
                        Environment.NewLine + Environment.NewLine +
                        ex.StackTrace,
                        null);
                }
            }
            finally
            {
                Console.WriteLine("");
                Console.WriteLine("Press any key to exit...");
                Console.ReadKey();
            }
        }

        // An entity that passed validation and is ready to run.
        class EntityRun
        {
            public EntityRegistration Entity;
            public List<PolicyItem> Policies;
        }

        // Every active row of dbo.AutomationCredentials is an entity.
        // --entity narrows the run to one of them, matched on the entity
        // column (or the AppKey, so either name works from the command line).
        static List<EntityRegistration> ResolveEntities(
            DatabaseService db, string onlyEntity)
        {
            List<EntityRegistration> all = db.GetActiveEntities();
            Console.WriteLine("  " + all.Count + " active row(s) found.");

            if (string.IsNullOrEmpty(onlyEntity))
            {
                return all;
            }

            List<EntityRegistration> filtered = new List<EntityRegistration>();
            foreach (EntityRegistration e in all)
            {
                bool match =
                    (e.Name != null &&
                        e.Name.Equals(onlyEntity, StringComparison.OrdinalIgnoreCase)) ||
                    (e.AppKey != null &&
                        e.AppKey.Equals(onlyEntity, StringComparison.OrdinalIgnoreCase));
                if (match)
                {
                    filtered.Add(e);
                }
            }
            Console.WriteLine("  restricted to entity: " + onlyEntity);
            return filtered;
        }

        // Validate each entity and load its policy list. An entity that cannot
        // be prepared is reported and skipped so the others still run.
        static List<EntityRun> PrepareRuns(
            DatabaseService db, List<EntityRegistration> entities,
            List<string> problems)
        {
            List<EntityRun> runs = new List<EntityRun>();

            foreach (EntityRegistration e in entities)
            {
                // A row with no entity name still has to be identifiable.
                if (string.IsNullOrEmpty(e.Name))
                {
                    e.Name = e.AppKey;
                }

                Console.WriteLine("");
                Console.WriteLine("Preparing entity '" + e.Name +
                    "' (AppKey " + e.AppKey + ", row Id " + e.Id + ")...");

                if (string.IsNullOrEmpty(e.Username) ||
                    string.IsNullOrEmpty(e.Password))
                {
                    Skip(problems, e, "Username or Password is empty on this row.");
                    continue;
                }
                Console.WriteLine("  portal user: " + e.Username);

                if (string.IsNullOrEmpty(e.StoredProcedure))
                {
                    Skip(problems, e, "StoredProcedure is empty. Set it on this row, " +
                        "for example: UPDATE dbo.AutomationCredentials " +
                        "SET StoredProcedure = '<proc name>' WHERE Id = " + e.Id + ";");
                    continue;
                }

                if (!db.ProcedureExists(e.StoredProcedure))
                {
                    Skip(problems, e, "stored procedure '" + e.StoredProcedure +
                        "' does not exist. Fix StoredProcedure on " +
                        "dbo.AutomationCredentials row Id " + e.Id + ".");
                    continue;
                }

                // Entities share one procedure and are separated by ACTCOD.
                // Running without it would hand this entity another entity's
                // policies and write the wrong amounts, so it is a hard stop.
                bool takesActCod = db.ProcedureHasActCodParameter(e.StoredProcedure);
                if (takesActCod && string.IsNullOrEmpty(e.ActCod))
                {
                    Skip(problems, e, e.StoredProcedure + " takes an ACTCOD " +
                        "parameter but ACTCOD is empty on row Id " + e.Id +
                        ". Set it: UPDATE dbo.AutomationCredentials " +
                        "SET ACTCOD = '<code>' WHERE Id = " + e.Id + ";");
                    continue;
                }
                if (!takesActCod && !string.IsNullOrEmpty(e.ActCod))
                {
                    Console.WriteLine("  WARNING: ACTCOD " + e.ActCod + " is set, but " +
                        e.StoredProcedure + " declares no ACTCOD parameter so it " +
                        "cannot be passed. Every entity using this procedure will " +
                        "get the same policy list.");
                }

                List<PolicyItem> policies;
                try
                {
                    policies = db.GetPoliciesFromStoredProc(e.StoredProcedure, e.ActCod);
                }
                catch (Exception ex)
                {
                    Skip(problems, e, e.StoredProcedure + " failed: " + ex.Message);
                    continue;
                }

                Console.WriteLine("  " + policies.Count + " policies returned by " +
                    e.StoredProcedure + ".");
                if (policies.Count == 0)
                {
                    Console.WriteLine("  SKIPPED: nothing to process.");
                    continue;
                }

                EntityRun run = new EntityRun();
                run.Entity = e;
                run.Policies = policies;
                runs.Add(run);
            }

            return runs;
        }

        // Report a skipped entity and record it so the alert email can list it.
        // "nothing to process" is not recorded: no premiums due is normal.
        static void Skip(List<string> problems, EntityRegistration e, string reason)
        {
            Console.WriteLine("  SKIPPED: " + reason);
            problems.Add(e.Name + " (AppKey " + e.AppKey + ", row Id " + e.Id +
                "): " + reason);
        }

        // Each entity is a different portal user, so it gets its own browser
        // session: a fresh driver avoids carrying the previous Okta session
        // over and having to drive a logout flow.
        static List<ProcessResult> RunEntity(
            AppConfig config, DatabaseService db, EntityRun run,
            EmailNotifier notifier)
        {
            IWebDriver driver = null;
            SeleniumHelper helper = null;

            try
            {
                config.Username = run.Entity.Username;
                config.Password = run.Entity.Password;

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

                helper = new SeleniumHelper(
                    driver, config.ElementWaitTimeoutSec, config.StepDelayMs);

                LoginHandler login = new LoginHandler(driver, helper, config);
                login.Login();

                // The portal opens extra tabs of its own after login (the
                // Broker Buddy chatbot). Close them and settle on the portal
                // tab, otherwise that tab becomes the working window and the
                // document lookups run against the wrong page.
                helper.CloseTabsExceptUrl(config.Login.LoginSuccessUrl);

                PdfAmountExtractor pdf =
                    new PdfAmountExtractor(driver, config.TotalPayableLabel);

                ExtractionService extractor = new ExtractionService(
                    driver, helper, config, db, pdf, run.Entity.Name);

                return extractor.Run(run.Policies);
            }
            catch (Exception ex)
            {
                // One entity failing (bad login, MFA timeout) must not stop the
                // others. Record it and move on.
                Console.WriteLine("ERROR: entity '" + run.Entity.Name +
                    "' aborted: " + ex.Message);

                // Screenshot first, while the browser is still on the failing
                // page. helper is null if the driver itself never started.
                string shot = null;
                if (helper != null)
                {
                    shot = helper.CaptureScreenshot(
                        Path.Combine(AppDomain.CurrentDomain.BaseDirectory,
                            "ErrorScreenshots"),
                        run.Entity.Name + "_failure");
                }

                if (notifier != null)
                {
                    notifier.SendAlert(
                        "Paypln automation FAILED for " + run.Entity.Name,
                        "Entity '" + run.Entity.Name + "' stopped before " +
                        "processing its policies." + Environment.NewLine +
                        Environment.NewLine +
                        "Machine  : " + Environment.MachineName + Environment.NewLine +
                        "Time     : " + DateTime.Now + Environment.NewLine +
                        "Entity   : " + run.Entity.Name +
                            " (AppKey " + run.Entity.AppKey + ")" +
                            Environment.NewLine +
                        "Username : " + run.Entity.Username + Environment.NewLine +
                        "ACTCOD   : " + run.Entity.ActCod + Environment.NewLine +
                        "Policies : " + run.Policies.Count + " were queued" +
                            Environment.NewLine + Environment.NewLine +
                        "Error:" + Environment.NewLine + ex.Message +
                        (shot == null
                            ? Environment.NewLine + Environment.NewLine +
                              "(no screenshot: the browser was not running)"
                            : ""),
                        shot);
                }

                ProcessResult aborted = new ProcessResult();
                aborted.Entity = run.Entity.Name;
                aborted.Status = "Failed";
                aborted.Message = "Entity aborted before processing: " + ex.Message;

                try { db.InsertLog(aborted); }
                catch { }

                List<ProcessResult> one = new List<ProcessResult>();
                one.Add(aborted);
                return one;
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

        // Sends one alert through the real Email API so delivery can be checked
        // without running the pipeline:
        //   AutomatePayplnForSunLifeFor2yrs.exe --test-email
        static void RunEmailSelfTest()
        {
            try
            {
                EmailConfig settings = EmailSettingsProvider.Load();
                EmailNotifier notifier = new EmailNotifier(settings);

                if (!notifier.IsEnabled)
                {
                    Console.WriteLine("FAILED: email alerts are not enabled. Check " +
                        "email_enabled / email_api_base_url / failure_email_to in " +
                        "App.config.");
                    return;
                }

                Console.WriteLine("Sending test alert to " + settings.To + "...");
                notifier.SendAlert(
                    "Paypln automation: test alert",
                    "This is a test of the failure-alert email." +
                    Environment.NewLine + Environment.NewLine +
                    "Machine : " + Environment.MachineName + Environment.NewLine +
                    "Time    : " + DateTime.Now + Environment.NewLine +
                    Environment.NewLine +
                    "If you received this, real failures will reach you too.",
                    null);
            }
            catch (Exception ex)
            {
                Console.WriteLine("FAILED: " + ex.Message);
            }
        }

        static void PrintSummary(List<ProcessResult> results)
        {
            // Counts per entity, in the order the entities were processed.
            List<string> order = new List<string>();
            Dictionary<string, int[]> byEntity = new Dictionary<string, int[]>();

            foreach (ProcessResult r in results)
            {
                string key = string.IsNullOrEmpty(r.Entity) ? "(none)" : r.Entity;
                if (!byEntity.ContainsKey(key))
                {
                    byEntity.Add(key, new int[3]);
                    order.Add(key);
                }

                int[] c = byEntity[key];
                if (r.Status == "Success") c[0]++;
                else if (r.Status == "Disabled") c[1]++;
                else c[2]++;
            }

            int success = 0, disabled = 0, failed = 0;

            Console.WriteLine("");
            Console.WriteLine("==================== SUMMARY ====================");
            foreach (string key in order)
            {
                int[] c = byEntity[key];
                success += c[0];
                disabled += c[1];
                failed += c[2];

                Console.WriteLine(key.PadRight(16) +
                    " total=" + (c[0] + c[1] + c[2]) +
                    "  success=" + c[0] +
                    "  disabled=" + c[1] +
                    "  failed=" + c[2]);
            }
            Console.WriteLine("-------------------------------------------------");
            Console.WriteLine("Total processed : " + results.Count);
            Console.WriteLine("Success         : " + success);
            Console.WriteLine("Disabled/skipped: " + disabled);
            Console.WriteLine("Failed          : " + failed);
            Console.WriteLine("=================================================");
        }
    }
}
