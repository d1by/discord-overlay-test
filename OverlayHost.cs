// OverlayHost: a fully transparent, always-on-top, click-through, fullscreen
// OpenGL window that presents empty frames continuously, so third-party
// overlays (hooking SwapBuffers/Present) have something to attach to.
// Quit: Ctrl+Alt+Shift+Q.   Args: --fps N (default: vsync)  --test (draw a visible marker)
using System;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Threading;

static class OverlayHost
{
    delegate IntPtr WndProcDelegate(IntPtr h, uint m, IntPtr w, IntPtr l);
    delegate bool SwapIntervalDelegate(int i);

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    struct WNDCLASS { public uint style; public WndProcDelegate proc; public int cb, cw; public IntPtr inst, icon, cursor, bg; public string menu, cls; }
    [StructLayout(LayoutKind.Sequential)] struct MSG { public IntPtr h; public uint m; public IntPtr w, l; public uint t; public int x, y; }
    [StructLayout(LayoutKind.Sequential)] struct BLUR { public uint flags; public int enable; public IntPtr rgn; public int transitions; }
    [StructLayout(LayoutKind.Sequential)]
    struct PFD { public ushort size, ver; public uint flags; public byte pixType, color, rb, rs, gb, gs, bb, bs, ab, ashift, accum, ar, ag, ab2, aa, depth, stencil, aux, layer, res; public uint lm, vm, dm; }

    [DllImport("user32")] static extern ushort RegisterClassW(ref WNDCLASS c);
    [DllImport("user32", CharSet = CharSet.Unicode)] static extern IntPtr CreateWindowExW(uint ex, string cls, string name, uint style, int x, int y, int w, int h, IntPtr p, IntPtr m, IntPtr i, IntPtr lp);
    [DllImport("user32")] static extern IntPtr DefWindowProcW(IntPtr h, uint m, IntPtr w, IntPtr l);
    [DllImport("user32")] static extern bool PeekMessageW(out MSG m, IntPtr h, uint a, uint b, uint rm);
    [DllImport("user32")] static extern bool TranslateMessage(ref MSG m);
    [DllImport("user32")] static extern IntPtr DispatchMessageW(ref MSG m);
    [DllImport("user32")] static extern bool ShowWindow(IntPtr h, int c);
    [DllImport("user32")] static extern bool SetWindowPos(IntPtr h, IntPtr after, int x, int y, int w, int cx, uint f);
    [DllImport("user32")] static extern bool SetLayeredWindowAttributes(IntPtr h, uint key, byte a, uint f);
    [DllImport("user32")] static extern bool RegisterHotKey(IntPtr h, int id, uint mod, uint vk);
    [DllImport("user32")] static extern IntPtr GetDC(IntPtr h);
    [DllImport("user32")] static extern int GetSystemMetrics(int i);
    [DllImport("user32")] static extern bool SetProcessDPIAware();
    [DllImport("user32")] static extern void PostQuitMessage(int c);
    [DllImport("gdi32")] static extern int ChoosePixelFormat(IntPtr dc, ref PFD p);
    [DllImport("gdi32")] static extern bool SetPixelFormat(IntPtr dc, int f, ref PFD p);
    [DllImport("gdi32")] static extern bool SwapBuffers(IntPtr dc);
    [DllImport("gdi32")] static extern IntPtr CreateRectRgn(int a, int b, int c, int d);
    [DllImport("opengl32")] static extern IntPtr wglCreateContext(IntPtr dc);
    [DllImport("opengl32")] static extern bool wglMakeCurrent(IntPtr dc, IntPtr rc);
    [DllImport("opengl32")] static extern IntPtr wglGetProcAddress(string n);
    [DllImport("opengl32")] static extern void glClearColor(float r, float g, float b, float a);
    [DllImport("opengl32")] static extern void glClear(uint mask);
    [DllImport("opengl32")] static extern void glEnable(uint cap);
    [DllImport("opengl32")] static extern void glScissor(int x, int y, int w, int h);
    [DllImport("dwmapi")] static extern int DwmEnableBlurBehindWindow(IntPtr h, ref BLUR b);
    [DllImport("kernel32")] static extern IntPtr GetModuleHandleW(string n);

    static readonly IntPtr HWND_TOPMOST = new IntPtr(-1);
    static WndProcDelegate proc = WndProc;   // keep alive

    static IntPtr WndProc(IntPtr h, uint m, IntPtr w, IntPtr l)
    {
        if (m == 0x0312) { PostQuitMessage(0); return IntPtr.Zero; }   // WM_HOTKEY
        if (m == 0x0021) return new IntPtr(3);                          // WM_MOUSEACTIVATE -> MA_NOACTIVATE
        if (m == 0x0084) return new IntPtr(-1);                         // WM_NCHITTEST -> HTTRANSPARENT
        if (m == 0x0002) { PostQuitMessage(0); return IntPtr.Zero; }    // WM_DESTROY
        return DefWindowProcW(h, m, w, l);
    }

    [STAThread]
    static int Main(string[] args)
    {
        int fps = 0; bool test = false;
        for (int i = 0; i < args.Length; i++)
        {
            if (args[i] == "--fps" && i + 1 < args.Length) int.TryParse(args[++i], out fps);
            else if (args[i] == "--test") test = true;
        }

        bool created;
        using (new Mutex(true, "OverlayHost.single", out created))
        {
            if (!created) return 1;

            Process.GetCurrentProcess().PriorityClass = ProcessPriorityClass.BelowNormal;
            SetProcessDPIAware();

            IntPtr inst = GetModuleHandleW(null);
            WNDCLASS wc = new WNDCLASS(); wc.proc = proc; wc.inst = inst; wc.cls = "OverlayHostWnd";
            RegisterClassW(ref wc);

            int W = GetSystemMetrics(0), H = GetSystemMetrics(1);
            // TOPMOST | TOOLWINDOW | NOACTIVATE | TRANSPARENT | LAYERED
            uint ex = 0x8 | 0x80 | 0x08000000 | 0x20 | 0x80000;
            IntPtr hwnd = CreateWindowExW(ex, "OverlayHostWnd", "OverlayHost", 0x80000000 /*WS_POPUP*/, 0, 0, W, H, IntPtr.Zero, IntPtr.Zero, inst, IntPtr.Zero);
            SetLayeredWindowAttributes(hwnd, 0, 255, 2);

            // Per-pixel transparency for GL: empty blur region on DWM.
            BLUR b = new BLUR(); b.flags = 3; b.enable = 1; b.rgn = CreateRectRgn(0, 0, -1, -1);
            DwmEnableBlurBehindWindow(hwnd, ref b);

            IntPtr dc = GetDC(hwnd);
            PFD pfd = new PFD();
            pfd.size = (ushort)Marshal.SizeOf(typeof(PFD)); pfd.ver = 1;
            pfd.flags = 0x4 | 0x20 | 0x1 | 0x8000;   // DOUBLEBUFFER | SUPPORT_OPENGL | DRAW_TO_WINDOW | SUPPORT_COMPOSITION
            pfd.color = 32; pfd.ab = 8; pfd.depth = 0;
            SetPixelFormat(dc, ChoosePixelFormat(dc, ref pfd), ref pfd);
            IntPtr rc = wglCreateContext(dc);
            wglMakeCurrent(dc, rc);

            bool vsync = false;
            IntPtr p = wglGetProcAddress("wglSwapIntervalEXT");
            if (p != IntPtr.Zero && fps == 0)
                vsync = ((SwapIntervalDelegate)Marshal.GetDelegateForFunctionPointer(p, typeof(SwapIntervalDelegate)))(1);
            int sleepMs = fps > 0 ? Math.Max(1, 1000 / fps) : (vsync ? 0 : 16);

            ShowWindow(hwnd, 4 /*SW_SHOWNOACTIVATE*/);
            SetWindowPos(hwnd, HWND_TOPMOST, 0, 0, W, H, 0x10 | 0x40);
            RegisterHotKey(hwnd, 1, 0x1 | 0x2 | 0x4 | 0x4000, 0x51);   // ALT|CTRL|SHIFT|NOREPEAT + Q

            Stopwatch top = Stopwatch.StartNew();
            MSG msg;
            bool run = true;
            while (run)
            {
                while (PeekMessageW(out msg, IntPtr.Zero, 0, 0, 1))
                {
                    if (msg.m == 0x0012) { run = false; break; }   // WM_QUIT
                    TranslateMessage(ref msg); DispatchMessageW(ref msg);
                }
                if (!run) break;

                glClearColor(0, 0, 0, 0);
                glClear(0x4000);
                if (test)   // faint red box top-left, to confirm rendering
                {
                    glEnable(0x0C11); glScissor(20, H - 120, 100, 100);
                    glClearColor(1, 0, 0, 0.5f); glClear(0x4000);
                }
                SwapBuffers(dc);

                if (top.ElapsedMilliseconds > 2000)   // stay above borderless-fullscreen apps
                {
                    SetWindowPos(hwnd, HWND_TOPMOST, 0, 0, W, H, 0x10 | 0x40 | 0x2 | 0x1);
                    top.Restart();
                }
                if (sleepMs > 0) Thread.Sleep(sleepMs);
            }
        }
        return 0;
    }
}
