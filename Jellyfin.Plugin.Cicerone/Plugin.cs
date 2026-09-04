using System;
using System.Collections.Generic;
using Jellyfin.Plugin.Cicerone.Configuration;
using MediaBrowser.Common.Configuration;
using MediaBrowser.Common.Plugins;
using MediaBrowser.Model.Plugins;
using MediaBrowser.Model.Serialization;

namespace Jellyfin.Plugin.Cicerone
{
    /// <summary>
    /// The Cicerone plugin: checks that a subtitle track is the language it claims
    /// and is timed to the dialogue it sits over.
    /// </summary>
    public class Plugin : BasePlugin<PluginConfiguration>, IHasWebPages
    {
        /// <summary>Initialises a new instance of the <see cref="Plugin"/> class.</summary>
        /// <param name="applicationPaths">Server paths.</param>
        /// <param name="xmlSerializer">Configuration serializer.</param>
        public Plugin(IApplicationPaths applicationPaths, IXmlSerializer xmlSerializer)
            : base(applicationPaths, xmlSerializer)
        {
            Instance = this;
        }

        /// <inheritdoc />
        public override Guid Id => Guid.Parse("83004bd9-e2d6-4e13-9f3a-73bb12ef1f96");

        /// <inheritdoc />
        public override string Name => "Cicerone";

        /// <inheritdoc />
        public override string Description =>
            "Checks subtitle tracks against the dialogue in the file — the right language, the right cut, "
            + "and timed to the audio rather than merely present.";

        /// <summary>Gets the current plugin instance.</summary>
        public static Plugin? Instance { get; private set; }

        /// <inheritdoc />
        public IEnumerable<PluginPageInfo> GetPages()
        {
            return
            [
                new PluginPageInfo
                {
                    Name = Name,
                    EmbeddedResourcePath = GetType().Namespace + ".Configuration.configPage.html",
                },
            ];
        }
    }
}
