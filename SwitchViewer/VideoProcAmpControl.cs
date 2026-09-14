using System;
using System.Runtime.InteropServices;
using SharpDX.MediaFoundation;

namespace SwitchViewer
{
    /// <summary>
    /// Propriétés réglables sur un périphérique de capture WDM/UVC.
    /// Correspond exactement à l'énumération native VideoProcAmpProperty (strmif.h).
    /// L'ordre des valeurs compte : c'est un enum natif, pas une simple liste.
    /// </summary>
    public enum VideoProcAmpProperty
    {
        Brightness = 0,
        Contrast = 1,
        Hue = 2,
        Saturation = 3,
        Sharpness = 4,
        Gamma = 5,
        ColorEnable = 6,
        WhiteBalance = 7,
        BacklightCompensation = 8,
        Gain = 9
    }

    [Flags]
    public enum VideoProcAmpFlags
    {
        Auto = 0x0001,   // Le pilote gère la valeur automatiquement (ex : auto white balance)
        Manual = 0x0002  // On impose nous-même la valeur
    }

    public struct VideoProcAmpRange
    {
        public int Min;
        public int Max;
        public int Step;
        public int Default;
        public VideoProcAmpFlags Flags;
    }

    /// <summary>
    /// Traduction C# de l'interface native IAMVideoProcAmp (strmif.h).
    /// Le GUID doit être EXACTEMENT celui de l'interface native, sinon QueryInterface échoue
    /// silencieusement (on obtient simplement "non supporté" au lieu du vrai contrôle).
    /// </summary>
    [ComImport]
    [Guid("C6E13360-30AC-11D0-A18C-00A0C9118956")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    internal interface IAMVideoProcAmp
    {
        // PreserveSig : on veut le HRESULT brut en retour (pas d'exception automatique),
        // pour pouvoir gérer "propriété non supportée" proprement au lieu de planter.
        [PreserveSig]
        int GetRange(VideoProcAmpProperty property, out int min, out int max, out int step, out int defaultValue, out VideoProcAmpFlags flags);

        [PreserveSig]
        int Set(VideoProcAmpProperty property, int value, VideoProcAmpFlags flags);

        [PreserveSig]
        int Get(VideoProcAmpProperty property, out int value, out VideoProcAmpFlags flags);
    }

    /// <summary>
    /// Enveloppe autour de IAMVideoProcAmp pour un périphérique de capture donné.
    /// IMPORTANT : ceci pilote le MATÉRIEL (le pilote de la carte), pas un traitement logiciel.
    /// Les valeurs appliquées ici sont donc visibles par TOUT logiciel qui lirait la même
    /// source en parallèle (OBS compris) — exactement le comportement que tu as observé.
    /// </summary>
    public class VideoProcAmpControl : IDisposable
    {
        private readonly IAMVideoProcAmp _procAmp;

        private VideoProcAmpControl(IAMVideoProcAmp procAmp)
        {
            _procAmp = procAmp;
        }

        /// <summary>
        /// Tente d'obtenir le contrôle ProcAmp sur une MediaSource vidéo active.
        /// Retourne null si le device ne supporte pas cette interface : dans ce cas,
        /// l'onglet "Vidéo" des options devra simplement se désactiver, pas planter.
        /// </summary>
        public static VideoProcAmpControl TryCreate(MediaSource videoSource)
        {
            if (videoSource == null) return null;

            try
            {
                object comObject = Marshal.GetObjectForIUnknown(videoSource.NativePointer);
                if (comObject is IAMVideoProcAmp procAmp)
                    return new VideoProcAmpControl(procAmp);
            }
            catch (InvalidCastException)
            {
                // Le device n'implémente pas IAMVideoProcAmp : matériel trop basique.
            }
            catch (COMException)
            {
                // QueryInterface a échoué côté pilote.
            }

            return null;
        }

        public bool TryGetRange(VideoProcAmpProperty property, out VideoProcAmpRange range)
        {
            range = default;
            int hr = _procAmp.GetRange(property, out int min, out int max, out int step, out int def, out var flags);
            if (hr < 0) return false;

            range = new VideoProcAmpRange { Min = min, Max = max, Step = step, Default = def, Flags = flags };
            return true;
        }

        public bool TrySet(VideoProcAmpProperty property, int value)
        {
            int hr = _procAmp.Set(property, value, VideoProcAmpFlags.Manual);
            return hr >= 0;
        }

        public bool TryGet(VideoProcAmpProperty property, out int value)
        {
            int hr = _procAmp.Get(property, out value, out _);
            return hr >= 0;
        }

        public void Dispose()
        {
            if (_procAmp != null && Marshal.IsComObject(_procAmp))
                Marshal.ReleaseComObject(_procAmp);
        }
    }
}