// ============================================================
// PlayerEngines/MpvInterop.cs
//
// libmpv'nin C API'sinin (client.h) GlyphTV'nin ihtiyaç duyduğu alt
// kümesinin P/Invoke bildirimleri. Tam bir binding değildir — sadece
// MpvPlayerEngine'in kullandığı fonksiyonlar/enum'lar/struct'lar
// tanımlıdır. Daha geniş bir yüzey gerekirse (ör. mpv_node tabanlı
// karmaşık property'ler) buraya eklenmelidir.
//
// ÖNEMLİ (lisans): DllImport hedefi "mpv-2.dll" — bu DLL'in GlyphTV'ye
// gömülecek build'inin MUTLAKA LGPL modunda derlenmiş olması gerekir
// (shinchiro'nun Windows "LGPL" build'leri gibi). GPL modunda
// derlenmiş bir libmpv, GlyphTV'nin kapalı/farklı lisanslı
// dağıtımıyla uyumsuz olabilir. Bkz. https://mpv.io/installation/
//
// mpv_command'a verilen string dizisi native tarafta char* const*
// (null sonlandırıcılı) olarak beklenir; bu yüzden çağıran taraf
// diziye MUTLAKA bir null eleman eklemelidir (bkz. MpvPlayerEngine.
// SendCommand).
// ============================================================

using System;
using System.Runtime.InteropServices;

namespace GlyphTV.PlayerEngines
{
    internal static class MpvInterop
    {
        private const string LibName = "mpv-2.dll";

        public enum mpv_format
        {
            MPV_FORMAT_NONE = 0,
            MPV_FORMAT_STRING = 1,
            MPV_FORMAT_OSD_STRING = 2,
            MPV_FORMAT_FLAG = 3,
            MPV_FORMAT_INT64 = 4,
            MPV_FORMAT_DOUBLE = 5,
            MPV_FORMAT_NODE = 6,
            MPV_FORMAT_NODE_ARRAY = 7,
            MPV_FORMAT_NODE_MAP = 8,
            MPV_FORMAT_BYTE_ARRAY = 9
        }

        public enum mpv_event_id
        {
            MPV_EVENT_NONE = 0,
            MPV_EVENT_SHUTDOWN = 1,
            MPV_EVENT_LOG_MESSAGE = 2,
            MPV_EVENT_GET_PROPERTY_REPLY = 3,
            MPV_EVENT_SET_PROPERTY_REPLY = 4,
            MPV_EVENT_COMMAND_REPLY = 5,
            MPV_EVENT_START_FILE = 6,
            MPV_EVENT_END_FILE = 7,
            MPV_EVENT_FILE_LOADED = 8,
            MPV_EVENT_IDLE = 11,
            MPV_EVENT_TICK = 14,
            MPV_EVENT_CLIENT_MESSAGE = 16,
            MPV_EVENT_VIDEO_RECONFIG = 17,
            MPV_EVENT_AUDIO_RECONFIG = 18,
            MPV_EVENT_SEEK = 20,
            MPV_EVENT_PLAYBACK_RESTART = 21,
            MPV_EVENT_PROPERTY_CHANGE = 22,
            MPV_EVENT_QUEUE_OVERFLOW = 24,
            MPV_EVENT_HOOK = 25
        }

        [StructLayout(LayoutKind.Sequential)]
        public struct mpv_event
        {
            public mpv_event_id event_id;
            public int error;
            public ulong reply_userdata;
            public IntPtr data;
        }

        [StructLayout(LayoutKind.Sequential)]
        public struct mpv_event_property
        {
            public IntPtr name;      // char*
            public mpv_format format;
            public IntPtr data;      // format'a göre değişir (double*, int64*, char**)
        }

        [StructLayout(LayoutKind.Sequential)]
        public struct mpv_event_end_file
        {
            public int reason; // 0 = MPV_END_FILE_REASON_EOF
            public int error;
        }

        // ── Yaşam döngüsü ───────────────────────────────────────────
        [DllImport(LibName, CallingConvention = CallingConvention.Cdecl)]
        public static extern IntPtr mpv_create();

        [DllImport(LibName, CallingConvention = CallingConvention.Cdecl)]
        public static extern int mpv_initialize(IntPtr ctx);

        [DllImport(LibName, CallingConvention = CallingConvention.Cdecl)]
        public static extern void mpv_terminate_destroy(IntPtr ctx);

        // ── Ayarlar / property'ler ──────────────────────────────────
        [DllImport(LibName, CallingConvention = CallingConvention.Cdecl, CharSet = CharSet.Ansi)]
        public static extern int mpv_set_option_string(IntPtr ctx, string name, string data);

        [DllImport(LibName, CallingConvention = CallingConvention.Cdecl, CharSet = CharSet.Ansi)]
        public static extern int mpv_set_property_string(IntPtr ctx, string name, string data);

        [DllImport(LibName, CallingConvention = CallingConvention.Cdecl, CharSet = CharSet.Ansi)]
        public static extern int mpv_set_property(IntPtr ctx, string name, mpv_format format, ref double value);

        [DllImport(LibName, CallingConvention = CallingConvention.Cdecl, CharSet = CharSet.Ansi)]
        public static extern IntPtr mpv_get_property_string(IntPtr ctx, string name);

        [DllImport(LibName, CallingConvention = CallingConvention.Cdecl, CharSet = CharSet.Ansi)]
        public static extern int mpv_get_property(IntPtr ctx, string name, mpv_format format, ref long value);

        [DllImport(LibName, CallingConvention = CallingConvention.Cdecl, CharSet = CharSet.Ansi)]
        public static extern int mpv_get_property(IntPtr ctx, string name, mpv_format format, ref double value);

        [DllImport(LibName, CallingConvention = CallingConvention.Cdecl, CharSet = CharSet.Ansi)]
        public static extern int mpv_get_property(IntPtr ctx, string name, mpv_format format, ref int value); // FLAG

        [DllImport(LibName, CallingConvention = CallingConvention.Cdecl, CharSet = CharSet.Ansi)]
        public static extern int mpv_observe_property(IntPtr ctx, ulong reply_userdata, string name, mpv_format format);

        [DllImport(LibName, CallingConvention = CallingConvention.Cdecl)]
        public static extern void mpv_free(IntPtr data);

        // ── Komutlar ─────────────────────────────────────────────────
        [DllImport(LibName, CallingConvention = CallingConvention.Cdecl, CharSet = CharSet.Ansi)]
        public static extern int mpv_command(IntPtr ctx, [In] string?[] args);

        [DllImport(LibName, CallingConvention = CallingConvention.Cdecl, CharSet = CharSet.Ansi)]
        public static extern int mpv_command_string(IntPtr ctx, string args);

        // ── Event döngüsü ────────────────────────────────────────────
        [DllImport(LibName, CallingConvention = CallingConvention.Cdecl)]
        public static extern IntPtr mpv_wait_event(IntPtr ctx, double timeout);

        [DllImport(LibName, CallingConvention = CallingConvention.Cdecl)]
        public static extern void mpv_wakeup(IntPtr ctx);
    }
}
