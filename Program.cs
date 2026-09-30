using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using OpenQA.Selenium;
using OpenQA.Selenium.Chrome;
using AutomatePayplnForSunLifeFor2yrs.Models;
using AutomatePayplnForSunLifeFor2yrs.Services;

namespace AutomatePayplnForSunLifeFor2yrs
{
    class Program
    {
        // Returns 0 when everything succeeded, 1 otherwise, so Task Scheduler
        // (and any monitoring on top of it) can tell a good run from a bad one.
        static int Main(string[] args)
        {
            int exitCode = 0;

            // Clear out any chromedriver left behind by a previous run before
            // anything else. A crash or a closed console window orphans the
            // driver, and those keep a Chrome instance and its memory alive.
            RunLog.Start();
            KillAllChromeDriverProcesses();
            InstallExitHandlers();

            string configPath = "config.json";
            bool driverTestOnly = false;
            bool emailTestOnly = false;
            bool waitOnExit = false;
            bool portalTestOnly = false;
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
                else if (a.Equals("--test-portal", StringComparison.OrdinalIgnoreCase))
                {
                    portalTestOnly = true;
                }
                else if (a.Equals("--test-email", StringComparison.OrdinalIgnoreCase))
                {
                    emailTestOnly = true;
                }
                else if (a.Equals("--wait", StringComparison.OrdinalIgnoreCase))
                {
                    waitOnExit = true;
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
                return 0;
            }

            if (emailTestOnly)
            {
                RunEmailSelfTest();
                return 0;
            }

            if (portalTestOnly)
            {
                RunPortalSelfTest(configPath);
                return 0;
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
                    exitCode = 1;
                    return exitCode;
                }

                // Validate everything against the database BEFORE opening a
                // browser, so a bad AppKey or procedure name does not surface
                // only after a manual MFA prompt.
                List<EntityRun> runs = PrepareRuns(db, entities,
                    new ResultWriter(config.OutputFile), config.DryRun);

                if (runs.Count == 0)
                {
                    Console.WriteLine("Nothing to do. Exiting.");
                    return exitCode;
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

                    bool aborted;
                    all.AddRange(RunEntity(config, db, run, notifier, out aborted));

                    // Stop at the first entity that fails instead of carrying
                    // on: whatever broke (login, driver, portal) will almost
                    // certainly break the next one too.
                    if (aborted)
                    {
                        Console.WriteLine("");
                        Console.WriteLine("STOPPING: entity '" + run.Entity.Name +
                            "' failed. Remaining entities will not run.");
                        break;
                    }
                }

                string summary = BuildSummary(all);
                Console.WriteLine(summary);

                // A failed policy anywhere means the run did not fully succeed.
                foreach (ProcessResult r in all)
                {
                    if (r.Status != "Success" && r.Status != "Disabled")
                    {
                        exitCode = 1;
                    }
                }

                // Always sent, so that receiving nothing means the run never
                // started at all, rather than "it probably went fine".
                if (notifier != null)
                {
                    notifier.SendSummary(
                        (exitCode == 0 ? "Paypln automation OK" :
                                         "Paypln automation completed WITH FAILURES") +
                        " - " + Environment.MachineName,
                        "Machine : " + Environment.MachineName + Environment.NewLine +
                        "Time    : " + DateTime.Now + Environment.NewLine +
                        "Log     : " + RunLog.Path + Environment.NewLine +
                        Environment.NewLine + summary);
                }
            }
            catch (Exception ex)
            {
                exitCode = 1;
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
                // Last line of defence: anything Quit could not close — after a
                // crash, or a driver whose session was already gone — is swept
                // up here so no Chrome is left running once this exits.
                KillAllChromeDriverProcesses();

                Console.WriteLine("");
                RunLog.Stop();

                // Exit immediately once the work is done. Pass --wait to hold
                // the window open when running by hand; unattended runs must
                // never sit waiting for a keystroke nobody will press.
                if (waitOnExit && !Console.IsInputRedirected)
                {
                    try
                    {
                        Console.WriteLine("Press any key to exit...");
                        Console.ReadKey();
                    }
                    catch (Exception)
                    {
                        // No console attached; exit straight away.
                    }
                }
            }

            Console.WriteLine("Exit code: " + exitCode);
            return exitCode;
        }

        // The browser currently in use, so it can still be closed if the run is
        // interrupted rather than finishing normally.
        private static IWebDriver _activeDriver;

        // Closes whatever is still open. Safe to call more than once.
        private static void CleanupBrowser()
        {
            IWebDriver driver = _activeDriver;
            _activeDriver = null;

            if (driver != null)
            {
                try { driver.Quit(); }
                catch { }
            }

            KillAllChromeDriverProcesses();
        }

        // Ctrl+C and a normal process exit both give us a chance to tidy up.
        // A forced kill (taskkill /F, End Task) does not: the OS terminates the
        // process without running any code, so nothing can cover that case.
        private static void InstallExitHandlers()
        {
            try
            {
                Console.CancelKeyPress += delegate(
                    object sender, ConsoleCancelEventArgs e)
                {
                    Console.WriteLine("");
                    Console.WriteLine("Interrupted - closing the browser...");
                    CleanupBrowser();
                };

                AppDomain.CurrentDomain.ProcessExit += delegate(
                    object sender, EventArgs e)
                {
                    CleanupBrowser();
                };
            }
            catch (Exception)
            {
                // Handlers are a safety net; never let them stop the run.
            }
        }

        // Kills every chromedriver on the machine, not just ours: an orphan has
        // no parent left to ask. Note this will also stop a chromedriver owned
        // by another automation running at the same time on this box.
        private static void KillAllChromeDriverProcesses()
        {
            var processes = Process.GetProcessesByName("chromedriver");
            if (processes.Length == 0)
            {
                return;
            }

            Console.WriteLine("Killing " + processes.Length +
                " leftover chromedriver process(es)...");

            foreach (var process in processes)
            {
                try
                {
                    process.Kill();
                }
                catch
                {
                }
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

        // Validate each entity and load its policy list. Any entity that cannot
        // be prepared stops the application — see Fail.
        static List<EntityRun> PrepareRuns(
            DatabaseService db, List<EntityRegistration> entities,
            ResultWriter writer, bool dryRun)
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
                    Fail(db, writer, dryRun, e, "Username or Password is empty on " +
                        "dbo.AutomationCredentials row Id " + e.Id + ".");
                }
                Console.WriteLine("  portal user: " + e.Username);

                if (string.IsNullOrEmpty(e.StoredProcedure))
                {
                    Fail(db, writer, dryRun, e, "StoredProcedure is empty. Set it on this row, " +
                        "for example: UPDATE dbo.AutomationCredentials " +
                        "SET StoredProcedure = '<proc name>' WHERE Id = " + e.Id + ";");
                }

                if (!db.ProcedureExists(e.StoredProcedure))
                {
                    Fail(db, writer, dryRun, e, "stored procedure '" + e.StoredProcedure +
                        "' does not exist. Fix StoredProcedure on " +
                        "dbo.AutomationCredentials row Id " + e.Id + ".");
                }

                // Entities share one procedure and are separated by ACTCOD.
                // Running without it would hand this entity another entity's
                // policies and write the wrong amounts, so it is a hard stop.
                bool takesActCod = db.ProcedureHasActCodParameter(e.StoredProcedure);
                if (takesActCod && string.IsNullOrEmpty(e.ActCod))
                {
                    Fail(db, writer, dryRun, e, e.StoredProcedure + " takes an ACTCOD " +
                        "parameter but ACTCOD is empty on row Id " + e.Id +
                        ". Set it: UPDATE dbo.AutomationCredentials " +
                        "SET ACTCOD = '<code>' WHERE Id = " + e.Id + ";");
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
                    Fail(db, writer, dryRun, e, e.StoredProcedure + " failed: " + ex.Message);
                    return null; // unreachable: Fail always throws.
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

        // Any problem preparing an entity stops the whole application: it is
        // logged to the console and to PremiumExtractionLog, then thrown so
        // Main reports it and emails the alert. Never returns.
        //
        // "Nothing to process" does NOT come through here — no premiums due is
        // a normal night, not a failure.
        // Send a result row wherever this run's results are going: the CSV in a
        // dry run, PremiumExtractionLog otherwise. A logging failure is
        // reported but never stops the run.
        static void RecordRow(DatabaseService db, ResultWriter writer,
            bool dryRun, ProcessResult row)
        {
            if (dryRun)
            {
                writer.Write(row);
                return;
            }

            try
            {
                db.InsertLog(row);
            }
            catch (Exception ex)
            {
                Console.WriteLine("WARNING: could not write log row: " + ex.Message);
            }
        }

        static void Fail(DatabaseService db, ResultWriter writer, bool dryRun,
            EntityRegistration e, string reason)
        {
            string message = "Entity '" + e.Name + "' (AppKey " + e.AppKey +
                "): " + reason;

            Console.WriteLine("  FATAL: " + message);

            ProcessResult failed = new ProcessResult();
            failed.Entity = e.Name;
            failed.Status = "Failed";
            failed.Message = message;
            RecordRow(db, writer, dryRun, failed);

            throw new Exception(message + " Stopping the application.");
        }

        // Each entity is a different portal user, so it gets its own browser
        // session: a fresh driver avoids carrying the previous Okta session
        // over and having to drive a logout flow.
        static List<ProcessResult> RunEntity(
            AppConfig config, DatabaseService db, EntityRun run,
            EmailNotifier notifier, out bool aborted)
        {
            IWebDriver driver = null;
            SeleniumHelper helper = null;
            aborted = false;

            // Declared out here so the failure path can log through it too.
            ResultWriter writer = new ResultWriter(config.OutputFile);

            try
            {
                config.Username = run.Entity.Username;
                config.Password = run.Entity.Password;

                ChromeOptions options = new ChromeOptions();
                options.AddArgument("--disable-notifications");
                options.AddArgument("--disable-popup-blocking");

                if (config.Headless)
                {
                    // Note: the SunLife portal sits behind Akamai, which blocks
                    // headless Chrome outright with "Access Denied" before the
                    // login page ever renders. Prefer hideWindow instead.
                    options.AddArgument("--headless=new");
                    options.AddArgument("--window-size=1920,1080");
                }
                else if (config.HideWindow)
                {
                    // A real, visible-to-Chrome browser (so Akamai serves the
                    // page normally) parked far off-screen, so nothing appears
                    // on the desktop. Needs an interactive session to run in.
                    options.AddArgument("--window-size=1920,1080");
                    options.AddArgument("--window-position=-32000,-32000");
                }
                else
                {
                    options.AddArgument("--start-maximized");
                }

                // Chrome needs a desktop. Under Task Scheduler's "Run whether
                // user is logged on or not" the process lands in session 0,
                // which has none, and the browser fails in a way that looks
                // like an unrelated portal error. Say what is actually wrong.
                if (!Environment.UserInteractive)
                {
                    throw new Exception(
                        "No interactive desktop session (session 0), so Chrome " +
                        "cannot start. In Task Scheduler set the task to " +
                        "'Run only when user is logged on', and keep a logged-on " +
                        "session on this machine. Headless is not an option here: " +
                        "the portal's WAF blocks headless Chrome.");
                }

                // Resolves a chromedriver.exe whose major version matches the
                // installed Chrome and starts the service against that exact
                // binary. See ChromeDriverFactory for why the path is explicit.
                Console.WriteLine("Setting up ChromeDriver...");
                driver = ChromeDriverFactory.Create(options, config.PageLoadTimeoutSec);
                driver.Manage().Timeouts().PageLoad =
                    TimeSpan.FromSeconds(config.PageLoadTimeoutSec);
                _activeDriver = driver;

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

                if (config.DryRun)
                {
                    Console.WriteLine("DRY RUN: nothing will be written to " +
                        "paypln or PremiumExtractionLog.");
                    Console.WriteLine("Results file: " + writer.Path_);
                }

                ExtractionService extractor = new ExtractionService(
                    driver, helper, config, db, pdf, run.Entity.Name,
                    writer, config.DryRun);

                return extractor.Run(run.Policies);
            }
            catch (Exception ex)
            {
                // Caught here so the screenshot and the alert can be produced
                // while the browser is still up; the caller then stops the run.
                aborted = true;

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

                // Log every policy that was queued, not just one row for the
                // entity: each polrefno has to be accounted for even though
                // nothing was extracted for it.
                string reason = "Entity aborted before processing: " + ex.Message;
                List<ProcessResult> rows = new List<ProcessResult>();

                foreach (PolicyItem queued in run.Policies)
                {
                    ProcessResult row = new ProcessResult();
                    row.Entity = run.Entity.Name;
                    row.PolRefNo = queued.PolRefNo;
                    row.PolCod = queued.PolCod;
                    row.Status = "Failed";
                    row.Message = reason;
                    rows.Add(row);
                }

                // Nothing queued: still record that the entity failed.
                if (rows.Count == 0)
                {
                    ProcessResult row = new ProcessResult();
                    row.Entity = run.Entity.Name;
                    row.Status = "Failed";
                    row.Message = reason;
                    rows.Add(row);
                }

                foreach (ProcessResult row in rows)
                {
                    RecordRow(db, writer, config.DryRun, row);
                }

                return rows;
            }
            finally
            {
                // Closes the browser and its chromedriver. Quit can fail when
                // the session has already died, so report it instead of
                // swallowing it — that is the case that leaves Chrome running.
                if (driver != null)
                {
                    try
                    {
                        driver.Quit();
                        Console.WriteLine("Browser closed for " +
                            run.Entity.Name + ".");
                    }
                    catch (Exception quitEx)
                    {
                        Console.WriteLine("WARNING: could not close the browser " +
                            "cleanly (" + quitEx.Message +
                            "); it will be cleaned up at exit.");
                    }
                }
                _activeDriver = null;
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

        // Loads the portal's login page in both browser modes and reports which
        // ones the WAF serves. Read-only: no credentials are entered and the
        // database is never touched.
        //   AutomatePayplnForSunLifeFor2yrs.exe --test-portal
        static void RunPortalSelfTest(string configPath)
        {
            try
            {
                AppConfig config = new CredentialProvider().Load(configPath);
                Console.WriteLine("Checking: " + config.Url);
                Console.WriteLine("");

                CheckPortalMode(config, true, "headless        ");
                CheckPortalMode(config, false, "off-screen window");
            }
            catch (Exception ex)
            {
                Console.WriteLine("FAILED: " + ex.Message);
            }
        }

        static void CheckPortalMode(AppConfig config, bool headless, string label)
        {
            IWebDriver driver = null;
            try
            {
                ChromeOptions options = new ChromeOptions();
                options.AddArgument("--disable-notifications");
                options.AddArgument("--disable-popup-blocking");
                options.AddArgument("--window-size=1920,1080");
                if (headless)
                {
                    options.AddArgument("--headless=new");
                }
                else
                {
                    options.AddArgument("--window-position=-32000,-32000");
                }

                driver = ChromeDriverFactory.Create(options, config.PageLoadTimeoutSec);
                driver.Navigate().GoToUrl(config.Url);
                System.Threading.Thread.Sleep(8000);

                string title = driver.Title ?? "";
                string body = "";
                try
                {
                    body = driver.FindElement(By.TagName("body")).Text ?? "";
                }
                catch (Exception)
                {
                }

                bool blocked =
                    body.IndexOf("Access Denied", StringComparison.OrdinalIgnoreCase) >= 0 ||
                    title.IndexOf("Access Denied", StringComparison.OrdinalIgnoreCase) >= 0;

                Console.WriteLine(label + " : " +
                    (blocked ? "BLOCKED by the WAF" : "page served"));
                Console.WriteLine("                   title = '" + title.Trim() + "'");

                foreach (string line in body.Split('\n'))
                {
                    string t = line.Trim();
                    if (t.StartsWith("Reference #") ||
                        t.IndexOf("edgesuite", StringComparison.OrdinalIgnoreCase) >= 0)
                    {
                        Console.WriteLine("                   " + t);
                    }
                }
                Console.WriteLine("");
            }
            catch (Exception ex)
            {
                Console.WriteLine(label + " : ERROR " + ex.Message);
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

        // Builds the run summary as text, so the same wording goes to the
        // console, the log file and the summary email.
        static string BuildSummary(List<ProcessResult> results)
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

            System.Text.StringBuilder sb = new System.Text.StringBuilder();
            sb.AppendLine("");
            sb.AppendLine("==================== SUMMARY ====================");
            foreach (string key in order)
            {
                int[] c = byEntity[key];
                success += c[0];
                disabled += c[1];
                failed += c[2];

                sb.AppendLine(key.PadRight(16) +
                    " total=" + (c[0] + c[1] + c[2]) +
                    "  success=" + c[0] +
                    "  disabled=" + c[1] +
                    "  failed=" + c[2]);
            }
            sb.AppendLine("-------------------------------------------------");
            sb.AppendLine("Total processed : " + results.Count);
            sb.AppendLine("Success         : " + success);
            sb.AppendLine("Disabled/skipped: " + disabled);
            sb.AppendLine("Failed          : " + failed);
            sb.AppendLine("=================================================");

            return sb.ToString();
        }
    }
}
