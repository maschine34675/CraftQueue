using System;
using System.Runtime.CompilerServices;

namespace CraftQueue.UI
{
    internal static class WebOverlayGate
    {
        public const string LibraryGuid = "com.anvil.weboverlay";

        private static bool? loaded;
        private static object overlay;
        private static string currentUrl;
        private static volatile bool overlayFailed;
        private static volatile bool overlayWasReady;

        public static bool IsLoaded
        {
            get
            {
                if (loaded == null)
                    loaded = BepInEx.Bootstrap.Chainloader.PluginInfos.ContainsKey(LibraryGuid);
                return loaded.Value;
            }
        }
        [MethodImpl(MethodImplOptions.NoInlining)]
        public static bool Toggle(string url, bool showFrame, int[] closeKeys, Action<string> logWarning, out bool starting)
        {
            starting = false;
            if (overlayFailed)
                return false;

            var handle = overlay as WebOverlay.IWebOverlay;
            if (handle == null)
            {
                handle = WebOverlay.WebOverlays.Create(
                    "CraftQueue - press F9 or Escape to close",
                    new WebOverlay.OverlayOptions
                    {
                        CloseKeys = closeKeys,
                        Frame = showFrame
                    });
                if (handle == null)
                {
                    logWarning("overlays are unavailable (is the WebView2 runtime installed?); using the browser.");
                    return false;
                }
                overlay = handle;
                currentUrl = url;
                overlayWasReady = false;

                var created = handle;
                handle.Ready += () => overlayWasReady = true;
                handle.Failed += () =>
                {
                    bool openBrowser = !overlayWasReady;
                    overlayFailed = true;
                    created.Dispose();
                    if (ReferenceEquals(overlay, created))
                        overlay = null;
                    logWarning("the overlay failed; using the browser.");
                    if (!openBrowser)
                        return;
                    try
                    {
                        System.Diagnostics.Process.Start(url);
                    }
                    catch
                    {
                    }
                };

                handle.Navigate(url);
                starting = true;
                return true;
            }
            if (!string.Equals(currentUrl, url, StringComparison.Ordinal))
            {
                currentUrl = url;
                handle.Navigate(url);
            }

            handle.Toggle();
            return true;
        }

        [MethodImpl(MethodImplOptions.NoInlining)]
        public static void Shutdown()
        {
            (overlay as WebOverlay.IWebOverlay)?.Dispose();
            overlay = null;
        }
    }
}
