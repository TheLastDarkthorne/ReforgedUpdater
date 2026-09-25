using System;
using System.Windows.Threading;

namespace ReforgedUpdater.Gui
{
    /// <summary>
    /// Carries the engine's messages and progress to the window. The engine reports from
    /// worker threads, so everything is posted to the window's dispatcher.
    /// </summary>
    internal sealed class WindowSink : IUiSink
    {
        private readonly Dispatcher _dispatcher;
        private readonly Action<UiLevel, string> _message;
        private readonly Action<string, long, long, double> _progress;
        private readonly Action _endProgress;

        public WindowSink(Dispatcher dispatcher, Action<UiLevel, string> message,
                          Action<string, long, long, double> progress, Action endProgress)
        {
            _dispatcher = dispatcher;
            _message = message;
            _progress = progress;
            _endProgress = endProgress;
        }

        public void Message(UiLevel level, string text)
        {
            if (string.IsNullOrWhiteSpace(text)) return;
            _dispatcher.BeginInvoke(_message, level, text.Trim());
        }

        public void Progress(string label, long done, long total, double bytesPerSecond) =>
            _dispatcher.BeginInvoke(_progress, label, done, total, bytesPerSecond);

        public void EndProgress() => _dispatcher.BeginInvoke(_endProgress);
    }
}
