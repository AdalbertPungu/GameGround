namespace GameGround.AgentFramework;

internal sealed class NarratorResponseLimitHandler(HttpMessageHandler innerHandler) : DelegatingHandler(innerHandler)
{
    protected override async Task<HttpResponseMessage> SendAsync(
        HttpRequestMessage request, CancellationToken cancellationToken)
    {
        var response = await base.SendAsync(request, cancellationToken);
        var transferred = false;
        try
        {
            if (response.Content.Headers.ContentLength > NarratorLimits.MaxTransportBytes)
                throw new NarratorOutputException(NarratorRejection.SizeLimit);
            await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
            using var buffer = new MemoryStream();
            var chunk = new byte[8192];
            int read;
            while ((read = await stream.ReadAsync(chunk, cancellationToken)) > 0)
            {
                if (buffer.Length + read > NarratorLimits.MaxTransportBytes)
                    throw new NarratorOutputException(NarratorRejection.SizeLimit);
                buffer.Write(chunk, 0, read);
            }
            var content = new ByteArrayContent(buffer.ToArray());
            foreach (var header in response.Content.Headers)
                content.Headers.TryAddWithoutValidation(header.Key, header.Value);
            response.Content.Dispose();
            response.Content = content;
            transferred = true;
            return response;
        }
        finally
        {
            if (!transferred)
                response.Dispose();
        }
    }
}
