
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
        public string Sha256 { get; set; }
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

        [DataMember(Name = "digest")]
        public string Sha256 { get; set; }
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
            // Dernière release stable publiée (ni brouillon, ni pré-release).
            // Compatibilité avec l'ancien schéma qui utilisait un tag fixe « gold ».
            string api = "https://api.github.com/repos/" + GitHubOwner + "/" + GitHubRepo;
            GitHubRelease release = await FetchReleaseAsync(api + "/releases/latest").ConfigureAwait(false);
            if (release == null)
                release = await FetchReleaseAsync(api + "/releases/tags/gold").ConfigureAwait(false);

            if (release == null)
                throw new InvalidOperationException("Aucune release GitHub publique trouvée (latest ou gold).");

            // Le nom peut être « Broadcaster v1.5.0 » ou simplement « v1.5.0 ».
            // Sinon, on essaye le tag (ex : v1.5.0).
            Match match = Regex.Match(release.Name ?? "", @"\bv(\d+\.\d+\.\d+(?:\.\d+)?)\b", RegexOptions.IgnoreCase);
            if (!match.Success)
                match = Regex.Match(release.TagName ?? "", @"\bv?(\d+\.\d+\.\d+(?:\.\d+)?)\b", RegexOptions.IgnoreCase);

            if (!match.Success || !Version.TryParse(match.Groups[1].Value, out Version parsedVersion))
                throw new InvalidOperationException("Version introuvable dans la release : " + (release.Name ?? release.TagName));

            GitHubAsset selected = null;
            if (release.Assets != null)
            {
                foreach (GitHubAsset asset in release.Assets)
                {
                    if (asset == null || string.IsNullOrEmpty(asset.Name) ||
                        !asset.Name.EndsWith(".zip", StringComparison.OrdinalIgnoreCase) ||
                        string.IsNullOrWhiteSpace(asset.DownloadUrl))
                        continue;

                    // Priorité au ZIP de Broadcaster si plusieurs archives sont proposées.
                    if (selected == null ||
                        (asset.Name.IndexOf("Broadcaster", StringComparison.OrdinalIgnoreCase) >= 0 &&
                         selected.Name.IndexOf("Broadcaster", StringComparison.OrdinalIgnoreCase) < 0))
                        selected = asset;
                }
            }

            Version latest = Normalize(parsedVersion);
            return new UpdateInfo
            {
                LatestVersion = latest,
                IsNewer = latest.CompareTo(CurrentVersion) > 0,
                AssetName = selected?.Name,
                DownloadUrl = selected?.DownloadUrl,
                Sha256 = selected?.Sha256
            };
        }

        private static async Task<GitHubRelease> FetchReleaseAsync(string url)
        {
            using (HttpResponseMessage response = await Http.GetAsync(url).ConfigureAwait(false))
            {
                if (response.StatusCode == HttpStatusCode.NotFound)
                    return null;

                response.EnsureSuccessStatusCode();
                using (Stream stream = await response.Content.ReadAsStreamAsync().ConfigureAwait(false))
                {
                    var serializer = new DataContractJsonSerializer(typeof(GitHubRelease));
                    return (GitHubRelease)serializer.ReadObject(stream);
                }
            }
        }
    }
}
