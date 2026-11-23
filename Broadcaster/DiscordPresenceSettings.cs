using System;

namespace Broadcaster
{
    // Discord settings are application-level, never tied to a capture-card profile.
    public sealed class DiscordPresenceSettings
    {
        public bool Enabled { get; set; } = false;
        public string ApplicationId { get; set; } = "";
        public string Mode { get; set; } = "Manual"; // Manual or Nintendo
        public string Platform { get; set; } = "Switch2";
        public string ManualGame { get; set; } = "";
        public string ImageKey { get; set; } = ""; // optional asset key from the Discord developer portal
        public string NintendoPresenceUrl { get; set; } = "";
        public bool ShowElapsed { get; set; } = true;

        public DiscordPresenceSettings Clone() => (DiscordPresenceSettings)MemberwiseClone();

        public void Normalize()
        {
            ApplicationId = (ApplicationId ?? "").Trim();
            Mode = string.Equals(Mode, "Nintendo", StringComparison.OrdinalIgnoreCase) ? "Nintendo" : "Manual";
            switch (Platform)
            {
                case "Switch": case "Switch2": case "PS5": case "Xbox": case "Other": break;
                default: Platform = "Other"; break;
            }
            ManualGame = Limit(ManualGame, 120);
            ImageKey = Limit(ImageKey, 256);
            NintendoPresenceUrl = Limit(NintendoPresenceUrl, 1024);
        }

        private static string Limit(string source, int max)
        {
            var clean = (source ?? "").Trim().Replace("\r", " ").Replace("\n", " ");
            return clean.Length > max ? clean.Substring(0, max) : clean;
        }

        public static bool ValidApplicationId(string id)
        {
            if (string.IsNullOrWhiteSpace(id) || id.Length < 17 || id.Length > 21) return false;
            foreach (char c in id) if (c < '0' || c > '9') return false;
            return true;
        }

        public static bool ValidPresenceUrl(string value)
        {
            if (string.IsNullOrWhiteSpace(value) || value.Length > 1024 ||
                !Uri.TryCreate(value, UriKind.Absolute, out Uri uri) ||
                !string.IsNullOrEmpty(uri.UserInfo) || !string.IsNullOrEmpty(uri.Query) ||
                !string.IsNullOrEmpty(uri.Fragment)) return false;
            bool local = uri.IsLoopback;
            if (uri.Scheme != Uri.UriSchemeHttps && !(local && uri.Scheme == Uri.UriSchemeHttp)) return false;
            // Accept only Nintendo presence endpoints, not arbitrary resources or URLs containing tokens.
            return uri.AbsolutePath.IndexOf("/api/presence/", StringComparison.OrdinalIgnoreCase) >= 0;
        }
    }
}
