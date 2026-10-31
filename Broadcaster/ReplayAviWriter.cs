using System;
using System.Collections.Generic;
using System.IO;

namespace Broadcaster
{
    /// <summary>
    /// AVI 1.0 de séquences JPEG indépendantes + audio PCM 16 bits stéréo.
    /// Aucune dépendance externe (pas de FFmpeg ou de codec propriétaire).
    /// Le clip est construit sur un thread séparé à partir d'une copie du tampon circulaire.
    /// </summary>
    internal static class ReplayAviWriter
    {
        private const int PcmBlockAlign = 4; // deux canaux 16 bits

        private struct ChunkIndex
        {
            internal string Id;
            internal int Flags;
            internal uint Offset;
            internal uint Length;
        }

        public static void Write(string path, List<ReplayVideoFrame> source,
            List<ReplayAudioChunk> sourceAudio, int sampleRate,
            int width, int height, int fps, long maxFileBytes)
        {
            if (width < 160 || height < 120 || width > 3840 || height > 2160 ||
                fps < 10 || fps > 60)
                throw new ArgumentException("Paramètres de vidéo invalides.");
            if (source == null || source.Count < 1) throw new InvalidOperationException("Tampon vidéo vide.");

            // Images sources horodatées. Un rééchantillonnage au plus proche
            // minimise les doublons dus au décalage entre capture et grille 60 FPS.
            // Des images restent répétées si le matériel/capture produit réellement
            // moins d'images distinctes que le FPS demandé.
            DateTime start = source[0].Time;
            DateTime end = source[source.Count - 1].Time.AddSeconds(1.0 / fps);
            int frameCount = Math.Max(1, (int)Math.Ceiling((end - start).TotalSeconds * fps));
            frameCount = Math.Min(frameCount, 120 * fps + fps);
            byte[][] images = new byte[frameCount][];
            int cursor = 0;
            int maximumImageBytes = 0;
            for (int i = 0; i < frameCount; i++)
            {
                DateTime step = start.AddSeconds(i / (double)fps);
                while (cursor + 1 < source.Count && source[cursor + 1].Time <= step)
                    cursor++;
                int nearest = cursor;
                if (nearest + 1 < source.Count &&
                    (source[nearest + 1].Time - step).Duration() <
                    (step - source[nearest].Time).Duration())
                    nearest++;
                byte[] jpeg = source[nearest].Jpeg;
                images[i] = jpeg;
                maximumImageBytes = Math.Max(maximumImageBytes, jpeg.Length);
            }

            bool audioAvailable = sampleRate >= 8000 && sampleRate <= 192000 &&
                                  sourceAudio != null && sourceAudio.Count > 0;
            int totalAudioSamples = audioAvailable
                ? (int)Math.Round(frameCount * (double)sampleRate / fps) : 0;
            byte[] alignedPcm = audioAvailable
                ? new byte[totalAudioSamples * PcmBlockAlign] : null;

            // Les chunks WASAPI sont datés en UTC ; on remplit les trous avec du silence.
            if (audioAvailable)
            {
                foreach (ReplayAudioChunk chunk in sourceAudio)
                {
                    int atSample = (int)Math.Round((chunk.Start - start).TotalSeconds * sampleRate);
                    int fromSample = Math.Max(0, -atSample);
                    int toSample = Math.Max(0, atSample);
                    int samples = Math.Min(chunk.Pcm.Length / PcmBlockAlign - fromSample,
                                           totalAudioSamples - toSample);
                    if (samples > 0)
                        Buffer.BlockCopy(chunk.Pcm, fromSample * PcmBlockAlign,
                            alignedPcm, toSample * PcmBlockAlign, samples * PcmBlockAlign);
                }
            }

            // AVI 1.0 utilise des tailles/offsets 32 bits : ne pas créer un fichier
            // corrompu quand une longue capture 4K/60 FPS dépasse cette limite.
            // Les JPEG référencés plusieurs fois comptent à chaque image écrite.
            long projectedVideoBytes = 0;
            foreach (byte[] jpg in images)
                projectedVideoBytes += 8L + jpg.Length + (jpg.Length & 1);
            long projectedAudioBytes = audioAvailable
                ? (long)alignedPcm.Length + (long)frameCount * 8 + 8 : 0;
            long projectedIndexBytes = (long)frameCount * (audioAvailable ? 2 : 1) * 16;
            if (projectedVideoBytes + projectedAudioBytes + projectedIndexBytes + 8192 > maxFileBytes)
                throw new IOException("Clip trop volumineux pour le format AVI. " +
                    "Réduis le FPS cible, la résolution de la source ou la durée du clip dans les options.");

            using (var file = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            using (var writer = new BinaryWriter(file))
            {
                var indexes = new List<ChunkIndex>(frameCount * (audioAvailable ? 2 : 1));
                long riff = BeginRiff(writer, "AVI ");
                long hdrl = BeginList(writer, "hdrl");

                // Main AVI header (56 bytes)
                WriteFourCC(writer, "avih");
                writer.Write(56u);
                writer.Write((uint)(1000000 / fps));
                writer.Write((uint)(maximumImageBytes * fps + (audioAvailable ? sampleRate * PcmBlockAlign : 0)));
                writer.Write(0u); // padding
                writer.Write(0x10u); // AVIF_HASINDEX
                writer.Write((uint)frameCount);
                writer.Write(0u); // initial frames
                writer.Write((uint)(audioAvailable ? 2 : 1));
                writer.Write((uint)maximumImageBytes);
                writer.Write((uint)width);
                writer.Write((uint)height);
                writer.Write(0u); writer.Write(0u); writer.Write(0u); writer.Write(0u);

                long videoStrl = BeginList(writer, "strl");
                WriteFourCC(writer, "strh");
                writer.Write(56u);
                WriteFourCC(writer, "vids");
                WriteFourCC(writer, "MJPG");
                writer.Write(0u); // flags
                writer.Write((ushort)0); writer.Write((ushort)0); // priority + language
                writer.Write(0u); // initial frames
                writer.Write(1u); // scale
                writer.Write((uint)fps); // rate
                writer.Write(0u); // start
                writer.Write((uint)frameCount);
                writer.Write((uint)maximumImageBytes);
                writer.Write(0xFFFFFFFFu); // quality = default
                writer.Write(0u); // sampleSize (variable-size MJPEG)
                writer.Write((short)0); writer.Write((short)0);
                writer.Write((short)width); writer.Write((short)height);

                WriteFourCC(writer, "strf");
                writer.Write(40u);
                writer.Write(40u); // BITMAPINFOHEADER.biSize
                writer.Write(width);
                writer.Write(height);
                writer.Write((ushort)1);
                writer.Write((ushort)24);
                WriteFourCC(writer, "MJPG");
                writer.Write((uint)(width * height * 3));
                writer.Write(0); writer.Write(0); writer.Write(0u); writer.Write(0u);
                EndList(writer, videoStrl);

                if (audioAvailable)
                {
                    long audioStrl = BeginList(writer, "strl");
                    WriteFourCC(writer, "strh");
                    writer.Write(56u);
                    WriteFourCC(writer, "auds");
                    writer.Write(0u); // handler
                    writer.Write(0u); // flags
                    writer.Write((ushort)0); writer.Write((ushort)0);
                    writer.Write(0u); // initial frames
                    writer.Write((uint)PcmBlockAlign); // dwScale
                    writer.Write((uint)(sampleRate * PcmBlockAlign)); // dwRate
                    writer.Write(0u);
                    writer.Write((uint)totalAudioSamples); // dwLength
                    writer.Write((uint)(sampleRate * PcmBlockAlign / fps));
                    writer.Write(0xFFFFFFFFu); // default quality
                    writer.Write((uint)PcmBlockAlign); // fixed sample size
                    writer.Write((short)0); writer.Write((short)0);
                    writer.Write((short)0); writer.Write((short)0);
                    WriteFourCC(writer, "strf");
                    writer.Write(16u);
                    writer.Write((ushort)1); // WAVE_FORMAT_PCM
                    writer.Write((ushort)2); // stéréo
                    writer.Write((uint)sampleRate);
                    writer.Write((uint)(sampleRate * PcmBlockAlign));
                    writer.Write((ushort)PcmBlockAlign);
                    writer.Write((ushort)16);
                    EndList(writer, audioStrl);
                }
                EndList(writer, hdrl);

                long movi = BeginList(writer, "movi");
                // Index AVI traditionnel : offset depuis le début du contenu LIST movi
                // en comptant les 4 octets « movi » (le premier bloc vaut donc 4).
                long moviTypePosition = movi + 8;

                for (int i = 0; i < frameCount; i++)
                {
                    WriteChunk(writer, indexes, moviTypePosition, "00dc", images[i], 0,
                        images[i].Length, 0x10); // AVIIF_KEYFRAME

                    if (audioAvailable)
                    {
                        int fromSample = (int)Math.Round(i * (double)sampleRate / fps);
                        int toSample = (int)Math.Round((i + 1) * (double)sampleRate / fps);
                        int count = Math.Max(0, toSample - fromSample) * PcmBlockAlign;
                        WriteChunk(writer, indexes, moviTypePosition, "01wb", alignedPcm,
                            fromSample * PcmBlockAlign, count, 0);
                    }
                }
                EndList(writer, movi);

                WriteFourCC(writer, "idx1");
                writer.Write(checked((uint)indexes.Count * 16u));
                foreach (ChunkIndex index in indexes)
                {
                    WriteFourCC(writer, index.Id);
                    writer.Write(index.Flags);
                    writer.Write(index.Offset);
                    writer.Write(index.Length);
                }
                EndList(writer, riff);
                writer.Flush();
            }
        }

        private static long BeginRiff(BinaryWriter w, string type)
        {
            long pos = w.BaseStream.Position;
            WriteFourCC(w, "RIFF"); w.Write(0u); WriteFourCC(w, type);
            return pos;
        }
        private static long BeginList(BinaryWriter w, string type)
        {
            long pos = w.BaseStream.Position;
            WriteFourCC(w, "LIST"); w.Write(0u); WriteFourCC(w, type);
            return pos;
        }
        private static void EndList(BinaryWriter w, long start)
        {
            long end = w.BaseStream.Position;
            // La longueur d'un RIFF/LIST exclut les huit octets de l'en-tête.
            w.BaseStream.Position = start + 4;
            w.Write(checked((uint)(end - start - 8)));
            w.BaseStream.Position = end;
            if ((end - start) % 2 != 0) w.Write((byte)0);
        }
        private static void WriteChunk(BinaryWriter w, List<ChunkIndex> index, long moviTypePosition,
            string id, byte[] buffer, int offset, int count, int flags)
        {
            long position = w.BaseStream.Position;
            WriteFourCC(w, id);
            w.Write((uint)count);
            w.Write(buffer, offset, count);
            if ((count & 1) != 0) w.Write((byte)0);
            index.Add(new ChunkIndex
            {
                Id = id, Flags = flags, Offset = checked((uint)(position - moviTypePosition)),
                Length = (uint)count
            });
        }
        private static void WriteFourCC(BinaryWriter w, string fourcc)
        {
            if (fourcc.Length != 4) throw new ArgumentException("FourCC incorrect : " + fourcc);
            foreach (char c in fourcc) w.Write((byte)c);
        }
    }
}
