// ============================================================
// PlayerEngines/MpvVideoHost.cs
//
// mpv'nin wid (window-id) video çıktısı için native Win32 HWND
// barındıran Avalonia NativeControlHost kontrolü.
// ============================================================

using Avalonia.Controls;
using Avalonia.Platform;
using System;
using System.Runtime.InteropServices;
using System.Threading;

namespace GlyphTV.PlayerEngines
{
    public class MpvVideoHost : NativeControlHost
    {
        public event Action<IntPtr>? HandleReady;

        private IntPtr _hwnd = IntPtr.Zero;
        private IntPtr _parentHwnd = IntPtr.Zero;
        private bool _isRevealed = false;
        private static bool _classRegistered = false;
        private static readonly Lock _classLock = new();
        private const string WindowClassName = "GlyphTV_MpvVideoHost_Class";

        public IntPtr Hwnd => _hwnd;

        public MpvVideoHost()
        {
            this.IsVisible = false;
            RegisterWindowClass();
        }

        protected override IPlatformHandle CreateNativeControlCore(IPlatformHandle parent)
        {
            _parentHwnd = parent.Handle;

            int style = WS_CHILD | WS_CLIPCHILDREN | WS_CLIPSIBLINGS;

            _hwnd = CreateWindowEx(
                0,
                WindowClassName,
                "",
                style,
                0, 0, 0, 0,
                _parentHwnd,
                IntPtr.Zero,
                GetModuleHandle(null),
                IntPtr.Zero
            );

            if (_hwnd != IntPtr.Zero)
            {
                FillBackgroundBlack(_hwnd);
                _ = ShowWindow(_hwnd, SW_HIDE);
                HandleReady?.Invoke(_hwnd);
            }

            return new PlatformHandle(_hwnd, "HWND");
        }

        protected override void DestroyNativeControlCore(IPlatformHandle control)
        {
            if (_hwnd != IntPtr.Zero)
            {
                _ = DestroyWindow(_hwnd);
                _hwnd = IntPtr.Zero;
            }
            base.DestroyNativeControlCore(control);
        }

        public void RevealVideoSurface()
        {
            if (_hwnd == IntPtr.Zero) return;
            _isRevealed = true;
            if (Avalonia.Threading.Dispatcher.UIThread.CheckAccess())
            {
                this.IsVisible = true;
            }
            else
            {
                Avalonia.Threading.Dispatcher.UIThread.Post(() => this.IsVisible = true);
            }
            _ = ShowWindow(_hwnd, SW_SHOW);
            _ = SetWindowPos(_hwnd, IntPtr.Zero, 0, 0, 0, 0,
                SWP_NOMOVE | SWP_NOSIZE | SWP_NOZORDER | SWP_FRAMECHANGED | SWP_SHOWWINDOW);
        }

        public void HideForReload()
        {
            if (_hwnd == IntPtr.Zero) return;
            _isRevealed = false;
            if (Avalonia.Threading.Dispatcher.UIThread.CheckAccess())
            {
                this.IsVisible = false;
            }
            else
            {
                Avalonia.Threading.Dispatcher.UIThread.Post(() => this.IsVisible = false);
            }
            FillBackgroundBlack(_hwnd);
            _ = ShowWindow(_hwnd, SW_HIDE);
            _ = SetWindowPos(_hwnd, IntPtr.Zero, 0, 0, 0, 0,
                SWP_NOMOVE | SWP_NOSIZE | SWP_NOZORDER | SWP_FRAMECHANGED | SWP_HIDEWINDOW);
        }

        public void SetOverlayVisibility(bool visible)
        {
            if (_hwnd == IntPtr.Zero) return;
            if (visible && _isRevealed)
            {
                if (Avalonia.Threading.Dispatcher.UIThread.CheckAccess())
                {
                    this.IsVisible = true;
                }
                else
                {
                    Avalonia.Threading.Dispatcher.UIThread.Post(() => this.IsVisible = true);
                }
                _ = ShowWindow(_hwnd, SW_SHOW);
            }
            else
            {
                if (Avalonia.Threading.Dispatcher.UIThread.CheckAccess())
                {
                    this.IsVisible = false;
                }
                else
                {
                    Avalonia.Threading.Dispatcher.UIThread.Post(() => this.IsVisible = false);
                }
                _ = ShowWindow(_hwnd, SW_HIDE);
            }
        }

        private static void FillBackgroundBlack(IntPtr hwnd)
        {
            if (hwnd == IntPtr.Zero) return;
            RECT rect = default;
            if (GetClientRect(hwnd, out rect) && rect.Right > 0 && rect.Bottom > 0)
            {
                IntPtr hdc = GetDC(hwnd);
                if (hdc != IntPtr.Zero)
                {
                    try
                    {
                        IntPtr blackBrush = GetStockObject(BLACK_BRUSH);
                        if (blackBrush != IntPtr.Zero)
                        {
                            _ = FillRect(hdc, ref rect, blackBrush);
                        }
                    }
                    finally
                    {
                        _ = ReleaseDC(hwnd, hdc);
                    }
                }
                _ = InvalidateRect(hwnd, IntPtr.Zero, true);
                _ = UpdateWindow(hwnd);
            }
        }

        private static void RegisterWindowClass()
        {
            lock (_classLock)
            {
                if (_classRegistered) return;

                var wndClass = new WNDCLASSEX
                {
                    cbSize = (uint)Marshal.SizeOf<WNDCLASSEX>(),
                    style = CS_HREDRAW | CS_VREDRAW,
                    lpfnWndProc = Marshal.GetFunctionPointerForDelegate(_wndProcDelegate),
                    cbClsExtra = 0,
                    cbWndExtra = 0,
                    hInstance = GetModuleHandle(null),
                    hIcon = IntPtr.Zero,
                    hCursor = IntPtr.Zero,
                    hbrBackground = GetStockObject(BLACK_BRUSH),
                    lpszMenuName = null,
                    lpszClassName = WindowClassName,
                    hIconSm = IntPtr.Zero
                };

                ushort atom = RegisterClassEx(ref wndClass);
                if (atom != 0)
                {
                    _classRegistered = true;
                }
            }
        }

        private delegate IntPtr WndProcDelegate(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam);
        private static readonly WndProcDelegate _wndProcDelegate = CustomWndProc;

        private static IntPtr CustomWndProc(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam)
        {
            const uint WM_ERASEBKGND = 0x0014;
            if (msg == WM_ERASEBKGND)
            {
                return (IntPtr)1;
            }
            return DefWindowProc(hWnd, msg, wParam, lParam);
        }

        #region Win32 Constants & Structs & P/Invoke

        private const int WS_CHILD = 0x40000000;
        private const int WS_VISIBLE = 0x10000000;
        private const int WS_CLIPCHILDREN = 0x02000000;
        private const int WS_CLIPSIBLINGS = 0x04000000;

        private const uint CS_VREDRAW = 0x0001;
        private const uint CS_HREDRAW = 0x0002;
        private const int BLACK_BRUSH = 4;

        private const int SW_HIDE = 0;
        private const int SW_SHOW = 5;

        private const uint SWP_NOSIZE = 0x0001;
        private const uint SWP_NOMOVE = 0x0002;
        private const uint SWP_NOZORDER = 0x0004;
        private const uint SWP_FRAMECHANGED = 0x0020;
        private const uint SWP_SHOWWINDOW = 0x0040;
        private const uint SWP_HIDEWINDOW = 0x0080;

        [StructLayout(LayoutKind.Sequential)]
        private struct RECT
        {
            public int Left;
            public int Top;
            public int Right;
            public int Bottom;
        }

        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        private struct WNDCLASSEX
        {
            public uint cbSize;
            public uint style;
            public IntPtr lpfnWndProc;
            public int cbClsExtra;
            public int cbWndExtra;
            public IntPtr hInstance;
            public IntPtr hIcon;
            public IntPtr hCursor;
            public IntPtr hbrBackground;
            [MarshalAs(UnmanagedType.LPWStr)]
            public string? lpszMenuName;
            [MarshalAs(UnmanagedType.LPWStr)]
            public string lpszClassName;
            public IntPtr hIconSm;
        }

        [DllImport("user32.dll", SetLastError = true, CharSet = CharSet.Unicode, EntryPoint = "RegisterClassExW")]
        private static extern ushort RegisterClassEx([In] ref WNDCLASSEX lpwcx);

        [DllImport("user32.dll", SetLastError = true, CharSet = CharSet.Unicode, EntryPoint = "UnregisterClassW")]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool UnregisterClass([MarshalAs(UnmanagedType.LPWStr)] string lpClassName, IntPtr hInstance);

        [DllImport("user32.dll", CharSet = CharSet.Unicode, EntryPoint = "DefWindowProcW")]
        private static extern IntPtr DefWindowProc(IntPtr hWnd, uint uMsg, IntPtr wParam, IntPtr lParam);

        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, EntryPoint = "GetModuleHandleW")]
        private static extern IntPtr GetModuleHandle([MarshalAs(UnmanagedType.LPWStr)] string? lpModuleName);

        [DllImport("user32.dll", SetLastError = true, CharSet = CharSet.Unicode, EntryPoint = "CreateWindowExW")]
        private static extern IntPtr CreateWindowEx(
            uint dwExStyle,
            [MarshalAs(UnmanagedType.LPWStr)] string lpClassName,
            [MarshalAs(UnmanagedType.LPWStr)] string lpWindowName,
            int dwStyle,
            int x, int y,
            int nWidth, int nHeight,
            IntPtr hWndParent,
            IntPtr hMenu,
            IntPtr hInstance,
            IntPtr lpParam);

        [DllImport("user32.dll", SetLastError = true)]
        private static extern bool DestroyWindow(IntPtr hWnd);

        [DllImport("user32.dll", SetLastError = true)]
        private static extern bool SetWindowPos(IntPtr hWnd, IntPtr hWndInsertAfter, int X, int Y, int cx, int cy, uint uFlags);

        [DllImport("user32.dll")]
        private static extern bool ShowWindow(IntPtr hWnd, int nCmdShow);

        [DllImport("user32.dll")]
        private static extern bool GetClientRect(IntPtr hWnd, out RECT lpRect);

        [DllImport("user32.dll")]
        private static extern bool InvalidateRect(IntPtr hWnd, IntPtr lpRect, bool bErase);

        [DllImport("user32.dll")]
        private static extern bool UpdateWindow(IntPtr hWnd);

        [DllImport("user32.dll")]
        private static extern IntPtr GetDC(IntPtr hWnd);

        [DllImport("user32.dll")]
        private static extern int ReleaseDC(IntPtr hWnd, IntPtr hDC);

        [DllImport("user32.dll")]
        private static extern int FillRect(IntPtr hDC, [In] ref RECT lprc, IntPtr hbr);

        [DllImport("gdi32.dll")]
        private static extern IntPtr GetStockObject(int fnObject);

        #endregion
    }
}