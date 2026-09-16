using KeyWars.Services;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.Extensions.Options;

namespace KeyWars.Infrastructure;

public sealed class TextUploadRequestLimitMiddleware(RequestDelegate next)
{
    private const long MultipartOverheadBytes = 64 * 1024;

    public async Task InvokeAsync(HttpContext context, IOptions<ContentOptions> options)
    {
        if (!IsTextUpload(context.Request))
        {
            await next(context);
            return;
        }

        var maximumBodyBytes = checked((long)Math.Max(1, options.Value.MaxUploadBytes) + MultipartOverheadBytes);
        var bodySizeFeature = context.Features.Get<IHttpMaxRequestBodySizeFeature>();
        if (bodySizeFeature is { IsReadOnly: false })
        {
            bodySizeFeature.MaxRequestBodySize = maximumBodyBytes;
        }

        if (context.Request.ContentLength is > 0 && context.Request.ContentLength > maximumBodyBytes)
        {
            context.Response.StatusCode = StatusCodes.Status413PayloadTooLarge;
            return;
        }

        await next(context);
    }

    private static bool IsTextUpload(HttpRequest request) =>
        HttpMethods.IsPost(request.Method) &&
        (request.Path.Equals("/texte/neu", StringComparison.OrdinalIgnoreCase) ||
            request.Path.Equals("/texte/neu/", StringComparison.OrdinalIgnoreCase)) &&
        request.Query.TryGetValue("handler", out var handler) &&
        string.Equals(handler.ToString(), "Upload", StringComparison.OrdinalIgnoreCase);
}
