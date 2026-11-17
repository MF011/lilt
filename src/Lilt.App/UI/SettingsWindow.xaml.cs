using System.Windows;

namespace Lilt.App.UI;

public partial class SettingsWindow : Window
{
    private readonly Func<string, Task> _connectAsync;
    private readonly Func<Task> _disconnectAsync;
    private bool _isConnected;
    private bool _working;

    public SettingsWindow(string? clientId, bool isConnected, Func<string, Task> connectAsync, Func<Task> disconnectAsync)
    {
        _connectAsync = connectAsync;
        _disconnectAsync = disconnectAsync;
        _isConnected = isConnected;
        InitializeComponent();
        ClientIdBox.Text = clientId ?? "";
        StatusText.Text = isConnected ? "Connected." : "Playback works without connecting. Likes need an account.";
        UpdateControls();
    }

    public string ClientId => ClientIdBox.Text.Trim();

    private async void OnConnect(object sender, RoutedEventArgs e)
    {
        var clientId = ClientId;
        if (clientId.Length != 32 || !clientId.All(Uri.IsHexDigit))
        {
            StatusText.Text = "Enter the Client ID from your Spotify developer app.";
            ClientIdBox.Focus();
            return;
        }

        SetWorking(true);
        StatusText.Text = "Finish connecting in your browser…";
        try
        {
            await _connectAsync(clientId);
            _isConnected = true;
            StatusText.Text = "Connected.";
        }
        catch (OperationCanceledException) { StatusText.Text = "Connection cancelled."; }
        catch { StatusText.Text = "Couldn't connect. Check your app settings and try again."; }
        finally { SetWorking(false); }
    }

    private async void OnDisconnect(object sender, RoutedEventArgs e)
    {
        SetWorking(true);
        try
        {
            await _disconnectAsync();
            _isConnected = false;
            StatusText.Text = "Disconnected. Local playback is still available.";
        }
        catch { StatusText.Text = "Couldn't disconnect. Try again."; }
        finally { SetWorking(false); }
    }

    private void OnDone(object sender, RoutedEventArgs e) => Close();

    private void SetWorking(bool working)
    {
        _working = working;
        UpdateControls();
    }

    private void UpdateControls()
    {
        ClientIdBox.IsEnabled = !_working;
        ConnectButton.IsEnabled = !_working;
        DisconnectButton.IsEnabled = !_working && _isConnected;
    }
}
