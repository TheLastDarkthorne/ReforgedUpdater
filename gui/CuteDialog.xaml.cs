using System.Windows;
using System.Windows.Input;

namespace ReforgedUpdater.Gui
{
    /// <summary>A friendlier MessageBox. Returns true when the main button is pressed.</summary>
    public partial class CuteDialog : Window
    {
        private CuteDialog()
        {
            InitializeComponent();
        }

        public static bool Show(Window owner, string title, string message, string ok, string cancel = null,
                                MascotMood mood = MascotMood.Idle)
        {
            var dialog = new CuteDialog();
            dialog.Title = title;
            dialog.TitleText.Text = title;
            dialog.MessageText.Text = message;
            dialog.OkButton.Content = ok;
            dialog.Buddy.Mood = mood;

            if (cancel == null) dialog.CancelButton.Visibility = Visibility.Collapsed;
            else dialog.CancelButton.Content = cancel;

            if (owner != null && owner.IsVisible) dialog.Owner = owner;
            else dialog.WindowStartupLocation = WindowStartupLocation.CenterScreen;

            return dialog.ShowDialog() == true;
        }

        private void OnOk(object sender, RoutedEventArgs e) => DialogResult = true;
        private void OnCancel(object sender, RoutedEventArgs e) => DialogResult = false;

        private void OnDrag(object sender, MouseButtonEventArgs e)
        {
            if (e.ButtonState == MouseButtonState.Pressed) DragMove();
        }
    }
}
