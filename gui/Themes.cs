using System;
using System.Windows;
using Microsoft.Win32;

namespace ReforgedUpdater.Gui
{
    /// <summary>The light and dark palettes, and which one Windows asks for.</summary>
    internal static class Themes
    {
        public const string Light = "light";
        public const string Dark = "dark";

        /// <summary>The palette on screen. App.xaml starts with the light one.</summary>
        public static bool IsDark { get; private set; }

        /// <summary>Resolves a saved choice: "light", "dark", or null to follow Windows.</summary>
        public static bool WantsDark(string choice) =>
            choice == Dark || (choice != Light && WindowsPrefersDark());

        /// <summary>The "Choose your app mode" setting in Windows' colour settings.</summary>
        public static bool WindowsPrefersDark()
        {
            try
            {
                using (var key = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize"))
                    return key?.GetValue("AppsUseLightTheme") is int light && light == 0;
            }
            catch { return false; }
        }

        /// <summary>
        /// Swaps the palette in slot 0 of the application resources. Everything that looks
        /// its colours up with DynamicResource repaints at once.
        /// </summary>
        public static void Apply(bool dark)
        {
            var palette = new ResourceDictionary
            {
                Source = new Uri("/ReforgedUpdaterGui;component/Palette." + (dark ? "Dark" : "Light") + ".xaml", UriKind.Relative)
            };
            Application.Current.Resources.MergedDictionaries[0] = palette;
            IsDark = dark;
        }
    }
}
