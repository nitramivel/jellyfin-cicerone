using Jellyfin.Plugin.Cicerone.Services;
using Jellyfin.Plugin.Cicerone.Services.Runs;
using Jellyfin.Plugin.Cicerone.Services.Transcription;
using MediaBrowser.Controller;
using MediaBrowser.Controller.Plugins;
using MediaBrowser.Model.Tasks;
using Microsoft.Extensions.DependencyInjection;

namespace Jellyfin.Plugin.Cicerone
{
    /// <summary>Registers Cicerone's services with Jellyfin's DI container.</summary>
    public sealed class ServiceRegistrator : IPluginServiceRegistrator
    {
        /// <inheritdoc />
        public void RegisterServices(IServiceCollection serviceCollection, IServerApplicationHost applicationHost)
        {
            serviceCollection.AddSingleton<FfmpegRunner>();
            serviceCollection.AddSingleton<AudioSampler>();
            serviceCollection.AddSingleton<SpeechActivityReader>();
            serviceCollection.AddSingleton<SubtitleReader>();
            serviceCollection.AddSingleton<FullTranscriber>();
            serviceCollection.AddSingleton<SubtitleMaker>();
            serviceCollection.AddSingleton<SubtitleLibrary>();
            serviceCollection.AddSingleton<TranscriptionProviderFactory>();
            serviceCollection.AddSingleton<RepairWriter>();
            serviceCollection.AddSingleton<CheckService>();

            // Singletons because both hold state the settings page reads: the reports
            // are cached in memory after the first load, and the run store holds the
            // live run — which is what makes the progress panel realtime. A scoped
            // instance of either would be empty on every request.
            serviceCollection.AddSingleton<ReportStore>();
            serviceCollection.AddSingleton<RunLogStore>();
            serviceCollection.AddSingleton<VerifyRunService>();

            serviceCollection.AddSingleton<IScheduledTask, VerifySubtitlesTask>();
        }
    }
}
