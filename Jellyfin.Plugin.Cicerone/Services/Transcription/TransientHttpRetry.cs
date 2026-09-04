using System;
using System.Net;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.Cicerone.Services.Transcription
{
    /// <summary>
    /// Retries the failures that are worth retrying, and nothing else.
    /// </summary>
    /// <remarks>
    /// A run makes hundreds of calls over hours, unattended, so a rate limit or a
    /// gateway hiccup has to be survivable — losing an anchor to one is losing a
    /// fifth of an item's evidence. What must <em>not</em> be retried is a 400 or a
    /// 401: a malformed request and a bad key fail identically on the fourth attempt
    /// and take three times as long to say so.
    /// </remarks>
    public static class TransientHttpRetry
    {
        /// <summary>How many attempts in total, including the first.</summary>
        public const int Attempts = 3;

        /// <summary>Sends a request, retrying transient failures with backoff.</summary>
        /// <param name="client">The HTTP client.</param>
        /// <param name="build">
        /// Builds the request. A factory rather than a message, because a
        /// <see cref="HttpRequestMessage"/> cannot be sent twice — its content stream
        /// has been consumed, and reusing it fails in a way that looks like the server
        /// rejecting the second attempt.
        /// </param>
        /// <param name="logger">Where retries are reported.</param>
        /// <param name="cancellationToken">Cancellation token.</param>
        /// <returns>The last response, successful or not.</returns>
        public static async Task<HttpResponseMessage> SendAsync(
            HttpClient client,
            Func<HttpRequestMessage> build,
            ILogger logger,
            CancellationToken cancellationToken)
        {
            ArgumentNullException.ThrowIfNull(client);
            ArgumentNullException.ThrowIfNull(build);

            HttpResponseMessage? response = null;

            for (var attempt = 1; attempt <= Attempts; attempt++)
            {
                response?.Dispose();

                using var request = build();
                response = await client.SendAsync(request, cancellationToken).ConfigureAwait(false);

                if (response.IsSuccessStatusCode || !IsTransient(response.StatusCode) || attempt == Attempts)
                {
                    return response;
                }

                // Retry-After is honoured when the provider sends one, because it is
                // the only party that knows when the limit lifts. Guessing shorter
                // gets the next attempt rejected too and spends the retry budget.
                var wait = response.Headers.RetryAfter?.Delta
                    ?? TimeSpan.FromSeconds(Math.Pow(2, attempt));

                logger.LogDebug(
                    "Cicerone: transcription returned {Status}, retrying in {Seconds}s (attempt {Attempt})",
                    (int)response.StatusCode,
                    wait.TotalSeconds,
                    attempt);

                await Task.Delay(wait, cancellationToken).ConfigureAwait(false);
            }

            return response!;
        }

        private static bool IsTransient(HttpStatusCode status) => status is
            HttpStatusCode.TooManyRequests
            or HttpStatusCode.RequestTimeout
            or HttpStatusCode.InternalServerError
            or HttpStatusCode.BadGateway
            or HttpStatusCode.ServiceUnavailable
            or HttpStatusCode.GatewayTimeout;
    }
}
