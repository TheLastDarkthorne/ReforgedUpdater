using System;
using System.Globalization;

namespace ReforgedUpdater
{
    internal enum UiLevel { Info, Head, Good, Warn, Error }

    /// <summary>Takes the updater's output when a window, rather than a console, is showing it.</summary>
    internal interface IUiSink
    {
        void Message(UiLevel level, string text);
        void Progress(string label, long done, long total, double bytesPerSecond);
        void EndProgress();
    }

    /// <summary>Console output helpers: colours, sizes, and the in-place progress line.</summary>
    internal static class Ui
    {
        public static bool UseColor = true;
        public static bool Quiet;

        /// <summary>When set, output goes here instead of the console. Called from any thread.</summary>
        public static IUiSink Sink { get; set; }

        private static bool CanRedraw
        {
            get
            {
                try { return !Console.IsOutputRedirected && Console.WindowWidth > 20; }
                catch { return false; }
            }
        }

        private static void Write(string text, ConsoleColor color, System.IO.TextWriter stream = null)
        {
            stream = stream ?? Console.Out;
            if (!UseColor) { stream.WriteLine(text); return; }
            var previous = Console.ForegroundColor;
            Console.ForegroundColor = color;
            stream.WriteLine(text);
            Console.ForegroundColor = previous;
        }

        public static void Info(string text)
        {
            if (Sink != null) { Sink.Message(UiLevel.Info, text); return; }
            if (!Quiet) Console.WriteLine(text);
        }

        public static void Head(string text)
        {
            if (Sink != null) { Sink.Message(UiLevel.Head, text); return; }
            if (!Quiet) Write(text, ConsoleColor.Cyan);
        }

        public static void Good(string text)
        {
            if (Sink != null) { Sink.Message(UiLevel.Good, text); return; }
            if (!Quiet) Write(text, ConsoleColor.Green);
        }

        // Problems go to stderr, so machine-readable output on stdout (--json) stays clean.
        public static void Warn(string text)
        {
            if (Sink != null) { Sink.Message(UiLevel.Warn, text); return; }
            Write("! " + text, ConsoleColor.Yellow, Console.Error);
        }

        public static void Error(string text)
        {
            if (Sink != null) { Sink.Message(UiLevel.Error, text); return; }
            Write("x " + text, ConsoleColor.Red, Console.Error);
        }

        public static void Rule(string title = null)
        {
            if (Sink != null) { if (!string.IsNullOrEmpty(title)) Sink.Message(UiLevel.Head, title); return; }
            if (Quiet) return;
            int width = 64;
            if (string.IsNullOrEmpty(title)) { Write(new string('-', width), ConsoleColor.DarkGray); return; }
            string line = "-- " + title + " ";
            if (line.Length < width) line += new string('-', width - line.Length);
            Write(line, ConsoleColor.DarkGray);
        }

        public static bool Confirm(string question, bool assumeYes)
        {
            if (assumeYes) { Console.WriteLine(question + " [y/N] y"); return true; }
            Console.Write(question + " [y/N] ");
            string answer = Console.ReadLine();
            return answer != null && (answer.Trim().Equals("y", StringComparison.OrdinalIgnoreCase)
                                   || answer.Trim().Equals("yes", StringComparison.OrdinalIgnoreCase));
        }

        public static string Bytes(long value)
        {
            if (value < 0) return "?";
            string[] units = { "B", "KB", "MB", "GB", "TB" };
            double size = value;
            int unit = 0;
            while (size >= 1024 && unit < units.Length - 1) { size /= 1024; unit++; }
            return size.ToString(unit == 0 ? "0" : "0.00", CultureInfo.InvariantCulture) + " " + units[unit];
        }

        public static string Duration(TimeSpan span)
        {
            if (span.TotalSeconds < 0 || span.TotalDays > 1) return "--:--";
            if (span.TotalHours >= 1)
                return ((int)span.TotalHours) + "h" + span.Minutes.ToString("00") + "m";
            return span.Minutes.ToString("00") + ":" + span.Seconds.ToString("00");
        }

        private static int _lastProgressLength;
        private static DateTime _lastLoggedLine = DateTime.MinValue;

        /// <summary>Draws (or redraws) a single-line progress bar. Falls back to periodic lines when redirected.</summary>
        public static void Progress(string label, long done, long total, double bytesPerSecond)
        {
            if (Sink != null) { Sink.Progress(label, done, total, bytesPerSecond); return; }
            if (Quiet) return;

            double fraction = total > 0 ? (double)done / total : 0;
            string eta = total > 0 && bytesPerSecond > 1
                ? Duration(TimeSpan.FromSeconds((total - done) / bytesPerSecond))
                : "--:--";

            string tail = string.Format(CultureInfo.InvariantCulture,
                "{0,6:0.0}%  {1} / {2}  {3}/s  ETA {4}",
                fraction * 100, Bytes(done), Bytes(total), Bytes((long)bytesPerSecond), eta);

            if (!CanRedraw)
            {
                // Redirected to a file or a scheduled task: one line every few seconds,
                // plus the final one, instead of four per second.
                bool finished = total > 0 && done >= total;
                if (!finished && DateTime.UtcNow - _lastLoggedLine < TimeSpan.FromSeconds(5)) return;
                _lastLoggedLine = DateTime.UtcNow;
                Console.WriteLine(label + "  " + tail);
                return;
            }

            int barWidth = Math.Max(10, Math.Min(28, Console.WindowWidth - label.Length - tail.Length - 8));
            int filled = (int)Math.Round(fraction * barWidth);
            if (filled > barWidth) filled = barWidth;
            string bar = "[" + new string('#', filled) + new string('.', barWidth - filled) + "]";
            string line = label + " " + bar + " " + tail;

            if (line.Length > Console.WindowWidth - 1) line = line.Substring(0, Console.WindowWidth - 1);
            Console.Write("\r" + line + (line.Length < _lastProgressLength
                ? new string(' ', _lastProgressLength - line.Length) : string.Empty));
            _lastProgressLength = line.Length;
        }

        public static void EndProgress()
        {
            if (Sink != null) { Sink.EndProgress(); return; }
            if (Quiet) return;
            if (CanRedraw && _lastProgressLength > 0) Console.Write("\r" + new string(' ', _lastProgressLength) + "\r");
            _lastProgressLength = 0;
        }
    }
}
