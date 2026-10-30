// AlbumWall - the Accessibility page of Preferences: motion and text size, the two
// things here a person can choose, and what is always on.

using Avalonia.Controls;

namespace AlbumWall.App;

public partial class PrefsWindow
{
    private void WireAccessibility(MainWindow host)
    {
        void ChooseMotion(bool? reduce)
        {
            if (_filling) return;
            host.AppSettings.ReduceMotion = reduce;
            host.AppSettings.Save();
            Motion.Choose(reduce);
            host.ApplyMotion();
        }
        MotionSystem.IsCheckedChanged += (_, _) => { if (MotionSystem.IsChecked == true) ChooseMotion(null); };
        MotionReduced.IsCheckedChanged += (_, _) => { if (MotionReduced.IsChecked == true) ChooseMotion(true); };
        MotionFull.IsCheckedChanged += (_, _) => { if (MotionFull.IsChecked == true) ChooseMotion(false); };

        TextSizeChoice.ItemsSource = TextSize.Choices.Select(p => $"{p}%").ToList();
        TextSizeChoice.SelectionChanged += (_, _) =>
        {
            if (_filling || TextSizeChoice.SelectedIndex < 0) return;
            var percent = TextSize.Choices[TextSizeChoice.SelectedIndex];
            host.AppSettings.TextSize = percent == 100 ? null : percent;
            host.AppSettings.Save();
            TextSize.Choose(percent);
        };
        TextSizeAbout.Text = "Makes the text in this window and the app's other light windows larger: "
                           + "Properties, a library's settings, the statistics, signing in to a server, "
                           + "and the questions the app asks. Their buttons and spacing grow with it.";
        TextSizeNote.Text = "Applies at once.";
    }

    private void FillAccessibility()
    {
        if (_host is null) return;
        MotionSystem.Content = $"Follow the system ({(Motion.FromSystem ? "reduced" : "full motion")} now)";
        MotionSystemAbout.Text = OperatingSystem.IsMacOS()
            ? "System Settings, Accessibility, Display, Reduce motion."
            : OperatingSystem.IsWindows()
                ? "Settings, Accessibility, Visual effects, Animation effects."
                : "GNOME's Settings, Accessibility, Reduce animation. Other Linux desktops have no setting this app can read: choose here.";
        var chosen = _host.AppSettings.ReduceMotion;
        MotionSystem.IsChecked = chosen is null;
        MotionReduced.IsChecked = chosen == true;
        MotionFull.IsChecked = chosen == false;
        TextSizeChoice.SelectedIndex = Array.IndexOf(TextSize.Choices, TextSize.Percent);
    }
}
