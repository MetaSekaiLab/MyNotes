using System.Buffers.Binary;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.Net.Http.Headers;

namespace MyNotes.Services;

public readonly record struct GrpcFrame(bool Compressed, ReadOnlyMemory<byte> Payload);

public sealed class GrpcFrameException(string status, string message) : Exception(message)
{
    public string Status { get; } = status;
}

public static class GrpcFraming
{
    public const string ContentType = "application/grpc";
    public const int MaxMessageLength = 16 * 1024 * 1024;

    public static bool IsGrpcRequest(HttpRequest request)
    {
        if (!MediaTypeHeaderValue.TryParse(request.ContentType, out var contentType) ||
            contentType.Parameters.Any(parameter => parameter.Value.Length == 0))
            return false;

        var mediaType = contentType.MediaType.Value;
        return string.Equals(mediaType, ContentType, StringComparison.OrdinalIgnoreCase) ||
               string.Equals(mediaType, ContentType + "+proto", StringComparison.OrdinalIgnoreCase);
    }

    public static async Task<GrpcFrame> ReadRequestAsync(HttpRequest request, CancellationToken cancellationToken)
    {
        if (request.ContentLength > MaxMessageLength + 5L)
            throw new GrpcFrameException("8", "grpc message exceeds the size limit");

        var prefix = new byte[5];
        try
        {
            await request.Body.ReadExactlyAsync(prefix, cancellationToken);
        }
        catch (EndOfStreamException)
        {
            throw new GrpcFrameException("3", "grpc request requires a complete frame header");
        }

        if (prefix[0] is not (0 or 1))
            throw new GrpcFrameException("3", "invalid grpc compression flag");

        var length = BinaryPrimitives.ReadUInt32BigEndian(prefix.AsSpan(1, 4));
        if (length > MaxMessageLength)
            throw new GrpcFrameException("8", "grpc message exceeds the size limit");

        if (request.ContentLength.HasValue && request.ContentLength.Value != length + 5L)
            throw new GrpcFrameException("3", "grpc frame length must match the request body");

        var payload = new byte[(int)length];
        try
        {
            await request.Body.ReadExactlyAsync(payload, cancellationToken);
        }
        catch (EndOfStreamException)
        {
            throw new GrpcFrameException("3", "grpc frame payload is incomplete");
        }

        var trailingByte = new byte[1];
        if (await request.Body.ReadAsync(trailingByte, cancellationToken) != 0)
            throw new GrpcFrameException("3", "unary grpc requests require exactly one frame");

        return new GrpcFrame(prefix[0] == 1, payload);
    }

    public static async Task WriteResponseAsync(HttpContext context, byte[] protobuf)
    {
        var response = context.Response;
        response.StatusCode = StatusCodes.Status200OK;
        response.ContentType = ContentType;
        response.Headers.Append("grpc-encoding", "identity");
        if (response.SupportsTrailers())
            response.DeclareTrailer("grpc-status");
        else
            response.Headers.Append("grpc-status", "0");
        response.HttpContext.Features.Get<IHttpResponseBodyFeature>()?.DisableBuffering();

        var prefix = new byte[5];
        BinaryPrimitives.WriteUInt32BigEndian(prefix.AsSpan(1), (uint)protobuf.Length);
        await response.Body.WriteAsync(prefix, context.RequestAborted);
        await response.Body.WriteAsync(protobuf, context.RequestAborted);

        if (response.SupportsTrailers())
            response.AppendTrailer("grpc-status", "0");
    }

    public static async Task WriteErrorAsync(HttpContext context, string status, string message)
    {
        context.Response.StatusCode = StatusCodes.Status200OK;
        context.Response.ContentType = ContentType;
        if (context.Response.SupportsTrailers())
        {
            context.Response.DeclareTrailer("grpc-status");
            context.Response.DeclareTrailer("grpc-message");
            context.Response.AppendTrailer("grpc-status", status);
            context.Response.AppendTrailer("grpc-message", Uri.EscapeDataString(message));
        }
        else
        {
            context.Response.Headers.Append("grpc-status", status);
            context.Response.Headers.Append("grpc-message", Uri.EscapeDataString(message));
        }

        await context.Response.Body.FlushAsync(context.RequestAborted);
    }
}
