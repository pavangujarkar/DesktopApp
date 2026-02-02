using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.Maui.ApplicationModel;
using Microsoft.Maui.Authentication;

namespace MauiClient;

public class AuthService
{
    // NOTE: For Android Emulator, use 10.0.2.2 instead of localhost, or use your PC's LAN IP
    private const string AuthUrl = "http://localhost:5000/authorize";
    private const string TokenUrl = "http://localhost:5000/token";
    
#if WINDOWS
    private const string RedirectUri = "http://localhost:5555/callback/";
#else
    private const string RedirectUri = "myapp://callback";
#endif
    
    private const string ClientId = "maui_app";

    public async Task<string> LoginAsync()
    {
        var state = Guid.NewGuid().ToString("N");
        var codeVerifier = GenerateCodeVerifier();
        var codeChallenge = GenerateCodeChallenge(codeVerifier);

#if WINDOWS
        return await LoginWindowsAsync(state, codeVerifier, codeChallenge);
#else
        return await LoginMobileAsync(state, codeVerifier, codeChallenge);
#endif
    }

#if WINDOWS
    private async Task<string> LoginWindowsAsync(string state, string codeVerifier, string codeChallenge)
    {
        var url = $"{AuthUrl}?client_id={ClientId}&redirect_uri={RedirectUri}&code_challenge={codeChallenge}&state={state}&response_type=code&code_challenge_method=S256";

        using var listener = new HttpListener();
        listener.Prefixes.Add(RedirectUri);
        listener.Start();

        await Launcher.OpenAsync(new Uri(url));

        var context = await listener.GetContextAsync();
        var code = context.Request.QueryString["code"];
        var responseState = context.Request.QueryString["state"];

        if (string.IsNullOrEmpty(code))
        {
            listener.Stop();
            return "Error: No authorization code received";
        }

        if (responseState != state)
        {
            listener.Stop();
            return "Error: State mismatch";
        }

        // Send success response to browser
        using var writer = new StreamWriter(context.Response.OutputStream);
        await writer.WriteAsync("<html><body><h2>Login Successful!</h2><p>You can close this window and return to the application.</p></body></html>");
        await writer.FlushAsync();
        context.Response.Close();
        listener.Stop();

        return await ExchangeCodeForToken(code, codeVerifier);
    }
#else
    private async Task<string> LoginMobileAsync(string state, string codeVerifier, string codeChallenge)
    {
        var url = $"{AuthUrl}?client_id={ClientId}&redirect_uri={RedirectUri}&code_challenge={codeChallenge}&state={state}&response_type=code&code_challenge_method=S256";

        try
        {
            var result = await WebAuthenticator.Default.AuthenticateAsync(
                new WebAuthenticatorOptions
                {
                    Url = new Uri(url),
                    CallbackUrl = new Uri(RedirectUri),
                    PrefersEphemeralWebBrowserSession = true
                });

            var code = result?.Properties.ContainsKey("code") == true ? result.Properties["code"] : null;
            var responseState = result?.Properties.ContainsKey("state") == true ? result.Properties["state"] : null;

            if (string.IsNullOrEmpty(code)) return "Error: No code received";
            if (responseState != state) return "Error: State mismatch";

            return await ExchangeCodeForToken(code, codeVerifier);
        }
        catch (TaskCanceledException)
        {
            return "Login canceled.";
        }
    }
#endif

    private async Task<string> ExchangeCodeForToken(string code, string codeVerifier)
    {
        using var client = new HttpClient();
        var content = new FormUrlEncodedContent(new[]
        {
            new KeyValuePair<string, string>("code", code),
            new KeyValuePair<string, string>("code_verifier", codeVerifier),
            new KeyValuePair<string, string>("client_id", ClientId),
            new KeyValuePair<string, string>("redirect_uri", RedirectUri),
            new KeyValuePair<string, string>("grant_type", "authorization_code")
        });

        var response = await client.PostAsync(TokenUrl, content);
        var json = await response.Content.ReadAsStringAsync();

        if (response.IsSuccessStatusCode)
        {
             using var doc = JsonDocument.Parse(json);
             if (doc.RootElement.TryGetProperty("access_token", out var tokenElement))
             {
                 return $"Success! Token: {tokenElement.GetString()}";
             }
        }
        
        return $"Token Exchange Failed: {json}";
    }

    private string GenerateCodeVerifier()
    {
        var bytes = new byte[32];
        using var rng = RandomNumberGenerator.Create();
        rng.GetBytes(bytes);
        return Base64UrlEncode(bytes);
    }

    private string GenerateCodeChallenge(string codeVerifier)
    {
        using var sha256 = SHA256.Create();
        var bytes = sha256.ComputeHash(Encoding.ASCII.GetBytes(codeVerifier));
        return Base64UrlEncode(bytes);
    }

    private static string Base64UrlEncode(byte[] input)
    {
        var output = Convert.ToBase64String(input);
        output = output.Split('=')[0]; 
        output = output.Replace('+', '-'); 
        output = output.Replace('/', '_'); 
        return output;
    }
}
