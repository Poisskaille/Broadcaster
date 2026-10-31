
using System;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Reflection;
using System.Runtime.Serialization;
using System.Runtime.Serialization.Json;
using System.Text.RegularExpressions;
using System.Threading.Tasks;

namespace Broadcaster
{
    public class UpdateInfo
    {
        public Version LatestVersion { get; set; }
        public bool IsNewer { get; set; }
        public string AssetName { get; set; }
        public string DownloadUrl { get; set; }
    }

    [DataContract]
    internal class GitHubRelease
    {
        [DataMember(Name = "name")]
        public string Name { get; set; }

        [DataMember(Name = "tag_name")]
        public string TagName { get; set; }

        [DataMember(Name = "assets")]
        public GitHubAsset[] Assets { get; set; }
    }

    [DataContract]
    internal class GitHubAsset
    {
        [DataMember(Name = "name")]
        public string Name { get; set; }

        [DataMember(Name = "browser_download_url")]
        public string DownloadUrl { get; set; }
    }

    public static class UpdateChecker
    {
        private const string GitHubOwner = "Poisskaille";
        private const string GitHubRepo = "Broadcaster";
        private static readonly HttpClient Http = CreateClient();

        private static HttpClient CreateClient()
        {
            ServicePointManager.SecurityProtocol |=
                SecurityProtocolType.Tls12;

            var client = new HttpClient();
            client.Timeout = TimeSpan.FromSeconds(15);

            client.DefaultRequestHeaders.UserAgent
                .ParseAdd("Broadcaster-Updater");

            client.DefaultRequestHeaders.Accept
                .ParseAdd("application/vnd.github+json");

            return client;
        }

        // Version actuelle de l'executable
        public static Version CurrentVersion
        {
            get
            {
                Assembly assembly = Assembly.GetEntryAssembly();

                if (assembly == null)
                    assembly = Assembly.GetExecutingAssembly();

                Version version = assembly.GetName().Version;

                return Normalize(version);
            }
        }

        // Normalise les versions sur 4 chiffres
        private static Version Normalize(Version version)
        {
            if (version == null)
                return new Version(0, 0, 0, 0);

            return new Version(
                version.Major,
                version.Minor,
                Math.Max(0, version.Build),
                Math.Max(0, version.Revision)
            );
        }

        public static async Task<UpdateInfo> CheckAsync()
        {
            string url =
                "https://api.github.com/repos/" +
                GitHubOwner + "/" +
                GitHubRepo + "/releases/tags/gold";

            using (HttpResponseMessage response =
                await Http.GetAsync(url).ConfigureAwait(false))
            {
                if (response.StatusCode == HttpStatusCode.NotFound)
                {
                    throw new InvalidOperationException(
                        "Aucune release GitHub trouvée. " +
                        "Vérifie le dépôt et ses releases.");
                }

                response.EnsureSuccessStatusCode();

                using (Stream stream =
                    await response.Content.ReadAsStreamAsync()
                        .ConfigureAwait(false))
                {
                    var serializer =
                        new DataContractJsonSerializer(
                            typeof(GitHubRelease));

                    GitHubRelease release =
                        (GitHubRelease)serializer.ReadObject(stream);

                    if (release == null ||
    string.IsNullOrWhiteSpace(release.Name))
                    {
                        throw new InvalidOperationException(
                            "Le nom de la release GitHub est introuvable.");
                    }

                    // Exemple : "broadcaster v1.1.0"
                    Match match = Regex.Match(
                        release.Name,
                        @"\bv(\d+\.\d+\.\d+(?:\.\d+)?)\b",
                        RegexOptions.IgnoreCase
                    );

                    if (!match.Success)
                    {
                        throw new InvalidOperationException(
                            "Impossible de trouver la version dans : " +
                            release.Name);
                    }

                    Version parsedVersion;

                    if (!Version.TryParse(match.Groups[1].Value,
                                          out parsedVersion))
                    {
                        throw new InvalidOperationException(
                            "Version invalide : " + release.Name);
                    }

                    string assetName = null;
                    string downloadUrl = null;

                    if (release.Assets != null)
                    {
                        foreach (GitHubAsset asset in release.Assets)
                        {
                            if (asset == null ||
                                string.IsNullOrEmpty(asset.Name))
                                continue;

                            if (asset.Name.EndsWith(
                                ".zip",
                                StringComparison.OrdinalIgnoreCase)
                                && !string.IsNullOrEmpty(asset.DownloadUrl))
                            {
                                assetName = asset.Name;
                                downloadUrl = asset.DownloadUrl;
                                break;
                            }
                        }
                    }

                    Version latest = Normalize(parsedVersion);
                    Version current = CurrentVersion;

                    return new UpdateInfo
                    {
                        LatestVersion = latest,
                        IsNewer = latest.CompareTo(current) > 0,
                        AssetName = assetName,
                        DownloadUrl = downloadUrl
                    };
                }
            }
        }
    }
}
