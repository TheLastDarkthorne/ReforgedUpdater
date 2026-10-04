using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;

namespace ReforgedUpdater.Gui
{
    /// <summary>One row in the list of patches that would be copied.</summary>
    public sealed class CopyLine
    {
        public string Name { get; set; }
        public string Size { get; set; }
    }

    /// <summary>
    /// Picks the game to copy patches from and shows exactly what would be copied and what
    /// would be left out, the same way "copy --from" lists it on the command line.
    /// </summary>
    public partial class CopyPatchesWindow : Window
    {
        private readonly Func<GameChoice, CopyPlan> _plan;

        /// <summary>What to copy, once the user presses Copy.</summary>
        internal CopyPlan Plan { get; private set; }

        /// <summary>True when the user asked for each copy to be read back and checked.</summary>
        public bool VerifyCopy => VerifyBox.IsChecked == true;

        internal CopyPatchesWindow(string targetName, IList<GameChoice> sources, Func<GameChoice, CopyPlan> plan)
        {
            InitializeComponent();
            _plan = plan;
            IntoText.Text = "Into " + targetName + ". Saves downloading them again.";
            SourcePicker.ItemsSource = sources;
            SourcePicker.SelectedIndex = 0;
        }

        private void OnSourceChanged(object sender, SelectionChangedEventArgs e)
        {
            if (!(SourcePicker.SelectedItem is GameChoice game)) return;

            SourceHint.Text = game.Path;
            Plan = null;
            CopyButton.IsEnabled = false;
            ErrorBox.Visibility = Visibility.Collapsed;
            PlanBox.Visibility = Visibility.Visible;

            CopyPlan plan;
            try { plan = _plan(game); }
            catch (UpdaterException ex)
            {
                PlanBox.Visibility = Visibility.Collapsed;
                SkipBox.Visibility = Visibility.Collapsed;
                ErrorText.Text = ex.Message;
                ErrorBox.Visibility = Visibility.Visible;
                return;
            }

            int count = plan.Items.Count;
            Summary.Text = count == 0
                ? "There's nothing to copy from " + game.Name + "."
                : (count == 1 ? "1 patch" : count + " patches") + " · " + Ui.Bytes(plan.Bytes) + " to copy";
            CopyLines.ItemsSource = plan.Items
                .Select(i => new CopyLine
                {
                    Name = i.Entry.Display + (i.Source.Version != null ? "  ·  v" + i.Source.Version : string.Empty),
                    Size = Ui.Bytes(i.Size)
                }).ToList();
            CopyLines.Visibility = count == 0 ? Visibility.Collapsed : Visibility.Visible;

            SkipLines.ItemsSource = plan.Skipped;
            SkipBox.Visibility = plan.Skipped.Count == 0 ? Visibility.Collapsed : Visibility.Visible;

            CopyText.Text = count == 1 ? "Copy 1 patch" : count > 1 ? "Copy " + count + " patches" : "Copy";
            if (count > 0)
            {
                Plan = plan;
                CopyButton.IsEnabled = true;
            }
        }

        // The whole row is the click target, not just the small checkbox.
        private void OnVerifyLabel(object sender, MouseButtonEventArgs e)
        {
            if (e.OriginalSource is DependencyObject source && FindAncestor<CheckBox>(source) != null) return;
            VerifyBox.IsChecked = VerifyBox.IsChecked != true;
        }

        private static T FindAncestor<T>(DependencyObject node) where T : DependencyObject
        {
            for (; node != null; node = System.Windows.Media.VisualTreeHelper.GetParent(node))
                if (node is T match) return match;
            return null;
        }

        private void OnCopy(object sender, RoutedEventArgs e)
        {
            if (Plan != null) DialogResult = true;
        }

        private void OnDrag(object sender, MouseButtonEventArgs e)
        {
            if (e.ButtonState == MouseButtonState.Pressed) DragMove();
        }
    }
}
