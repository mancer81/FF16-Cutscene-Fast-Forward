using System.Globalization;
using System.Runtime.InteropServices;
using ff16.qol.cutscenefastforward.Configuration;
using ff16.qol.cutscenefastforward.Diagnostics;

namespace ff16.qol.cutscenefastforward.UI;

internal readonly record struct OverlayState(
    bool Visible,
    bool IsFastForwarding,
    double Speed,
    string ModeTag,
    string HintLabel,
    string DebugDetail,
    OverlayCorner Corner,
    double UserScale,
    double Opacity);

/// <summary>
/// Minimalist, click-through, per-pixel alpha Win32 layered overlay (pure Win32 + GDI+ flat API).
/// - Zero DX12 hooks (safe alongside FFXVIFix, DLSS/FSR FrameGen, ReShade, and Steam Overlay).
/// - Zero WindowsDesktop/WinForms managed assembly dependencies.
/// - Auto-scales cleanly for 1080p / 1440p / 4K and respects window visibility/occlusion in borderless fullscreen.
/// </summary>
internal sealed class CutsceneOverlay : IDisposable
{
    private const string WindowClassName = "FF16_CutsceneFF_Overlay_V1";
    private const uint WmAppUpdate = 0x8001;
    private const uint WmQuit = 0x0012;
    private const uint WmTimer = 0x0113;
    private const nint TimerIdPoll = 1;

    private readonly DebugLogger? _log;
    private readonly uint _currentPid = (uint)Environment.ProcessId;
    private readonly object _stateLock = new();
    private readonly Thread _uiThread;

    private OverlayState _desiredState;
    private OverlayState _renderedState;
    private Rect _lastClientRect;
    private Rect _lastPillRect;
    private nint _cachedGameHwnd;
    private bool _isWindowVisible;
    private bool _hasRenderedOnce;

    private volatile nint _hwnd;
    private volatile uint _uiThreadId;
    private volatile bool _disposed;

    // Keep WndProc delegate rooted for native lifetime
    private WndProcDelegate? _wndProc;

    public CutsceneOverlay(DebugLogger? log)
    {
        _log = log;
        _uiThread = new Thread(UiThreadMain)
        {
            Name = "CutsceneFF-Overlay",
            IsBackground = true
        };
        _uiThread.Start();
    }

    public void UpdateState(in OverlayState state)
    {
        if (_disposed)
            return;

        bool changed;
        lock (_stateLock)
        {
            changed = state != _desiredState;
            _desiredState = state;
        }

        if (changed && _hwnd != 0)
        {
            PostMessageW(_hwnd, WmAppUpdate, 0, 0);
        }
    }

    public void Dispose()
    {
        if (_disposed)
            return;
        _disposed = true;

        nint hwnd = _hwnd;
        if (hwnd != 0)
            PostMessageW(hwnd, WmQuit, 0, 0);
        else if (_uiThreadId != 0)
            PostThreadMessageW(_uiThreadId, WmQuit, 0, 0);

        if (_uiThread.IsAlive && Thread.CurrentThread != _uiThread)
            _uiThread.Join(500);
    }

    private void UiThreadMain()
    {
        _uiThreadId = GetCurrentThreadId();
        nint gdiplusToken = 0;

        try
        {
            var startupInput = new GdiplusStartupInput { GdiplusVersion = 1 };
            int gpStatus = GdiplusStartup(out gdiplusToken, ref startupInput, 0);
            if (gpStatus != 0)
            {
                _log?.Warn($"Overlay disabled: GdiplusStartup failed with status {gpStatus}");
                return;
            }

            _wndProc = WndProc;
            nint hInstance = GetModuleHandleW(null);

            var wc = new WndClassExW
            {
                cbSize = (uint)Marshal.SizeOf<WndClassExW>(),
                lpfnWndProc = Marshal.GetFunctionPointerForDelegate(_wndProc),
                hInstance = hInstance,
                lpszClassName = WindowClassName
            };

            ushort atom = RegisterClassExW(ref wc);
            if (atom == 0)
            {
                int err = Marshal.GetLastWin32Error();
                if (err != 1410) // ERROR_CLASS_ALREADY_EXISTS
                {
                    _log?.Warn($"Overlay disabled: RegisterClassExW failed (err={err})");
                    return;
                }
            }

            const uint exStyle =
                0x00080000u | // WS_EX_LAYERED
                0x00000020u | // WS_EX_TRANSPARENT (click-through)
                0x00000008u | // WS_EX_TOPMOST
                0x08000000u | // WS_EX_NOACTIVATE
                0x00000080u;  // WS_EX_TOOLWINDOW (hidden from Alt-Tab / taskbar)
            const uint style = 0x80000000u; // WS_POPUP

            nint hwnd = CreateWindowExW(
                exStyle,
                WindowClassName,
                string.Empty,
                style,
                0, 0, 1, 1,
                0, 0, hInstance, 0);

            if (hwnd == 0)
            {
                _log?.Warn($"Overlay disabled: CreateWindowExW failed (err={Marshal.GetLastWin32Error()})");
                return;
            }

            _hwnd = hwnd;
            _log?.Debug($"Overlay window initialized (HWND=0x{hwnd:X})");

            // Lightweight 150ms timer tracks window move/resize, focus, and occlusion changes
            SetTimer(hwnd, TimerIdPoll, 150, 0);

            while (!_disposed && GetMessageW(out Msg msg, 0, 0, 0) > 0)
            {
                TranslateMessage(ref msg);
                DispatchMessageW(ref msg);
            }

            KillTimer(hwnd, TimerIdPoll);
            DestroyWindow(hwnd);
            _hwnd = 0;
        }
        catch (Exception ex)
        {
            _log?.Error("Overlay UI thread crashed", ex);
        }
        finally
        {
            if (gdiplusToken != 0)
                GdiplusShutdown(gdiplusToken);
        }
    }

    private nint WndProc(nint hwnd, uint msg, nint wParam, nint lParam)
    {
        if (msg == WmAppUpdate || msg == WmTimer)
        {
            RefreshOverlay(hwnd);
            return 0;
        }
        return DefWindowProcW(hwnd, msg, wParam, lParam);
    }

    private void RefreshOverlay(nint hwnd)
    {
        OverlayState state;
        lock (_stateLock)
            state = _desiredState;

        if (!state.Visible)
        {
            HideIfVisible(hwnd);
            return;
        }

        nint gameHwnd = FindVisibleGameWindow(out bool isFocused);
        if (gameHwnd == 0 || !TryGetClientScreenRect(gameHwnd, out Rect clientRect))
        {
            HideIfVisible(hwnd);
            return;
        }

        int clientW = clientRect.Right - clientRect.Left;
        int clientH = clientRect.Bottom - clientRect.Top;
        if (clientW < 200 || clientH < 200)
        {
            HideIfVisible(hwnd);
            return;
        }

        // When FFXVI is visible in borderless fullscreen on one monitor while Windows focus is elsewhere
        // (e.g. user Alt-Tabbed and resumed with a DualSense controller), keep the HUD visible unless
        // another non-transparent window actually covers the pill area on screen.
        if (!isFocused)
        {
            Rect checkRect = _lastPillRect.Right > _lastPillRect.Left && clientRect == _lastClientRect
                ? _lastPillRect
                : EstimateCornerProbeRect(clientRect, clientW, clientH, state.Corner);

            if (IsRegionOccludedAboveWindow(gameHwnd, checkRect))
            {
                HideIfVisible(hwnd);
                return;
            }
        }

        bool needsRedraw = !_hasRenderedOnce || state != _renderedState || clientRect != _lastClientRect || !_isWindowVisible;
        if (!needsRedraw)
            return;

        RenderAndPresent(hwnd, state, clientRect, clientW, clientH);
        _renderedState = state;
        _lastClientRect = clientRect;
        _hasRenderedOnce = true;
    }

    private void HideIfVisible(nint hwnd)
    {
        if (!_isWindowVisible)
            return;
        ShowWindow(hwnd, 0); // SW_HIDE
        _isWindowVisible = false;
    }

    private void RenderAndPresent(nint hwnd, in OverlayState state, in Rect clientRect, int clientW, int clientH)
    {
        // Scale proportionally to 1080p baseline, then apply user scale (default 0.75 -> compact & readable)
        float baseScale = Math.Clamp(clientH / 1080.0f, 0.85f, 2.5f);
        float userScale = (float)Math.Clamp(state.UserScale > 0.01 ? state.UserScale : 0.75, 0.40, 1.50);
        float scale = baseScale * userScale;

        bool hasDebug = !string.IsNullOrEmpty(state.DebugDetail);
        bool hasModeTag = !string.IsNullOrEmpty(state.ModeTag);
        string mainText = state.IsFastForwarding
            ? (hasModeTag
                ? string.Format(CultureInfo.InvariantCulture, "\u00BB\u00BB {0:0.##}x \u00B7 {1}", state.Speed, state.ModeTag)
                : string.Format(CultureInfo.InvariantCulture, "\u00BB\u00BB {0:0.##}x", state.Speed))
            : (hasModeTag
                ? string.Format(CultureInfo.InvariantCulture, "\u00BB {0} \u00B7 {1} \u00B7 {2:0.##}x", state.ModeTag, state.HintLabel, state.Speed)
                : string.Format(CultureInfo.InvariantCulture, "\u00BB {0} \u00B7 {1:0.##}x", state.HintLabel, state.Speed));

        float mainFontSize = (state.IsFastForwarding ? 17.0f : 14.2f) * scale;
        float subFontSize = 10.5f * scale;
        float padX = (state.IsFastForwarding ? 15.0f : 13.5f) * scale;
        float padY = 6.5f * scale;
        float subGap = hasDebug ? (2.0f * scale) : 0f;

        int estMainW = (int)Math.Ceiling(mainText.Length * mainFontSize * 0.58f);
        int estSubW = hasDebug ? (int)Math.Ceiling(state.DebugDetail.Length * subFontSize * 0.58f) : 0;
        int minW = Math.Max(68, (int)((state.IsFastForwarding ? 86 : 124) * scale));
        int maxW = Math.Max(minW, (int)(540 * scale));
        int width = Math.Clamp(Math.Max(estMainW, estSubW) + (int)(padX * 2.1f), minW, maxW);
        int mainLineH = Math.Max(14, (int)Math.Ceiling(mainFontSize * 1.35f));
        int subLineH = hasDebug ? Math.Max(11, (int)Math.Ceiling(subFontSize * 1.30f)) : 0;
        int height = Math.Max(22, mainLineH + subLineH + (int)(padY * 2.0f + subGap));

        int marginX = (int)(clientW * 0.024f);
        int marginY = (int)(clientH * 0.042f);

        int posX = state.Corner is OverlayCorner.BottomRight or OverlayCorner.TopRight
            ? clientRect.Right - marginX - width
            : clientRect.Left + marginX;

        int posY = state.Corner is OverlayCorner.BottomRight or OverlayCorner.BottomLeft
            ? clientRect.Bottom - marginY - height
            : clientRect.Top + marginY;

        nint screenDc = GetDC(0);
        nint memDc = CreateCompatibleDC(screenDc);
        nint hBitmap = 0;
        nint oldBitmap = 0;
        nint gdipBitmap = 0;
        nint graphics = 0;

        try
        {
            var bmi = new BitmapInfoHeader
            {
                biSize = (uint)Marshal.SizeOf<BitmapInfoHeader>(),
                biWidth = width,
                biHeight = -height, // top-down DIB
                biPlanes = 1,
                biBitCount = 32,
                biCompression = 0 // BI_RGB
            };

            hBitmap = CreateDIBSection(memDc, ref bmi, 0, out nint dibBits, 0, 0);
            if (hBitmap == 0 || dibBits == 0)
                return;

            oldBitmap = SelectObject(memDc, hBitmap);

            // PixelFormat32bppPARGB (0x000E200B) writes premultiplied alpha required by UpdateLayeredWindow
            if (GdipCreateBitmapFromScan0(width, height, width * 4, 0x000E200B, dibBits, out gdipBitmap) != 0)
                return;
            if (GdipGetImageGraphicsContext(gdipBitmap, out graphics) != 0)
                return;

            GdipGraphicsClear(graphics, 0x00000000u);
            GdipSetSmoothingMode(graphics, 4);        // SmoothingModeAntiAlias
            GdipSetTextRenderingHint(graphics, 4);    // TextRenderingHintAntiAlias

            // Discreet FFXVI obsidian + warm gold palette
            uint bgArgb = state.IsFastForwarding ? 0xE2201607u : 0x8E0D1017u;
            uint borderArgb = state.IsFastForwarding ? 0xF2F3C65Fu : 0x6CC4A462u;
            uint textArgb = state.IsFastForwarding ? 0xFFFFE699u : 0xDDEAE3D2u;
            uint subArgb = state.IsFastForwarding ? 0xD2DFC280u : 0xA4B5B8C2u;

            float borderW = Math.Max(1.0f, (state.IsFastForwarding ? 1.8f : 1.1f) * scale);
            float inset = borderW + 0.75f;
            float radius = Math.Min(height * 0.45f, 12.0f * scale);

            DrawRoundedPill(graphics, inset, inset, width - inset * 2f, height - inset * 2f, radius, bgArgb, borderArgb, borderW);

            DrawCenteredText(
                graphics,
                mainText,
                mainFontSize,
                bold: state.IsFastForwarding,
                textArgb,
                new RectF(padX * 0.5f, padY, width - padX, mainLineH));

            if (hasDebug)
            {
                DrawCenteredText(
                    graphics,
                    state.DebugDetail,
                    subFontSize,
                    bold: false,
                    subArgb,
                    new RectF(padX * 0.5f, padY + mainLineH + subGap, width - padX, subLineH));
            }

            GdipFlush(graphics, 1); // FlushIntentionSync

            double effectiveOpacity = state.IsFastForwarding
                ? Math.Clamp(Math.Max(state.Opacity, 0.92), 0.25, 1.0)
                : Math.Clamp(state.Opacity, 0.25, 1.0);

            var ptDst = new Point { X = posX, Y = posY };
            var size = new Size { Width = width, Height = height };
            var ptSrc = new Point { X = 0, Y = 0 };
            var blend = new BlendFunction
            {
                BlendOp = 0,          // AC_SRC_OVER
                BlendFlags = 0,
                SourceConstantAlpha = (byte)Math.Round(effectiveOpacity * 255.0),
                AlphaFormat = 1       // AC_SRC_ALPHA
            };

            UpdateLayeredWindow(hwnd, screenDc, ref ptDst, ref size, memDc, ref ptSrc, 0, ref blend, 2); // ULW_ALPHA

            // Ensure topmost without ever activating/stealing focus
            const uint swpFlags = 0x0010u | 0x0040u | 0x0200u; // SWP_NOACTIVATE | SWP_SHOWWINDOW | SWP_NOOWNERZORDER
            SetWindowPos(hwnd, -1, posX, posY, width, height, swpFlags);
            _lastPillRect = new Rect { Left = posX, Top = posY, Right = posX + width, Bottom = posY + height };
            _isWindowVisible = true;
        }
        finally
        {
            if (graphics != 0) GdipDeleteGraphics(graphics);
            if (gdipBitmap != 0) GdipDisposeImage(gdipBitmap);
            if (oldBitmap != 0) SelectObject(memDc, oldBitmap);
            if (hBitmap != 0) DeleteObject(hBitmap);
            if (memDc != 0) DeleteDC(memDc);
            if (screenDc != 0) ReleaseDC(0, screenDc);
        }
    }

    private static void DrawRoundedPill(nint graphics, float x, float y, float w, float h, float r, uint fillArgb, uint strokeArgb, float strokeWidth)
    {
        if (GdipCreatePath(0, out nint path) != 0)
            return;

        nint brush = 0;
        nint pen = 0;
        try
        {
            float d = r * 2f;
            GdipAddPathArc(path, x, y, d, d, 180f, 90f);
            GdipAddPathArc(path, x + w - d, y, d, d, 270f, 90f);
            GdipAddPathArc(path, x + w - d, y + h - d, d, d, 0f, 90f);
            GdipAddPathArc(path, x, y + h - d, d, d, 90f, 90f);
            GdipClosePathFigure(path);

            if (GdipCreateSolidFill(fillArgb, out brush) == 0)
                GdipFillPath(graphics, brush, path);

            if (GdipCreatePen1(strokeArgb, strokeWidth, 2 /* UnitPixel */, out pen) == 0)
                GdipDrawPath(graphics, pen, path);
        }
        finally
        {
            if (pen != 0) GdipDeletePen(pen);
            if (brush != 0) GdipDeleteBrush(brush);
            GdipDeletePath(path);
        }
    }

    private static void DrawCenteredText(nint graphics, string text, float fontSizePx, bool bold, uint argb, RectF layout)
    {
        nint family = 0;
        nint font = 0;
        nint brush = 0;
        nint format = 0;
        try
        {
            if (GdipCreateFontFamilyFromName("Segoe UI", 0, out family) != 0)
            {
                if (GdipGetGenericFontFamilySansSerif(out family) != 0)
                    return;
            }

            int style = bold ? 1 : 0; // FontStyleBold : FontStyleRegular
            if (GdipCreateFont(family, fontSizePx, style, 2 /* UnitPixel */, out font) != 0)
                return;

            if (GdipCreateSolidFill(argb, out brush) != 0)
                return;

            if (GdipCreateStringFormat(0, 0, out format) == 0)
            {
                GdipSetStringFormatAlign(format, 1);     // Center horizontally
                GdipSetStringFormatLineAlign(format, 1); // Center vertically
            }

            GdipDrawString(graphics, text, text.Length, font, ref layout, format, brush);
        }
        finally
        {
            if (format != 0) GdipDeleteStringFormat(format);
            if (brush != 0) GdipDeleteBrush(brush);
            if (font != 0) GdipDeleteFont(font);
            if (family != 0) GdipDeleteFontFamily(family);
        }
    }

    private nint FindVisibleGameWindow(out bool isFocused)
    {
        isFocused = false;
        nint fg = GetForegroundWindow();
        if (fg != 0 && fg != _hwnd && !IsIconic(fg))
        {
            GetWindowThreadProcessId(fg, out uint pid);
            if (pid == _currentPid && IsWindowVisible(fg))
            {
                _cachedGameHwnd = fg;
                isFocused = true;
                return fg;
            }
        }

        if (IsValidVisibleGameWindow(_cachedGameHwnd))
            return _cachedGameHwnd;

        nint bestHwnd = 0;
        long bestArea = 0;
        EnumWindows((w, _) =>
        {
            if (!IsValidVisibleGameWindow(w))
                return true;

            if (GetClientRect(w, out Rect r))
            {
                long area = (long)(r.Right - r.Left) * (r.Bottom - r.Top);
                if (area > bestArea)
                {
                    bestArea = area;
                    bestHwnd = w;
                }
            }
            return true;
        }, 0);

        _cachedGameHwnd = bestHwnd;
        return bestHwnd;
    }

    private bool IsValidVisibleGameWindow(nint hwnd)
    {
        if (hwnd == 0 || hwnd == _hwnd || !IsWindow(hwnd) || !IsWindowVisible(hwnd) || IsIconic(hwnd))
            return false;

        GetWindowThreadProcessId(hwnd, out uint pid);
        if (pid != _currentPid)
            return false;

        if (!GetClientRect(hwnd, out Rect r))
            return false;

        return (r.Right - r.Left) >= 200 && (r.Bottom - r.Top) >= 200;
    }

    private static Rect EstimateCornerProbeRect(in Rect clientRect, int clientW, int clientH, OverlayCorner corner)
    {
        int marginX = (int)(clientW * 0.024f);
        int marginY = (int)(clientH * 0.042f);
        int probeW = Math.Clamp((int)(clientW * 0.08f), 100, 300);
        int probeH = Math.Clamp((int)(clientH * 0.04f), 32, 96);

        int left = corner is OverlayCorner.BottomRight or OverlayCorner.TopRight
            ? clientRect.Right - marginX - probeW
            : clientRect.Left + marginX;
        int top = corner is OverlayCorner.BottomRight or OverlayCorner.BottomLeft
            ? clientRect.Bottom - marginY - probeH
            : clientRect.Top + marginY;

        return new Rect { Left = left, Top = top, Right = left + probeW, Bottom = top + probeH };
    }

    private bool IsRegionOccludedAboveWindow(nint gameHwnd, in Rect targetRect)
    {
        const uint gwHwndPrev = 3; // Next window higher in Z-order
        const int gwlExStyle = -20;
        const long wsExLayered = 0x00080000L;
        const long wsExTransparent = 0x00000020L;
        const uint dwmwaCloaked = 14;

        int guard = 0;
        for (nint w = GetWindow(gameHwnd, gwHwndPrev); w != 0 && guard < 256; w = GetWindow(w, gwHwndPrev), guard++)
        {
            if (w == _hwnd || !IsWindowVisible(w) || IsIconic(w))
                continue;

            // Ignore click-through layered overlays (e.g. Steam/RTSS/our own overlay)
            long exStyle = GetWindowLongPtrW(w, gwlExStyle);
            if ((exStyle & (wsExLayered | wsExTransparent)) == (wsExLayered | wsExTransparent))
                continue;

            // Ignore cloaked UWP/Windows background shell windows
            if (DwmGetWindowAttribute(w, dwmwaCloaked, out int cloaked, sizeof(int)) == 0 && cloaked != 0)
                continue;

            if (!GetWindowRect(w, out Rect r) || r.Right <= r.Left || r.Bottom <= r.Top)
                continue;

            if (r.Left < targetRect.Right && r.Right > targetRect.Left &&
                r.Top < targetRect.Bottom && r.Bottom > targetRect.Top)
            {
                return true;
            }
        }

        return false;
    }

    private static bool TryGetClientScreenRect(nint gameHwnd, out Rect screenRect)
    {
        screenRect = default;
        if (!GetClientRect(gameHwnd, out Rect client))
            return false;

        var tl = new Point { X = client.Left, Y = client.Top };
        var br = new Point { X = client.Right, Y = client.Bottom };
        if (!ClientToScreen(gameHwnd, ref tl) || !ClientToScreen(gameHwnd, ref br))
            return false;

        screenRect = new Rect { Left = tl.X, Top = tl.Y, Right = br.X, Bottom = br.Y };
        return true;
    }

    // ---- Win32 & GDI+ Flat API P/Invoke ----

    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    private delegate nint WndProcDelegate(nint hwnd, uint msg, nint wParam, nint lParam);

    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    private delegate bool EnumWindowsProc(nint hwnd, nint lParam);

    [StructLayout(LayoutKind.Sequential)]
    private struct Point { public int X, Y; }

    [StructLayout(LayoutKind.Sequential)]
    private struct Size { public int Width, Height; }

    [StructLayout(LayoutKind.Sequential)]
    private struct Rect : IEquatable<Rect>
    {
        public int Left, Top, Right, Bottom;
        public bool Equals(Rect other) => Left == other.Left && Top == other.Top && Right == other.Right && Bottom == other.Bottom;
        public override bool Equals(object? obj) => obj is Rect r && Equals(r);
        public override int GetHashCode() => HashCode.Combine(Left, Top, Right, Bottom);
        public static bool operator ==(Rect a, Rect b) => a.Equals(b);
        public static bool operator !=(Rect a, Rect b) => !a.Equals(b);
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct RectF
    {
        public float X, Y, Width, Height;
        public RectF(float x, float y, float w, float h) { X = x; Y = y; Width = w; Height = h; }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct BlendFunction
    {
        public byte BlendOp;
        public byte BlendFlags;
        public byte SourceConstantAlpha;
        public byte AlphaFormat;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct BitmapInfoHeader
    {
        public uint biSize;
        public int biWidth;
        public int biHeight;
        public ushort biPlanes;
        public ushort biBitCount;
        public uint biCompression;
        public uint biSizeImage;
        public int biXPelsPerMeter;
        public int biYPelsPerMeter;
        public uint biClrUsed;
        public uint biClrImportant;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct WndClassExW
    {
        public uint cbSize;
        public uint style;
        public nint lpfnWndProc;
        public int cbClsExtra;
        public int cbWndExtra;
        public nint hInstance;
        public nint hIcon;
        public nint hCursor;
        public nint hbrBackground;
        public string? lpszMenuName;
        public string lpszClassName;
        public nint hIconSm;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct Msg
    {
        public nint hwnd;
        public uint message;
        public nint wParam;
        public nint lParam;
        public uint time;
        public Point pt;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct GdiplusStartupInput
    {
        public uint GdiplusVersion;
        public nint DebugEventCallback;
        public int SuppressBackgroundThread;
        public int SuppressExternalCodecs;
    }

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
    private static extern nint GetModuleHandleW(string? lpModuleName);

    [DllImport("kernel32.dll")]
    private static extern uint GetCurrentThreadId();

    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern ushort RegisterClassExW(ref WndClassExW lpWndClass);

    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern nint CreateWindowExW(
        uint dwExStyle, string lpClassName, string lpWindowName, uint dwStyle,
        int x, int y, int nWidth, int nHeight,
        nint hWndParent, nint hMenu, nint hInstance, nint lpParam);

    [DllImport("user32.dll")]
    private static extern bool DestroyWindow(nint hWnd);

    [DllImport("user32.dll")]
    private static extern bool ShowWindow(nint hWnd, int nCmdShow);

    [DllImport("user32.dll")]
    private static extern bool SetWindowPos(nint hWnd, nint hWndInsertAfter, int x, int y, int cx, int cy, uint uFlags);

    [DllImport("user32.dll")]
    private static extern nint SetTimer(nint hWnd, nint nIDEvent, uint uElapse, nint lpTimerFunc);

    [DllImport("user32.dll")]
    private static extern bool KillTimer(nint hWnd, nint uIDEvent);

    [DllImport("user32.dll")]
    private static extern int GetMessageW(out Msg lpMsg, nint hWnd, uint wMsgFilterMin, uint wMsgFilterMax);

    [DllImport("user32.dll")]
    private static extern bool TranslateMessage(ref Msg lpMsg);

    [DllImport("user32.dll")]
    private static extern nint DispatchMessageW(ref Msg lpMsg);

    [DllImport("user32.dll")]
    private static extern nint DefWindowProcW(nint hWnd, uint msg, nint wParam, nint lParam);

    [DllImport("user32.dll")]
    private static extern bool PostMessageW(nint hWnd, uint msg, nint wParam, nint lParam);

    [DllImport("user32.dll")]
    private static extern bool PostThreadMessageW(uint idThread, uint msg, nint wParam, nint lParam);

    [DllImport("user32.dll")]
    private static extern nint GetForegroundWindow();

    [DllImport("user32.dll")]
    private static extern bool EnumWindows(EnumWindowsProc lpEnumFunc, nint lParam);

    [DllImport("user32.dll")]
    private static extern bool IsWindow(nint hWnd);

    [DllImport("user32.dll")]
    private static extern bool IsWindowVisible(nint hWnd);

    [DllImport("user32.dll")]
    private static extern bool IsIconic(nint hWnd);

    [DllImport("user32.dll")]
    private static extern nint GetWindow(nint hWnd, uint uCmd);

    [DllImport("user32.dll")]
    private static extern bool GetWindowRect(nint hWnd, out Rect lpRect);

    [DllImport("user32.dll", EntryPoint = "GetWindowLongPtrW")]
    private static extern long GetWindowLongPtrW(nint hWnd, int nIndex);

    [DllImport("dwmapi.dll")]
    private static extern int DwmGetWindowAttribute(nint hwnd, uint dwAttribute, out int pvAttribute, int cbAttribute);

    [DllImport("user32.dll")]
    private static extern uint GetWindowThreadProcessId(nint hWnd, out uint lpdwProcessId);

    [DllImport("user32.dll")]
    private static extern bool GetClientRect(nint hWnd, out Rect lpRect);

    [DllImport("user32.dll")]
    private static extern bool ClientToScreen(nint hWnd, ref Point lpPoint);

    [DllImport("user32.dll")]
    private static extern nint GetDC(nint hWnd);

    [DllImport("user32.dll")]
    private static extern int ReleaseDC(nint hWnd, nint hDC);

    [DllImport("user32.dll")]
    private static extern bool UpdateLayeredWindow(
        nint hwnd, nint hdcDst, ref Point pptDst, ref Size psize,
        nint hdcSrc, ref Point pptSrc, uint crKey, ref BlendFunction pblend, uint dwFlags);

    [DllImport("gdi32.dll")]
    private static extern nint CreateCompatibleDC(nint hDC);

    [DllImport("gdi32.dll")]
    private static extern bool DeleteDC(nint hdc);

    [DllImport("gdi32.dll")]
    private static extern nint SelectObject(nint hdc, nint h);

    [DllImport("gdi32.dll")]
    private static extern bool DeleteObject(nint ho);

    [DllImport("gdi32.dll")]
    private static extern nint CreateDIBSection(nint hdc, ref BitmapInfoHeader pbmi, uint usage, out nint ppvBits, nint hSection, uint offset);

    [DllImport("gdiplus.dll")]
    private static extern int GdiplusStartup(out nint token, ref GdiplusStartupInput input, nint output);

    [DllImport("gdiplus.dll")]
    private static extern void GdiplusShutdown(nint token);

    [DllImport("gdiplus.dll")]
    private static extern int GdipCreateBitmapFromScan0(int width, int height, int stride, int format, nint scan0, out nint bitmap);

    [DllImport("gdiplus.dll")]
    private static extern int GdipDisposeImage(nint image);

    [DllImport("gdiplus.dll")]
    private static extern int GdipGetImageGraphicsContext(nint image, out nint graphics);

    [DllImport("gdiplus.dll")]
    private static extern int GdipDeleteGraphics(nint graphics);

    [DllImport("gdiplus.dll")]
    private static extern int GdipGraphicsClear(nint graphics, uint color);

    [DllImport("gdiplus.dll")]
    private static extern int GdipSetSmoothingMode(nint graphics, int smoothingMode);

    [DllImport("gdiplus.dll")]
    private static extern int GdipSetTextRenderingHint(nint graphics, int hint);

    [DllImport("gdiplus.dll")]
    private static extern int GdipFlush(nint graphics, int intention);

    [DllImport("gdiplus.dll")]
    private static extern int GdipCreatePath(int brushMode, out nint path);

    [DllImport("gdiplus.dll")]
    private static extern int GdipDeletePath(nint path);

    [DllImport("gdiplus.dll")]
    private static extern int GdipAddPathArc(nint path, float x, float y, float width, float height, float startAngle, float sweepAngle);

    [DllImport("gdiplus.dll")]
    private static extern int GdipClosePathFigure(nint path);

    [DllImport("gdiplus.dll")]
    private static extern int GdipCreateSolidFill(uint color, out nint brush);

    [DllImport("gdiplus.dll")]
    private static extern int GdipDeleteBrush(nint brush);

    [DllImport("gdiplus.dll")]
    private static extern int GdipFillPath(nint graphics, nint brush, nint path);

    [DllImport("gdiplus.dll")]
    private static extern int GdipCreatePen1(uint color, float width, int unit, out nint pen);

    [DllImport("gdiplus.dll")]
    private static extern int GdipDeletePen(nint pen);

    [DllImport("gdiplus.dll")]
    private static extern int GdipDrawPath(nint graphics, nint pen, nint path);

    [DllImport("gdiplus.dll", CharSet = CharSet.Unicode)]
    private static extern int GdipCreateFontFamilyFromName(string name, nint fontCollection, out nint fontFamily);

    [DllImport("gdiplus.dll")]
    private static extern int GdipGetGenericFontFamilySansSerif(out nint nativeFamily);

    [DllImport("gdiplus.dll")]
    private static extern int GdipDeleteFontFamily(nint fontFamily);

    [DllImport("gdiplus.dll")]
    private static extern int GdipCreateFont(nint fontFamily, float emSize, int style, int unit, out nint font);

    [DllImport("gdiplus.dll")]
    private static extern int GdipDeleteFont(nint font);

    [DllImport("gdiplus.dll")]
    private static extern int GdipCreateStringFormat(int formatAttributes, ushort language, out nint format);

    [DllImport("gdiplus.dll")]
    private static extern int GdipDeleteStringFormat(nint format);

    [DllImport("gdiplus.dll")]
    private static extern int GdipSetStringFormatAlign(nint format, int align);

    [DllImport("gdiplus.dll")]
    private static extern int GdipSetStringFormatLineAlign(nint format, int align);

    [DllImport("gdiplus.dll", CharSet = CharSet.Unicode)]
    private static extern int GdipDrawString(nint graphics, string str, int length, nint font, ref RectF layoutRect, nint stringFormat, nint brush);
}
