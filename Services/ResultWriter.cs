using System;
using System.Globalization;
using System.IO;
using System.Text;
using AutomatePayplnForSunLifeFor2yrs.Models;

namespace AutomatePayplnForSunLifeFor2yrs.Services
{
    // Test-mode sink. In a dry run nothing is written to paypln or to
    // PremiumExtractionLog; every result is appended to a CSV instead so the
    // extracted amounts can be checked before any database is touched.
    public class ResultWriter
    {
        private readonly string _path;
        private bool _headerWritten;

        public ResultWriter(string path)
        {
            _path = string.IsNullOrEmpty(path)
                ? Path.Combine(AppDomain.CurrentDomain.BaseDirectory,
                    "extraction-results.csv")
                : path;

            if (!Path.IsPathRooted(_path))
            {
                _path = Path.Combine(
                    AppDomain.CurrentDomain.BaseDirectory, _path);
            }
        }

        public string Path_ { get { return _path; } }

        public void Write(ProcessResult result)
        {
            try
            {
                EnsureHeader();

                StringBuilder sb = new StringBuilder();
                sb.Append(Csv(DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss"))).Append(',');
                sb.Append(Csv(result.Entity)).Append(',');
                sb.Append(Csv(result.PolRefNo)).Append(',');
                sb.Append(Csv(result.PolCod)).Append(',');
                sb.Append(Csv(result.Amount.HasValue
                    ? result.Amount.Value.ToString(CultureInfo.InvariantCulture)
                    : "")).Append(',');
                sb.Append(Csv(result.Status)).Append(',');
                sb.Append(Csv(result.Message));

                File.AppendAllText(_path, sb.ToString() + Environment.NewLine,
                    Encoding.UTF8);
            }
            catch (Exception ex)
            {
                Console.WriteLine("WARNING: could not write result row: " +
                    ex.Message);
            }
        }

        private void EnsureHeader()
        {
            if (_headerWritten)
            {
                return;
            }
            _headerWritten = true;

            string dir = System.IO.Path.GetDirectoryName(_path);
            if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir))
            {
                Directory.CreateDirectory(dir);
            }

            if (!File.Exists(_path))
            {
                File.AppendAllText(_path,
                    "Timestamp,Entity,PolRefNo,PolCod,Amount,Status,Message" +
                    Environment.NewLine, Encoding.UTF8);
            }
        }

        private static string Csv(string value)
        {
            if (value == null)
            {
                return "";
            }
            // Quote when the value could break the column layout.
            if (value.IndexOf(',') >= 0 || value.IndexOf('"') >= 0 ||
                value.IndexOf('\n') >= 0 || value.IndexOf('\r') >= 0)
            {
                return "\"" + value.Replace("\"", "\"\"").Replace("\r", " ")
                    .Replace("\n", " ") + "\"";
            }
            return value;
        }
    }
}
