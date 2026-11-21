using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Windows.Forms;

namespace MusicBeePlugin
{
    public static partial class MusicBeeArtworkBridge
    {
        private static bool _initialized;
        private static bool _isAvailable;
        private static readonly object _initLock = new object();
        private static Func<IntPtr> _windowHandleProvider;

        private static ConstructorInfo _filePathCtor;
        private static MethodInfo _tagGetTrackMethod;
        private static ConstructorInfo _artworkRefCtor;
        private static FieldInfo _trackArtworkField;
        private static MethodInfo _artworkCollectionSetMethod;
        private static MethodInfo _artClearMethod;

        private static object _albumCoverEnumVal;
        private static object _linkToSourceEnumVal;

        public static void Initialize(MusicBeeApiInterface api)
        {
            if (api.MB_GetWindowHandle != null)
                _windowHandleProvider = () => api.MB_GetWindowHandle();
        }

        public static bool IsAvailable
        {
            get
            {
                EnsureInitialized();
                return _isAvailable;
            }
        }

        private static void EnsureInitialized()
        {
            if (_initialized) return;
            lock (_initLock)
            {
                if (_initialized) return;
                _initialized = true;

                try
                {
                    Assembly mbAssembly = AppDomain.CurrentDomain.GetAssemblies()
                        .FirstOrDefault(a => string.Equals(a.GetName().Name, "MusicBee", StringComparison.OrdinalIgnoreCase));

                    if (mbAssembly == null)
                    {
                        _isAvailable = false;
                        return;
                    }

                    _isAvailable = DiscoverBridgeMembers(mbAssembly);
                }
                catch
                {
                    _isAvailable = false;
                }
            }
        }

        private static void RunOnUiThread(Action action)
        {
            if (action == null) return;
            try
            {
                Control hostControl = null;
                if (_windowHandleProvider != null)
                {
                    IntPtr hwnd = _windowHandleProvider();
                    if (hwnd != IntPtr.Zero)
                        hostControl = Control.FromHandle(hwnd);
                }
                if (hostControl == null && Application.OpenForms.Count > 0)
                    hostControl = Application.OpenForms[0];

                if (hostControl != null && !hostControl.IsDisposed && hostControl.IsHandleCreated)
                {
                    if (hostControl.InvokeRequired)
                    {
                        hostControl.Invoke(action);
                        return;
                    }
                }
            }
            catch { }
            action();
        }

        private static bool AttachCore(string trackUrl, object arObj)
        {
            if (string.IsNullOrEmpty(trackUrl) || arObj == null) return false;
            try
            {
                object trackFp = _filePathCtor.Invoke(new object[] { trackUrl });
                if (trackFp == null) return false;

                object track = _tagGetTrackMethod.Invoke(null, new[] { trackFp });
                if (track == null) return false;

                object boxedCol = _trackArtworkField.GetValue(track);
                if (boxedCol != null)
                {
                    _artworkCollectionSetMethod.Invoke(boxedCol, new[] { arObj });
                    _trackArtworkField.SetValue(track, boxedCol);
                }

                _artClearMethod.Invoke(null, new[] { track });
                return true;
            }
            catch
            {
                return false;
            }
        }

        public static bool AttachArtwork(string trackUrl, string localArtworkPath)
        {
            if (string.IsNullOrEmpty(trackUrl) || string.IsNullOrEmpty(localArtworkPath))
                return false;
            if (!IsAvailable) return false;

            try
            {
                if (!File.Exists(localArtworkPath)) return false;
                if (new FileInfo(localArtworkPath).Length < 12) return false;

                object artFp = _filePathCtor.Invoke(new object[] { localArtworkPath });
                if (artFp == null) return false;

                object arObj = _artworkRefCtor.Invoke(new[] { _albumCoverEnumVal, null, _linkToSourceEnumVal, artFp });
                if (arObj == null) return false;

                bool result = false;
                RunOnUiThread(() => result = AttachCore(trackUrl, arObj));
                return result;
            }
            catch
            {
                return false;
            }
        }

        public static int AttachArtworkBatch(IEnumerable<KeyValuePair<string, string>> items, Action<int, int> progressCallback = null)
        {
            if (!IsAvailable || items == null) return 0;

            int count = 0;
            var arObjCache = new Dictionary<string, object>(StringComparer.OrdinalIgnoreCase);

            try
            {
                var itemList = items as IList<KeyValuePair<string, string>> ?? items.ToList();
                int total = itemList.Count;
                var work = new List<KeyValuePair<string, object>>(total);

                foreach (var kvp in itemList)
                {
                    if (string.IsNullOrEmpty(kvp.Key) || string.IsNullOrEmpty(kvp.Value)) continue;
                    try
                    {
                        if (!arObjCache.TryGetValue(kvp.Value, out object arObj))
                        {
                            if (!File.Exists(kvp.Value) || new FileInfo(kvp.Value).Length < 12)
                                continue;
                            object artFp = _filePathCtor.Invoke(new object[] { kvp.Value });
                            if (artFp != null)
                            {
                                arObj = _artworkRefCtor.Invoke(new[] { _albumCoverEnumVal, null, _linkToSourceEnumVal, artFp });
                                if (arObj != null)
                                    arObjCache[kvp.Value] = arObj;
                            }
                        }
                        if (arObj != null)
                            work.Add(new KeyValuePair<string, object>(kvp.Key, arObj));
                    }
                    catch { }
                }

                const int chunkSize = 100;
                for (int offset = 0; offset < work.Count; offset += chunkSize)
                {
                    int end = Math.Min(offset + chunkSize, work.Count);
                    int start = offset;
                    RunOnUiThread(() =>
                    {
                        for (int i = start; i < end; i++)
                        {
                            if (AttachCore(work[i].Key, work[i].Value))
                                count++;
                        }
                    });
                    if (progressCallback != null && (end % 200 == 0 || end == work.Count))
                        progressCallback(end, work.Count);
                }
            }
            catch { }

            return count;
        }
    }
}
