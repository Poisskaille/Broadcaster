using System;
using System.Diagnostics;
using System.IO;
using System.IO.Pipes;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace Broadcaster
{
    // Discord's local IPC Rich Presence transport. Does not request Discord OAuth tokens.
    // Runs outside the UI thread and cannot stall the HDMI capture pipeline.
    public sealed class DiscordPresenceService : IDisposable
    {
        private readonly object _sync = new object();
        private CancellationTokenSource _stopping;
        private Task _task;
        private int _revision;
        public string Status { get; private set; } = "";

        public void Apply(DiscordPresenceSettings next)
        {
            if (next == null) next = new DiscordPresenceSettings();
            FrameGenerationDebugLog.Log(DebugLogFlags.DiscordPresence,
                "Apply Discord Rich Presence: enabled=" + (next.Enabled ? "1" : "0") +
                ", mode=" + (next.Mode ?? "") + ", platform=" + (next.Platform ?? ""));
            var settings = next.Clone();
            settings.Normalize();
            CancellationTokenSource old;
            lock (_sync)
            {
                old = _stopping;
                _stopping = null;
                _revision++;
                if (settings.Enabled && DiscordPresenceSettings.ValidApplicationId(settings.ApplicationId))
                {
                    var cts = new CancellationTokenSource();
                    _stopping = cts;
                    int id = _revision;
                    _task = Task.Run(() => RunAsync(settings, id, cts.Token));
                }
                else
                {
                    Status = settings.Enabled ? "Invalid Discord Application ID" : "Disabled";
                    _task = null;
                }
            }
            old?.Cancel();
        }

        private async Task RunAsync(DiscordPresenceSettings settings, int revision, CancellationToken token)
        {
            NamedPipeClientStream pipe = null;
            string lastTitle = null;
            string lastAsset = null;
            long startSeconds = 0;
            // Close a blocked IPC read promptly when the user disables Rich Presence.
            var cancellationRegistration = token.Register(() => { try { pipe?.Dispose(); } catch { } });
            try
            {
                while (!token.IsCancellationRequested)
                {
                    try
                    {
                        NintendoGamePresence nintendo = null;
                        string game = settings.Mode == "Manual" ? settings.ManualGame : null;
                        string asset = settings.ImageKey;
                        if (settings.Mode == "Nintendo")
                        {
                            nintendo = await NintendoPresenceReader.ReadAsync(settings.NintendoPresenceUrl, token).ConfigureAwait(false);
                            game = nintendo?.Title;
                            asset = nintendo?.ImageUrl ?? settings.ImageKey;
                        }
                        if (string.IsNullOrWhiteSpace(game))
                        {
                            // Disconnect clears Rich Presence; no stale activity when offline.
                            pipe?.Dispose(); pipe = null;
                            lastTitle = null;
                            SetStatus(revision, settings.Mode == "Nintendo" ? "Nintendo: no active game" : "Enter a game name");
                        }
                        else
                        {
                            if (pipe == null || !pipe.IsConnected)
                            {
                                pipe?.Dispose();
                                pipe = Connect(settings.ApplicationId);
                                lastTitle = null; // force an activity update after reconnect
                            }
                            if (game != lastTitle || asset != lastAsset)
                                startSeconds = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
                            SendActivity(pipe, game, asset, settings.Platform, settings.ShowElapsed ? startSeconds : 0);
                            lastTitle = game;
                            lastAsset = asset;
                            SetStatus(revision, "Discord: " + game);
                        }
                    }
                    catch (OperationCanceledException) when (token.IsCancellationRequested) { break; }
                    catch (Exception ex)
                    {
                        pipe?.Dispose(); pipe = null;
                        lastTitle = null;
                        SetStatus(revision, "Discord/Nintendo: " + ex.Message);
                    }
                    await Task.Delay(TimeSpan.FromSeconds(settings.Mode == "Nintendo" ? 30 : 25), token).ConfigureAwait(false);
                }
            }
            catch (OperationCanceledException) { }
            finally
            {
                cancellationRegistration.Dispose();
                pipe?.Dispose();
                SetStatus(revision, "Disabled");
            }
        }

        private void SetStatus(int revision, string value)
        {
            bool changed = false;
            lock (_sync)
            {
                if (_revision == revision && Status != value)
                {
                    Status = value;
                    changed = true;
                }
            }
            if (changed)
                FrameGenerationDebugLog.Log(DebugLogFlags.DiscordPresence, "Status: " + value);
        }

        private static NamedPipeClientStream Connect(string applicationId)
        {
            Exception last = null;
            for (int i = 0; i < 10; i++)
            {
                NamedPipeClientStream candidate = null;
                try
                {
                    candidate = new NamedPipeClientStream(".", "discord-ipc-" + i, PipeDirection.InOut);
                    candidate.Connect(200);
                    if (candidate.CanTimeout)
                    {
                        candidate.ReadTimeout = 3000;
                        candidate.WriteTimeout = 3000;
                    }
                    SendFrame(candidate, 0, new JObject { ["v"] = 1, ["client_id"] = applicationId });
                    var ready = ReadFrame(candidate);
                    if (ready?.Value<string>("evt") != "READY")
                        throw new IOException("Discord rejected the IPC handshake.");
                    return candidate;
                }
                catch (Exception ex) { last = ex; candidate?.Dispose(); }
            }
            throw new IOException("Discord desktop app not available: " + last?.Message);
        }

        private static void SendActivity(NamedPipeClientStream pipe, string game, string asset, string platform, long start)
        {
            string platformText;
            switch (platform)
            {
                case "Switch": platformText = "Nintendo Switch"; break;
                case "Switch2": platformText = "Nintendo Switch 2"; break;
                case "PS5": platformText = "PlayStation 5"; break;
                case "Xbox": platformText = "Xbox"; break;
                default: platformText = "HDMI capture"; break;
            }

            // The title is common to manual and Nintendo modes. On Discord clients
            // supporting the activity.name override, it replaces "Broadcaster" in
            // the bold first line, with just one descriptive line underneath.
            string gameTitle = game.Length > 120 ? game.Substring(0, 120) : game;
            string description = "Playing on " + platformText + " via Broadcaster";
            var activity = new JObject
            {
                ["type"] = 0,
                ["name"] = gameTitle,
                ["status_display_type"] = 0, // Show the activity name in the user's status.
                ["details"] = description
            };
            if (start > 0) activity["timestamps"] = new JObject { ["start"] = start };
            if (!string.IsNullOrWhiteSpace(asset))
            {
                asset = asset.Replace("\r", "").Replace("\n", "");
                activity["assets"] = new JObject
                {
                    ["large_image"] = asset,
                    ["large_text"] = gameTitle
                };
            }

            JObject response = SendActivityCommand(pipe, activity);
            string acknowledgedName = response?["data"]?["name"]?.ToString();
            bool rejected = string.Equals(response?.Value<string>("evt"), "ERROR", StringComparison.Ordinal);
            bool nameWasReplaced = !string.IsNullOrWhiteSpace(acknowledgedName) &&
                                   !string.Equals(acknowledgedName, gameTitle, StringComparison.Ordinal);

            if (rejected || nameWasReplaced)
            {
                // Older Discord clients may refuse or ignore custom activity names.
                // Fall back to the classic two text fields rather than hide the game.
                var fallback = (JObject)activity.DeepClone();
                fallback.Remove("name");
                fallback.Remove("status_display_type");
                fallback["details"] = gameTitle;
                fallback["state"] = description;
                response = SendActivityCommand(pipe, fallback);
                if (string.Equals(response?.Value<string>("evt"), "ERROR", StringComparison.Ordinal))
                    throw new IOException(response?["data"]?["message"]?.ToString() ?? "Discord rejected Rich Presence.");
            }
        }

        private static JObject SendActivityCommand(NamedPipeClientStream pipe, JObject activity)
        {
            SendFrame(pipe, 1, new JObject
            {
                ["cmd"] = "SET_ACTIVITY",
                ["nonce"] = Guid.NewGuid().ToString("N"),
                ["args"] = new JObject
                {
                    ["pid"] = Process.GetCurrentProcess().Id,
                    ["activity"] = activity
                }
            });
            return ReadFrame(pipe);
        }

        private static void SendFrame(Stream stream, int opcode, JToken json)
        {
            byte[] body = Encoding.UTF8.GetBytes(json.ToString(Formatting.None));
            byte[] header = new byte[8];
            Buffer.BlockCopy(BitConverter.GetBytes(opcode), 0, header, 0, 4);
            Buffer.BlockCopy(BitConverter.GetBytes(body.Length), 0, header, 4, 4);
            stream.Write(header, 0, header.Length);
            stream.Write(body, 0, body.Length);
            stream.Flush();
        }

        private static JObject ReadFrame(Stream stream)
        {
            byte[] header = new byte[8];
            ReadFully(stream, header);
            int opcode = BitConverter.ToInt32(header, 0);
            int length = BitConverter.ToInt32(header, 4);
            if (length < 0 || length > 256 * 1024) throw new IOException("Invalid Discord response size.");
            byte[] body = new byte[length];
            ReadFully(stream, body);
            if (opcode == 2) throw new IOException("Discord disconnected the presence session.");
            if (opcode == 3) // PING: Discord expects a matching PONG.
            {
                byte[] pong = new byte[8];
                Buffer.BlockCopy(BitConverter.GetBytes(4), 0, pong, 0, 4);
                Buffer.BlockCopy(BitConverter.GetBytes(length), 0, pong, 4, 4);
                stream.Write(pong, 0, pong.Length);
                stream.Write(body, 0, body.Length);
                stream.Flush();
                return ReadFrame(stream);
            }
            if (opcode != 1) throw new IOException("Unexpected Discord IPC opcode.");
            return JObject.Parse(Encoding.UTF8.GetString(body));
        }

        private static void ReadFully(Stream stream, byte[] buffer)
        {
            int n = 0;
            while (n < buffer.Length)
            {
                int count = stream.Read(buffer, n, buffer.Length - n);
                if (count <= 0) throw new EndOfStreamException();
                n += count;
            }
        }

        public void Dispose()
        {
            CancellationTokenSource old;
            lock (_sync) { old = _stopping; _stopping = null; _revision++; _task = null; Status = "Disabled"; }
            old?.Cancel();
        }
    }
}
