namespace KeyWars.Infrastructure;

public sealed class SecurityHeadersMiddleware(RequestDelegate next)
{
    public async Task InvokeAsync(HttpContext context)
    {
        context.Response.OnStarting(() =>
        {
            var headers = context.Response.Headers;
            headers.TryAdd("X-Content-Type-Options", "nosniff");
            headers.TryAdd("Referrer-Policy", "no-referrer");
            headers.TryAdd("X-Frame-Options", "DENY");
            headers.TryAdd("Permissions-Policy", "camera=(), microphone=(), geolocation=(), payment=(), usb=()");
            headers.TryAdd("Content-Security-Policy", BuildContentSecurityPolicy(context.Request));
            if (context.User.Identity?.IsAuthenticated == true)
            {
                headers.CacheControl = "no-store";
            }

            return Task.CompletedTask;
        });

        await next(context);
    }

    private static string BuildContentSecurityPolicy(HttpRequest request)
    {
        var websocketSource = BuildWebSocketSource(request);
        var connectSource = string.IsNullOrWhiteSpace(websocketSource)
            ? "'self'"
            : $"'self' {websocketSource}";
        return $"default-src 'self'; base-uri 'self'; frame-ancestors 'none'; object-src 'none'; img-src 'self' data:; script-src 'self'; style-src 'self'; connect-src {connectSource}";
    }

    private static string BuildWebSocketSource(HttpRequest request)
    {
        if (!request.Host.HasValue)
        {
            return string.Empty;
        }

        var host = request.Host.ToUriComponent();
        if (host.Any(character => !char.IsLetterOrDigit(character) && character is not '.' and not '-' and not ':' and not '[' and not ']'))
        {
            return string.Empty;
        }

        return $"{(request.IsHttps ? "wss" : "ws")}://{host}";
    }
}
