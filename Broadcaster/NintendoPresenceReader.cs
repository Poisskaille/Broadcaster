using System;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using Newtonsoft.Json.Linq;

namespace Broadcaster
{
    internal sealed class NintendoGamePresence
    {
        public string Title { get; set; }
        public string ImageUrl { get; set; }
    }

    // nxapi is configured by the user, outside Broadcaster. No Nintendo credentials or tokens
    // ever enter the app. The source can be a localhost nxapi presence-server or a trusted
    // HTTPS presence endpoint explicitly supplied by the user.
    internal static class NintendoPresenceReader
    {
        private static readonly HttpClient Client = new HttpClient
        {
            Timeout = TimeSpan.FromSeconds(6),
            MaxResponseContentBufferSize = 256 * 1024
        };

        internal static async Task<NintendoGamePresence> ReadAsync(string url, CancellationToken token)
        {
            if (!DiscordPresenceSettings.ValidPresenceUrl(url))
                throw new ArgumentException("Invalid Nintendo presence URL.");
            using (var message = await Client.GetAsync(url, HttpCompletionOption.ResponseContentRead, token).ConfigureAwait(false))
            {
                message.EnsureSuccessStatusCode();
                // Limit response size: this is a small presence document, not a media download.
                if (message.Content.Headers.ContentLength.HasValue && message.Content.Headers.ContentLength > 256 * 1024)
                    throw new InvalidOperationException("Nintendo presence response is too large.");
                string json = await message.Content.ReadAsStringAsync().ConfigureAwait(false);
                if (json.Length > 256 * 1024) throw new InvalidOperationException("Nintendo presence response is too large.");
                return Parse(json);
            }
        }

        internal static NintendoGamePresence Parse(string json)
        {
            JToken root = JToken.Parse(json);
            JToken data = Pick(root, "result") ?? Pick(root, "data") ?? root;
            JToken profile = Pick(data, "user") ?? Pick(data, "friend") ?? data;
            JToken presence = Pick(data, "presence") ?? Pick(profile, "presence") ?? data;
            string state = GetString(Pick(presence, "state")) ?? GetString(Pick(presence, "status")) ??
                GetString(Pick(data, "state"));
            if (!string.IsNullOrWhiteSpace(state))
            {
                string normalized = state.Trim().ToUpperInvariant();
                if (normalized == "OFFLINE" || normalized == "INACTIVE" || normalized == "NOT_PLAYING" ||
                    normalized == "HIDDEN" || normalized == "AWAY") return null;
            }
            JToken game = Pick(presence, "game") ?? Pick(profile, "game") ??
                Pick(data, "game") ?? Pick(root, "game");
            string title = GetString(Pick(game, "name")) ?? GetString(Pick(game, "title"));
            if (string.IsNullOrWhiteSpace(title)) return null;
            title = title.Trim();
            if (title.Length > 120) title = title.Substring(0, 120);
            string image = GetString(Pick(game, "imageUri")) ?? GetString(Pick(game, "imageUrl")) ??
                GetString(Pick(game, "image_url"));
            // Discord accepts HTTPS external images on supported clients. Never send an untrusted local URL.
            if (!Uri.TryCreate(image, UriKind.Absolute, out var imageUri) || imageUri.Scheme != Uri.UriSchemeHttps)
                image = null;
            return new NintendoGamePresence { Title = title, ImageUrl = image };
        }

        private static JToken Pick(JToken token, string field) => token is JObject obj ? obj[field] : null;
        private static string GetString(JToken token) => token?.Type == JTokenType.String ? (string)token : null;
    }
}
