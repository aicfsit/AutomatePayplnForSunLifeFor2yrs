# AutomatePayplnForSunLifeFor2yrs

Console app that:
1. Runs `SP_PREMIUM_DUE_NOTIFICATION_JUVO_NEW_FOR_SECONDYRPAYPLN` to get the list of policies (polrefno + polcod).
2. Logs into the SunLife (Okta) portal, pausing for MFA if needed.
3. For each polcod: searches the client, opens the policy, goes to the Documents tab.
4. Checks **Premium Payment Notices** — skips if disabled.
5. Opens the premium notice PDF (blob URL), extracts **Total Payable Amount**.
6. `UPDATE paypln SET clnprmamt = <amount> WHERE polrefno = <ibs#> AND planyr = 2`.
7. Logs every policy to `dbo.PremiumExtractionLog`.

## Requirements
- .NET Framework **4.6.2 or higher** (4.8 recommended).
  > Note: true C# 5 / .NET 4.5 cannot restore Selenium 4 or PdfPig. The *source* is written in C# 5 syntax (`LangVersion=5`), but the packages need 4.6.2+. If you are locked to .NET 4.5, tell me and I'll downgrade Selenium to a 3.x version that supports it.
- Google Chrome installed (WebDriverManager auto-downloads the matching driver).
- Network access to the SQL Server and the portal.

## Credentials
The **portal (Okta) username + password** are stored in `dbo.AutomationCredentials`, NOT in config.json. The app reads them at startup using the `connectionString` + `credentialAppKey` from config.

1. Run `sql/AutomationCredentials.sql` (or let the app auto-create the empty table).
2. Insert a row with your real username and the password. Example:
   ```sql
   INSERT INTO dbo.AutomationCredentials (AppKey, Username, [Password], IsActive)
   VALUES ('SunLifePortal', 'your.user', 'May2026!', 1);
   ```
3. `credentialAppKey` in config.json must match the `AppKey` value (default `SunLifePortal`).

Note: password is stored as plain text per requirement. The DB connection string itself still lives in config.json (it has to, to reach the table in the first place).

## Setup
1. Open the folder in Visual Studio (or `dotnet build`).
2. Restore NuGet packages (Selenium.WebDriver, WebDriverManager, Newtonsoft.Json, PdfPig).
3. Edit **config.json**:
   - `url`, `username`, `password`
   - `connectionString` (points to ERP_IBS_01062026)
   - Verify the **login xpaths** — the username/password values you gave pointed at a label and a div, not inputs. Defaults use Okta's standard inputs (`input[@name='identifier']`, `input[@type='password']`). Adjust if wrong.
   - Tune `stepDelayMs`, `retryDelayMs`, `mfaWaitTimeoutSec` for the portal's speed.
4. Build and run.

## Failure handling
- Any step that throws is retried once after `retryDelayMs`. Still failing → logged as `Failed`, loop continues.
- Disabled Premium Payment Notices → logged as `Disabled`, skipped.
- Configurable `stepDelayMs` between each UI action for the slow portal.

## Things you must verify before a live run
- All xpaths against the live DOM (these break easily on SPAs).
- The blob PDF actually contains selectable text. If the notice is a *scanned image*, PdfPig returns nothing and you'd need OCR — tell me and I'll add a Tesseract fallback.
- Test on ONE polcod first (comment out the loop or hardcode a single item) before running the whole SP list against the live `paypln` table.
