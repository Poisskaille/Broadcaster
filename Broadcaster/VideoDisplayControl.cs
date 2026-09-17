using System;
using System.Runtime.InteropServices;
using SharpDX.MediaFoundation;

namespace Broadcaster
{
    [StructLayout(LayoutKind.Sequential)]
    internal struct MFRect
    {
        public int Left;
        public int Top;
        public int Right;
        public int Bottom;
    }

    /// <summary>
    /// Traduction C# partielle de IMFVideoDisplayControl (evr.h). On ne déclare que
    /// les méthodes jusqu'à SetVideoPosition (celle qu'on utilise réellement) : dans une
    /// interface COM basée sur un vtable, l'ordre compte, mais on peut s'arrêter dès
    /// qu'on a couvert la dernière méthode dont on a besoin.
    /// </summary>
    [ComImport]
    [Guid("A490B1E4-AB84-4D31-A1B2-181E03B1077A")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    internal interface IMFVideoDisplayControl
    {
        [PreserveSig] int GetNativeVideoSize(IntPtr pszVideo, IntPtr pszARVideo);
        [PreserveSig] int GetIdealVideoSize(IntPtr pszMin, IntPtr pszMax);
        [PreserveSig] int SetVideoPosition(IntPtr pnrcSource, ref MFRect prcDest);
    }

    /// <summary>
    /// Pilote le rectangle de destination du rendu vidéo EVR. Sans appel explicite,
    /// l'EVR garde la taille de fenêtre qu'il avait au moment où le flux a démarré et
    /// ne suit JAMAIS les redimensionnements suivants — ce n'est pas automatique côté
    /// Media Foundation, contrairement à ce qu'on pourrait attendre.
    /// </summary>
    internal class VideoDisplayPositionControl : IDisposable
    {
        private static readonly Guid MR_VIDEO_RENDER_SERVICE = new Guid("1092A86C-AB1A-459A-A336-831FBC4D11FF");
        private static readonly Guid IID_IMFVideoDisplayControl = new Guid("A490B1E4-AB84-4D31-A1B2-181E03B1077A");

        [DllImport("mf.dll", ExactSpelling = true)]
        private static extern int MFGetService(IntPtr punkObject, ref Guid guidService, ref Guid riid, out IntPtr ppvObject);

        private readonly IMFVideoDisplayControl _control;

        private VideoDisplayPositionControl(IMFVideoDisplayControl control)
        {
            _control = control;
        }

        /// <summary>
        /// À appeler une fois la session vidéo VRAIMENT démarrée (évènement SessionStarted) —
        /// avant, le service de rendu n'est pas garanti disponible, même principe que pour
        /// IAMVideoProcAmp.
        /// </summary>
        public static VideoDisplayPositionControl TryCreate(MediaSession videoSession)
        {
            if (videoSession == null) return null;

            IntPtr rawPointer = IntPtr.Zero;
            try
            {
                var serviceGuid = MR_VIDEO_RENDER_SERVICE;
                var interfaceGuid = IID_IMFVideoDisplayControl;

                int hr = MFGetService(videoSession.NativePointer, ref serviceGuid, ref interfaceGuid, out rawPointer);
                if (hr < 0 || rawPointer == IntPtr.Zero) return null;

                if (Marshal.GetObjectForIUnknown(rawPointer) is IMFVideoDisplayControl control)
                    return new VideoDisplayPositionControl(control);

                return null;
            }
            catch (COMException)
            {
                return null;
            }
            finally
            {
                // GetObjectForIUnknown a fait son propre AddRef interne pour le RCW .NET ;
                // on relâche donc le pointeur brut reçu de MFGetService pour ne pas fuiter.
                if (rawPointer != IntPtr.Zero)
                    Marshal.Release(rawPointer);
            }
        }

        public void Resize(int width, int height)
        {
            if (_control == null || width <= 0 || height <= 0) return;

            var rect = new MFRect { Left = 0, Top = 0, Right = width, Bottom = height };
            try { _control.SetVideoPosition(IntPtr.Zero, ref rect); } catch { }
        }

        public void Dispose()
        {
            if (_control != null && Marshal.IsComObject(_control))
                Marshal.ReleaseComObject(_control);
        }
    }
}
