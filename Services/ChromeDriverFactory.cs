using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using Microsoft.Win32;
using OpenQA.Selenium.Chrome;

namespace AutomatePayplnForSunLifeFor2yrs.Services
{
    // Starts ChromeDriver against a driver whose MAJOR version matches the
    // installed Chrome. Only the major version has to match, so Chrome
    // 152.0.7977.76 runs fine on driver 152.0.7977.75.
    //
    // Resolution order:
    //   1. A cached chromedriver.exe under the output folder whose major
    //      version matches Chrome. Keeps working with no network access.
    //   2. Selenium Manager (ships with Selenium 4.18) which detects Chrome
    //      and downloads the matching driver from Chrome for Testing.
    //
    // WebDriverManager is deliberately NOT used: it predates the Chrome for
    // Testing endpoints, so it returns 404 for any exact Chrome 115+ build and
    // its "latest" path silently downloads nothing.
    //
    // The driver path is passed explicitly rather than relying on
    // "new ChromeDriver(options)" because the default service takes the FIRST
    // chromedriver.exe it finds in the app base directory, then PATH. A stale
    // copy there (for example one dropped by the Selenium.WebDriver.ChromeDriver
    // package) silently shadows the correct driver, and Chrome then refuses the
    // session with "only supports Chrome version N".
    public static class ChromeDriverFactory
    {
        public static ChromeDriver Create(ChromeOptions options, int pageLoadTimeoutSec)
        {
            string baseDir = AppDomain.CurrentDomain.BaseDirectory;
            string chromeVersion = GetInstalledChromeVersion();
            int chromeMajor = GetMajor(chromeVersion);

            if (chromeMajor > 0)
            {
                Console.WriteLine("Installed Chrome version: " + chromeVersion +
                    " (major " + chromeMajor + ")");
            }
            else
            {
                Console.WriteLine("WARNING: could not detect the installed Chrome " +
                    "version; leaving the driver choice to Selenium Manager.");
            }

            WarnAboutShadowingDriver(baseDir, chromeMajor);

            int commandTimeout = pageLoadTimeoutSec < 60 ? 60 : pageLoadTimeoutSec;
            string driverPath = FindCachedDriver(baseDir, chromeMajor);

            if (!string.IsNullOrEmpty(driverPath))
            {
                Console.WriteLine("Using cached ChromeDriver: " + driverPath +
                    " (" + GetFileVersion(driverPath) + ")");

                ChromeDriverService service = ChromeDriverService.CreateDefaultService(
                    Path.GetDirectoryName(driverPath), Path.GetFileName(driverPath));
                service.HideCommandPromptWindow = true;

                try
                {
                    return new ChromeDriver(service, options,
                        TimeSpan.FromSeconds(commandTimeout));
                }
                catch (Exception ex)
                {
                    // A same-major driver can still be rejected (for example
                    // after a Chrome update mid-session). Let Selenium Manager
                    // fetch a fresh one rather than aborting the whole run.
                    Console.WriteLine("Cached driver was rejected (" + ex.Message +
                        "); falling back to Selenium Manager.");
                }
            }
            else
            {
                Console.WriteLine("No cached driver matches Chrome " + chromeMajor +
                    "; using Selenium Manager.");
            }

            return new ChromeDriver(options);
        }

        // A chromedriver.exe in the output root wins over everything else when
        // Selenium builds a default service. Say so loudly if it is the wrong one.
        private static void WarnAboutShadowingDriver(string baseDir, int chromeMajor)
        {
            string rootDriver = Path.Combine(baseDir, "chromedriver.exe");
            if (!File.Exists(rootDriver) || chromeMajor <= 0)
            {
                return;
            }

            int rootMajor = GetMajor(GetFileVersion(rootDriver));
            if (rootMajor > 0 && rootMajor != chromeMajor)
            {
                Console.WriteLine("WARNING: " + rootDriver + " is version " +
                    rootMajor + " but Chrome is " + chromeMajor +
                    ". Delete it, and remove any Selenium.WebDriver.ChromeDriver " +
                    "package reference that keeps putting it back.");
            }
        }

        // Pick the cached chromedriver.exe whose major version matches Chrome,
        // preferring the highest build when several are present.
        private static string FindCachedDriver(string baseDir, int chromeMajor)
        {
            if (chromeMajor <= 0)
            {
                return null;
            }

            List<string> candidates = new List<string>();
            try
            {
                candidates.AddRange(Directory.GetFiles(
                    baseDir, "chromedriver.exe", SearchOption.AllDirectories));
            }
            catch (Exception)
            {
                return null;
            }

            string best = null;
            Version bestVersion = null;

            foreach (string path in candidates)
            {
                string raw = GetFileVersion(path);
                if (GetMajor(raw) != chromeMajor)
                {
                    continue;
                }

                Version parsed = ParseVersion(raw);
                if (best == null || (parsed != null &&
                    (bestVersion == null || parsed > bestVersion)))
                {
                    best = path;
                    bestVersion = parsed;
                }
            }

            return best;
        }

        // Detect the installed Chrome version on Windows. Tries both registry
        // hives, then the file version of chrome.exe. Returns null if unknown.
        public static string GetInstalledChromeVersion()
        {
            string[] regPaths = new string[]
            {
                @"SOFTWARE\Google\Chrome\BLBeacon",
                @"SOFTWARE\Wow6432Node\Google\Chrome\BLBeacon"
            };
            RegistryKey[] hives = new RegistryKey[]
            {
                Registry.CurrentUser,
                Registry.LocalMachine
            };

            // Each hive is checked separately: a key that exists but carries no
            // "version" value must not stop us looking in the other hive.
            foreach (RegistryKey hive in hives)
            {
                foreach (string rp in regPaths)
                {
                    try
                    {
                        using (RegistryKey key = hive.OpenSubKey(rp))
                        {
                            if (key == null)
                            {
                                continue;
                            }
                            object v = key.GetValue("version");
                            if (v != null && !string.IsNullOrEmpty(v.ToString()))
                            {
                                return v.ToString();
                            }
                        }
                    }
                    catch (Exception)
                    {
                    }
                }
            }

            string programFiles =
                Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles);
            string programFilesX86 =
                Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86);
            string localAppData =
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);

            string[] exePaths = new string[]
            {
                Path.Combine(programFiles, @"Google\Chrome\Application\chrome.exe"),
                Path.Combine(programFilesX86, @"Google\Chrome\Application\chrome.exe"),
                Path.Combine(localAppData, @"Google\Chrome\Application\chrome.exe")
            };

            foreach (string ep in exePaths)
            {
                string fv = GetFileVersion(ep);
                if (!string.IsNullOrEmpty(fv))
                {
                    return fv;
                }
            }

            return null;
        }

        private static string GetFileVersion(string path)
        {
            try
            {
                if (!File.Exists(path))
                {
                    return null;
                }
                FileVersionInfo fvi = FileVersionInfo.GetVersionInfo(path);
                return fvi.FileVersion;
            }
            catch (Exception)
            {
                return null;
            }
        }

        private static int GetMajor(string version)
        {
            if (string.IsNullOrEmpty(version))
            {
                return 0;
            }

            string head = version.Trim().Split('.')[0];
            int major;
            return int.TryParse(head, out major) ? major : 0;
        }

        private static Version ParseVersion(string version)
        {
            if (string.IsNullOrEmpty(version))
            {
                return null;
            }

            Version parsed;
            return Version.TryParse(version.Trim(), out parsed) ? parsed : null;
        }
    }
}
