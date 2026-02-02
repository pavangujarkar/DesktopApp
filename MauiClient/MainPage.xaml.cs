using System.Text;

namespace MauiClient;

public partial class MainPage : ContentPage
{
    private readonly AuthService _authService;
    private readonly P2PService _p2pService;

    public MainPage(AuthService authService, P2PService p2pService)
    {
        InitializeComponent();
        _authService = authService;
        _p2pService = p2pService;
    }

    private async void OnLoginClicked(object sender, EventArgs e)
    {
        RawOutput.Text = "Starting Login...";
        try
        {
            var result = await _authService.LoginAsync();
            RawOutput.Text = result;
        }
        catch (Exception ex)
        {
            RawOutput.Text = $"Error: {ex.Message}";
        }
    }

    private async void OnStartReceiverClicked(object sender, EventArgs e)
    {
        try
        {
            var ip = await _p2pService.GetPublicIpAsync();
            var port = 8080;
            RawOutput.Text += $"\nMy Local IP: {ip}";
            
            // Register as 'receiver'
            await _p2pService.RegisterAsync("receiver", ip, port);
            RawOutput.Text += "\nRegistered as 'receiver'. Waiting...";

            _ = _p2pService.StartListenerAsync(port, msg => 
            {
                MainThread.BeginInvokeOnMainThread(() => RawOutput.Text += $"\n{msg}");
            });
        }
        catch (Exception ex) { RawOutput.Text += $"\nError: {ex.Message}"; }
    }

    private async void OnSendFileClicked(object sender, EventArgs e)
    {
        try
        {
            // Find 'receiver'
            var address = await _p2pService.GetPeerAddressAsync("receiver");
            if (address == null) 
            {
                RawOutput.Text += "\nReceiver not found!";
                return;
            }
            
            var parts = address.Split(':');
            var ip = parts[0];
            var port = int.Parse(parts[1]);

            // For POC, we'll create a dummy large file to demonstrate chunking
            var dummyFilePath = Path.Combine(FileSystem.CacheDirectory, "demo_large.txt");
            if (!File.Exists(dummyFilePath))
            {
                var largeContent = new StringBuilder();
                for (int i = 0; i < 50000; i++) largeContent.AppendLine("Chunked transfer demo line " + i);
                await File.WriteAllTextAsync(dummyFilePath, largeContent.ToString());
            }

            RawOutput.Text += $"\nStarting chunked send to {ip}:{port}...";
            await _p2pService.SendFileChunkedAsync(ip, port, dummyFilePath, msg => 
            {
                MainThread.BeginInvokeOnMainThread(() => RawOutput.Text += $"\n{msg}");
            });
        }
        catch (Exception ex) { RawOutput.Text += $"\nError: {ex.Message}"; }
    }
}
