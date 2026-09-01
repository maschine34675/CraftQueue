using System;
using System.Runtime.CompilerServices;

namespace CraftQueue.UI
{
    internal static class WebOverlayGate
    {
        public const string LibraryGuid = "com.anvil.weboverlay";
        public static readonly Version MinimumVersion = new Version(1, 7, 0);

        private static readonly Version ClickThroughSince = new Version(1, 8, 8);

        private static bool? loaded;
        private static Version foundVersion;
        private static object overlay;
        private static string currentUrl;
        private static volatile bool overlayFailed;
        private static volatile bool overlayWasReady;

        public static bool IsLoaded
        {
            get
            {
                if (loaded == null)
                {
                    BepInEx.PluginInfo info;
                    loaded = BepInEx.Bootstrap.Chainloader.PluginInfos.TryGetValue(LibraryGuid, out info);
                    if (loaded.Value && info != null && info.Metadata != null)
                        foundVersion = info.Metadata.Version;
                }
                return loaded.Value;
            }
        }
        public static Version FoundVersion
        {
            get
            {
                bool unused = IsLoaded;
                return foundVersion;
            }
        }
        public static bool IsUsable => IsLoaded && foundVersion != null && foundVersion >= MinimumVersion;
        [MethodImpl(MethodImplOptions.NoInlining)]
        private static void letTheMouseThrough(object options)
        {
            ((WebOverlay.OverlayOptions)options).ClickThroughWhenUnfocused = true;
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
                var options = new WebOverlay.OverlayOptions
                {
                    CloseKeys = closeKeys,
                    Frame = showFrame,
                    FreeCursorWhileShown = true
                };
                if (foundVersion >= ClickThroughSince)
                    letTheMouseThrough(options);
                handle = WebOverlay.WebOverlays.Create(
                    "CraftQueue - press F9 or Escape to close",
                    options);
                if (handle == null)
                {
                    logWarning("overlays are unavailable (is the WebView2 runtime installed?); using the browser.");
                    return false;
                }
                overlay = handle;
                currentUrl = url;
                overlayWasReady = false;
                object created = handle;
                handle.Ready += () => overlayWasReady = true;
                handle.Failed += () =>
                {
                    bool openBrowser = !overlayWasReady;
                    overlayFailed = true;
                    ((WebOverlay.IWebOverlay)created).Dispose();
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
