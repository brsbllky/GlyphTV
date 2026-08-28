using Avalonia.Controls;
using System;
using System.Collections.Generic;

namespace GlyphTV.PlayerEngines
{
    public enum PlayerEngineType
    {
        Vlc,
        Mpv
    }

    public sealed class EngineTrackInfo
    {
        public int Id { get; init; }
        public string Name { get; init; } = "";
    }

    public interface IPlayerEngine : IDisposable
    {
        PlayerEngineType EngineType { get; }
        Control VideoSurface { get; }

        bool IsInitialized { get; }
        bool IsPlaying { get; }
        bool IsSeekable { get; }

        long Time { get; set; }
        long Length { get; }
        int Volume { get; set; }
        bool Mute { get; set; }
        float PlaybackRate { get; set; }

        IReadOnlyList<EngineTrackInfo> AudioTracks { get; }
        IReadOnlyList<EngineTrackInfo> SubtitleTracks { get; }
        int ActiveAudioTrackId { get; }
        int ActiveSubtitleTrackId { get; }

        long SubtitleDelayMs { get; set; }
        void SetSubtitleDelay(long delayMs);

        long AudioDelayMs { get; set; }
        void SetAudioDelay(long delayMs);

        void Initialize();
        void Play(string url, long startPositionMs = 0);
        void PauseToggle();
        void Stop();

        void SetAudioTrack(int id);
        void SetSubtitleTrack(int id);
        void SetAspectRatio(string? ratio);
        void SetVideoSurfaceVisible(bool visible);

        (uint Width, uint Height) GetVideoSize();
        MediaInfoSnapshot GetMediaInfo();

        /// <summary>
        /// Anlık akış / dosya bitrate değerini (kbps cinsinden) döner.
        /// </summary>
        double GetBitrateKbps();

        void SetHardwareDecoding(string mode);
        void SetDeinterlace(bool enabled, string mode = "yadif2x");
        void SetDeinterlaceMode(string mode);

        void SetBrightness(int value);
        void SetContrast(int value);
        void SetSaturation(int value);
        void SetGamma(int value);

        void SetShaderMode(string mode);
        void SetFastZapping(bool enabled);
        void SetAudioEnhancement(string mode);
        void SetIsLiveStream(bool isLive);

        event EventHandler<long>? TimeChanged;
        event EventHandler? EndReached;
        event EventHandler? TracksChanged;
    }

    public sealed class MediaInfoSnapshot
    {
        public uint Width;
        public uint Height;
        public double Fps;
        public string VideoCodec = "";
        public string AudioCodec = "";
        public int AudioChannels;
        public double BitrateKbps; // YENİ: Anlık Bitrate
    }
}