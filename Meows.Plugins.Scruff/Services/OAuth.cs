using System.Net;
using System.Net.Http;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Web;

namespace Meows.Plugins.Scruff.Services;

/// <summary>The app registered with the service, in the person's own name. Both halves are secret.</summary>
public sealed record OAuthApp(string ClientId, string ClientSecret);

/// <summary>What the service handed back, and when the short half of it stops working.</summary>
public sealed record OAuthTokens(string AccessToken, string? RefreshToken, DateTimeOffset ExpiresAt)
{
    /// <summary>A minute early, so a post started just before the hour is not the one that fails.</summary>
    public bool IsFresh(DateTimeOffset now) => AccessToken.Length > 0 && now < ExpiresAt - TimeSpan.FromMinutes(1);
}

/// <summary>Where a service does its three OAuth steps, and what to ask for.</summary>
public sealed record OAuthEndpoints(string AuthorizeUrl, string TokenUrl, string Scope);

/// <summary>
/// The authorization code dance, done the way a desktop app has to do it.
///
/// The browser is sent to the service with the app's id; the person says yes; the service sends
/// the browser back to localhost with a code; the code is swapped for tokens over the back
/// channel with the app's secret. The "back to localhost" part is a socket listening on one
/// port for one request, which is why the port is fixed: the service will only redirect to an
/// address the app was registered with, so it has to be the same one every time.
///
/// It is a raw socket rather than HttpListener because HttpListener wants a URL reservation
/// that only an administrator can make, and one GET is not worth a prompt for that.
/// </summary>
public static class OAuthFlow
{
    public const int Port = 41597;

    /// <summary>What to register with the service, exactly as spelled here.</summary>
    public static readonly string RedirectUri = $"http://localhost:{Port}/callback";

    public static string NewState() => Convert.ToHexString(RandomNumberGenerator.GetBytes(16)).ToLowerInvariant();

    public static string AuthorizeUrl(OAuthEndpoints endpoints, OAuthApp app, string state)
    {
        var separator = endpoints.AuthorizeUrl.Contains('?') ? "&" : "?";
        return endpoints.AuthorizeUrl + separator +
               "response_type=code" +
               "&client_id=" + Uri.EscapeDataString(app.ClientId.Trim()) +
               "&redirect_uri=" + Uri.EscapeDataString(RedirectUri) +
               "&scope=" + Uri.EscapeDataString(endpoints.Scope) +
               "&state=" + Uri.EscapeDataString(state);
    }

    /// <summary>The code, or why there is none, out of the first line of the browser's request.</summary>
    public static (string? Code, string? State, string? Error) ParseCallback(string requestLine)
    {
        var parts = requestLine.Split(' ');
        if (parts.Length < 2)
            return (null, null, "not an HTTP request");

        var target = parts[1];
        var question = target.IndexOf('?');
        if (question < 0)
            return (null, null, "no query");

        var query = HttpUtility.ParseQueryString(target[(question + 1)..]);
        var error = query["error"];
        if (error is { Length: > 0 })
        {
            var description = query["error_description"];
            return (null, query["state"], description is { Length: > 0 } ? $"{error}: {description}" : error);
        }

        return (query["code"], query["state"], null);
    }

    /// <summary>
    /// Waits for the browser to come back with a code, answering it with a page that says so.
    /// One request only; anything with the wrong state is answered and ignored, since a stale
    /// tab from an earlier attempt is the likeliest wrong caller.
    /// </summary>
    public static async Task<string> WaitForCodeAsync(string state, TimeSpan timeout, CancellationToken token)
    {
        using var timer = CancellationTokenSource.CreateLinkedTokenSource(token);
        timer.CancelAfter(timeout);

        var listener = new TcpListener(IPAddress.Loopback, Port);
        listener.Start();
        try
        {
            while (true)
            {
                using var client = await listener.AcceptTcpClientAsync(timer.Token);
                using var stream = client.GetStream();
                using var reader = new StreamReader(stream, Encoding.ASCII, false, 1024, leaveOpen: true);

                var requestLine = await reader.ReadLineAsync(timer.Token) ?? "";
                var (code, callbackState, error) = ParseCallback(requestLine);

                if (callbackState != state)
                {
                    await AnswerAsync(stream, 400, "That is not the sign-in that is waiting. Close this tab and try again from Meows.", timer.Token);
                    continue;
                }

                if (error is not null)
                {
                    await AnswerAsync(stream, 400, "Sign-in was refused: " + error + ". You can close this tab.", timer.Token);
                    throw new InvalidOperationException(error);
                }

                if (code is not { Length: > 0 })
                {
                    await AnswerAsync(stream, 400, "No code came back. Close this tab and try again from Meows.", timer.Token);
                    continue;
                }

                await AnswerAsync(stream, 200, "Signed in. You can close this tab and go back to Meows.", timer.Token);
                return code;
            }
        }
        catch (OperationCanceledException) when (!token.IsCancellationRequested)
        {
            throw new TimeoutException("The browser did not come back in time.");
        }
        finally
        {
            listener.Stop();
        }
    }

    private static async Task AnswerAsync(NetworkStream stream, int status, string message, CancellationToken token)
    {
        var body = "<!doctype html><meta charset=\"utf-8\"><title>Meows</title>" +
                   "<body style=\"font-family:system-ui;margin:3em;max-width:32em\"><h1>Meows</h1><p>" +
                   WebUtility.HtmlEncode(message) + "</p></body>";
        var bytes = Encoding.UTF8.GetBytes(body);
        var head = $"HTTP/1.1 {status} {(status == 200 ? "OK" : "Bad Request")}\r\n" +
                   "Content-Type: text/html; charset=utf-8\r\n" +
                   $"Content-Length: {bytes.Length}\r\n" +
                   "Connection: close\r\n\r\n";

        await stream.WriteAsync(Encoding.ASCII.GetBytes(head), token);
        await stream.WriteAsync(bytes, token);
        await stream.FlushAsync(token);
    }

    public static Task<OAuthTokens> ExchangeAsync(HttpClient http, OAuthEndpoints endpoints, OAuthApp app, string code, CancellationToken token) =>
        TokenRequestAsync(http, endpoints, app, new Dictionary<string, string>
        {
            ["grant_type"] = "authorization_code",
            ["code"] = code,
            ["redirect_uri"] = RedirectUri,
        }, token);

    public static Task<OAuthTokens> RefreshAsync(HttpClient http, OAuthEndpoints endpoints, OAuthApp app, string refreshToken, CancellationToken token) =>
        TokenRequestAsync(http, endpoints, app, new Dictionary<string, string>
        {
            ["grant_type"] = "refresh_token",
            ["refresh_token"] = refreshToken,
        }, token);

    private static async Task<OAuthTokens> TokenRequestAsync(HttpClient http, OAuthEndpoints endpoints, OAuthApp app, Dictionary<string, string> form, CancellationToken token)
    {
        form["client_id"] = app.ClientId.Trim();
        form["client_secret"] = app.ClientSecret.Trim();

        using var response = await http.PostAsync(endpoints.TokenUrl, new FormUrlEncodedContent(form), token);
        var body = await response.Content.ReadAsStringAsync(token);
        if (!response.IsSuccessStatusCode)
            throw new InvalidOperationException(ExplainToken(response, body));

        return ParseTokens(body, DateTimeOffset.UtcNow);
    }

    /// <summary>
    /// The token response as both services send it. A refresh that comes without a new refresh
    /// token keeps the old one, which is what the standard says and what Tumblr does; DeviantArt
    /// sends a new one every time and the old one stops working, so the caller has to keep
    /// whatever comes back.
    /// </summary>
    public static OAuthTokens ParseTokens(string json, DateTimeOffset now, string? previousRefreshToken = null)
    {
        using var document = JsonDocument.Parse(json);
        var root = document.RootElement;

        var access = root.GetProperty("access_token").GetString() ?? "";
        var refresh = root.TryGetProperty("refresh_token", out var r) ? r.GetString() : null;
        var seconds = root.TryGetProperty("expires_in", out var e) && e.TryGetInt64(out var value) ? value : 3600;

        return new OAuthTokens(access, refresh ?? previousRefreshToken, now.AddSeconds(seconds));
    }

    private static string ExplainToken(HttpResponseMessage response, string body)
    {
        try
        {
            using var document = JsonDocument.Parse(body);
            var root = document.RootElement;
            if (root.TryGetProperty("error_description", out var description) && description.GetString() is { Length: > 0 } text)
                return text;
            if (root.TryGetProperty("error", out var error) && error.GetString() is { Length: > 0 } code)
                return code;
        }
        catch (JsonException)
        {
        }

        return $"HTTP {(int)response.StatusCode}";
    }
}

/// <summary>
/// A place reached through OAuth: DeviantArt and Tumblr.
///
/// What is kept is the app and the tokens. The access token is short lived and renewed from the
/// refresh token when it runs out, and since a renewal can hand back a new refresh token, every
/// renewal is written back through <see cref="Persist"/> so the next start has the working one.
/// </summary>
public abstract class OAuthTarget : IApiTarget
{
    protected readonly HttpClient Http;

    protected OAuthTarget(HttpClient http) => Http = http;

    public abstract string Id { get; }

    public abstract string Name { get; }

    public abstract Media.MediaLimits Limits { get; }

    public abstract OAuthEndpoints Endpoints { get; }

    public bool PostsItself => true;

    public abstract bool TakesTextOnly { get; }

    public virtual bool CarriesAltText => false;

    public virtual TagSpelling Spelling => TagSpelling.None;

    public OAuthApp? App { get; set; }

    public OAuthTokens? Tokens { get; set; }

    /// <summary>Called with the new tokens whenever they change, so they can be sealed away.</summary>
    public Action<OAuthApp, OAuthTokens>? Persist { get; set; }

    public bool IsSignedIn => App is not null && Tokens is not null &&
                              (Tokens.RefreshToken is { Length: > 0 } || Tokens.IsFresh(DateTimeOffset.UtcNow));

    public string? Account { get; set; }

    public abstract Composed Compose(Draft draft, IReadOnlyList<Outgoing> images);

    public abstract Task<PostResult> PostAsync(Draft draft, IReadOnlyList<Outgoing> images, CancellationToken token);

    /// <summary>Who the token belongs to, for the card. The service's own idea of a name.</summary>
    protected abstract Task<string> WhoAmIAsync(string accessToken, CancellationToken token);

    /// <summary>
    /// The whole sign in: browser out, code back, tokens in, name looked up. The app is only
    /// kept once the name has come back, so a wrong secret leaves nothing behind.
    /// </summary>
    public async Task<string> SignInAsync(OAuthApp app, Action<string> openBrowser, CancellationToken token)
    {
        var state = OAuthFlow.NewState();
        var waiting = OAuthFlow.WaitForCodeAsync(state, TimeSpan.FromMinutes(3), token);

        openBrowser(OAuthFlow.AuthorizeUrl(Endpoints, app, state));

        var code = await waiting;
        var tokens = await OAuthFlow.ExchangeAsync(Http, Endpoints, app, code, token);
        var account = await WhoAmIAsync(tokens.AccessToken, token);

        App = app;
        Tokens = tokens;
        Account = account;
        Persist?.Invoke(app, tokens);
        return account;
    }

    public void SignOut()
    {
        App = null;
        Tokens = null;
        Account = null;
    }

    /// <summary>A token that works right now, renewed first if the one held has run out.</summary>
    protected async Task<string> AccessTokenAsync(CancellationToken token)
    {
        if (App is null || Tokens is null)
            throw new InvalidOperationException("Not signed in.");

        if (Tokens.IsFresh(DateTimeOffset.UtcNow))
            return Tokens.AccessToken;

        if (Tokens.RefreshToken is not { Length: > 0 } refresh)
            throw new InvalidOperationException("The sign-in has run out. Sign in again.");

        var renewed = await OAuthFlow.RefreshAsync(Http, Endpoints, App, refresh, token);
        Tokens = renewed.RefreshToken is null ? renewed with { RefreshToken = refresh } : renewed;
        Persist?.Invoke(App, Tokens);
        return Tokens.AccessToken;
    }

    /// <summary>The first of the given paths that holds text, "errors.0.detail" style, or the status.</summary>
    protected static string Explain(HttpResponseMessage response, string body, params string[] keys)
    {
        try
        {
            using var document = JsonDocument.Parse(body);
            var root = document.RootElement;
            foreach (var key in keys)
            {
                var element = root;
                var found = true;
                foreach (var step in key.Split('.'))
                {
                    if (element.ValueKind == JsonValueKind.Object && element.TryGetProperty(step, out var next))
                        element = next;
                    else if (element.ValueKind == JsonValueKind.Array && int.TryParse(step, out var index) && index < element.GetArrayLength())
                        element = element[index];
                    else
                    {
                        found = false;
                        break;
                    }
                }

                if (found && element.ValueKind == JsonValueKind.String && element.GetString() is { Length: > 0 } text)
                    return text;
            }
        }
        catch (JsonException)
        {
        }

        return $"HTTP {(int)response.StatusCode}";
    }
}
