using System;
using System.Runtime.InteropServices;

namespace CraftQueue.WebViewProbe
{
    /// <summary>
    /// Minimal WebView2 host that talks to the COM API directly instead of going
    /// through Microsoft's managed wrapper.
    ///
    /// The wrapper cannot be used here: creating an environment through it makes
    /// Mono build COM callable wrappers for the SDK's interfaces, and Mono
    /// ignores the `_VtblGap` markers those interfaces use for inherited slots,
    /// so native code ends up calling the wrong function. Probing that path
    /// killed the game process outright.
    ///
    /// Here every vtable is built by hand, in the slot order taken from the
    /// official WebView2.h, and every callback is a plain function pointer from
    /// Marshal.GetFunctionPointerForDelegate - a mechanism Mono handles well.
    /// </summary>
    internal sealed class RawWebView : IDisposable
    {
        private const int S_OK = 0;
        private const int E_NOINTERFACE = unchecked((int)0x80004002);
        private const int E_POINTER = unchecked((int)0x80004003);

        private static readonly Guid IID_IUnknown = new Guid("00000000-0000-0000-C000-000000000046");
        private static readonly Guid IID_EnvironmentCompleted = new Guid("4e8a3389-c9d8-4bd2-b6b5-124fee6cc14d");
        private static readonly Guid IID_ControllerCompleted = new Guid("6c4819f3-c9b7-4260-8127-c9f5bde7f68c");

        private readonly Action<int, string> report;
        private Callback environmentCallback;
        private Callback controllerCallback;

        private IntPtr environment;
        private IntPtr controller;
        private IntPtr webView;

        public RawWebView(Action<int, string> reporter)
        {
            report = reporter;
        }

        public bool IsReady => webView != IntPtr.Zero;

        public void Create(string userDataFolder, IntPtr parentWindow, string url)
        {
            environmentCallback = new Callback(IID_EnvironmentCompleted, (result, pointer) =>
            {
                if (result != S_OK || pointer == IntPtr.Zero)
                {
                    report(7, "environment callback reported hr=0x" + result.ToString("X8"));
                    return S_OK;
                }

                environment = pointer;
                Marshal.AddRef(environment);
                report(7, "environment created - manual vtable callback works");
                createController(parentWindow, url);
                return S_OK;
            });

            report(6, "calling CreateCoreWebView2EnvironmentWithOptions...");
            int hr = CreateCoreWebView2EnvironmentWithOptions(
                null,
                userDataFolder,
                IntPtr.Zero,
                environmentCallback.Pointer);
            if (hr != S_OK)
                report(6, "FAILED CreateCoreWebView2EnvironmentWithOptions hr=0x" + hr.ToString("X8"));
            else
                report(6, "environment creation requested");
        }

        private void createController(IntPtr parentWindow, string url)
        {
            controllerCallback = new Callback(IID_ControllerCompleted, (result, pointer) =>
            {
                if (result != S_OK || pointer == IntPtr.Zero)
                {
                    report(9, "controller callback reported hr=0x" + result.ToString("X8"));
                    return S_OK;
                }

                controller = pointer;
                Marshal.AddRef(controller);
                report(9, "controller created");
                finish(url);
                return S_OK;
            });

            // ICoreWebView2Environment slot 3.
            var create = getMethod<CreateControllerDelegate>(environment, 3);
            int hr = create(environment, parentWindow, controllerCallback.Pointer);
            report(8, hr == S_OK
                ? "controller creation requested"
                : "FAILED CreateCoreWebView2Controller hr=0x" + hr.ToString("X8"));
        }

        private void finish(string url)
        {
            // ICoreWebView2Controller slots: 4 put_IsVisible, 6 put_Bounds,
            // 25 get_CoreWebView2 (from WebView2.h).
            getMethod<PutBoolDelegate>(controller, 4)(controller, 1);
            report(10, "visibility set");

            getMethod<GetPointerDelegate>(controller, 25)(controller, out webView);
            if (webView == IntPtr.Zero)
            {
                report(10, "FAILED get_CoreWebView2 returned null");
                return;
            }
            report(10, "core web view obtained");

            // ICoreWebView2 slot 5 Navigate.
            int hr = getMethod<NavigateDelegate>(webView, 5)(webView, url);
            report(11, hr == S_OK ? "navigation requested" : "FAILED Navigate hr=0x" + hr.ToString("X8"));
        }

        public void SetBounds(int left, int top, int width, int height)
        {
            if (controller == IntPtr.Zero)
                return;
            var bounds = new RECT { left = left, top = top, right = left + width, bottom = top + height };
            getMethod<PutBoundsDelegate>(controller, 6)(controller, bounds);
        }

        public void SetVisible(bool visible)
        {
            if (controller == IntPtr.Zero)
                return;
            getMethod<PutBoolDelegate>(controller, 4)(controller, visible ? 1 : 0);
        }

        private static T getMethod<T>(IntPtr comObject, int slot) where T : class
        {
            IntPtr vtable = Marshal.ReadIntPtr(comObject);
            IntPtr function = Marshal.ReadIntPtr(vtable, slot * IntPtr.Size);
            return Marshal.GetDelegateForFunctionPointer(function, typeof(T)) as T;
        }

        public void Dispose()
        {
            try
            {
                if (controller != IntPtr.Zero)
                {
                    // ICoreWebView2Controller slot 24 Close.
                    getMethod<CloseDelegate>(controller, 24)(controller);
                    Marshal.Release(controller);
                    controller = IntPtr.Zero;
                }
                if (environment != IntPtr.Zero)
                {
                    Marshal.Release(environment);
                    environment = IntPtr.Zero;
                }
            }
            catch
            {
            }

            webView = IntPtr.Zero;
            environmentCallback?.Dispose();
            controllerCallback?.Dispose();
        }

        /// <summary>
        /// A COM object built by hand: a block of memory whose first field points
        /// at a vtable of four function pointers. The delegates are held as
        /// fields because the runtime does not keep them alive on its own, and a
        /// collected delegate means an access violation on the next call.
        /// </summary>
        private sealed class Callback : IDisposable
        {
            private readonly Guid interfaceId;
            private readonly Func<int, IntPtr, int> onInvoke;
            private readonly QueryInterfaceDelegate queryInterface;
            private readonly AddRefDelegate addRef;
            private readonly AddRefDelegate release;
            private readonly InvokeDelegate invoke;
            private readonly IntPtr vtable;
            private int references = 1;

            public Callback(Guid interfaceId, Func<int, IntPtr, int> handler)
            {
                this.interfaceId = interfaceId;
                onInvoke = handler;
                queryInterface = onQueryInterface;
                addRef = onAddRef;
                release = onRelease;
                invoke = onInvokeThunk;

                vtable = Marshal.AllocHGlobal(IntPtr.Size * 4);
                Marshal.WriteIntPtr(vtable, 0 * IntPtr.Size, Marshal.GetFunctionPointerForDelegate(queryInterface));
                Marshal.WriteIntPtr(vtable, 1 * IntPtr.Size, Marshal.GetFunctionPointerForDelegate(addRef));
                Marshal.WriteIntPtr(vtable, 2 * IntPtr.Size, Marshal.GetFunctionPointerForDelegate(release));
                Marshal.WriteIntPtr(vtable, 3 * IntPtr.Size, Marshal.GetFunctionPointerForDelegate(invoke));

                Pointer = Marshal.AllocHGlobal(IntPtr.Size);
                Marshal.WriteIntPtr(Pointer, vtable);
            }

            public IntPtr Pointer { get; private set; }

            // Every thunk swallows exceptions: letting one unwind into Chromium's
            // native frames would take the process down.
            private int onQueryInterface(IntPtr self, ref Guid requested, out IntPtr result)
            {
                try
                {
                    if (requested == interfaceId || requested == IID_IUnknown)
                    {
                        result = self;
                        onAddRef(self);
                        return S_OK;
                    }
                    result = IntPtr.Zero;
                    return E_NOINTERFACE;
                }
                catch
                {
                    result = IntPtr.Zero;
                    return E_POINTER;
                }
            }

            private uint onAddRef(IntPtr self)
            {
                try { return (uint)System.Threading.Interlocked.Increment(ref references); }
                catch { return 1; }
            }

            private uint onRelease(IntPtr self)
            {
                try { return (uint)Math.Max(0, System.Threading.Interlocked.Decrement(ref references)); }
                catch { return 0; }
            }

            private int onInvokeThunk(IntPtr self, int errorCode, IntPtr result)
            {
                try { return onInvoke(errorCode, result); }
                catch { return S_OK; }
            }

            public void Dispose()
            {
                if (Pointer != IntPtr.Zero)
                {
                    Marshal.FreeHGlobal(Pointer);
                    Pointer = IntPtr.Zero;
                }
                if (vtable != IntPtr.Zero)
                    Marshal.FreeHGlobal(vtable);
            }
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct RECT
        {
            public int left;
            public int top;
            public int right;
            public int bottom;
        }

        [UnmanagedFunctionPointer(CallingConvention.StdCall)]
        private delegate int QueryInterfaceDelegate(IntPtr self, ref Guid requested, out IntPtr result);

        [UnmanagedFunctionPointer(CallingConvention.StdCall)]
        private delegate uint AddRefDelegate(IntPtr self);

        [UnmanagedFunctionPointer(CallingConvention.StdCall)]
        private delegate int InvokeDelegate(IntPtr self, int errorCode, IntPtr result);

        [UnmanagedFunctionPointer(CallingConvention.StdCall)]
        private delegate int CreateControllerDelegate(IntPtr self, IntPtr parentWindow, IntPtr handler);

        [UnmanagedFunctionPointer(CallingConvention.StdCall)]
        private delegate int PutBoolDelegate(IntPtr self, int value);

        [UnmanagedFunctionPointer(CallingConvention.StdCall)]
        private delegate int PutBoundsDelegate(IntPtr self, RECT bounds);

        [UnmanagedFunctionPointer(CallingConvention.StdCall)]
        private delegate int GetPointerDelegate(IntPtr self, out IntPtr value);

        [UnmanagedFunctionPointer(CallingConvention.StdCall)]
        private delegate int NavigateDelegate(IntPtr self, [MarshalAs(UnmanagedType.LPWStr)] string url);

        [UnmanagedFunctionPointer(CallingConvention.StdCall)]
        private delegate int CloseDelegate(IntPtr self);

        [DllImport("WebView2Loader.dll", CharSet = CharSet.Unicode)]
        private static extern int CreateCoreWebView2EnvironmentWithOptions(
            string browserExecutableFolder,
            string userDataFolder,
            IntPtr environmentOptions,
            IntPtr handler);
    }
}
