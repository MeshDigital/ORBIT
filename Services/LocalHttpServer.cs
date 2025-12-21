using System;
using System.Net;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;

namespace SLSKDONET.Services;

/// <summary>
/// Temporary HTTP server for OAuth callback handling.
/// Listens on localhost for the OAuth redirect and extracts the authorization code.
/// </summary>
public class LocalHttpServer : IDisposable
{
    private readonly ILogger<LocalHttpServer> _logger;
    private HttpListener? _listener;
    private bool _disposed;

    public LocalHttpServer(ILogger<LocalHttpServer> logger)
    {
        _logger = logger;
    }

    /// <summary>
    /// Starts the HTTP server and waits for the OAuth callback.
    /// </summary>
    /// <param name="redirectUri">The redirect URI to listen on (e.g., http://localhost:5000/callback)</param>
    /// <param name="cancellationToken">Cancellation token to stop waiting</param>
    /// <returns>The authorization code from the callback, or null if cancelled/failed</returns>
    public async Task<string?> WaitForCallbackAsync(string redirectUri, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(redirectUri))
            throw new ArgumentException("Redirect URI cannot be null or empty", nameof(redirectUri));

        try
        {
            _listener = new HttpListener();
            
            // 1. Listen on root to capture all traffic on this port
            // This avoids issues where Spotify adds/removes trailing slashes
            var port = new Uri(redirectUri).Port;
            var prefix = $"http://localhost:{port}/";
            var prefixIp = $"http://127.0.0.1:{port}/";
            
            _listener.Prefixes.Add(prefix);
            _listener.Prefixes.Add(prefixIp);

            _listener.Start();
            _logger.LogInformation("OAuth callback server listening on: {Prefixes}", string.Join(", ", _listener.Prefixes));

            while (!cancellationToken.IsCancellationRequested)
            {
                // Wait for a request
                var contextTask = _listener.GetContextAsync();
                var completedTask = await Task.WhenAny(contextTask, Task.Delay(-1, cancellationToken));

                if (cancellationToken.IsCancellationRequested)
                {
                    _logger.LogWarning("OAuth callback cancelled by user or timeout");
                    return null;
                }

                var context = await contextTask;
                var request = context.Request;
                var response = context.Response;

                _logger.LogInformation("Received request: {Url}", request.Url);

                // 2. Filter for legitimate callback path
                // Accept /callback, /callback/, or just check if it contains the code
                if (request.Url == null || (!request.Url.AbsolutePath.Contains("/callback") && string.IsNullOrEmpty(request.QueryString["code"])))
                {
                    // Ignore favicon.ico or other stray requests
                    response.StatusCode = 404;
                    response.Close();
                    continue;
                }

                // Extract authorization code or error
                var code = request.QueryString["code"];
                var error = request.QueryString["error"];
                var errorDescription = request.QueryString["error_description"];

                // Send response to browser
                string htmlResponse;
                if (!string.IsNullOrEmpty(error))
                {
                    _logger.LogError("OAuth error: {Error} - {Description}", error, errorDescription);
                    htmlResponse = GetErrorHtml(error, errorDescription);
                    response.StatusCode = 400;
                }
                else if (!string.IsNullOrEmpty(code))
                {
                    _logger.LogInformation("Successfully received authorization code");
                    htmlResponse = GetSuccessHtml();
                    response.StatusCode = 200;
                }
                else
                {
                    _logger.LogWarning("OAuth callback received without code or error");
                    htmlResponse = GetErrorHtml("invalid_request", "No authorization code received");
                    response.StatusCode = 400;
                }

                // Write response to browser
                var buffer = Encoding.UTF8.GetBytes(htmlResponse);
                response.ContentLength64 = buffer.Length;
                response.ContentType = "text/html; charset=utf-8";
                await response.OutputStream.WriteAsync(buffer, cancellationToken);
                response.Close();
                
                // We are done, return the code
                return !string.IsNullOrEmpty(error) ? null : code;
            }

            return null;
        }
        catch (HttpListenerException ex)
        {
            _logger.LogError(ex, "Failed to start OAuth callback server. Port {Port} may be in use.", new Uri(redirectUri).Port);
            throw new InvalidOperationException($"Failed to start callback server on port {new Uri(redirectUri).Port}. Is another instance running?", ex);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error during OAuth callback handling");
            throw;
        }
        finally
        {
            Stop();
        }
    }

    /// <summary>
    /// Stops the HTTP server.
    /// </summary>
    public void Stop()
    {
        if (_listener?.IsListening == true)
        {
            _listener.Stop();
            _logger.LogInformation("OAuth callback server stopped");
        }
    }

    private static string GetSuccessHtml()
    {
        return @"<!DOCTYPE html>
<html>
<head>
    <meta charset='utf-8'>
    <title>Authorization Successful</title>
    <style>
        body {
            font-family: 'Segoe UI', Tahoma, Geneva, Verdana, sans-serif;
            background: linear-gradient(135deg, #667eea 0%, #764ba2 100%);
            display: flex;
            justify-content: center;
            align-items: center;
            height: 100vh;
            margin: 0;
        }
        .container {
            background: white;
            padding: 40px;
            border-radius: 10px;
            box-shadow: 0 10px 40px rgba(0,0,0,0.2);
            text-align: center;
            max-width: 400px;
        }
        h1 {
            color: #1DB954;
            margin-bottom: 20px;
        }
        p {
            color: #666;
            line-height: 1.6;
        }
        .checkmark {
            font-size: 64px;
            color: #1DB954;
            margin-bottom: 20px;
        }
    </style>
</head>
<body>
    <div class='container'>
        <div class='checkmark'>✓</div>
        <h1>Success!</h1>
        <p>You have successfully signed in with Spotify.</p>
        <p>You can close this window and return to ORBIT.</p>
    </div>
</body>
</html>";
    }

    private static string GetErrorHtml(string error, string? description)
    {
        return $@"<!DOCTYPE html>
<html>
<head>
    <meta charset='utf-8'>
    <title>Authorization Failed</title>
    <style>
        body {{
            font-family: 'Segoe UI', Tahoma, Geneva, Verdana, sans-serif;
            background: linear-gradient(135deg, #f093fb 0%, #f5576c 100%);
            display: flex;
            justify-content: center;
            align-items: center;
            height: 100vh;
            margin: 0;
        }}
        .container {{
            background: white;
            padding: 40px;
            border-radius: 10px;
            box-shadow: 0 10px 40px rgba(0,0,0,0.2);
            text-align: center;
            max-width: 400px;
        }}
        h1 {{
            color: #f5576c;
            margin-bottom: 20px;
        }}
        p {{
            color: #666;
            line-height: 1.6;
        }}
        .error-icon {{
            font-size: 64px;
            color: #f5576c;
            margin-bottom: 20px;
        }}
        .error-details {{
            background: #f8f8f8;
            padding: 15px;
            border-radius: 5px;
            margin-top: 20px;
            font-size: 14px;
            color: #999;
        }}
    </style>
</head>
<body>
    <div class='container'>
        <div class='error-icon'>✗</div>
        <h1>Authorization Failed</h1>
        <p>There was a problem signing in with Spotify.</p>
        <p>Please close this window and try again in ORBIT.</p>
        <div class='error-details'>
            <strong>Error:</strong> {error}<br>
            {(string.IsNullOrEmpty(description) ? "" : $"<strong>Details:</strong> {description}")}
        </div>
    </div>
</body>
</html>";
    }

    public void Dispose()
    {
        if (_disposed)
            return;

        Stop();
        _listener?.Close();
        _disposed = true;
    }
}
