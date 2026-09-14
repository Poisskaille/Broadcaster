using SharpDX.MediaFoundation;

namespace SwitchViewer
{
    /// <summary>
    /// Représente un périphérique de capture vidéo détecté par Media Foundation
    /// (ex : une carte de capture USB branchée en tant que "USB Video").
    /// </summary>
    public class VideoDeviceInfo
    {
        public string FriendlyName { get; set; }
        public string SymbolicLink { get; set; }

        /// <summary>
        /// L'objet Activate MF permettant de créer la MediaSource correspondante.
        /// Conservé tant que la liste des périphériques est en vie.
        /// </summary>
        public Activate Activate { get; set; }

        public override string ToString() => FriendlyName;
    }

    /// <summary>
    /// Représente un périphérique de capture AUDIO détecté par Media Foundation
    /// (ex : "USB Audio Device" exposé séparément par le dongle de capture,
    /// comme il faut le configurer dans OBS avec une piste audio à part).
    /// </summary>
    public class AudioCaptureDeviceInfo
    {
        public string FriendlyName { get; set; }
        public Activate Activate { get; set; }

        public override string ToString() => FriendlyName;
    }

    /// <summary>
    /// Représente un périphérique de sortie audio (haut-parleurs, casque, etc.)
    /// détecté via l'API WASAPI (Core Audio), utilisé pour le rendu et le volume.
    /// </summary>
    public class AudioDeviceInfo
    {
        public string FriendlyName { get; set; }
        public string EndpointId { get; set; }

        public override string ToString() => FriendlyName;
    }
}
