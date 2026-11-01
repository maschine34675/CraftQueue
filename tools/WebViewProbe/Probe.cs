using BepInEx;
using BepInEx.Logging;
using System;
using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Threading;
using UnityEngine;

namespace CraftQueue.WebViewProbe
{
    /// <summary>
    /// Throwaway diagnostic plugin. It answers, in a single game launch, whether
    /// the official WebView2 managed wrapper can host a browser window from
    /// inside EFT's Mono runtime - something that cannot be tested outside the
    /// game, because SPT ships only the embedded runtime.
    ///
    /// Every step writes both a log line and a stamp file, so a hard native
    /// crash still tells us which step was running when it happened.
    /// </summary>
    [BepInPlugin("com.maschine.CraftQueue.WebViewProbe", "CraftQueue WebView2 Probe", "2.4.3")]
    public class Probe : BaseUnityPlugin
    {
        private const string StageFileName = "craftqueue-webview2-probe.txt";

        private static readonly string StagePath =
            Path.Combine(Path.GetTempPath(), StageFileName);

        private static ManualLogSource log;
        private static uint unityThreadId;
        private static IntPtr gameWindow;

        // Kept as static fields on purpose: native code holds these pointers, and
        // a collected delegate is an access violation at the next callback.
        private static WndProcDelegate wndProcDelegate;
        private static IntPtr overlayWindow;
        private static RawWebView webView;

        private void Awake()
        {
            log = this.Logger;
            unityThreadId = GetCurrentThreadId();

            try
            {
                File.WriteAllText(StagePath, "start\n");
            }
            catch
            {
            }

            stage(0, "probe starting");
            stage(0, "apartment of Unity main thread: " + Thread.CurrentThread.GetApartmentState());
            stage(0, "plugin folder: " + pluginFolder());
            stage(0, "OS: " + Environment.OSVersion + " / 64bit process: " + (IntPtr.Size == 8));

            gameWindow = findGameWindow();
            stage(1, gameWindow == IntPtr.Zero
                ? "FAILED to find the game window"
                : "game window found: 0x" + gameWindow.ToString("X"));

            var thread = new Thread(runProbe) { IsBackground = true, Name = "CraftQueueWebViewProbe" };
            thread.SetApartmentState(ApartmentState.STA);
            thread.Start();
        }

        private static void runProbe()
        {
            try
            {
                stage(2, "STA probe thread running, apartment: " + Thread.CurrentThread.GetApartmentState());

                if (!createOverlayWindow())
                    return;

                string loaderFolder = pluginFolder();
                IntPtr loader = LoadLibrary(Path.Combine(loaderFolder, "WebView2Loader.dll"));
                if (loader == IntPtr.Zero)
                {
                    stage(4, "FAILED to load WebView2Loader.dll, win32 error " + Marshal.GetLastWin32Error());
                    return;
                }
                stage(4, "WebView2Loader.dll loaded from " + loaderFolder);

                // Proves the runtime is installed and reachable before anything
                // heavier is attempted.
                int versionResult = GetAvailableCoreWebView2BrowserVersionString(null, out IntPtr versionPointer);
                if (versionResult != 0 || versionPointer == IntPtr.Zero)
                {
                    stage(5, "FAILED runtime lookup hr=0x" + versionResult.ToString("X8"));
                    return;
                }
                stage(5, "runtime version: " + Marshal.PtrToStringUni(versionPointer));
                Marshal.FreeCoTaskMem(versionPointer);

                string userData = Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                    "CraftQueue", "WebView2Probe");
                Directory.CreateDirectory(userData);

                // Everything from here on goes through hand-built vtables. The
                // managed wrapper was tried first and killed the process at this
                // exact point, because Mono mis-builds the wrappers it needs.
                webView = new RawWebView(stage);
                webView.Create(userData, overlayWindow, "https://example.com/");

                var timer = Stopwatch.StartNew();
                while (!webView.IsReady && timer.Elapsed.TotalSeconds < 30)
                {
                    pumpOnce();
                    Thread.Sleep(10);
                }

                if (!webView.IsReady)
                {
                    stage(11, "TIMEOUT - no web view after 30s");
                    return;
                }

                webView.SetBounds(0, 0, 900, 600);
                ShowWindow(overlayWindow, SW_SHOWNOACTIVATE);
                stage(12, "SUCCESS - window shown; pumping messages");
                pumpForever();
            }
            catch (Exception ex)
            {
                stage(-1, "UNEXPECTED: " + describe(ex));
            }
        }

        private static bool createOverlayWindow()
        {
            try
            {
                wndProcDelegate = wndProc;
                var windowClass = new WNDCLASSEX
                {
                    cbSize = Marshal.SizeOf(typeof(WNDCLASSEX)),
                    lpfnWndProc = Marshal.GetFunctionPointerForDelegate(wndProcDelegate),
                    hInstance = GetModuleHandle(null),
                    lpszClassName = "CraftQueueWebViewProbe",
                    hbrBackground = (IntPtr)(COLOR_WINDOW + 1)
                };

                if (RegisterClassEx(ref windowClass) == 0)
                {
                    int error = Marshal.GetLastWin32Error();
                    if (error != ERROR_CLASS_ALREADY_EXISTS)
                    {
                        stage(3, "FAILED RegisterClassEx, win32 error " + error);
                        return false;
                    }
                }

                // Owned popup, not a child window: Unity presents through a
                // flip-model swapchain, which does not composite child windows.
                overlayWindow = CreateWindowEx(
                    WS_EX_NOACTIVATE | WS_EX_TOOLWINDOW,
                    "CraftQueueWebViewProbe",
                    "CraftQueue WebView2 Probe",
                    WS_POPUP,
                    120, 120, 900, 600,
                    gameWindow,
                    IntPtr.Zero,
                    GetModuleHandle(null),
                    IntPtr.Zero);

                if (overlayWindow == IntPtr.Zero)
                {
                    stage(3, "FAILED CreateWindowEx, win32 error " + Marshal.GetLastWin32Error());
                    return false;
                }

                stage(3, "overlay window created: 0x" + overlayWindow.ToString("X"));
                return true;
            }
            catch (Exception ex)
            {
                stage(3, "FAILED window creation: " + describe(ex));
                return false;
            }
        }

        private static IntPtr wndProc(IntPtr hwnd, uint message, IntPtr wParam, IntPtr lParam)
        {
            // Never let a managed exception unwind into native frames.
            try
            {
                if (message == WM_CLOSE)
                {
                    ShowWindow(hwnd, SW_HIDE);
                    return IntPtr.Zero;
                }
            }
            catch
            {
            }

            return DefWindowProc(hwnd, message, wParam, lParam);
        }

        private static void pumpOnce()
        {
            while (PeekMessage(out MSG message, IntPtr.Zero, 0, 0, PM_REMOVE))
            {
                TranslateMessage(ref message);
                DispatchMessage(ref message);
            }
        }

        private static void pumpForever()
        {
            while (true)
            {
                pumpOnce();
                Thread.Sleep(10);
            }
        }

        private static IntPtr findGameWindow()
        {
            IntPtr found = IntPtr.Zero;
            EnumThreadWindows(unityThreadId, (hwnd, param) =>
            {
                var className = new System.Text.StringBuilder(256);
                GetClassName(hwnd, className, className.Capacity);
                if (className.ToString() != "UnityWndClass")
                    return true;
                if (!IsWindowVisible(hwnd) || GetWindow(hwnd, GW_OWNER) != IntPtr.Zero)
                    return true;

                found = hwnd;
                return false;
            }, IntPtr.Zero);
            return found;
        }

        private static string pluginFolder()
        {
            try
            {
                return Path.GetDirectoryName(Assembly.GetExecutingAssembly().Location) ?? "";
            }
            catch
            {
                return "";
            }
        }

        private static string describe(Exception ex)
        {
            Exception inner = ex is AggregateException aggregate && aggregate.InnerException != null
                ? aggregate.InnerException
                : ex;
            return inner.GetType().Name + ": " + inner.Message;
        }

        private static void stage(int number, string message)
        {
            string line = "[WV2] stage=" + number + " " + message;
            try
            {
                log?.LogInfo(line);
            }
            catch
            {
            }

            try
            {
                File.AppendAllText(StagePath, line + Environment.NewLine);
            }
            catch
            {
            }
        }

        private const int COLOR_WINDOW = 5;
        private const int ERROR_CLASS_ALREADY_EXISTS = 1410;
        private const uint WS_POPUP = 0x80000000;
        private const uint WS_EX_NOACTIVATE = 0x08000000;
        private const uint WS_EX_TOOLWINDOW = 0x00000080;
        private const uint WM_CLOSE = 0x0010;
        private const uint PM_REMOVE = 0x0001;
        private const uint GW_OWNER = 4;
        private const int SW_HIDE = 0;
        private const int SW_SHOWNOACTIVATE = 4;

        private delegate IntPtr WndProcDelegate(IntPtr hwnd, uint message, IntPtr wParam, IntPtr lParam);

        private delegate bool EnumThreadWindowsProc(IntPtr hwnd, IntPtr param);

        [StructLayout(LayoutKind.Sequential)]
        private struct WNDCLASSEX
        {
            public int cbSize;
            public uint style;
            public IntPtr lpfnWndProc;
            public int cbClsExtra;
            public int cbWndExtra;
            public IntPtr hInstance;
            public IntPtr hIcon;
            public IntPtr hCursor;
            public IntPtr hbrBackground;
            [MarshalAs(UnmanagedType.LPWStr)] public string lpszMenuName;
            [MarshalAs(UnmanagedType.LPWStr)] public string lpszClassName;
            public IntPtr hIconSm;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct MSG
        {
            public IntPtr hwnd;
            public uint message;
            public IntPtr wParam;
            public IntPtr lParam;
            public uint time;
            public int x;
            public int y;
        }

        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern IntPtr LoadLibrary(string fileName);

        [DllImport("WebView2Loader.dll", CharSet = CharSet.Unicode)]
        private static extern int GetAvailableCoreWebView2BrowserVersionString(string browserExecutableFolder, out IntPtr versionInfo);

        [DllImport("kernel32.dll")]
        private static extern uint GetCurrentThreadId();

        [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
        private static extern IntPtr GetModuleHandle(string moduleName);

        [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern ushort RegisterClassEx(ref WNDCLASSEX windowClass);

        [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern IntPtr CreateWindowEx(
            uint exStyle, string className, string windowName, uint style,
            int x, int y, int width, int height,
            IntPtr parent, IntPtr menu, IntPtr instance, IntPtr param);

        [DllImport("user32.dll", CharSet = CharSet.Unicode)]
        private static extern IntPtr DefWindowProc(IntPtr hwnd, uint message, IntPtr wParam, IntPtr lParam);

        [DllImport("user32.dll")]
        private static extern bool ShowWindow(IntPtr hwnd, int command);

        [DllImport("user32.dll", CharSet = CharSet.Unicode)]
        private static extern bool PeekMessage(out MSG message, IntPtr hwnd, uint filterMin, uint filterMax, uint removeMessage);

        [DllImport("user32.dll")]
        private static extern bool TranslateMessage(ref MSG message);

        [DllImport("user32.dll", CharSet = CharSet.Unicode)]
        private static extern IntPtr DispatchMessage(ref MSG message);

        [DllImport("user32.dll")]
        private static extern bool EnumThreadWindows(uint threadId, EnumThreadWindowsProc callback, IntPtr param);

        [DllImport("user32.dll", CharSet = CharSet.Unicode)]
        private static extern int GetClassName(IntPtr hwnd, System.Text.StringBuilder className, int maxCount);

        [DllImport("user32.dll")]
        private static extern bool IsWindowVisible(IntPtr hwnd);

        [DllImport("user32.dll")]
        private static extern IntPtr GetWindow(IntPtr hwnd, uint command);
    }
}
