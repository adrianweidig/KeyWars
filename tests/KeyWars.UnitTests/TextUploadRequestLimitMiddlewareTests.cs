using KeyWars.Infrastructure;
using KeyWars.Services;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.Extensions.Options;

namespace KeyWars.UnitTests;

public sealed class TextUploadRequestLimitMiddlewareTests
{
    [Fact]
    public async Task OversizedTextUploadIsRejectedBeforeTheEndpointRuns()
    {
        var endpointCalled = false;
        var middleware = new TextUploadRequestLimitMiddleware(_ =>
        {
            endpointCalled = true;
            return Task.CompletedTask;
        });
        var context = new DefaultHttpContext();
        context.Request.Method = HttpMethods.Post;
        context.Request.Path = "/texte/neu";
        context.Request.QueryString = new QueryString("?handler=Upload");
        context.Request.ContentLength = 70 * 1024;

        await middleware.InvokeAsync(
            context,
            Options.Create(new ContentOptions { MaxUploadBytes = 4096 }));

        Assert.Equal(StatusCodes.Status413PayloadTooLarge, context.Response.StatusCode);
        Assert.False(endpointCalled);
    }

    [Fact]
    public async Task NonUploadRequestIsNotLimited()
    {
        var endpointCalled = false;
        var middleware = new TextUploadRequestLimitMiddleware(_ =>
        {
            endpointCalled = true;
            return Task.CompletedTask;
        });
        var context = new DefaultHttpContext();
        context.Request.Method = HttpMethods.Post;
        context.Request.Path = "/texte/neu";
        context.Request.ContentLength = 10 * 1024 * 1024;

        await middleware.InvokeAsync(
            context,
            Options.Create(new ContentOptions { MaxUploadBytes = 4096 }));

        Assert.True(endpointCalled);
    }

    [Theory]
    [InlineData("/texte/neu")]
    [InlineData("/texte/neu/")]
    public async Task ChunkedUploadGetsAKestrelBodyLimitBeforeReading(string path)
    {
        var middleware = new TextUploadRequestLimitMiddleware(_ => Task.CompletedTask);
        var context = new DefaultHttpContext();
        context.Request.Method = HttpMethods.Post;
        context.Request.Path = path;
        context.Request.QueryString = new QueryString("?handler=Upload");
        var feature = new WritableBodySizeFeature();
        context.Features.Set<IHttpMaxRequestBodySizeFeature>(feature);

        await middleware.InvokeAsync(
            context,
            Options.Create(new ContentOptions { MaxUploadBytes = 4096 }));

        Assert.Equal(4096 + 64 * 1024, feature.MaxRequestBodySize);
    }

    private sealed class WritableBodySizeFeature : IHttpMaxRequestBodySizeFeature
    {
        public bool IsReadOnly => false;
        public long? MaxRequestBodySize { get; set; }
    }
}
