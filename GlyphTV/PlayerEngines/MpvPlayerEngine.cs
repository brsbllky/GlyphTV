using Avalonia.Controls;
using Avalonia.Threading;
using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;

namespace GlyphTV.PlayerEngines
{
    public sealed class MpvPlayerEngine : IPlayerEngine
    {
        private readonly MpvVideoHost _host;
        private IntPtr _ctx = IntPtr.Zero;
        private IntPtr _hwnd = IntPtr.Zero;
        private Thread? _eventThread;
        private volatile bool _stopping = false;

        private const ulong OBS_TIME_POS = 1;
        private const ulong OBS_TRACK_LIST_COUNT = 2;
        private const ulong OBS_DURATION = 3;
        private const ulong OBS_VO_FRAME_COUNT = 4;

        private long _lastKnownLengthMs = 0;
        private bool _pendingWid = false;
        private int _playGeneration = 0;

        // ── YENİ: Anlık MPV Bitrate Hesaplayıcı ve Yumuşatıcı ──
        private double _smoothedBitrateKbps = 0;
        private DateTime _lastBitrateTime = DateTime.MinValue;

        private string? _pendingPlayUrl = null;
        private long _pendingPlayStartMs = 0;

        private string _hwDecodeMode = "auto";
        private bool _deinterlace = false;
        private string _hdrToneMapping = "auto";
        private string _hdrTargetPeak = "auto";
        private string _scalingQuality = "default";
        private string? _lastAspectRatio = "12:5";

        private int _brightness = 0;
        private int _contrast = 0;
        private int _saturation = 0;
        private int _gamma = 0;

        private static string MapHwDecodeToMpv(string mode) => mode switch
        {
            "d3d11va"    => "d3d11va",
            "nvdec"      => "nvdec",
            "nvdec-copy" => "nvdec-copy",
            "no"         => "no",
            _            => "auto"
        };

        public MpvPlayerEngine()
        {
            // Thread-Safe Host Başlatma: MpvVideoHost her zaman UI Thread üzerinde örneklenir
            if (Dispatcher.UIThread.CheckAccess())
            {
                _host = new MpvVideoHost();
            }
            else
            {
                _host = Dispatcher.UIThread.Invoke(() => new MpvVideoHost());
            }

            _host.HandleReady += hwnd =>
            {
                _hwnd = hwnd;
                if (_pendingWid) CompleteInitialize();
            };
        }

        public PlayerEngineType EngineType => PlayerEngineType.Mpv;
        public Control VideoSurface => _host;
        public bool IsInitialized { get; private set; }

        public bool IsPlaying
        {
            get
            {
                if (_ctx == IntPtr.Zero) return false;
                int pause = 0;
                MpvInterop.mpv_get_property(_ctx, "pause", MpvInterop.mpv_format.MPV_FORMAT_FLAG, ref pause);
                return pause == 0;
            }
        }

        public bool IsSeekable
        {
            get
            {
                if (_ctx == IntPtr.Zero) return false;
                int seekable = 0;
                MpvInterop.mpv_get_property(_ctx, "seekable", MpvInterop.mpv_format.MPV_FORMAT_FLAG, ref seekable);
                return seekable != 0;
            }
        }

        public long Time
        {
            get
            {
                if (_ctx == IntPtr.Zero) return 0;
                double sec = 0;
                MpvInterop.mpv_get_property(_ctx, "time-pos", MpvInterop.mpv_format.MPV_FORMAT_DOUBLE, ref sec);
                return (long)(sec * 1000.0);
            }
            set
            {
                if (_ctx == IntPtr.Zero) return;
                double sec = value / 1000.0;
                SendCommand("seek", sec.ToString(System.Globalization.CultureInfo.InvariantCulture), "absolute");
            }
        }

        public long Length => _lastKnownLengthMs;

        public int Volume
        {
            get
            {
                if (_ctx == IntPtr.Zero) return 100;
                double vol = 100;
                MpvInterop.mpv_get_property(_ctx, "volume", MpvInterop.mpv_format.MPV_FORMAT_DOUBLE, ref vol);
                return (int)vol;
            }
            set { if (_ctx != IntPtr.Zero) MpvInterop.mpv_set_property_string(_ctx, "volume", value.ToString()); }
        }

        public bool Mute
        {
            get
            {
                if (_ctx == IntPtr.Zero) return false;
                var ptr = MpvInterop.mpv_get_property_string(_ctx, "mute");
                if (ptr == IntPtr.Zero) return false;
                string val = PtrToStringUtf8(ptr);
                MpvInterop.mpv_free(ptr);
                return val == "yes";
            }
            set { if (_ctx != IntPtr.Zero) MpvInterop.mpv_set_property_string(_ctx, "mute", value ? "yes" : "no"); }
        }

        public float PlaybackRate
        {
            get
            {
                if (_ctx == IntPtr.Zero) return 1.0f;
                double speed = 1.0;
                MpvInterop.mpv_get_property(_ctx, "speed", MpvInterop.mpv_format.MPV_FORMAT_DOUBLE, ref speed);
                return (float)speed;
            }
            set
            {
                if (_ctx == IntPtr.Zero) return;
                MpvInterop.mpv_set_property_string(_ctx, "speed",
                    value.ToString(System.Globalization.CultureInfo.InvariantCulture));
            }
        }

        public IReadOnlyList<EngineTrackInfo> AudioTracks => ReadTrackList("audio");
        public IReadOnlyList<EngineTrackInfo> SubtitleTracks => ReadTrackList("sub");

        public int ActiveAudioTrackId => GetIntProperty("aid", -1);
        public int ActiveSubtitleTrackId => GetIntProperty("sid", -1);

        private long _subtitleDelayMs = 0;
        public long SubtitleDelayMs
        {
            get => _subtitleDelayMs;
            set => SetSubtitleDelay(value);
        }

        public void SetSubtitleDelay(long delayMs)
        {
            _subtitleDelayMs = delayMs;
            if (_ctx != IntPtr.Zero)
            {
                // mpv sub-delay saniye cinsindendir: örn. 500 ms = 0.5 s, -100 ms = -0.1 s
                double sec = delayMs / 1000.0;
                MpvInterop.mpv_set_property_string(_ctx, "sub-delay",
                    sec.ToString(System.Globalization.CultureInfo.InvariantCulture));
            }
        }

        private long _audioDelayMs = 0;
        public long AudioDelayMs
        {
            get => _audioDelayMs;
            set => SetAudioDelay(value);
        }

        public void SetAudioDelay(long delayMs)
        {
            _audioDelayMs = delayMs;
            if (_ctx != IntPtr.Zero)
            {
                // mpv audio-delay saniye cinsindendir: örn. 500 ms = 0.5 s, -100 ms = -0.1 s
                double sec = delayMs / 1000.0;
                MpvInterop.mpv_set_property_string(_ctx, "audio-delay",
                    sec.ToString(System.Globalization.CultureInfo.InvariantCulture));
            }
        }

        public event EventHandler<long>? TimeChanged;
        public event EventHandler? EndReached;
        public event EventHandler? TracksChanged;

        private readonly object _initLock = new();

        public void Initialize()
        {
            if (IsInitialized) return;
            if (_hwnd == IntPtr.Zero) { _pendingWid = true; return; }
            CompleteInitialize();
        }

        private void CompleteInitialize()
        {
            if (IsInitialized) return;
            Task.Run(() =>
            {
                lock (_initLock)
                {
                    if (IsInitialized || _ctx != IntPtr.Zero) return;
                    try
                    {
                        if (_hwnd == IntPtr.Zero) return;
                        var ctx = MpvInterop.mpv_create();
                        if (ctx == IntPtr.Zero) return;

                        MpvInterop.mpv_set_option_string(ctx, "wid", _hwnd.ToString());
                        MpvInterop.mpv_set_option_string(ctx, "vo", "gpu");
                        MpvInterop.mpv_set_option_string(ctx, "gpu-api", "d3d11");
                        MpvInterop.mpv_set_option_string(ctx, "gpu-context", "d3d11");
                        MpvInterop.mpv_set_option_string(ctx, "hwdec", "auto-safe");
                        MpvInterop.mpv_set_option_string(ctx, "hwdec-codecs", "all");
                        MpvInterop.mpv_set_option_string(ctx, "d3d11-flip", "no");
                        MpvInterop.mpv_set_option_string(ctx, "deinterlace", _deinterlace ? "yes" : "no");
                        if (_deinterlace)
                        {
                            string vfFilter = _deinterlaceMode switch
                            {
                                "yadif2x" => "yadif=mode=send_field",
                                "yadif"   => "yadif=mode=send_frame",
                                "bob"     => "yadif=mode=send_field:parity=auto",
                                "linear"  => "bwdif=mode=send_frame",
                                _         => "yadif=mode=send_field"
                            };
                            MpvInterop.mpv_set_option_string(ctx, "vf", vfFilter);
                        }
                        MpvInterop.mpv_set_option_string(ctx, "vd-lavc-dr", "yes");
                        MpvInterop.mpv_set_option_string(ctx, "vd-lavc-threads", "0");
                        MpvInterop.mpv_set_option_string(ctx, "video-sync", "audio");
                        MpvInterop.mpv_set_option_string(ctx, "framedrop", "vo");

                        _ctx = ctx;
                        ApplyDemuxerAndCachingProfile(useOptionApi: true);
                        ApplyShaderOptions(useOptionApi: true);
                        ApplyAudioEnhancement(useOptionApi: true);
                        ApplyHdrToneMappingOptions(useOptionApi: true);
                        ApplyScalingQualityOptions(useOptionApi: true);
                        ApplyEqOptions(useOptionApi: true);
                        MpvInterop.mpv_set_option_string(ctx, "osc", "no");
                        MpvInterop.mpv_set_option_string(ctx, "keep-open", "no");
                        MpvInterop.mpv_set_option_string(ctx, "gapless-audio", "yes");
                        MpvInterop.mpv_set_option_string(ctx, "volume-max", "200");
                        MpvInterop.mpv_set_option_string(ctx, "audio-normalize-downmix", "no");
                        MpvInterop.mpv_set_option_string(ctx, "audio-channels", "stereo");
                        MpvInterop.mpv_set_option_string(ctx, "audio-pitch-correction", "yes");
                        MpvInterop.mpv_set_option_string(ctx, "audio-buffer", "0.2");
                        MpvInterop.mpv_set_option_string(ctx, "reset-on-next-file", "pause,video-pan-x,video-pan-y,video-zoom");
                        MpvInterop.mpv_set_option_string(ctx, "background-color", "#000000");

                        int rc = MpvInterop.mpv_initialize(ctx);
                        if (rc < 0)
                        {
                            Console.WriteLine($"mpv_initialize başarısız: {rc}");
                            MpvInterop.mpv_terminate_destroy(ctx);
                            _ctx = IntPtr.Zero;
                            return;
                        }

                        MpvInterop.mpv_observe_property(ctx, OBS_TIME_POS, "time-pos", MpvInterop.mpv_format.MPV_FORMAT_DOUBLE);
                        MpvInterop.mpv_observe_property(ctx, OBS_DURATION, "duration", MpvInterop.mpv_format.MPV_FORMAT_DOUBLE);
                        MpvInterop.mpv_observe_property(ctx, OBS_TRACK_LIST_COUNT, "track-list/count", MpvInterop.mpv_format.MPV_FORMAT_INT64);
                        MpvInterop.mpv_observe_property(ctx, OBS_VO_FRAME_COUNT, "vo-frame-count", MpvInterop.mpv_format.MPV_FORMAT_INT64);

                        _stopping = false;
                        _eventThread = new Thread(EventLoop) { IsBackground = true, Name = "GlyphTV-mpv-events" };
                        _eventThread.Start();

                        IsInitialized = true;

                        if (!string.IsNullOrEmpty(_pendingPlayUrl))
                        {
                            string url = _pendingPlayUrl;
                            long pos = _pendingPlayStartMs;
                            _pendingPlayUrl = null;
                            _pendingPlayStartMs = 0;
                            Dispatcher.UIThread.Post(() => Play(url, pos));
                        }
                    }
                    catch (Exception ex)
                    {
                        Console.WriteLine("mpv init hatası: " + ex.Message);
                    }
                }
            });
        }

        public void Play(string url, long startPositionMs = 0)
        {
            if (!IsInitialized)
            {
                _pendingPlayUrl = url;
                _pendingPlayStartMs = startPositionMs;
                Initialize();
                return;
            }

            _pendingPlayUrl = null;
            _pendingPlayStartMs = 0;
            _lastKnownLengthMs = 0;
            _smoothedBitrateKbps = 0;
            _lastBitrateTime = DateTime.MinValue;
            _playGeneration++;

            _host.RevealVideoSurface();
            if (_ctx != IntPtr.Zero)
            {
                try
                {
                    MpvInterop.mpv_set_property_string(_ctx, "vid", "no");
                    MpvInterop.mpv_command_string(_ctx, "stop\n");
                }
                catch { }
            }
            ApplyAspectRatioAndResetPan(_lastAspectRatio);
            if (_ctx != IntPtr.Zero)
            {
                try
                {
                    MpvInterop.mpv_set_property_string(_ctx, "vid", "auto");
                }
                catch { }
            }

            if (startPositionMs > 0)
            {
                double startSec = startPositionMs / 1000.0;
                string startSecStr = startSec.ToString(System.Globalization.CultureInfo.InvariantCulture);
                try
                {
                    MpvInterop.mpv_set_property(_ctx, "start", MpvInterop.mpv_format.MPV_FORMAT_DOUBLE, ref startSec);
                    MpvInterop.mpv_set_property_string(_ctx, "start", startSecStr);
                    MpvInterop.mpv_set_property_string(_ctx, "file-local-options/start", startSecStr);
                }
                catch { }
                SendCommand("loadfile", url, "replace", "0", $"start={startSecStr}");
            }
            else
            {
                try
                {
                    double zero = 0.0;
                    MpvInterop.mpv_set_property(_ctx, "start", MpvInterop.mpv_format.MPV_FORMAT_DOUBLE, ref zero);
                    MpvInterop.mpv_set_property_string(_ctx, "start", "0");
                    MpvInterop.mpv_set_property_string(_ctx, "file-local-options/start", "0");
                }
                catch { }
                SendCommand("loadfile", url, "replace", "0", "start=0");
            }

            MpvInterop.mpv_set_property_string(_ctx, "pause", "no");
        }

        public void PauseToggle()
        {
            if (_ctx == IntPtr.Zero) return;
            MpvInterop.mpv_set_property_string(_ctx, "pause", IsPlaying ? "yes" : "no");
        }

        public void Stop()
        {
            _playGeneration++;
            _smoothedBitrateKbps = 0;
            _lastBitrateTime = DateTime.MinValue;

            if (_ctx != IntPtr.Zero)
            {
                try
                {
                    MpvInterop.mpv_set_property_string(_ctx, "vid", "no");
                    MpvInterop.mpv_command_string(_ctx, "stop\n");
                }
                catch { }
            }

            _host.HideForReload();
        }

        public void SetAudioTrack(int id)
        {
            if (_ctx == IntPtr.Zero) return;
            MpvInterop.mpv_set_property_string(_ctx, "aid", id.ToString());
        }

        public void SetSubtitleTrack(int id)
        {
            if (_ctx == IntPtr.Zero) return;
            MpvInterop.mpv_set_property_string(_ctx, "sid", id <= 0 ? "no" : id.ToString());
        }

        private string _deinterlaceMode = "yadif2x";

        public void SetHardwareDecoding(string mode)
        {
            _hwDecodeMode = string.IsNullOrEmpty(mode) ? "auto" : mode;
            if (_ctx != IntPtr.Zero)
                MpvInterop.mpv_set_property_string(_ctx, "hwdec", MapHwDecodeToMpv(_hwDecodeMode));
        }

        public void SetDeinterlace(bool enabled, string mode = "yadif2x")
        {
            _deinterlace = enabled;
            if (!string.IsNullOrEmpty(mode)) _deinterlaceMode = mode;
            ApplyDeinterlace();
        }

        public void SetDeinterlaceMode(string mode)
        {
            _deinterlaceMode = string.IsNullOrEmpty(mode) ? "yadif2x" : mode;
            if (_deinterlace) ApplyDeinterlace();
        }

        private void ApplyDeinterlace()
        {
            if (_ctx == IntPtr.Zero) return;
            try
            {
                MpvInterop.mpv_set_property_string(_ctx, "deinterlace", _deinterlace ? "yes" : "no");
                if (_deinterlace)
                {
                    string vfFilter = _deinterlaceMode switch
                    {
                        "yadif2x" => "yadif=mode=send_field",
                        "yadif"   => "yadif=mode=send_frame",
                        "bob"     => "yadif=mode=send_field:parity=auto",
                        "linear"  => "bwdif=mode=send_frame",
                        _         => "yadif=mode=send_field"
                    };
                    MpvInterop.mpv_set_property_string(_ctx, "vf", vfFilter);
                }
                else
                {
                    MpvInterop.mpv_set_property_string(_ctx, "vf", "");
                }
            }
            catch { }
        }

        public void SetHdrToneMapping(string mode)
        {
            _hdrToneMapping = string.IsNullOrEmpty(mode) ? "auto" : mode;
            if (_ctx != IntPtr.Zero) ApplyHdrToneMappingOptions();
        }

        public void SetHdrTargetPeak(string peakNits)
        {
            _hdrTargetPeak = string.IsNullOrEmpty(peakNits) ? "auto" : peakNits;
            if (_ctx != IntPtr.Zero) ApplyHdrToneMappingOptions();
        }

        public void SetScalingQuality(string quality)
        {
            _scalingQuality = string.IsNullOrEmpty(quality) ? "default" : quality;
            if (_ctx != IntPtr.Zero) ApplyScalingQualityOptions();
        }

        public void SetBrightness(int value)
        {
            int newVal = Math.Clamp(value, -100, 100);
            if (_brightness == newVal) return;
            _brightness = newVal;
            if (_ctx != IntPtr.Zero)
                MpvInterop.mpv_set_property_string(_ctx, "brightness", _brightness.ToString());
        }

        public void SetContrast(int value)
        {
            int newVal = Math.Clamp(value, -100, 100);
            if (_contrast == newVal) return;
            _contrast = newVal;
            if (_ctx != IntPtr.Zero)
                MpvInterop.mpv_set_property_string(_ctx, "contrast", _contrast.ToString());
        }

        public void SetSaturation(int value)
        {
            int newVal = Math.Clamp(value, -100, 100);
            if (_saturation == newVal) return;
            _saturation = newVal;
            if (_ctx != IntPtr.Zero)
                MpvInterop.mpv_set_property_string(_ctx, "saturation", _saturation.ToString());
        }

        public void SetGamma(int value)
        {
            int newVal = Math.Clamp(value, -100, 100);
            if (_gamma == newVal) return;
            _gamma = newVal;
            if (_ctx != IntPtr.Zero)
                MpvInterop.mpv_set_property_string(_ctx, "gamma", _gamma.ToString());
        }

        private void ApplyEqOptions(bool useOptionApi = false)
        {
            if (useOptionApi)
            {
                MpvInterop.mpv_set_option_string(_ctx, "brightness", _brightness.ToString());
                MpvInterop.mpv_set_option_string(_ctx, "contrast", _contrast.ToString());
                MpvInterop.mpv_set_option_string(_ctx, "saturation", _saturation.ToString());
                MpvInterop.mpv_set_option_string(_ctx, "gamma", _gamma.ToString());
            }
            else
            {
                MpvInterop.mpv_set_property_string(_ctx, "brightness", _brightness.ToString());
                MpvInterop.mpv_set_property_string(_ctx, "contrast", _contrast.ToString());
                MpvInterop.mpv_set_property_string(_ctx, "saturation", _saturation.ToString());
                MpvInterop.mpv_set_property_string(_ctx, "gamma", _gamma.ToString());
            }
        }

        private void ApplyHdrToneMappingOptions(bool useOptionApi = false)
        {
            if (useOptionApi)
            {
                MpvInterop.mpv_set_option_string(_ctx, "tone-mapping", _hdrToneMapping);
                MpvInterop.mpv_set_option_string(_ctx, "target-peak", _hdrTargetPeak);
                MpvInterop.mpv_set_option_string(_ctx, "hdr-compute-peak",
                    _hdrTargetPeak == "auto" ? "yes" : "no");
            }
            else
            {
                MpvInterop.mpv_set_property_string(_ctx, "tone-mapping", _hdrToneMapping);
                MpvInterop.mpv_set_property_string(_ctx, "target-peak", _hdrTargetPeak);
                MpvInterop.mpv_set_property_string(_ctx, "hdr-compute-peak",
                    _hdrTargetPeak == "auto" ? "yes" : "no");
            }
        }

        private void ApplyScalingQualityOptions(bool useOptionApi = false)
        {
            string scaler = _scalingQuality == "quality" ? "ewa_lanczossharp" : "bilinear";
            if (useOptionApi)
            {
                MpvInterop.mpv_set_option_string(_ctx, "scale", scaler);
                MpvInterop.mpv_set_option_string(_ctx, "cscale", scaler);
            }
            else
            {
                MpvInterop.mpv_set_property_string(_ctx, "scale", scaler);
                MpvInterop.mpv_set_property_string(_ctx, "cscale", scaler);
            }
        }

        // ─────────────────────────────────────────────────────────────
        // YENİ: AMD FidelityFX CAS & FSR GLSL Shader Yönetimi
        // ─────────────────────────────────────────────────────────────
        private string _shaderMode = "off";

        public void SetShaderMode(string mode)
        {
            _shaderMode = string.IsNullOrEmpty(mode) ? "off" : mode;
            ApplyShaderOptions();
        }

        private static string GetOrExtractShaderPath(string shaderName)
        {
            try
            {
                string appDataDir = Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                    "GlyphTV", "shaders");
                if (!Directory.Exists(appDataDir)) Directory.CreateDirectory(appDataDir);

                string targetFile = Path.Combine(appDataDir, $"{shaderName}.glsl");
                if (File.Exists(targetFile) && new FileInfo(targetFile).Length > 0)
                    return targetFile;

                string localAppFile = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "Assets", "Shaders", $"{shaderName}.glsl");
                if (File.Exists(localAppFile))
                {
                    File.Copy(localAppFile, targetFile, true);
                    return targetFile;
                }
            }
            catch { }

            return Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "Assets", "Shaders", $"{shaderName}.glsl");
        }

        private void ApplyShaderOptions(bool useOptionApi = false)
        {
            string shaderPath = "";
            if (_shaderMode == "cas")
                shaderPath = GetOrExtractShaderPath("cas");
            else if (_shaderMode == "fsr")
                shaderPath = GetOrExtractShaderPath("fsr");

            if (useOptionApi)
            {
                MpvInterop.mpv_set_option_string(_ctx, "glsl-shaders", shaderPath);
            }
            else
            {
                if (_ctx != IntPtr.Zero)
                    MpvInterop.mpv_set_property_string(_ctx, "glsl-shaders", shaderPath);
            }
        }

        // ─────────────────────────────────────────────────────────────
        // YENİ: Canlı TV Ultra-Fast Zapping & Tampon Yönetimi
        // ─────────────────────────────────────────────────────────────
        private bool _fastZapping = true;
        private bool _isLiveStream = false;

        public void SetFastZapping(bool enabled)
        {
            _fastZapping = enabled;
            ApplyDemuxerAndCachingProfile();
        }

        public void SetIsLiveStream(bool isLive)
        {
            _isLiveStream = isLive;
            ApplyDemuxerAndCachingProfile();
        }

        private void ApplyDemuxerAndCachingProfile(bool useOptionApi = false)
        {
            if (_ctx == IntPtr.Zero) return;
            try
            {
                if (_isLiveStream)
                {
                    // Canlı TV (FHD/4K/HEVC) Ultra-Stabil & Kesintisiz Tampon Profili:
                    // 1 MB probesize ve 6 sn akıllı tampon sayesinde yüksek çözünürlüklü akışlarda
                    // kare atlama, donma ve ses asenkronluğu tamamen önlenir.
                    if (useOptionApi)
                    {
                        MpvInterop.mpv_set_option_string(_ctx, "demuxer-lavf-analyzeduration", "1.0");
                        MpvInterop.mpv_set_option_string(_ctx, "demuxer-lavf-probesize", "1048576");
                        MpvInterop.mpv_set_option_string(_ctx, "demuxer-lavf-o", "reorder_queue_size=1000");
                        MpvInterop.mpv_set_option_string(_ctx, "cache", "yes");
                        MpvInterop.mpv_set_option_string(_ctx, "cache-secs", "6");
                        MpvInterop.mpv_set_option_string(_ctx, "demuxer-readahead-secs", "3");
                        MpvInterop.mpv_set_option_string(_ctx, "demuxer-max-bytes", "64MiB");
                    }
                    else
                    {
                        MpvInterop.mpv_set_property_string(_ctx, "demuxer-lavf-analyzeduration", "1.0");
                        MpvInterop.mpv_set_property_string(_ctx, "demuxer-lavf-probesize", "1048576");
                        MpvInterop.mpv_set_property_string(_ctx, "demuxer-lavf-o", "reorder_queue_size=1000");
                        MpvInterop.mpv_set_property_string(_ctx, "cache", "yes");
                        MpvInterop.mpv_set_property_string(_ctx, "cache-secs", "6");
                        MpvInterop.mpv_set_property_string(_ctx, "demuxer-readahead-secs", "3");
                        MpvInterop.mpv_set_property_string(_ctx, "demuxer-max-bytes", "64MiB");
                    }
                }
                else
                {
                    if (useOptionApi)
                    {
                        MpvInterop.mpv_set_option_string(_ctx, "demuxer-lavf-analyzeduration", "1.5");
                        MpvInterop.mpv_set_option_string(_ctx, "demuxer-lavf-probesize", "1048576");
                        MpvInterop.mpv_set_option_string(_ctx, "demuxer-lavf-o", "");
                        MpvInterop.mpv_set_option_string(_ctx, "cache", "yes");
                        MpvInterop.mpv_set_option_string(_ctx, "cache-secs", "15");
                        MpvInterop.mpv_set_option_string(_ctx, "demuxer-readahead-secs", "8");
                        MpvInterop.mpv_set_option_string(_ctx, "demuxer-max-bytes", "64MiB");
                    }
                    else
                    {
                        MpvInterop.mpv_set_property_string(_ctx, "demuxer-lavf-analyzeduration", "1.5");
                        MpvInterop.mpv_set_property_string(_ctx, "demuxer-lavf-probesize", "1048576");
                        MpvInterop.mpv_set_property_string(_ctx, "demuxer-lavf-o", "");
                        MpvInterop.mpv_set_property_string(_ctx, "cache", "yes");
                        MpvInterop.mpv_set_property_string(_ctx, "cache-secs", "15");
                        MpvInterop.mpv_set_property_string(_ctx, "demuxer-readahead-secs", "8");
                        MpvInterop.mpv_set_property_string(_ctx, "demuxer-max-bytes", "64MiB");
                    }
                }
            }
            catch { }
        }

        // ─────────────────────────────────────────────────────────────
        // YENİ: Akıllı Ses İşleme (EBU R128 Loudnorm & Gece Modu Kompresör)
        // ─────────────────────────────────────────────────────────────
        private string _audioEnhancement = "off";

        public void SetAudioEnhancement(string mode)
        {
            _audioEnhancement = string.IsNullOrEmpty(mode) ? "off" : mode;
            ApplyAudioEnhancement();
        }

        private void ApplyAudioEnhancement(bool useOptionApi = false)
        {
            string afFilter = _audioEnhancement switch
            {
                "loudnorm" => "loudnorm=I=-16:TP=-1.5:LRA=11",
                "night"    => "acompressor=threshold=-25dB:ratio=4:attack=200:release=1000",
                _          => ""
            };

            if (useOptionApi)
            {
                MpvInterop.mpv_set_option_string(_ctx, "af", afFilter);
            }
            else
            {
                if (_ctx != IntPtr.Zero)
                    MpvInterop.mpv_set_property_string(_ctx, "af", afFilter);
            }
        }

        public void SetAspectRatio(string? ratio)
        {
            _lastAspectRatio = ratio;
            ApplyAspectRatioAndResetPan(ratio);
        }

        private void ApplyAspectRatioAndResetPan(string? ratio)
        {
            if (_ctx == IntPtr.Zero) return;

            MpvInterop.mpv_set_property_string(_ctx, "video-align-x", "0");
            MpvInterop.mpv_set_property_string(_ctx, "video-align-y", "0");
            MpvInterop.mpv_set_property_string(_ctx, "video-pan-x", "0");
            MpvInterop.mpv_set_property_string(_ctx, "video-pan-y", "0");
            MpvInterop.mpv_set_property_string(_ctx, "video-zoom", "0");

            if (ratio == "fill")
            {
                MpvInterop.mpv_set_property_string(_ctx, "video-aspect-override", "-1");
                MpvInterop.mpv_set_property_string(_ctx, "panscan", "1.0");
            }
            else if (string.IsNullOrEmpty(ratio) || ratio == "original")
            {
                MpvInterop.mpv_set_property_string(_ctx, "video-aspect-override", "-1");
                MpvInterop.mpv_set_property_string(_ctx, "panscan", "0.0");
            }
            else
            {
                MpvInterop.mpv_set_property_string(_ctx, "panscan", "0.0");
                MpvInterop.mpv_set_property_string(_ctx, "video-aspect-override", ratio);
            }
        }

        public void SetVideoSurfaceVisible(bool visible)
        {
            _host.IsVisible = visible;
            _host.SetOverlayVisibility(visible);
        }

        public (uint Width, uint Height) GetVideoSize()
        {
            if (_ctx == IntPtr.Zero) return (0, 0);
            long w = 0, h = 0;
            MpvInterop.mpv_get_property(_ctx, "width", MpvInterop.mpv_format.MPV_FORMAT_INT64, ref w);
            MpvInterop.mpv_get_property(_ctx, "height", MpvInterop.mpv_format.MPV_FORMAT_INT64, ref h);
            return ((uint)Math.Max(0, w), (uint)Math.Max(0, h));
        }

        // ─────────────────────────────────────────────────────────────
        // OPTİMİZE EDİLMİŞ MPV BİTRATE HESAPLAYICISI:
        // 1. video-bitrate + audio-bitrate (anlık akış verisi)
        // 2. demux-bitrate (genel konteyner akış verisi) fallback
        // 3. Dalgalanmaları yumuşatan ve sıçramaları önleyen EMA algoritması
        // ─────────────────────────────────────────────────────────────
        public double GetBitrateKbps()
        {
            if (_ctx == IntPtr.Zero) return 0;
            try
            {
                double vBitrate = 0, aBitrate = 0, demuxBitrate = 0;
                MpvInterop.mpv_get_property(_ctx, "video-bitrate", MpvInterop.mpv_format.MPV_FORMAT_DOUBLE, ref vBitrate);
                MpvInterop.mpv_get_property(_ctx, "audio-bitrate", MpvInterop.mpv_format.MPV_FORMAT_DOUBLE, ref aBitrate);

                double totalBps = 0;
                if (vBitrate > 0)
                {
                    totalBps = vBitrate + (aBitrate > 0 ? aBitrate : 0);
                }
                else
                {
                    // Fallback: Demuxer seviyesindeki genel akış bitrate değeri
                    MpvInterop.mpv_get_property(_ctx, "demux-bitrate", MpvInterop.mpv_format.MPV_FORMAT_DOUBLE, ref demuxBitrate);
                    if (demuxBitrate > 0)
                    {
                        totalBps = demuxBitrate;
                    }
                    else if (aBitrate > 0)
                    {
                        totalBps = aBitrate;
                    }
                }

                if (totalBps > 0)
                {
                    double instKbps = totalBps / 1000.0;
                    if (instKbps >= 50 && instKbps <= 200000)
                    {
                        if (_smoothedBitrateKbps <= 0)
                            _smoothedBitrateKbps = instKbps;
                        else
                            _smoothedBitrateKbps = (_smoothedBitrateKbps * 0.75) + (instKbps * 0.25);

                        _lastBitrateTime = DateTime.Now;
                        return _smoothedBitrateKbps;
                    }
                }

                // Canlı yayın paket gecikmelerinde anlık 0 düşüşlerini yumuşat
                if (_smoothedBitrateKbps > 0 && (DateTime.Now - _lastBitrateTime).TotalSeconds < 3.5)
                {
                    return _smoothedBitrateKbps;
                }
            }
            catch { }

            return _smoothedBitrateKbps > 0 ? _smoothedBitrateKbps : 0;
        }

        public MediaInfoSnapshot GetMediaInfo()
        {
            var info = new MediaInfoSnapshot();
            if (_ctx == IntPtr.Zero) return info;

            var (w, h) = GetVideoSize();
            info.Width = w;
            info.Height = h;

            double fps = 0;
            MpvInterop.mpv_get_property(_ctx, "container-fps", MpvInterop.mpv_format.MPV_FORMAT_DOUBLE, ref fps);
            info.Fps = fps;

            info.VideoCodec = MapMpvCodecToShortName(GetStringProperty("video-codec"));
            info.AudioCodec = MapMpvCodecToShortName(GetStringProperty("audio-codec-name"));

            long channels = 0;
            MpvInterop.mpv_get_property(_ctx, "audio-params/channel-count", MpvInterop.mpv_format.MPV_FORMAT_INT64, ref channels);
            info.AudioChannels = (int)channels;

            info.BitrateKbps = GetBitrateKbps();

            return info;
        }

        private static string MapMpvCodecToShortName(string raw)
        {
            if (string.IsNullOrWhiteSpace(raw)) return "";

            string token = raw.Trim();
            int cut = token.IndexOfAny(new[] { '(', '/', ' ', ',' });
            if (cut > 0) token = token[..cut];
            token = token.Trim().ToLowerInvariant();

            return token switch
            {
                "h264" or "avc" or "avc1" => "H.264",
                "hevc" or "h265" => "HEVC",
                "vp9" => "VP9",
                "vp8" => "VP8",
                "av1" => "AV1",
                "mpeg2video" or "mpeg2" => "MPEG2",
                "mpeg4" or "mpeg4video" => "MPEG4",
                "mjpeg" => "MJPEG",
                "aac" => "AAC",
                "ac3" => "AC3",
                "eac3" => "E-AC3",
                "mp3" or "mp3float" => "MP3",
                "dts" => "DTS",
                "truehd" => "TrueHD",
                "opus" => "Opus",
                "vorbis" => "Vorbis",
                "flac" => "FLAC",
                "pcm_s16le" or "pcm_s16be" or "pcm_s24le" or "pcm" => "PCM",
                _ => token.Length > 0 ? token.ToUpperInvariant() : raw
            };
        }

        private static string PtrToStringUtf8(IntPtr ptr)
        {
            if (ptr == IntPtr.Zero) return "";
            int len = 0;
            while (Marshal.ReadByte(ptr, len) != 0) len++;
            if (len == 0) return "";
            byte[] buffer = new byte[len];
            Marshal.Copy(ptr, buffer, 0, len);
            return System.Text.Encoding.UTF8.GetString(buffer);
        }

        private void SendCommand(params string?[] args)
        {
            if (_ctx == IntPtr.Zero) return;
            var full = new string?[args.Length + 1];
            Array.Copy(args, full, args.Length);
            full[args.Length] = null;
            try
            {
                int rc = MpvInterop.mpv_command(_ctx, full);
                if (rc < 0)
                    Console.WriteLine($"mpv_command başarısız (rc={rc}): {string.Join(" ", args)}");
            }
            catch { }
        }

        private string GetStringProperty(string name)
        {
            if (_ctx == IntPtr.Zero) return "";
            var ptr = MpvInterop.mpv_get_property_string(_ctx, name);
            if (ptr == IntPtr.Zero) return "";
            string val = PtrToStringUtf8(ptr);
            MpvInterop.mpv_free(ptr);
            return val;
        }

        private int GetIntProperty(string name, int fallback)
        {
            if (_ctx == IntPtr.Zero) return fallback;
            long val = fallback;
            int rc = MpvInterop.mpv_get_property(_ctx, name, MpvInterop.mpv_format.MPV_FORMAT_INT64, ref val);
            return rc < 0 ? fallback : (int)val;
        }

        private static readonly Dictionary<string, string> _langDisplayNames = new(StringComparer.OrdinalIgnoreCase)
        {
            ["tur"] = "Türkçe", ["tr"] = "Türkçe",
            ["eng"] = "İngilizce", ["en"] = "İngilizce",
            ["ara"] = "Arapça", ["ar"] = "Arapça",
            ["ger"] = "Almanca", ["deu"] = "Almanca", ["de"] = "Almanca",
            ["fre"] = "Fransızca", ["fra"] = "Fransızca", ["fr"] = "Fransızca",
            ["spa"] = "İspanyolca", ["es"] = "İspanyolca",
            ["ita"] = "İtalyanca", ["it"] = "İtalyanca",
            ["rus"] = "Rusça", ["ru"] = "Rusça",
            ["por"] = "Portekizce", ["pt"] = "Portekizce",
            ["jpn"] = "Japonca", ["ja"] = "Japonca",
            ["kor"] = "Korece", ["ko"] = "Korece",
            ["chi"] = "Çince", ["zho"] = "Çince", ["zh"] = "Çince",
            ["hin"] = "Hintçe", ["hi"] = "Hintçe",
            ["nld"] = "Felemenkçe", ["dut"] = "Felemenkçe", ["nl"] = "Felemenkçe",
            ["swe"] = "İsveççe", ["sv"] = "İsveççe",
            ["nor"] = "Norveççe", ["no"] = "Norveççe",
            ["dan"] = "Danca", ["da"] = "Danca",
            ["fin"] = "Fince", ["fi"] = "Fince",
            ["pol"] = "Lehçe", ["pl"] = "Lehçe",
            ["ukr"] = "Ukraynaca", ["uk"] = "Ukraynaca",
            ["gre"] = "Yunanca", ["ell"] = "Yunanca", ["el"] = "Yunanca",
            ["heb"] = "İbranice", ["he"] = "İbranice",
            ["fas"] = "Farsça", ["per"] = "Farsça", ["fa"] = "Farsça",
            ["hun"] = "Macarca", ["hu"] = "Macarca",
            ["ron"] = "Rumence", ["rum"] = "Rumence", ["ro"] = "Rumence",
            ["bul"] = "Bulgarca", ["bg"] = "Bulgarca",
            ["cze"] = "Çekçe", ["ces"] = "Çekçe", ["cs"] = "Çekçe",
            ["und"] = "Bilinmeyen",
        };

        private static string LangCodeToDisplayName(string code)
        {
            if (string.IsNullOrWhiteSpace(code)) return code;
            code = code.Trim();
            return _langDisplayNames.TryGetValue(code, out var name) ? name : code;
        }

        private List<EngineTrackInfo> ReadTrackList(string wantedType)
        {
            var result = new List<EngineTrackInfo>();
            if (_ctx == IntPtr.Zero) return result;

            long count = 0;
            MpvInterop.mpv_get_property(_ctx, "track-list/count", MpvInterop.mpv_format.MPV_FORMAT_INT64, ref count);

            for (int i = 0; i < count; i++)
            {
                string type = GetStringProperty($"track-list/{i}/type");
                if (type != wantedType) continue;

                long id = 0;
                MpvInterop.mpv_get_property(_ctx, $"track-list/{i}/id", MpvInterop.mpv_format.MPV_FORMAT_INT64, ref id);

                string title = GetStringProperty($"track-list/{i}/title");
                if (string.IsNullOrEmpty(title))
                {
                    string lang = GetStringProperty($"track-list/{i}/lang");
                    title = !string.IsNullOrEmpty(lang) ? LangCodeToDisplayName(lang) : "";
                }
                if (string.IsNullOrEmpty(title)) title = $"{(wantedType == "audio" ? "Ses" : "Altyazı")} {id}";

                result.Add(new EngineTrackInfo { Id = (int)id, Name = title });
            }
            return result;
        }

        private void EventLoop()
        {
            while (!_stopping)
            {
                IntPtr evPtr = MpvInterop.mpv_wait_event(_ctx, 1.0);
                if (evPtr == IntPtr.Zero) continue;

                var ev = Marshal.PtrToStructure<MpvInterop.mpv_event>(evPtr);

                switch (ev.event_id)
                {
                    case MpvInterop.mpv_event_id.MPV_EVENT_SHUTDOWN:
                        return;

                    case MpvInterop.mpv_event_id.MPV_EVENT_END_FILE:
                        if (ev.data != IntPtr.Zero)
                        {
                            var endFile = Marshal.PtrToStructure<MpvInterop.mpv_event_end_file>(ev.data);
                            if (endFile.reason == 0)
                                Dispatcher.UIThread.Post(() => EndReached?.Invoke(this, EventArgs.Empty));
                        }
                        break;

                    case MpvInterop.mpv_event_id.MPV_EVENT_PROPERTY_CHANGE:
                        HandlePropertyChange(ev);
                        break;

                    case MpvInterop.mpv_event_id.MPV_EVENT_VIDEO_RECONFIG:
                    case MpvInterop.mpv_event_id.MPV_EVENT_AUDIO_RECONFIG:
                        Dispatcher.UIThread.Post(() => TracksChanged?.Invoke(this, EventArgs.Empty));
                        break;
                }
            }
        }

        private void HandlePropertyChange(MpvInterop.mpv_event ev)
        {
            if (ev.data == IntPtr.Zero) return;
            var prop = Marshal.PtrToStructure<MpvInterop.mpv_event_property>(ev.data);

            if (ev.reply_userdata == OBS_TIME_POS && prop.format == MpvInterop.mpv_format.MPV_FORMAT_DOUBLE && prop.data != IntPtr.Zero)
            {
                double sec = Marshal.PtrToStructure<double>(prop.data);
                long ms = (long)(sec * 1000.0);
                Dispatcher.UIThread.Post(() => TimeChanged?.Invoke(this, ms));
            }
            else if (ev.reply_userdata == OBS_DURATION && prop.format == MpvInterop.mpv_format.MPV_FORMAT_DOUBLE && prop.data != IntPtr.Zero)
            {
                double sec = Marshal.PtrToStructure<double>(prop.data);
                _lastKnownLengthMs = (long)(sec * 1000.0);
            }
            else if (ev.reply_userdata == OBS_TRACK_LIST_COUNT)
            {
                Dispatcher.UIThread.Post(() => TracksChanged?.Invoke(this, EventArgs.Empty));
            }
        }

        public void Dispose()
        {
            _stopping = true;
            _playGeneration++;
            _pendingPlayUrl = null;
            _pendingPlayStartMs = 0;
            try
            {
                if (_ctx != IntPtr.Zero)
                {
                    try { MpvInterop.mpv_command_string(_ctx, "stop\n"); } catch { }
                    MpvInterop.mpv_wakeup(_ctx);
                    _eventThread?.Join(TimeSpan.FromSeconds(2));
                    MpvInterop.mpv_terminate_destroy(_ctx);
                }
            }
            catch { }
            _ctx = IntPtr.Zero;
            _hwnd = IntPtr.Zero;
            IsInitialized = false;

            try
            {
                _host.HideForReload();
            }
            catch { }
        }
    }
}