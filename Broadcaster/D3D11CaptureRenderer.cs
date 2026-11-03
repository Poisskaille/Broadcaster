using System;
using System.Runtime.InteropServices;
using System.Threading;
using SharpDX;
using SharpDX.D3DCompiler;
using SharpDX.Direct3D;
using SharpDX.Direct3D11;
using SharpDX.DXGI;
using SharpDX.MediaFoundation;
using D3DDevice = SharpDX.Direct3D11.Device;
using D3DBuffer = SharpDX.Direct3D11.Buffer;
using D3DResourceUsage = SharpDX.Direct3D11.ResourceUsage;
using DxgiFormat = SharpDX.DXGI.Format;

namespace Broadcaster
{
    // IMFMediaBuffer::Lock. L'interface COM évite de dépendre des surcharges
    // particulières de SharpDX sur les versions de Windows.
    [ComImport, Guid("045FA593-8799-42B8-BC8D-8968C6453507"),
     InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    internal interface IMFLockedMediaBuffer
    {
        [PreserveSig] int Lock(out IntPtr data, out int maxLength, out int currentLength);
        [PreserveSig] int Unlock();
        [PreserveSig] int GetCurrentLength(out int length);
        [PreserveSig] int SetCurrentLength(int length);
        [PreserveSig] int GetMaxLength(out int length);
    }

    /// <summary>
    /// Rendu GPU alternatif au renderer Media Foundation/EVR.
    /// SourceReader décode MJPEG si nécessaire; les samples NV12 / YUY2 sont
    /// convertis YUV -> RGB directement par un shader Direct3D11, AVANT sharpen.
    /// Le chemin EVR original reste utilisé pour les paramètres neutres.
    /// </summary>
    internal sealed class D3D11CaptureRenderer : IDisposable
    {
        private readonly MediaSource _source;
        private readonly IntPtr _hwnd;
        private readonly ColorProfile _profile;
        private readonly double _requestedSourceFps;
        private readonly bool _previewVsync;
        // When the user requests a pixel format explicitly, SourceReader is not
        // allowed to renegotiate a different native format without an error.
        private readonly string _forcedNativeFormat;
        private readonly Action _onReady;
        private readonly Action<string> _onError;
        private Thread _thread;
        private volatile bool _running;
        private volatile int _windowWidth = 1280;
        private volatile int _windowHeight = 720;

        // Objets MF et D3D11 confinés au thread de capture.
        private SourceReader _reader;
        private D3DDevice _device;
        private SwapChain _swapChain;
        private RenderTargetView _rtv;
        // Conversion YUV/RGB sur une surface GPU intermédiaire. La netteté
        // échantillonne ensuite du RGB via le filtrage bilinéaire matériel.
        private Texture2D _convertedTexture;
        private ShaderResourceView _convertedSrv;
        private RenderTargetView _convertedRtv;
        private PixelShader _convertShader;
        // Filtres post-traitement : facultatifs, jamais appliqués au profil par défaut.
        private PixelShader _fxaaShader;
        private PixelShader _smaaEdgeShader, _smaaWeightShader, _smaaBlendShader;
        private Texture2D _aaTexture, _edgeTexture, _weightTexture;
        private RenderTargetView _aaRtv, _edgeRtv, _weightRtv;
        private ShaderResourceView _aaSrv, _edgeSrv, _weightSrv;
        // L'anti-aliasing ne modifie ni les FPS négociés ni le flux capturé.
        private readonly string _antiAliasingMode;
        private readonly int _chromaMode; // 0 = standard, 1 = bicubic, 2 = adaptatif
        private readonly float _debandAmount; // 0..1, zéro = aucun traitement GPU supplémentaire
        // Trois surfaces d'upload évitent de réécrire une texture encore lue
        // par le GPU au moment d'une nouvelle image HDMI à 60 FPS.
        private readonly Texture2D[] _uploadTextures = new Texture2D[3];
        private readonly ShaderResourceView[] _uploadViews = new ShaderResourceView[3];
        private int _uploadIndex = -1;
        private VertexShader _vertexShader;
        private PixelShader _pixelShader;
        private SamplerState _sampler;
        private D3DBuffer _paramsBuffer;
        private byte[] _frameBuffer;
        private int _sourceWidth, _sourceHeight, _inputMode;
        private int _surfaceWidth, _surfaceHeight;
        private int _matrix, _range;
        private bool _flipRgbRows;
        private volatile int _measuredFpsTenths;
        public double MeasuredFps => _measuredFpsTenths / 10.0;
        // Comptage au point de réception Media Foundation, AVANT conversion GPU/Present.
        // Ne pas confondre avec le rafraîchissement du moniteur ou les FPS du jeu.
        private long _receivedSamples;
        private long _presentedSamples;
        public long ReceivedSamples => Interlocked.Read(ref _receivedSamples);
        public long PresentedSamples => Interlocked.Read(ref _presentedSamples);

        private const string Hlsl = @"
            Texture2D<float4> Frame : register(t0);
            Texture2D<float4> Converted : register(t1);
            Texture2D<float4> Edges : register(t2);
            Texture2D<float4> Weights : register(t3);
            SamplerState LinearSample : register(s0);
            cbuffer CaptureOptions : register(b0)
            {
                float4 Dimensions; // source width,height, destination width,height
                float4 Settings;   // sharpen, input mode, matrix (709/601), range (limited/full)
                float4 PostOptions; // chroma reconstruction mode, debanding strength, reserved, reserved
            };
            struct VertexOut { float4 position : SV_Position; float2 uv : TEXCOORD0; };
            VertexOut VS(uint id : SV_VertexID)
            {
                float2 uv = float2((id << 1) & 2, id & 2);
                VertexOut output;
                output.uv = uv;
                output.position = float4(uv * float2(2, -2) + float2(-1, 1), 0, 1);
                return output;
            }
            float3 DecodeYuv(float3 yuv)
            {
                bool tenBit = Settings.y > 3.5; // P010 stores 10 significant bits left-aligned
                float neutral = tenBit ? (512.0/1023.0) : (128.0/255.0);
                float y = yuv.x, u = yuv.y - neutral, v = yuv.z - neutral;
                if (Settings.w < 0.5) // vidéo limitée
                {
                    y = tenBit ? (y - 64.0/1023.0)*(1023.0/876.0) :
                                 (y - 16.0/255.0)*(255.0/219.0);
                    float chromaScale = tenBit ? 1023.0/896.0 : 255.0/224.0;
                    u *= chromaScale;
                    v *= chromaScale;
                }
                if (Settings.z < 0.5) // BT.709
                    return saturate(float3(y + 1.5748*v,
                                           y - 0.187324*u - 0.468124*v,
                                           y + 1.8556*u));
                return saturate(float3(y + 1.402*v,
                                       y - 0.344136*u - 0.714136*v,
                                       y + 1.772*u)); // BT.601
            }
            // Les échantillons chroma NV12 sont en 4:2:0. Interpoler les UV
            // entre blocs évite les marches de couleur de 2x2 pixels sans
            // prétendre recréer les détails chroma perdus par la source.
            float2 Nv12Chroma(int2 c, int2 size)
            {
                c = clamp(c, int2(0,0), size/2 - 1);
                int2 uv = int2(c.x*2, size.y + c.y);
                return float2(Frame.Load(int3(uv,0)).r,
                              Frame.Load(int3(uv + int2(1,0),0)).r);
            }
            // Catmull-Rom interpolation with local clamping to prevent chroma halos.
            float4 CubicWeights(float t)
            {
                float t2 = t*t, t3 = t2*t;
                return float4(-0.5*t + t2 - 0.5*t3,
                              1.0 - 2.5*t2 + 1.5*t3,
                              0.5*t + 2.0*t2 - 1.5*t3,
                              -0.5*t2 + 0.5*t3);
            }
            float2 AdvancedNv12Chroma(int2 p, int2 size)
            {
                float2 pos = (float2(p) - 0.5) * 0.5;
                int2 baseC = int2(floor(pos));
                float2 t = frac(pos);
                float2 c00 = Nv12Chroma(baseC, size);
                float2 c10 = Nv12Chroma(baseC + int2(1,0), size);
                float2 c01 = Nv12Chroma(baseC + int2(0,1), size);
                float2 c11 = Nv12Chroma(baseC + int2(1,1), size);
                float2 linearC = lerp(lerp(c00,c10,t.x),lerp(c01,c11,t.x),t.y);
                float4 wx = CubicWeights(t.x), wy = CubicWeights(t.y);
                float2 cubicC = float2(0,0);
                [unroll] for(int j=0; j<4; ++j)
                {
                    float2 rowC = float2(0,0);
                    [unroll] for(int i=0; i<4; ++i)
                    {
                        rowC += Nv12Chroma(baseC + int2(i-1,j-1), size) * wx[i];
                    }
                    cubicC += rowC * wy[j];
                }
                // Limit overshoot to the nearest 2x2 chroma samples.
                float2 lo = min(min(c00,c10),min(c01,c11));
                float2 hi = max(max(c00,c10),max(c01,c11));
                cubicC = clamp(cubicC, lo, hi);
                if(PostOptions.x > 1.5)
                {
                    float2 edge = max(abs(c00-c11),abs(c10-c01));
                    float sensitivity = max(edge.x,edge.y);
                    cubicC = lerp(cubicC,linearC,saturate((sensitivity-0.035)*10.0));
                }
                return cubicC;
            }
            float2 PackedChroma(int x, int y, int pairCount)
            {
                float4 sampleC = Frame.Load(int3(clamp(x,0,pairCount-1),y,0));
                return Settings.y < 1.5 ? sampleC.ga : sampleC.rb;
            }
            float2 AdvancedPackedChroma(int2 p, int2 size)
            {
                int pairs = size.x/2;
                float pos = (float(p.x)-0.5)*0.5;
                int baseC = (int)floor(pos);
                float t = frac(pos);
                float2 c0 = PackedChroma(baseC, p.y, pairs);
                float2 c1 = PackedChroma(baseC+1, p.y, pairs);
                float2 linearC = lerp(c0,c1,t);
                float4 w = CubicWeights(t);
                float2 cubicC = PackedChroma(baseC-1,p.y,pairs)*w.x +
                                c0*w.y + c1*w.z +
                                PackedChroma(baseC+2,p.y,pairs)*w.w;
                cubicC = clamp(cubicC, min(c0,c1), max(c0,c1));
                if(PostOptions.x > 1.5)
                {
                    float2 d = abs(c0-c1);
                    cubicC = lerp(cubicC,linearC,saturate((max(d.x,d.y)-0.035)*10.0));
                }
                return cubicC;
            }
            float3 Pixel(int2 coordinate)
            {
                int2 size = int2(Dimensions.xy);
                int2 p = clamp(coordinate, int2(0,0), size-1);
                if (Settings.y < 0.5 || Settings.y > 3.5) // NV12(R8) / P010(R16), Y puis UV
                {
                    float y = Frame.Load(int3(p,0)).r;
                    float2 uv;
                    if (PostOptions.x > 0.5)
                    {
                        uv = AdvancedNv12Chroma(p, size);
                    }
                    else
                    {
                        // Exact historical bilinear reconstruction in Standard mode.
                        float2 chromaPos = (float2(p) - 0.5) * 0.5;
                        int2 baseC = int2(floor(chromaPos));
                        float2 t = frac(chromaPos);
                        float2 top = lerp(Nv12Chroma(baseC, size),
                                           Nv12Chroma(baseC + int2(1,0), size), t.x);
                        float2 bottom = lerp(Nv12Chroma(baseC + int2(0,1), size),
                                              Nv12Chroma(baseC + int2(1,1), size), t.x);
                        uv = lerp(top, bottom, t.y);
                    }
                    return DecodeYuv(float3(y,uv.x,uv.y));
                }
                if (Settings.y < 1.5) // YUY2: Y0 U Y1 V
                {
                    float4 pair = Frame.Load(int3(p.x/2,p.y,0));
                    float2 chroma = PostOptions.x > 0.5 ? AdvancedPackedChroma(p,size) : pair.ga;
                    return DecodeYuv(float3((p.x & 1) == 0 ? pair.r : pair.b, chroma.x, chroma.y));
                }
                if (Settings.y < 2.5) // RGB32/BGRA déjà décodé par MF
                    return Frame.Load(int3(p,0)).rgb;
                if (Settings.y < 3.5) // UYVY: U Y0 V Y1
                {
                    float4 pair = Frame.Load(int3(p.x/2,p.y,0));
                    float2 chroma = PostOptions.x > 0.5 ? AdvancedPackedChroma(p,size) : pair.rb;
                    return DecodeYuv(float3((p.x & 1) == 0 ? pair.g : pair.a, chroma.x, chroma.y));
                }
                return float3(0,0,0);
            }
            float4 ConvertPS(VertexOut input) : SV_Target
            {
                int2 p = clamp(int2(input.uv * Dimensions.xy), int2(0,0), int2(Dimensions.xy)-1);
                return float4(Pixel(p), 1);
            }
            // Edge-aware debanding on the final RGB image. No extra texture / GPU pass.
            // Only smooth nearly-flat gradients, then use sub-LSB spatial dithering.
            float3 Deband(float2 uv, float3 c)
            {
                float2 px = 1.0 / Dimensions.xy;
                float3 l = Converted.Sample(LinearSample, uv - float2(px.x*2,0)).rgb;
                float3 r = Converted.Sample(LinearSample, uv + float2(px.x*2,0)).rgb;
                float3 t = Converted.Sample(LinearSample, uv - float2(0,px.y*2)).rgb;
                float3 b = Converted.Sample(LinearSample, uv + float2(0,px.y*2)).rgb;
                float3 d = max(max(abs(c-l),abs(c-r)),max(abs(c-t),abs(c-b)));
                float edge = max(max(d.r,d.g),d.b);
                float threshold = lerp(3.0/255.0,14.0/255.0,PostOptions.y);
                float flatWeight = saturate((threshold-edge) / max(threshold*0.65,0.00001));
                float3 average = (c*2.0+l+r+t+b)/6.0;
                c = lerp(c, average, flatWeight * PostOptions.y * 0.80);
                // Static interleaved noise: avoids temporal noise and color shifts.
                float2 pixel = floor(uv*Dimensions.xy);
                float noise = frac(52.9829189 * frac(dot(pixel,float2(0.06711056,0.00583715))));
                c += (noise-0.5) * (0.85/255.0) * PostOptions.y;
                return saturate(c);
            }
            float4 PS(VertexOut input) : SV_Target
            {
                float2 texel = 1.0 / Dimensions.xy;
                float3 c = Converted.Sample(LinearSample, input.uv).rgb;
                if (abs(Settings.x) > 0.001)
                {
                    float3 surround = (Converted.Sample(LinearSample, input.uv + float2(texel.x,0)).rgb +
                                       Converted.Sample(LinearSample, input.uv - float2(texel.x,0)).rgb +
                                       Converted.Sample(LinearSample, input.uv + float2(0,texel.y)).rgb +
                                       Converted.Sample(LinearSample, input.uv - float2(0,texel.y)).rgb) * 0.25;
                    if (Settings.x < 0.0)
                        c = lerp(c, surround, saturate(-Settings.x));
                    else
                        c = saturate(c + clamp(c-surround,-0.18,0.18)*Settings.x);
                }
                if (PostOptions.y > 0.001)
                    c = Deband(input.uv, c);
                return float4(c,1);
            }";

        // FXAA: filtre directionnel inspiré de FXAA 3.x (luma 3x3 + gradient).
        // SMAA: variante spatiale morphologique à 3 passes (détection des bords,
        // recherche/poids locaux, fusion), sans les LUT Area/Search du SMAA de référence.
        // Aucun historique d'images n'est utilisé : latence limitée à la trame actuelle.
        private const string AntiAliasingHlsl = @"
            float LumaAA(float3 v) { return dot(v, float3(0.299, 0.587, 0.114)); }
            float3 SampleRgbAA(float2 uv) { return Converted.SampleLevel(LinearSample, uv, 0).rgb; }
            float4 FxaaPS(VertexOut input) : SV_Target
            {
                float2 uv = input.uv;
                float2 px = 1.0 / Dimensions.xy;
                float3 rgbM = SampleRgbAA(uv);
                float3 rgbNW = SampleRgbAA(uv + px * float2(-1, -1));
                float3 rgbNE = SampleRgbAA(uv + px * float2( 1, -1));
                float3 rgbSW = SampleRgbAA(uv + px * float2(-1,  1));
                float3 rgbSE = SampleRgbAA(uv + px * float2( 1,  1));
                float lumaM = LumaAA(rgbM);
                float lumaNW = LumaAA(rgbNW);
                float lumaNE = LumaAA(rgbNE);
                float lumaSW = LumaAA(rgbSW);
                float lumaSE = LumaAA(rgbSE);
                float lumaMin = min(lumaM, min(min(lumaNW, lumaNE), min(lumaSW, lumaSE)));
                float lumaMax = max(lumaM, max(max(lumaNW, lumaNE), max(lumaSW, lumaSE)));
                if (lumaMax - lumaMin < max(0.0312, 0.125 * lumaMax))
                    return float4(rgbM, 1);
                float2 dir;
                dir.x = -((lumaNW + lumaNE) - (lumaSW + lumaSE));
                dir.y = ((lumaNW + lumaSW) - (lumaNE + lumaSE));
                float dirReduce = max((lumaNW + lumaNE + lumaSW + lumaSE) * (0.25 * 0.0312), 1.0/128.0);
                float rcpDirMin = 1.0 / (min(abs(dir.x), abs(dir.y)) + dirReduce);
                dir = clamp(dir * rcpDirMin, -8.0, 8.0) * px;
                float3 a = 0.5 * (SampleRgbAA(uv + dir * (-1.0/6.0)) +
                                   SampleRgbAA(uv + dir * ( 1.0/6.0)));
                float3 b = a * 0.5 + 0.25 * (SampleRgbAA(uv + dir * (-0.5)) +
                                             SampleRgbAA(uv + dir * ( 0.5)));
                float lumaB = LumaAA(b);
                return float4((lumaB < lumaMin || lumaB > lumaMax) ? a : b, 1);
            }
            // 1. Détection local-contraste de deux orientations.
            float4 SmaaEdgePS(VertexOut input) : SV_Target
            {
                float2 px = 1.0 / Dimensions.xy;
                float2 uv = input.uv;
                float c = LumaAA(SampleRgbAA(uv));
                float l = LumaAA(SampleRgbAA(uv - float2(px.x, 0)));
                float t = LumaAA(SampleRgbAA(uv - float2(0, px.y)));
                float r = LumaAA(SampleRgbAA(uv + float2(px.x, 0)));
                float b = LumaAA(SampleRgbAA(uv + float2(0, px.y)));
                float2 delta = abs(float2(c-l, c-t));
                float local = max(max(delta.x, delta.y), max(abs(c-r), abs(c-b)));
                float2 edges = step(float2(0.075, 0.075), delta);
                edges *= step(float2(local * 0.5, local * 0.5), delta);
                return float4(edges, 0, 1);
            }
            float2 SampleEdgeAA(float2 uv)
            {
                return Edges.SampleLevel(LinearSample, uv, 0).rg;
            }
            // 2. Recherche spatiale des extrémités du contour ; une longue ligne
            // droite reste préservée et les transitions en escalier sont lissées.
            float4 SmaaWeightPS(VertexOut input) : SV_Target
            {
                float2 uv = input.uv;
                float2 px = 1.0 / Dimensions.xy;
                float2 e = SampleEdgeAA(uv);
                float right = SampleEdgeAA(uv + float2(px.x, 0)).r;
                float down = SampleEdgeAA(uv + float2(0, px.y)).g;
                float vertical = max(e.r, right);
                float horizontal = max(e.g, down);
                if (vertical < 0.3 && horizontal < 0.3) return float4(0,0,0,0);
                float vUp = 0, vDown = 0, hLeft = 0, hRight = 0;
                // Première optimisation : les lignes droites continues ne sont
                // pas des marches et ne nécessitent pas de filtrage.
                float2 prevV = SampleEdgeAA(uv + float2(0, -px.y));
                float2 nextV = SampleEdgeAA(uv + float2(0, px.y));
                float2 prevH = SampleEdgeAA(uv + float2(-px.x, 0));
                float2 nextH = SampleEdgeAA(uv + float2(px.x, 0));
                if (vertical > 0.5 && horizontal < 0.3 && prevV.r > 0.5 && nextV.r > 0.5)
                    return float4(0,0,0,0);
                if (horizontal > 0.5 && vertical < 0.3 && prevH.g > 0.5 && nextH.g > 0.5)
                    return float4(0,0,0,0);

                // Recherche bornée avec arrêt précoce : pas de lecture sur
                // les 8 texels si le contour s'interrompt dès le voisin proche.
                [loop] for (int i = 1; i <= 8; ++i)
                {
                    if (SampleEdgeAA(uv + float2(0, -i * px.y)).r < 0.45) break;
                    vUp += 1;
                }
                [loop] for (int i = 1; i <= 8; ++i)
                {
                    if (SampleEdgeAA(uv + float2(0, i * px.y)).r < 0.45) break;
                    vDown += 1;
                }
                [loop] for (int i = 1; i <= 8; ++i)
                {
                    if (SampleEdgeAA(uv + float2(-i * px.x, 0)).g < 0.45) break;
                    hLeft += 1;
                }
                [loop] for (int i = 1; i <= 8; ++i)
                {
                    if (SampleEdgeAA(uv + float2(i * px.x, 0)).g < 0.45) break;
                    hRight += 1;
                }
                float shapeV = saturate((abs(vUp-vDown) + 1) / (vUp+vDown+2));
                float shapeH = saturate((abs(hLeft-hRight) + 1) / (hLeft+hRight+2));
                float wV = vertical * (0.26 * shapeV);
                float wH = horizontal * (0.26 * shapeH);
                // RGBA = mélange gauche, droite, haut, bas.
                return float4(wV * e.r, wV * right, wH * e.g, wH * down);
            }
            // 3. Fusion morphologique des voisins seulement aux zones détectées.
            float4 SmaaBlendPS(VertexOut input) : SV_Target
            {
                float2 uv = input.uv;
                float2 px = 1.0 / Dimensions.xy;
                float3 center = SampleRgbAA(uv);
                float4 w = Weights.SampleLevel(LinearSample, uv, 0);
                float total = dot(w, float4(1,1,1,1));
                if (total < 0.001) return float4(center, 1);
                float3 mixColor = center * max(0.0, 1.0-total);
                mixColor += SampleRgbAA(uv - float2(px.x, 0)) * w.x;
                mixColor += SampleRgbAA(uv + float2(px.x, 0)) * w.y;
                mixColor += SampleRgbAA(uv - float2(0, px.y)) * w.z;
                mixColor += SampleRgbAA(uv + float2(0, px.y)) * w.w;
                return float4(saturate(mixColor),1);
            }";

        public D3D11CaptureRenderer(MediaSource source, IntPtr hwnd, ColorProfile profile, double sourceFps, bool previewVsync,
            Action onReady, Action<string> onError)
        {
            _source = source ?? throw new ArgumentNullException(nameof(source));
            _hwnd = hwnd;
            _profile = profile ?? throw new ArgumentNullException(nameof(profile));
            _forcedNativeFormat = string.IsNullOrWhiteSpace(profile.SourceFormat) ||
                string.Equals(profile.SourceFormat, "Auto", StringComparison.OrdinalIgnoreCase)
                ? null : profile.SourceFormat.Trim();
            _antiAliasingMode = string.Equals(profile.AntiAliasingMode, "FXAA", StringComparison.OrdinalIgnoreCase) ? "FXAA" :
                string.Equals(profile.AntiAliasingMode, "SMAA", StringComparison.OrdinalIgnoreCase) ? "SMAA" : "Off";
            _chromaMode = string.Equals(profile.ChromaUpsamplingMode, "Bicubic", StringComparison.OrdinalIgnoreCase) ? 1 :
                string.Equals(profile.ChromaUpsamplingMode, "Adaptive", StringComparison.OrdinalIgnoreCase) ? 2 : 0;
            _debandAmount = profile.DebandEnabled ? Math.Max(0, Math.Min(100, profile.DebandStrength)) / 100f : 0f;
            _requestedSourceFps = sourceFps > 0 && sourceFps <= 1000 ? sourceFps : 60.0;
            _previewVsync = previewVsync;
            _onReady = onReady;
            _onError = onError;
        }

        public void Start()
        {
            if (_thread != null) throw new InvalidOperationException(Localization.T("Capture GPU déjà démarrée."));
            _running = true;
            _thread = new Thread(Run) { IsBackground = true, Name = "MF-D3D11-Video" };
            _thread.Start();
        }

        public void Resize(int width, int height)
        {
            if (width > 0) _windowWidth = width;
            if (height > 0) _windowHeight = height;
        }

        private void Run()
        {
            try
            {
                OpenMediaReader();
                CreateGraphics();
                if (!_running) return;
                _onReady?.Invoke();
                var fpsClock = System.Diagnostics.Stopwatch.StartNew();
                int framesInWindow = 0;

                while (_running)
                {
                    int actualStream;
                    SourceReaderFlags flags;
                    long timestamp;
                    using (var sample = _reader.ReadSample(SourceReaderIndex.FirstVideoStream,
                        SourceReaderControlFlags.None, out actualStream, out flags, out timestamp))
                    {
                        if (!_running) break;
                        // Media Foundation peut signaler une erreur ou la fin du flux
                        // sans lever d'exception. Une boucle de samples nuls bloquerait
                        // sinon indéfiniment le retour à Auto.
                        int sampleFlags = (int)flags;
                        if ((sampleFlags & 0x1) != 0) // MF_SOURCE_READERF_ERROR
                            throw new InvalidOperationException(Localization.T("Le pilote a signalé une erreur de lecture du flux vidéo."));
                        if ((sampleFlags & 0x2) != 0) // MF_SOURCE_READERF_ENDOFSTREAM
                            throw new InvalidOperationException(Localization.T("Le flux vidéo s'est terminé pendant la capture."));
                        if (sample == null)
                        {
                            if (_forcedNativeFormat != null && fpsClock.Elapsed.TotalSeconds > 8 &&
                                Interlocked.Read(ref _receivedSamples) == 0)
                                throw new InvalidOperationException(Localization.T("Aucune image reçue avec le format manuel sélectionné."));
                            Thread.Sleep(2);
                            continue;
                        }
                        Interlocked.Increment(ref _receivedSamples);
                        ResizeSwapChainIfNecessary();
                        UploadSample(sample);
                        Draw();
                        Interlocked.Increment(ref _presentedSamples);
                        framesInWindow++;
                        if (fpsClock.ElapsedMilliseconds >= 2000)
                        {
                            _measuredFpsTenths = (int)Math.Round(
                                framesInWindow * 10000.0 / fpsClock.ElapsedMilliseconds);
                            framesInWindow = 0;
                            fpsClock.Restart();
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                if (_running)
                    _onError?.Invoke(Localization.F("Capture DirectX 11 : {0}", ex.Message));
            }
            finally
            {
                DisposeGraphics();
                _reader?.Dispose();
                _reader = null;
            }
        }

        private void OpenMediaReader()
        {
            using (var attrs = new MediaAttributes(2))
            {
                // Do not insert Media Foundation's optional software video processor:
                // Direct3D11 already handles YUV conversion and scaling. Compressed
                // MJPEG/H264/HEVC streams still use the SourceReader's decoder MFT.
                attrs.Set(SourceReaderAttributeKeys.EnableVideoProcessing, 0);
                _reader = new SourceReader(_source, attrs);
            }

            using (var native = _reader.GetCurrentMediaType(SourceReaderIndex.FirstVideoStream))
            {
                ulong packed = (ulong)native.Get(MediaTypeAttributeKeys.FrameSize);
                _sourceWidth = (int)(packed >> 32);
                _sourceHeight = (int)(packed & 0xFFFFFFFF);
                if (_sourceWidth <= 0 || _sourceHeight <= 0 ||
                    _sourceWidth > 16384 || _sourceHeight > 16384)
                    throw new InvalidOperationException(Localization.T("Dimensions HDMI invalides ou trop grandes pour D3D11."));

                var nativeSubtype = native.Get<Guid>(MediaTypeAttributeKeys.Subtype);
                string nativeFormat = CaptureSession.NameOfFormat(nativeSubtype);
                if (_forcedNativeFormat != null &&
                    !string.Equals(nativeFormat, _forcedNativeFormat, StringComparison.OrdinalIgnoreCase))
                    throw new InvalidOperationException(Localization.F(
                        "Le pilote a remplacé le format demandé {0} par {1}.",
                        _forcedNativeFormat, nativeFormat));
                // Les formats non compressés passent sans conversion logicielle.
                if (nativeSubtype == MediaEngine.VideoNv12 || nativeSubtype == MediaEngine.VideoYuy2 ||
                    nativeSubtype == MediaEngine.VideoUyvy || nativeSubtype == MediaEngine.VideoRgb32 ||
                    nativeSubtype == MediaEngine.VideoP010)
                {
                    _reader.SetCurrentMediaType(SourceReaderIndex.FirstVideoStream, native);
                }
                else
                {
                    // MJPEG : décompression Media Foundation en format non compressé.
                    // Les autres formats vidéo peuvent également passer par cette voie.
                    Exception last = null;
                    foreach (Guid type in new[] { MediaEngine.VideoNv12, MediaEngine.VideoYuy2,
                        MediaEngine.VideoUyvy, MediaEngine.VideoRgb32 })
                    {
                        try
                        {
                            using (var output = new MediaType())
                            {
                                output.Set(MediaTypeAttributeKeys.MajorType, MediaTypeGuids.Video);
                                output.Set(MediaTypeAttributeKeys.Subtype, type);
                                output.Set(MediaTypeAttributeKeys.FrameSize, (long)packed);
                                _reader.SetCurrentMediaType(SourceReaderIndex.FirstVideoStream, output);
                            }
                            last = null;
                            break;
                        }
                        catch (Exception ex) { last = ex; }
                    }
                    if (last != null)
                        throw new InvalidOperationException(Localization.T("Aucun décodeur Media Foundation compatible NV12/YUY2/UYVY/RGB32 pour ce flux."), last);
                }
            }

            using (var output = _reader.GetCurrentMediaType(SourceReaderIndex.FirstVideoStream))
            {
                var subtype = output.Get<Guid>(MediaTypeAttributeKeys.Subtype);
                _inputMode = subtype == MediaEngine.VideoNv12 ? 0 :
                             subtype == MediaEngine.VideoYuy2 ? 1 :
                             subtype == MediaEngine.VideoRgb32 ? 2 :
                             subtype == MediaEngine.VideoUyvy ? 3 :
                             subtype == MediaEngine.VideoP010 ? 4 : -1;
                if (_inputMode == -1)
                    throw new NotSupportedException(Localization.F("Format de sortie non pris en charge par le shader : {0}", subtype));
                if ((_inputMode == 0 || _inputMode == 1 || _inputMode == 3 || _inputMode == 4) &&
                    ((_sourceWidth & 1) != 0 || (_sourceHeight & 1) != 0))
                    throw new InvalidOperationException(Localization.T("Le format YUV sélectionné nécessite une résolution paire."));

                // Media Foundation signale parfois RGB32 bottom-up (stride négatif).
                try { _flipRgbRows = _inputMode == 2 && (int)output.Get(MediaTypeAttributeKeys.DefaultStride) < 0; }
                catch { _flipRgbRows = false; }
            }
            _matrix = _profile.YuvMatrix == "BT.601" ? 1 :
                (_profile.YuvMatrix == "BT.709" ? 0 : (_sourceWidth >= 1280 ? 0 : 1));
            _range = _profile.YuvRange == "Full" ? 1 : 0; // YUV caméra : limité par défaut
        }

        private void CreateGraphics()
        {
            var description = new SwapChainDescription
            {
                BufferCount = 2,
                ModeDescription = new ModeDescription(Math.Max(1, _windowWidth), Math.Max(1, _windowHeight),
                    new Rational(0, 1), DxgiFormat.B8G8R8A8_UNorm), // refresh from display, not HDMI FPS
                Usage = Usage.RenderTargetOutput,
                OutputHandle = _hwnd,
                SampleDescription = new SampleDescription(1, 0),
                IsWindowed = true,
                SwapEffect = SwapEffect.Discard,
                Flags = SwapChainFlags.None
            };
            D3DDevice.CreateWithSwapChain(DriverType.Hardware, DeviceCreationFlags.BgraSupport,
                description, out _device, out _swapChain);
            _surfaceWidth = Math.Max(1, _windowWidth);
            _surfaceHeight = Math.Max(1, _windowHeight);
            CreateRenderTarget();

            var format = _inputMode == 0 ? DxgiFormat.R8_UNorm :
                _inputMode == 4 ? DxgiFormat.R16_UNorm :
                (_inputMode == 1 || _inputMode == 3) ? DxgiFormat.R8G8B8A8_UNorm : DxgiFormat.B8G8R8A8_UNorm;
            for (int i = 0; i < _uploadTextures.Length; i++)
            {
                _uploadTextures[i] = new Texture2D(_device, new Texture2DDescription
                {
                    Width = (_inputMode == 1 || _inputMode == 3) ? _sourceWidth / 2 : _sourceWidth,
                    Height = (_inputMode == 0 || _inputMode == 4) ? _sourceHeight + _sourceHeight / 2 : _sourceHeight,
                    ArraySize = 1, MipLevels = 1, Format = format,
                    BindFlags = BindFlags.ShaderResource, CpuAccessFlags = CpuAccessFlags.None,
                    Usage = D3DResourceUsage.Default, SampleDescription = new SampleDescription(1, 0)
                });
                _uploadViews[i] = new ShaderResourceView(_device, _uploadTextures[i]);
            }

            _convertedTexture = new Texture2D(_device, new Texture2DDescription
            {
                Width = _sourceWidth, Height = _sourceHeight,
                ArraySize = 1, MipLevels = 1, Format = DxgiFormat.B8G8R8A8_UNorm,
                BindFlags = BindFlags.RenderTarget | BindFlags.ShaderResource,
                CpuAccessFlags = CpuAccessFlags.None, Usage = D3DResourceUsage.Default,
                SampleDescription = new SampleDescription(1, 0)
            });
            _convertedRtv = new RenderTargetView(_device, _convertedTexture);
            _convertedSrv = new ShaderResourceView(_device, _convertedTexture);

            // Textures supplémentaires créées UNIQUEMENT si un filtre est sélectionné.
            // En Off, aucun coût GPU/mémoire supplémentaire par rapport au moteur YUY2 stable.
            if (_antiAliasingMode != "Off")
            {
                CreateFilterTarget(out _aaTexture, out _aaRtv, out _aaSrv);
                if (_antiAliasingMode == "SMAA")
                {
                    CreateFilterTarget(out _edgeTexture, out _edgeRtv, out _edgeSrv);
                    CreateFilterTarget(out _weightTexture, out _weightRtv, out _weightSrv);
                }
            }

            using (var vertexCode = ShaderBytecode.Compile(Hlsl, "VS", "vs_4_0").Bytecode)
                _vertexShader = new VertexShader(_device, vertexCode);
            using (var conversionCode = ShaderBytecode.Compile(Hlsl, "ConvertPS", "ps_4_0").Bytecode)
                _convertShader = new PixelShader(_device, conversionCode);
            using (var pixelCode = ShaderBytecode.Compile(Hlsl, "PS", "ps_4_0").Bytecode)
                _pixelShader = new PixelShader(_device, pixelCode);
            if (_antiAliasingMode != "Off")
            {
                string aaSource = Hlsl + "\n" + AntiAliasingHlsl;
                if (_antiAliasingMode == "FXAA")
                {
                    using (var code = ShaderBytecode.Compile(aaSource, "FxaaPS", "ps_4_0").Bytecode)
                        _fxaaShader = new PixelShader(_device, code);
                }
                else
                {
                    using (var code = ShaderBytecode.Compile(aaSource, "SmaaEdgePS", "ps_4_0").Bytecode)
                        _smaaEdgeShader = new PixelShader(_device, code);
                    using (var code = ShaderBytecode.Compile(aaSource, "SmaaWeightPS", "ps_4_0").Bytecode)
                        _smaaWeightShader = new PixelShader(_device, code);
                    using (var code = ShaderBytecode.Compile(aaSource, "SmaaBlendPS", "ps_4_0").Bytecode)
                        _smaaBlendShader = new PixelShader(_device, code);
                }
            }
            _paramsBuffer = new D3DBuffer(_device, new BufferDescription
            {
                SizeInBytes = 48, BindFlags = BindFlags.ConstantBuffer,
                Usage = D3DResourceUsage.Default, CpuAccessFlags = CpuAccessFlags.None
            });
            _sampler = new SamplerState(_device, new SamplerStateDescription
            {
                Filter = Filter.MinMagMipLinear,
                AddressU = TextureAddressMode.Clamp,
                AddressV = TextureAddressMode.Clamp,
                AddressW = TextureAddressMode.Clamp,
                ComparisonFunction = Comparison.Never,
                MinimumLod = 0,
                MaximumLod = float.MaxValue
            });
        }

        private void CreateFilterTarget(out Texture2D texture, out RenderTargetView target, out ShaderResourceView view)
        {
            texture = new Texture2D(_device, new Texture2DDescription
            {
                Width = _sourceWidth, Height = _sourceHeight,
                ArraySize = 1, MipLevels = 1, Format = DxgiFormat.B8G8R8A8_UNorm,
                BindFlags = BindFlags.RenderTarget | BindFlags.ShaderResource,
                CpuAccessFlags = CpuAccessFlags.None, Usage = D3DResourceUsage.Default,
                SampleDescription = new SampleDescription(1, 0)
            });
            target = new RenderTargetView(_device, texture);
            view = new ShaderResourceView(_device, texture);
        }

        private void CreateRenderTarget()
        {
            using (var backBuffer = _swapChain.GetBackBuffer<Texture2D>(0))
                _rtv = new RenderTargetView(_device, backBuffer);
        }

        private void ResizeSwapChainIfNecessary()
        {
            int w = _windowWidth, h = _windowHeight;
            if (w < 1 || h < 1 || w == _surfaceWidth && h == _surfaceHeight) return;
            _device.ImmediateContext.OutputMerger.SetTargets((RenderTargetView)null);
            _rtv.Dispose();
            _rtv = null;
            _swapChain.ResizeBuffers(2, w, h, DxgiFormat.B8G8R8A8_UNorm, SwapChainFlags.None);
            _surfaceWidth = w; _surfaceHeight = h;
            CreateRenderTarget();
        }

        private void UploadSample(Sample sample)
        {
            _uploadIndex = (_uploadIndex + 1) % _uploadTextures.Length;
            var uploadTexture = _uploadTextures[_uploadIndex];
            using (var buffer = sample.ConvertToContiguousBuffer())
            {
                var lockable = (IMFLockedMediaBuffer)Marshal.GetTypedObjectForIUnknown(
                    buffer.NativePointer, typeof(IMFLockedMediaBuffer));
                IntPtr data = IntPtr.Zero;
                bool locked = false;
                try
                {
                    Marshal.ThrowExceptionForHR(lockable.Lock(out data, out int maxLength, out int length));
                    locked = true;
                    int rows = (_inputMode == 0 || _inputMode == 4) ? _sourceHeight + _sourceHeight / 2 : _sourceHeight;
                    int rowBytes = _inputMode == 0 ? _sourceWidth :
                                   _inputMode == 4 ? _sourceWidth * 2 :
                                   (_inputMode == 1 || _inputMode == 3) ? _sourceWidth * 2 : _sourceWidth * 4;
                    int required = checked(rows * rowBytes);
                    if (length < required)
                        throw new InvalidOperationException(Localization.T("Sample HDMI trop court."));

                    // Dans le cas habituel (YUY2 compact 1080p60), UploadSubresource
                    // transfère directement depuis le pointeur MF vers D3D11.
                    // Plus de Marshal.Copy vers 4 Mo de mémoire managée, ni GCHandle.
                    if (length == required)
                    {
                        if (!_flipRgbRows)
                        {
                            _device.ImmediateContext.UpdateSubresource(
                                new DataBox(data, rowBytes, 0), uploadTexture, 0);
                            return;
                        }
                    }

                    int pitch = length / rows;
                    if (pitch < rowBytes || (long)pitch * rows > length) pitch = rowBytes;
                    int paddedYRows = _sourceHeight;
                    if ((_inputMode == 0 || _inputMode == 4) && length != required)
                    {
                        int[] candidates = { rowBytes, ((rowBytes + 63) / 64) * 64,
                            ((rowBytes + 127) / 128) * 128, ((rowBytes + 255) / 256) * 256 };
                        foreach (int candidate in candidates)
                        {
                            if (candidate <= 0 || length % candidate != 0) continue;
                            int possibleHeight = length / candidate * 2 / 3;
                            if (possibleHeight >= _sourceHeight && possibleHeight <= _sourceHeight + 64 &&
                                (long)possibleHeight * 3 / 2 * candidate == length)
                            {
                                pitch = candidate;
                                paddedYRows = possibleHeight;
                                break;
                            }
                        }
                    }

                    // Le rowPitch de D3D11 gère directement les lignes alignées
                    // YUY2/RGB sans recopie ; NV12 avec plans Y décalés ou RGB
                    // bottom-up gardent un repli sûr.
                    if (paddedYRows == _sourceHeight && !_flipRgbRows &&
                        (long)pitch * rows <= length)
                    {
                        _device.ImmediateContext.UpdateSubresource(
                            new DataBox(data, pitch, 0), uploadTexture, 0);
                        return;
                    }

                    if (_frameBuffer == null || _frameBuffer.Length != required)
                        _frameBuffer = new byte[required];
                    for (int y = 0; y < rows; y++)
                    {
                        int sourceRow = (_inputMode == 0 || _inputMode == 4) && y >= _sourceHeight
                            ? paddedYRows + y - _sourceHeight :
                            (_flipRgbRows ? rows - 1 - y : y);
                        if ((long)(sourceRow + 1) * pitch > length)
                            throw new InvalidOperationException(Localization.T("Stride du sample HDMI invalide."));
                        Marshal.Copy(IntPtr.Add(data, sourceRow * pitch),
                            _frameBuffer, y * rowBytes, rowBytes);
                    }
                    var pinned = GCHandle.Alloc(_frameBuffer, GCHandleType.Pinned);
                    try
                    {
                        _device.ImmediateContext.UpdateSubresource(
                            new DataBox(pinned.AddrOfPinnedObject(), rowBytes, 0), uploadTexture, 0);
                    }
                    finally { pinned.Free(); }
                }
                finally
                {
                    if (locked) lockable.Unlock();
                    Marshal.ReleaseComObject(lockable);
                }
            }
        }

        private struct ShaderConstants
        {
            public Vector4 Dimensions;
            public Vector4 Settings;
            public Vector4 PostOptions;
        }

        private void Draw()
        {
            var context = _device.ImmediateContext;
            var options = new ShaderConstants
            {
                Dimensions = new Vector4(_sourceWidth, _sourceHeight, _surfaceWidth, _surfaceHeight),
                Settings = new Vector4(_profile.SharpenEnabled ?
                    (_profile.SharpenAmount >= 0
                        ? Math.Min(100, _profile.SharpenAmount) / 65f
                        : Math.Max(-100, _profile.SharpenAmount) / 100f) : 0f,
                    _inputMode, _matrix, _range),
                PostOptions = new Vector4(_chromaMode, _debandAmount, 0f, 0f)
            };
            context.UpdateSubresource(ref options, _paramsBuffer);
            context.InputAssembler.PrimitiveTopology = PrimitiveTopology.TriangleList;
            context.VertexShader.Set(_vertexShader);
            context.PixelShader.SetSampler(0, _sampler);
            context.PixelShader.SetConstantBuffer(0, _paramsBuffer);

            // Unscaled, zero-sharpen frames can be decoded directly into the
            // backbuffer: one GPU render pass instead of two (especially 4K).
            bool noSharpen = !_profile.SharpenEnabled || _profile.SharpenAmount == 0;
            bool directConversion = _antiAliasingMode == "Off" && noSharpen && _debandAmount == 0f &&
                                    _sourceWidth == _surfaceWidth && _sourceHeight == _surfaceHeight;
            if (directConversion)
            {
                context.OutputMerger.SetTargets(_rtv);
                context.Rasterizer.SetViewport(0, 0, _surfaceWidth, _surfaceHeight);
                context.PixelShader.Set(_convertShader);
                context.PixelShader.SetShaderResource(0, _uploadViews[_uploadIndex]);
                context.Draw(3, 0);
                context.PixelShader.SetShaderResource(0, null);
                _swapChain.Present(_previewVsync ? 1 : 0, PresentFlags.None);
                return;
            }

            // Étape 1 : décodage YUY2/NV12/UYVY en RGB, une fois par pixel.
            context.OutputMerger.SetTargets(_convertedRtv);
            context.Rasterizer.SetViewport(0, 0, _sourceWidth, _sourceHeight);
            context.PixelShader.Set(_convertShader);
            context.PixelShader.SetShaderResource(0, _uploadViews[_uploadIndex]);
            context.Draw(3, 0);
            context.PixelShader.SetShaderResource(0, null);

            // Passe AA facultative, avant le sharpen. Lorsque la netteté est
            // coupée et qu'il n'y a pas de redimensionnement, AA -> backbuffer.
            bool aaDirect = _antiAliasingMode != "Off" && noSharpen && _debandAmount == 0f &&
                            _sourceWidth == _surfaceWidth && _sourceHeight == _surfaceHeight;
            if (_antiAliasingMode == "FXAA")
            {
                context.OutputMerger.SetTargets(aaDirect ? _rtv : _aaRtv);
                context.Rasterizer.SetViewport(0, 0, _sourceWidth, _sourceHeight);
                context.PixelShader.Set(_fxaaShader);
                context.PixelShader.SetShaderResource(1, _convertedSrv);
                context.Draw(3, 0);
                context.PixelShader.SetShaderResource(1, null);
            }
            else if (_antiAliasingMode == "SMAA")
            {
                // Détection des bords à partir de la même image RGB que FXAA.
                context.OutputMerger.SetTargets(_edgeRtv);
                context.Rasterizer.SetViewport(0, 0, _sourceWidth, _sourceHeight);
                context.PixelShader.Set(_smaaEdgeShader);
                context.PixelShader.SetShaderResource(1, _convertedSrv);
                context.Draw(3, 0);
                context.PixelShader.SetShaderResource(1, null);

                // Poids spatiaux ; aucune lecture en conflit avec le RTV actif.
                context.OutputMerger.SetTargets(_weightRtv);
                context.PixelShader.Set(_smaaWeightShader);
                context.PixelShader.SetShaderResource(2, _edgeSrv);
                context.Draw(3, 0);
                context.PixelShader.SetShaderResource(2, null);

                context.OutputMerger.SetTargets(aaDirect ? _rtv : _aaRtv);
                context.PixelShader.Set(_smaaBlendShader);
                context.PixelShader.SetShaderResource(1, _convertedSrv);
                context.PixelShader.SetShaderResource(3, _weightSrv);
                context.Draw(3, 0);
                context.PixelShader.SetShaderResource(1, null);
                context.PixelShader.SetShaderResource(3, null);
            }
            if (aaDirect)
            {
                _swapChain.Present(_previewVsync ? 1 : 0, PresentFlags.None);
                return;
            }

            // Dernière passe : échantillonnage/affichage, sharpen facultatif.
            // Si AA est activé, on traite la texture AA et non l'entrée brute.
            context.OutputMerger.SetTargets(_rtv);
            context.Rasterizer.SetViewport(0, 0, _surfaceWidth, _surfaceHeight);
            context.PixelShader.Set(_pixelShader);
            context.PixelShader.SetShaderResource(1, _antiAliasingMode == "Off" ? _convertedSrv : _aaSrv);
            context.Draw(3, 0);
            context.PixelShader.SetShaderResource(1, null);
            _swapChain.Present(_previewVsync ? 1 : 0, PresentFlags.None);
        }

        private void DisposeGraphics()
        {
            _rtv?.Dispose(); _rtv = null;
            _convertedRtv?.Dispose(); _convertedRtv = null;
            _convertedSrv?.Dispose(); _convertedSrv = null;
            _convertedTexture?.Dispose(); _convertedTexture = null;
            _convertShader?.Dispose(); _convertShader = null;
            _fxaaShader?.Dispose(); _fxaaShader = null;
            _smaaEdgeShader?.Dispose(); _smaaEdgeShader = null;
            _smaaWeightShader?.Dispose(); _smaaWeightShader = null;
            _smaaBlendShader?.Dispose(); _smaaBlendShader = null;
            _aaRtv?.Dispose(); _aaRtv = null;
            _aaSrv?.Dispose(); _aaSrv = null;
            _aaTexture?.Dispose(); _aaTexture = null;
            _edgeRtv?.Dispose(); _edgeRtv = null;
            _edgeSrv?.Dispose(); _edgeSrv = null;
            _edgeTexture?.Dispose(); _edgeTexture = null;
            _weightRtv?.Dispose(); _weightRtv = null;
            _weightSrv?.Dispose(); _weightSrv = null;
            _weightTexture?.Dispose(); _weightTexture = null;
            for (int i = 0; i < _uploadTextures.Length; i++)
            {
                _uploadViews[i]?.Dispose(); _uploadViews[i] = null;
                _uploadTextures[i]?.Dispose(); _uploadTextures[i] = null;
            }
            _sampler?.Dispose(); _sampler = null;
            _paramsBuffer?.Dispose(); _paramsBuffer = null;
            _pixelShader?.Dispose(); _pixelShader = null;
            _vertexShader?.Dispose(); _vertexShader = null;
            _swapChain?.Dispose(); _swapChain = null;
            _device?.Dispose(); _device = null;
        }

        public void Dispose()
        {
            _running = false;
            // ReadSample est synchrone et peut bloquer si aucun HDMI n'arrive.
            // Stopper la boucle puis, si nécessaire, Shutdown() réveille le reader.
            // La source activée sera ensuite retirée du cache via ShutdownObject()
            // dans CaptureSession.Stop(), avant la capture suivante.
            if (_thread == null) return;
            if (!_thread.Join(350))
            {
                try { _source.Shutdown(); } catch { }
                if (!_thread.Join(3000))
                    throw new TimeoutException(Localization.T("Le thread de capture DirectX 11 n’a pas pu être arrêté. Redémarre Broadcaster avant une nouvelle capture."));
            }
            _thread = null;
        }
    }
}
