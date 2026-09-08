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
            string onlyEntity = null;
            bool expectEntityName = false;

            // Usage: [config.json] [--test-driver] [--entity JUVO | --entity=JUVO]
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

            try
            {
                CredentialProvider provider = new CredentialProvider();
                AppConfig config = provider.Load(configPath);

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
                List<EntityRun> runs = PrepareRuns(db, entities);
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

                    all.AddRange(RunEntity(config, db, run));
                }

                PrintSummary(all);
            }
            catch (Exception ex)
            {
                Console.WriteLine("FATAL ERROR: " + ex.Message);
                Console.WriteLine(ex.StackTrace);
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
            DatabaseService db, List<EntityRegistration> entities)
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
                    Console.WriteLine("  SKIPPED: Username or Password is empty " +
                        "on this row.");
                    continue;
                }
                Console.WriteLine("  portal user: " + e.Username);

                if (string.IsNullOrEmpty(e.StoredProcedure))
                {
                    Console.WriteLine("  SKIPPED: StoredProcedure is empty. Set it " +
                        "on this row, for example: UPDATE dbo.AutomationCredentials " +
                        "SET StoredProcedure = '<proc name>' WHERE Id = " + e.Id + ";");
                    continue;
                }

                if (!db.ProcedureExists(e.StoredProcedure))
                {
                    Console.WriteLine("  SKIPPED: stored procedure '" +
                        e.StoredProcedure + "' does not exist. Fix " +
                        "StoredProcedure on dbo.AutomationCredentials row Id " +
                        e.Id + ".");
                    continue;
                }

                // Entities share one procedure and are separated by ACTCOD.
                // Running without it would hand this entity another entity's
                // policies and write the wrong amounts, so it is a hard stop.
                bool takesActCod = db.ProcedureHasActCodParameter(e.StoredProcedure);
                if (takesActCod && string.IsNullOrEmpty(e.ActCod))
                {
                    Console.WriteLine("  SKIPPED: " + e.StoredProcedure + " takes an " +
                        "ACTCOD parameter but ACTCOD is empty on row Id " + e.Id +
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
                    Console.WriteLine("  SKIPPED: " + e.StoredProcedure +
                        " failed: " + ex.Message);
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

        // Each entity is a different portal user, so it gets its own browser
        // session: a fresh driver avoids carrying the previous Okta session
        // over and having to drive a logout flow.
        static List<ProcessResult> RunEntity(
            AppConfig config, DatabaseService db, EntityRun run)
        {
            IWebDriver driver = null;

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

                SeleniumHelper helper = new SeleniumHelper(
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
