using System;
using System.IO;
using System.Text;

namespace AutomatePayplnForSunLifeFor2yrs.Services
{
    // Mirrors everything written to the console into a dated log file.
    //
    // A scheduled run has no console attached, so without this every message
    // the app produces is lost and a failure leaves no trace at all. Installed
    // once at startup: existing Console.WriteLine calls need no changes.
    //
    // The folder is resolved from the assembly location, NOT the working
    // directory, so the log lands next to the exe no matter what "Start in" is
    // set to in Task Scheduler.
    public static class RunLog
    {
        private static TextWriter _original;
        private static StreamWriter _file;

        public static string Path { get; private set; }

        public static void Start()
        {
            try
            {
                string dir = System.IO.Path.Combine(
                    AppDomain.CurrentDomain.BaseDirectory, "Logs");
                if (!Directory.Exists(dir))
                {
                    Directory.CreateDirectory(dir);
                }

                Path = System.IO.Path.Combine(dir,
                    "run-" + DateTime.Now.ToString("yyyyMMdd") + ".log");

                _file = new StreamWriter(Path, true, Encoding.UTF8);
                _file.AutoFlush = true;   // survive a hard kill mid-run

                _original = Console.Out;
                Console.SetOut(new TeeWriter(_original, _file));

                Console.WriteLine("");
                Console.WriteLine("================================================");
                Console.WriteLine(" Run started " + DateTime.Now +
                    " on " + Environment.MachineName +
                    " as " + Environment.UserName);
                Console.WriteLine(" Exe folder: " +
                    AppDomain.CurrentDomain.BaseDirectory);
                Console.WriteLine(" Working dir: " + Directory.GetCurrentDirectory());
                Console.WriteLine(" Interactive session: " +
                    Environment.UserInteractive);
                Console.WriteLine("================================================");
            }
            catch (Exception ex)
            {
                // Logging must never stop the run.
                Console.WriteLine("WARNING: could not start the run log: " +
                    ex.Message);
            }
        }

        public static void Stop()
        {
            try
            {
                Console.WriteLine(" Run finished " + DateTime.Now);

                if (_original != null)
                {
                    Console.SetOut(_original);
                    _original = null;
                }
                if (_file != null)
                {
                    _file.Flush();
                    _file.Dispose();
                    _file = null;
                }
            }
            catch (Exception)
            {
            }
        }

        // Writes to the console and the file at the same time.
        private class TeeWriter : TextWriter
        {
            private readonly TextWriter _a;
            private readonly TextWriter _b;

            public TeeWriter(TextWriter a, TextWriter b)
            {
                _a = a;
                _b = b;
            }

            public override Encoding Encoding
            {
                get { return Encoding.UTF8; }
            }

            public override void Write(char value)
            {
                try { _a.Write(value); }
                catch (Exception) { }
                try { _b.Write(value); }
                catch (Exception) { }
            }

            public override void Write(string value)
            {
                try { _a.Write(value); }
                catch (Exception) { }
                try { _b.Write(value); }
                catch (Exception) { }
            }

            public override void WriteLine(string value)
            {
                // Timestamp each line so a scheduled run can be followed later.
                string stamped = DateTime.Now.ToString("HH:mm:ss") + "  " + value;
                try { _a.WriteLine(value); }
                catch (Exception) { }
                try { _b.WriteLine(stamped); }
                catch (Exception) { }
            }
        }
    }
}
