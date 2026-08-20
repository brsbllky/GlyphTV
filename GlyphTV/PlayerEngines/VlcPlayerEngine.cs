using Avalonia.Controls;
using Avalonia.Threading;
using LibVLCSharp.Avalonia;
using LibVLCSharp.Shared;
using System;
using System.Collections.Generic;
using System.Linq;

namespace GlyphTV.PlayerEngines
{
    public sealed class VlcPlayerEngine : IPlayerEngine
    {
        private static LibVLC? _sharedLibVLC;
        private static readonly object _libVlcLock = new object();

        private static bool _coreInitialized = false;

        public static LibVLC GetOrCreateLibVLC()
        {
            if (_sharedLibVLC != null) return _sharedLibVLC;
            lock (_libVlcLock)
            {
                if (_sharedLibVLC != null) return _sharedLibVLC;
                if (!_coreInitialized)
                {
                    try { Core.Initialize(); _coreInitialized = true; } catch { }
                }
                _sharedLibVLC = new LibVLC(
                    "--network-caching=300",
                    "--live-caching=800",
                    "--file-caching=300",
                    "--avcodec-hw=any",
                    "--avcodec-fast",
                    "--stats",
                    "--no-video-title-show",
                    "--no-snapshot-preview",
                    "--no-sub-autodetect-file",
                    "--no-osd",
                    "--no-overlay"
                );
                return _sharedLibVLC;
            }
        }

        public static void WarmUp()
        {
            try
            {
                GetOrCreateLibVLC();
            }
            catch (Exception ex)
            {
                Console.WriteLine("LibVLC WarmUp Hatası: " + ex.Message);
            }
        }

        private LibVLC? _libVLC;
        private MediaPlayer? _mediaPlayer;
        private readonly VideoView _videoView;

        // ─────────────────────────────────────────────────────────────
        // YENİ: Zaman pencereli delta-byte bitrate hesaplayıcısı
        // LibVLC'nin anlık InputBitrate'inin 0'a düşmesini ve yanlış ölçeklenmesini önler.
        // ─────────────────────────────────────────────────────────────
        private long _lastReadBytes = 0;
        private DateTime _lastBitrateCalcTime = DateTime.MinValue;
        private double _smoothedBitrateKbps = 0;

        public VlcPlayerEngine()
        {
            if (Dispatcher.UIThread.CheckAccess())
            {
                _videoView = new VideoView { IsVisible = false };
            }
            else
            {
                _videoView = Dispatcher.UIThread.Invoke(() => new VideoView { IsVisible = false });
            }
        }

        public PlayerEngineType EngineType => PlayerEngineType.Vlc;
        public Control VideoSurface => _videoView;
        public bool IsInitialized { get; private set; }

        public bool IsPlaying => _mediaPlayer?.IsPlaying ?? false;
        public bool IsSeekable => _mediaPlayer?.IsSeekable ?? false;

        public long Time
        {
            get => _mediaPlayer?.Time ?? 0;
            set { if (_mediaPlayer != null) _mediaPlayer.Time = value; }
        }

        public long Length => _mediaPlayer?.Length ?? 0;

        public int Volume
        {
            get => _mediaPlayer?.Volume ?? 100;
            set { if (_mediaPlayer != null) _mediaPlayer.Volume = value; }
        }

        public bool Mute
        {
            get => _mediaPlayer?.Mute ?? false;
            set { if (_mediaPlayer != null) _mediaPlayer.Mute = value; }
        }

        public float PlaybackRate
        {
            get => _mediaPlayer?.Rate ?? 1.0f;
            set { var mp = _mediaPlayer; if (mp != null) System.Threading.Tasks.Task.Run(() => mp.SetRate(value)); }
        }

        public IReadOnlyList<EngineTrackInfo> AudioTracks =>
            _mediaPlayer?.AudioTrackDescription?
                .Select(t => new EngineTrackInfo { Id = t.Id, Name = t.Name })
                .ToList() ?? new List<EngineTrackInfo>();

        public IReadOnlyList<EngineTrackInfo> SubtitleTracks =>
            _mediaPlayer?.SpuDescription?
                .Where(s => s.Id != -1)
                .Select(s => new EngineTrackInfo { Id = s.Id, Name = s.Name })
                .ToList() ?? new List<EngineTrackInfo>();

        public int ActiveAudioTrackId => _mediaPlayer?.AudioTrack ?? -1;
        public int ActiveSubtitleTrackId => _mediaPlayer?.Spu ?? -1;

        public event EventHandler<long>? TimeChanged;
        public event EventHandler? EndReached;
        public event EventHandler? TracksChanged;

        private string _hwDecodeMode = "auto";
        private bool _deinterlace = false;

        public void SetHardwareDecoding(string mode) => _hwDecodeMode = string.IsNullOrEmpty(mode) ? "auto" : mode;
        public void SetDeinterlace(bool enabled) => _deinterlace = enabled;

        private static string MapHwDecodeToVlc(string mode) => mode == "no" ? "none" : "any";

        private volatile bool _timeUpdatePending = false;

        public void Initialize()
        {
            if (IsInitialized) return;
            try
            {
                _libVLC = GetOrCreateLibVLC();

                _mediaPlayer = new MediaPlayer(_libVLC) { Volume = 100 };
                _mediaPlayer.TimeChanged += OnVlcTimeChanged;
                _mediaPlayer.ESAdded += (s, e) => TracksChanged?.Invoke(this, EventArgs.Empty);
                _mediaPlayer.EndReached += (s, e) => Dispatcher.UIThread.Post(() => EndReached?.Invoke(this, EventArgs.Empty));

                IsInitialized = true;

                Dispatcher.UIThread.Post(() =>
                {
                    if (_videoView.MediaPlayer == null) _videoView.MediaPlayer = _mediaPlayer;
                });
            }
            catch (Exception ex)
            {
                Console.WriteLine("VLC init hatası: " + ex.Message);
            }
        }

        private void OnVlcTimeChanged(object? sender, MediaPlayerTimeChangedEventArgs e)
        {
            if (_timeUpdatePending) return;
            _timeUpdatePending = true;
            Dispatcher.UIThread.InvokeAsync(() =>
            {
                try { TimeChanged?.Invoke(this, _mediaPlayer?.Time ?? 0); }
                finally { _timeUpdatePending = false; }
            }, DispatcherPriority.Background);
        }

        public void Play(string url, long startPositionMs = 0)
        {
            if (!IsInitialized) Initialize();
            if (_mediaPlayer == null || _libVLC == null) return;

            // Her yeni oynatmada bitrate sayaçlarını sıfırla
            _lastReadBytes = 0;
            _lastBitrateCalcTime = DateTime.MinValue;
            _smoothedBitrateKbps = 0;

            _videoView.IsVisible = true;
            if (_videoView.MediaPlayer == null) _videoView.MediaPlayer = _mediaPlayer;

            try
            {
                if (_mediaPlayer.IsPlaying)
                {
                    _mediaPlayer.Stop();
                }
            }
            catch { }

            _mediaPlayer.Media?.Dispose();

            var media = new Media(_libVLC, new Uri(url));
            if (startPositionMs > 0)
            {
                double startSec = startPositionMs / 1000.0;
                media.AddOption($":start-time={startSec.ToString(System.Globalization.CultureInfo.InvariantCulture)}");
            }

            media.AddOption($":avcodec-hw={MapHwDecodeToVlc(_hwDecodeMode)}");
            media.AddOption(_deinterlace ? ":deinterlace=1" : ":deinterlace=0");
            if (_deinterlace) media.AddOption(":deinterlace-mode=blend");

            _mediaPlayer.AspectRatio = "12:5";
            _mediaPlayer.Play(media);
        }

        public void PauseToggle()
        {
            if (_mediaPlayer == null) return;
            if (_mediaPlayer.IsPlaying) _mediaPlayer.Pause();
            else _mediaPlayer.Play();
        }

        public void Stop()
        {
            _mediaPlayer?.Stop();
            _mediaPlayer?.Media?.Dispose();
            _lastReadBytes = 0;
            _lastBitrateCalcTime = DateTime.MinValue;
            _smoothedBitrateKbps = 0;
        }

        public void SetAudioTrack(int id) { if (_mediaPlayer != null) _mediaPlayer.SetAudioTrack(id); }
        public void SetSubtitleTrack(int id) { if (_mediaPlayer != null) _mediaPlayer.SetSpu(id); }

        public void SetAspectRatio(string? ratio)
        {
            if (_mediaPlayer == null) return;
            if (string.IsNullOrEmpty(ratio) || ratio == "fill")
            {
                _mediaPlayer.AspectRatio = null;
                _mediaPlayer.Scale = 0;
            }
            else
            {
                _mediaPlayer.AspectRatio = ratio;
            }
        }

        public void SetVideoSurfaceVisible(bool visible)
        {
            _videoView.IsVisible = visible;
        }

        public (uint Width, uint Height) GetVideoSize()
        {
            uint w = 0, h = 0;
            try { _mediaPlayer?.Size(0, ref w, ref h); } catch { }
            return (w, h);
        }

        // ─────────────────────────────────────────────────────────────
        // OPTİMİZE EDİLMİŞ VLC BİTRATE HESAPLAYICISI:
        // 1. DemuxReadBytes öncelikli delta-byte analizi
        // 2. Paket patlamalarını (burst) filtreleyen Exponential Moving Average (EMA)
        // 3. Çok katmanlı yedek (fallback) sistemleri ile kesintisiz ve doğru ölçüm
        // ─────────────────────────────────────────────────────────────
        public double GetBitrateKbps()
        {
            try
            {
                if (_mediaPlayer?.Media == null) return 0;
                var stats = _mediaPlayer.Media.Statistics;

                long currentBytes = stats.DemuxReadBytes > 0 ? stats.DemuxReadBytes : stats.ReadBytes;
                DateTime now = DateTime.Now;

                if (currentBytes > 0)
                {
                    if (_lastBitrateCalcTime != DateTime.MinValue && _lastReadBytes > 0)
                    {
                        double elapsedSeconds = (now - _lastBitrateCalcTime).TotalSeconds;
                        long byteDiff = currentBytes - _lastReadBytes;

                        if (elapsedSeconds >= 0.4 && byteDiff >= 0)
                        {
                            if (byteDiff > 0)
                            {
                                // byte/s -> kbps: (bytes * 8) / 1000 / seconds
                                double instantaneousKbps = (byteDiff * 8.0) / (elapsedSeconds * 1000.0);

                                if (instantaneousKbps >= 50 && instantaneousKbps <= 200000)
                                {
                                    if (_smoothedBitrateKbps <= 0)
                                        _smoothedBitrateKbps = instantaneousKbps;
                                    else
                                        _smoothedBitrateKbps = (_smoothedBitrateKbps * 0.75) + (instantaneousKbps * 0.25);
                                }

                                _lastReadBytes = currentBytes;
                                _lastBitrateCalcTime = now;
                            }
                            else if (elapsedSeconds > 3.5)
                            {
                                // Uzun süre yeni veri gelmediyse yavaşça sönümlendir
                                _smoothedBitrateKbps *= 0.85;
                                if (_smoothedBitrateKbps < 50) _smoothedBitrateKbps = 0;
                            }
                        }
                    }
                    else
                    {
                        _lastReadBytes = currentBytes;
                        _lastBitrateCalcTime = now;
                    }
                }

                if (_smoothedBitrateKbps > 0)
                    return _smoothedBitrateKbps;

                // Fallback 1: DemuxBitrate / InputBitrate kontrolü
                if (stats.DemuxBitrate > 0)
                {
                    float db = stats.DemuxBitrate;
                    double kbps = db > 50000 ? db / 1000.0 : db * 8.0;
                    if (kbps > 0) return kbps;
                }
                if (stats.InputBitrate > 0)
                {
                    float ib = stats.InputBitrate;
                    double kbps = ib > 50000 ? ib / 1000.0 : ib * 8.0;
                    if (kbps > 0) return kbps;
                }

                // Fallback 2: Track metadata
                var tracks = _mediaPlayer.Media.Tracks;
                if (tracks != null)
                {
                    uint totalTrackBitrate = 0;
                    foreach (var t in tracks)
                    {
                        if (t.Bitrate > 0) totalTrackBitrate += t.Bitrate;
                    }
                    if (totalTrackBitrate > 0) return totalTrackBitrate / 1000.0;
                }
            }
            catch { }

            return _smoothedBitrateKbps > 0 ? _smoothedBitrateKbps : 0;
        }

        public MediaInfoSnapshot GetMediaInfo()
        {
            var info = new MediaInfoSnapshot();
            try
            {
                info.BitrateKbps = GetBitrateKbps();
                var tracks = _mediaPlayer?.Media?.Tracks;
                if (tracks == null) return info;

                foreach (var t in tracks)
                {
                    if (t.TrackType == TrackType.Video && info.Width == 0)
                    {
                        var v = t.Data.Video;
                        info.Width = v.Width;
                        info.Height = v.Height;
                        if (v.FrameRateDen > 0) info.Fps = (double)v.FrameRateNum / v.FrameRateDen;
                        info.VideoCodec = FourCcToString(t.Codec);
                    }
                    if (t.TrackType == TrackType.Audio && string.IsNullOrEmpty(info.AudioCodec))
                    {
                        info.AudioCodec = FourCcToString(t.Codec);
                        info.AudioChannels = (int)t.Data.Audio.Channels;
                    }
                }
            }
            catch { }
            return info;
        }

        private static string FourCcToString(uint fourcc)
        {
            try
            {
                if (fourcc == 0) return "";
                var bytes = BitConverter.GetBytes(fourcc);
                var str = System.Text.Encoding.ASCII.GetString(bytes).Trim('\0', ' ').ToUpperInvariant();
                return str switch
                {
                    "H264" or "AVC1" => "H.264",
                    "HEVC" or "H265" => "HEVC",
                    "VP09" or "VP90" => "VP9",
                    "AV01" => "AV1",
                    "MP4A" or "MP4V" => "AAC",
                    "AC3" or "A52" or "A52B" => "AC3",
                    "EAC3" => "E-AC3",
                    "MP3" or "MPGA" or "MP3L" => "MP3",
                    "DTS " or "DTS" => "DTS",
                    "OPUS" => "Opus",
                    "VORB" or "VORBIS" => "Vorbis",
                    _ => str
                };
            }
            catch { return ""; }
        }

        public void Dispose()
        {
            try
            {
                if (_mediaPlayer != null)
                {
                    _mediaPlayer.TimeChanged -= OnVlcTimeChanged;
                    _mediaPlayer.Stop();
                    _mediaPlayer.Media?.Dispose();
                }
            }
            catch { }
            try { _videoView.MediaPlayer = null; } catch { }
            try { _mediaPlayer?.Dispose(); } catch { }
            _mediaPlayer = null;

            lock (_libVlcLock)
            {
                try
                {
                    _sharedLibVLC?.Dispose();
                }
                catch { }
                _sharedLibVLC = null;
                _coreInitialized = false;
            }

            _libVLC = null;
            IsInitialized = false;
        }
    }
}