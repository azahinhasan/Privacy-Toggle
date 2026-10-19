using System.Windows;
using System.Windows.Media.Imaging;
using PrivacyToggle.Helper;
using PrivacyToggle.Services;

namespace PrivacyToggle.Views;

/// <summary>Asks whether to close the program that keeps the camera from turning off.</summary>
public partial class BlockerDialog : Window
{
    public BlockerDialog(BlockerInfo blocker)
    {
        InitializeComponent();
        var decoder = BitmapDecoder.Create(new Uri("pack://application:,,,/Assets/cam-on.ico"),
            BitmapCreateOptions.None, BitmapCacheOption.OnLoad);
        Glyph.Source = decoder.Frames.OrderByDescending(f => f.PixelWidth).First();

        Message.Text = $"{blocker.Name} is holding the camera open, so Windows can't turn it off.\n\n" +
                       "Close it and turn the camera off?";
        Note.Text = blocker.IsService
            ? "It's a background service. PrivacyToggle will start it again when you turn the camera back on."
            : "Any unsaved work in that program will be lost.";

        SourceInitialized += (_, _) => ThemeService.ApplyTitleBar(this);
    }

    /// <summary>Shows the dialog in front of everything; true if the user chose to close the program.</summary>
    public static bool Ask(BlockerInfo blocker)
    {
        var dialog = new BlockerDialog(blocker);
        dialog.Loaded += (_, _) => dialog.Activate();
        return dialog.ShowDialog() == true;
    }

    private void Confirm_Click(object sender, RoutedEventArgs e) => DialogResult = true;
    private void Cancel_Click(object sender, RoutedEventArgs e) => DialogResult = false;
}
