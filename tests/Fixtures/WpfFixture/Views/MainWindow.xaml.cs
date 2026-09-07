namespace WpfFixture.Views;

/// <summary>Code-behind class matched by MainWindow.xaml's x:Class attribute. No WPF SDK
/// base class (Window) is referenced here since this fixture intentionally avoids the
/// Windows-only WindowsDesktop SDK — XamlMarkupAnalyzer resolves x:Class purely by
/// qualified-name lookup against the source graph, not by inheritance.</summary>
public sealed class MainWindow
{
    public void Save_Click(object sender, object e)
    {
    }
}
