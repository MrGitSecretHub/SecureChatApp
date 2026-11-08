using SecureChat.App.Services;

namespace SecureChat.App;

public partial class AppShell : Shell
{
    public AppShell()
    {
        InitializeComponent();

        var session = AppServices.Get<AppSession>();
        UserLabel.Text = session.Username;
        FingerprintLabel.Text = session.Fingerprint;
    }
}
