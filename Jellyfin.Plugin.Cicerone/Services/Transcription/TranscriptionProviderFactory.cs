using System;
using System.Net.Http;
using Jellyfin.Plugin.Cicerone.Configuration;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.Cicerone.Services.Transcription
{
    /// <summary>Builds the provider a profile describes.</summary>
    public sealed class TranscriptionProviderFactory
    {
        private readonly IHttpClientFactory _clients;
        private readonly ILoggerFactory _loggers;

        /// <summary>Initialises a new instance of the <see cref="TranscriptionProviderFactory"/> class.</summary>
        /// <param name="clients">Jellyfin's HTTP client factory.</param>
        /// <param name="loggers">The logger factory.</param>
        public TranscriptionProviderFactory(IHttpClientFactory clients, ILoggerFactory loggers)
        {
            _clients = clients;
            _loggers = loggers;
        }

        /// <summary>Creates a provider.</summary>
        /// <param name="profile">The profile.</param>
        /// <returns>The provider.</returns>
        /// <exception cref="InvalidOperationException">The profile is not usable.</exception>
        public ITranscriptionProvider Create(TranscriptionProfile profile)
        {
            ArgumentNullException.ThrowIfNull(profile);

            if (string.IsNullOrWhiteSpace(profile.Model))
            {
                throw new InvalidOperationException(
                    $"The transcription profile '{profile.Name}' has no model set.");
            }

            if (profile.Provider == TranscriptionProviderKind.OpenAiCompatible
                && string.IsNullOrWhiteSpace(profile.BaseUrl))
            {
                throw new InvalidOperationException(
                    $"The transcription profile '{profile.Name}' is OpenAI-compatible and needs a base URL "
                    + "(for example http://localhost:8000/v1).");
            }

            if (profile.Provider != TranscriptionProviderKind.OpenAiCompatible
                && string.IsNullOrWhiteSpace(profile.ApiKey))
            {
                throw new InvalidOperationException(
                    $"The transcription profile '{profile.Name}' has no API key.");
            }

            var client = _clients.CreateClient(NamedClient);

            // Per-profile rather than on the named client, because a local
            // whisper.cpp server on a CPU and a hosted endpoint want wildly different
            // patience and the owner sets each on its own profile.
            client.Timeout = TimeSpan.FromSeconds(Math.Clamp(profile.TimeoutSeconds, 10, 900));

            var logger = _loggers.CreateLogger("Cicerone.Transcription");

            return profile.Provider switch
            {
                TranscriptionProviderKind.Google => new GoogleTranscriptionProvider(profile, client, logger),
                _ => new OpenAiTranscriptionProvider(profile, client, logger),
            };
        }

        /// <summary>The named HTTP client Cicerone's outbound calls use.</summary>
        public const string NamedClient = "Cicerone";
    }
}
