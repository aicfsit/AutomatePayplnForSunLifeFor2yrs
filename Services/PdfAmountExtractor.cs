using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text.RegularExpressions;
using OpenQA.Selenium;
using UglyToad.PdfPig;
using UglyToad.PdfPig.Content;

namespace AutomatePayplnForSunLifeFor2yrs.Services
{
    public class PdfAmountExtractor
    {
        private readonly IWebDriver _driver;
        private readonly string _label;

        public PdfAmountExtractor(IWebDriver driver, string totalPayableLabel)
        {
            _driver = driver;
            _label = totalPayableLabel;
        }

        // The PDF opens as a blob: URL that only the browser session can read.
        // We fetch it inside the page as base64, bring the bytes back to C#,
        // then parse the text with PdfPig.
        public decimal? ExtractTotalPayable(string blobUrl)
        {
            string base64 = FetchBlobAsBase64(blobUrl);
            if (string.IsNullOrEmpty(base64))
            {
                return null;
            }

            byte[] bytes = Convert.FromBase64String(base64);
            string text = ReadPdfText(bytes);
            return ParseAmount(text);
        }

        private string FetchBlobAsBase64(string blobUrl)
        {
            IJavaScriptExecutor js = (IJavaScriptExecutor)_driver;

            // Async JS: fetch blob, read as base64 data URL, hand back just the base64 part.
            string script =
                "var callback = arguments[arguments.length - 1];" +
                "var url = arguments[0];" +
                "fetch(url).then(function(r){ return r.blob(); })" +
                ".then(function(b){" +
                "  var reader = new FileReader();" +
                "  reader.onloadend = function(){" +
                "    var res = reader.result;" +
                "    var idx = res.indexOf(',');" +
                "    callback(idx >= 0 ? res.substring(idx + 1) : res);" +
                "  };" +
                "  reader.onerror = function(){ callback(''); };" +
                "  reader.readAsDataURL(b);" +
                "}).catch(function(e){ callback(''); });";

            // Async script timeout must be set on the driver (see Program.cs).
            object result = js.ExecuteAsyncScript(script, blobUrl);
            return result == null ? null : result.ToString();
        }

        private string ReadPdfText(byte[] bytes)
        {
            System.Text.StringBuilder sb = new System.Text.StringBuilder();
            using (PdfDocument document = PdfDocument.Open(bytes))
            {
                foreach (Page page in document.GetPages())
                {
                    sb.AppendLine(page.Text);
                }
            }
            return sb.ToString();
        }

        // Look for the label, then capture the first currency-like number after it.
        // Falls back to the largest $ amount on the page if the label isn't found.
        private decimal? ParseAmount(string text)
        {
            if (string.IsNullOrEmpty(text))
            {
                return null;
            }

            // Normalise whitespace so the label and number sit close together.
            string flat = Regex.Replace(text, "\\s+", " ");

            // Try: "<label> ... $1,869.19"
            if (!string.IsNullOrEmpty(_label))
            {
                int idx = flat.IndexOf(_label, StringComparison.OrdinalIgnoreCase);
                if (idx >= 0)
                {
                    string after = flat.Substring(idx + _label.Length);
                    Match m = Regex.Match(after, "[\\$]?\\s*([0-9][0-9,]*\\.[0-9]{2})");
                    if (m.Success)
                    {
                        return ToDecimal(m.Groups[1].Value);
                    }
                }
            }

            // Fallback: pick the largest currency value found anywhere.
            MatchCollection all = Regex.Matches(flat, "[0-9][0-9,]*\\.[0-9]{2}");
            decimal best = -1m;
            foreach (Match mm in all)
            {
                decimal? v = ToDecimal(mm.Value);
                if (v.HasValue && v.Value > best)
                {
                    best = v.Value;
                }
            }
            return best >= 0 ? (decimal?)best : null;
        }

        private decimal? ToDecimal(string raw)
        {
            string cleaned = raw.Replace(",", "").Replace("$", "").Trim();
            decimal val;
            if (decimal.TryParse(cleaned, NumberStyles.Any,
                CultureInfo.InvariantCulture, out val))
            {
                return val;
            }
            return null;
        }
    }
}
