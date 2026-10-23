namespace MyNotes.Middleware;

public sealed class RequestLoggingScopeMiddleware(RequestDelegate next, ILogger<RequestLoggingScopeMiddleware> logger)
{
    public async Task Invoke(HttpContext context)
    {
        if (context.Request.Headers.TryGetValue("authorization", out var authHeader))
        {
            var authorization = authHeader.ToString();
            var scheme = authorization.StartsWith("Basic ", StringComparison.OrdinalIgnoreCase)
                ? "Basic"
                : authorization.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase)
                    ? "Bearer"
                    : "Other";
            logger.LogDebug("authorization header present: {Scheme}", scheme);
        }

        if (context.Request.Headers.TryGetValue("user-agent", out var userAgent))
            logger.LogDebug("user-agent: {UserAgent}", userAgent.ToString());

        await next(context);
    }
}
