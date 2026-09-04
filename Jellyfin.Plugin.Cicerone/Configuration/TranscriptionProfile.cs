namespace Jellyfin.Plugin.Cicerone.Configuration
{
    /// <summary>Which speech-to-text backend a profile calls.</summary>
    public enum TranscriptionProviderKind
    {
        /// <summary>OpenAI's audio transcription endpoint.</summary>
        OpenAi = 0,

        /// <summary>
        /// Anything else speaking the same wire format at a base URL you supply.
        /// </summary>
        /// <remarks>
        /// This is the one that makes Cicerone free to run. Whisper is an open model
        /// and several servers host it behind OpenAI's exact endpoint — Speaches,
        /// faster-whisper-server, whisper.cpp's own server, LM Studio. Pointed at one
        /// of those, no audio leaves the network and no money is spent. Groq's hosted
        /// Whisper answers here too, at a price that rounds to nothing.
        /// </remarks>
        OpenAiCompatible = 1,

        /// <summary>Google Gemini, which takes audio directly.</summary>
        Google = 2,
    }

    /// <summary>
    /// One saved way of turning audio into words: the provider, the model, the
    /// credential, and what a minute of audio costs through it.
    /// </summary>
    /// <remarks>
    /// Pricing lives on the profile rather than on the configuration because a list
    /// you switch between turns "remember to change the price when you change
    /// provider" from an occasional mistake into the normal case. Switching profile
    /// switches the price with it.
    /// <para>
    /// The price is <b>per minute of audio</b>, not per token, because that is the
    /// unit Cicerone actually controls: the anchor count and window length decide the
    /// bill exactly, before a single call is made, and a run's cost can therefore be
    /// quoted in advance rather than discovered. Providers that bill audio as tokens
    /// convert at a fixed rate per second, so the number is still exact — it is just
    /// arithmetic done once, here.
    /// </para>
    /// </remarks>
    /// <remarks>
    /// A mutable class with a parameterless constructor, not a record: Jellyfin
    /// persists plugin configuration with <see cref="System.Xml.Serialization.XmlSerializer"/>,
    /// which requires both.
    /// </remarks>
    public class TranscriptionProfile
    {
        /// <summary>
        /// Gets or sets the stable identifier for this profile.
        /// </summary>
        /// <remarks>
        /// Referenced by <see cref="PluginConfiguration.DefaultProfileId"/>. It must
        /// survive renaming and reordering, so nothing may key a profile by its name
        /// or its position — both are things the owner can change at will.
        /// </remarks>
        public string Id { get; set; } = string.Empty;

        /// <summary>Gets or sets the display name shown in the profile list.</summary>
        public string Name { get; set; } = string.Empty;

        /// <summary>Gets or sets the backend this profile calls.</summary>
        public TranscriptionProviderKind Provider { get; set; } = TranscriptionProviderKind.OpenAi;

        /// <summary>Gets or sets the model identifier sent to the provider.</summary>
        public string Model { get; set; } = "whisper-1";

        /// <summary>
        /// Gets or sets the provider API key. Stored in plaintext in the plugin
        /// configuration file, as with every Jellyfin plugin.
        /// </summary>
        public string ApiKey { get; set; } = string.Empty;

        /// <summary>
        /// Gets or sets an optional base URL override. Empty means the provider's
        /// default endpoint; required for <see cref="TranscriptionProviderKind.OpenAiCompatible"/>
        /// (e.g. <c>http://localhost:8000/v1</c>).
        /// </summary>
        public string BaseUrl { get; set; } = string.Empty;

        /// <summary>
        /// Gets or sets what a minute of audio costs through this profile, in USD.
        /// 0 logs the minutes without a cost.
        /// </summary>
        public decimal CostPerAudioMinute { get; set; }

        /// <summary>
        /// Gets or sets whether the transcriber is told which language to expect.
        /// </summary>
        /// <remarks>
        /// On by default, and it is worth understanding what it does. Told the
        /// language, Whisper transcribes; left to guess, it sometimes <em>translates</em>
        /// — and a Spanish clip returned as English words shares nothing with the
        /// Spanish subtitle track being checked, so a correctly synced file is
        /// reported as belonging to a different film. Turn it off only when the
        /// subtitle tracks in the library are known to be mistagged, in which case
        /// the language check should be run first anyway.
        /// </remarks>
        public bool HintLanguage { get; set; } = true;

        /// <summary>Gets or sets the request timeout in seconds.</summary>
        /// <remarks>
        /// Generous, because a local whisper.cpp server on a CPU can take most of a
        /// minute over a thirty-second clip and that is a slow success rather than a
        /// failure.
        /// </remarks>
        public int TimeoutSeconds { get; set; } = 180;
    }
}
