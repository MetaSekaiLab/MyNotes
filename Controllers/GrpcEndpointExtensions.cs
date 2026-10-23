using Google.Protobuf;
using MyNotes.Config;
using MyNotes.Services;

namespace MyNotes.Middleware;

public sealed class GrpcStatusException(string status, string message) : Exception(message)
{
    public string Status { get; } = status;
}

public static class GrpcEndpointExtensions
{
    public static IEndpointRouteBuilder MapGrpcUnary<TRequest, TResponse>(
        this IEndpointRouteBuilder endpoints,
        string pattern,
        MessageParser<TRequest> requestParser,
        Func<HttpContext, TRequest, Task<TResponse>> handler)
        where TRequest : IMessage<TRequest>
        where TResponse : IMessage<TResponse>
    {
        return endpoints.MapGrpcUnary(pattern, async (context, payload) =>
        {
            var request = requestParser.ParseFrom(payload.ToArray());
            var response = await handler(context, request);
            return response.ToByteArray();
        });
    }

    public static IEndpointRouteBuilder MapGrpcUnary(
        this IEndpointRouteBuilder endpoints,
        string pattern,
        Func<HttpContext, ReadOnlyMemory<byte>, Task<byte[]>> handler)
    {
        endpoints.MapPost(pattern, async context =>
        {
            try
            {
                if (context.Request.Protocol != "HTTP/2")
                {
                    await GrpcFraming.WriteErrorAsync(context, "12", "grpc requires HTTP/2");
                    return;
                }
                if (!IsAuthorized(context))
                {
                    await GrpcFraming.WriteErrorAsync(context, "16", "unauthenticated");
                    return;
                }

                if (!GrpcFraming.IsGrpcRequest(context.Request))
                {
                    await GrpcFraming.WriteErrorAsync(context, "3", "grpc content type is required");
                    return;
                }

                var frame = await GrpcFraming.ReadRequestAsync(context.Request, context.RequestAborted);
                if (frame.Compressed)
                {
                    await GrpcFraming.WriteErrorAsync(context, "12", "compressed grpc messages are not supported");
                    return;
                }

                var response = await handler(context, frame.Payload);
                await GrpcFraming.WriteResponseAsync(context, response);
            }
            catch (GrpcStatusException exception)
            {
                await GrpcFraming.WriteErrorAsync(context, exception.Status, exception.Message);
            }
            catch (GrpcFrameException exception)
            {
                await GrpcFraming.WriteErrorAsync(context, exception.Status, exception.Message);
            }
            catch (InvalidProtocolBufferException)
            {
                await GrpcFraming.WriteErrorAsync(context, "3", "invalid protobuf message");
            }
            catch (BadHttpRequestException exception) when (exception.StatusCode is 400 or 413)
            {
                await GrpcFraming.WriteErrorAsync(context, exception.StatusCode == 413 ? "8" : "3", "invalid grpc request body");
            }
            catch (UnauthorizedAccessException)
            {
                await GrpcFraming.WriteErrorAsync(context, "16", "unauthenticated");
            }
        });

        return endpoints;
    }

    private static bool IsAuthorized(HttpContext context)
    {
        var request = context.Request;
        if (!request.Headers.TryGetValue("authorization", out var value))
            return !ServerConfig.RequireBasicAuth;

        if (value.Count != 1)
            return false;

        var authorization = value.ToString();
        if (ServerConfig.IsExpectedBasicAuth(authorization))
            return true;

        if (ServerConfig.RequireBasicAuth)
            return false;

        var players = context.RequestServices.GetRequiredService<PlayerManager>();
        return players.IsKnownCredential(request);
    }
}
