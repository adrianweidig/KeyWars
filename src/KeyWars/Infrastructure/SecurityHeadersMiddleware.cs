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
            headers.TryAdd("Content-Security-Policy", "default-src 'self'; base-uri 'self'; frame-ancestors 'none'; object-src 'none'; img-src 'self' data:; script-src 'self'; style-src 'self'; connect-src 'self' ws: wss:");
            if (context.User.Identity?.IsAuthenticated == true)
            {
                headers.CacheControl = "no-store";
            }

            return Task.CompletedTask;
        });

        await next(context);
    }
}
