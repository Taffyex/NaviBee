using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Windows.Forms;

namespace MusicBeePlugin
{
    /// <summary>Safely injects synced/unsynced lyrics into MusicBee tracks and live Lyrics panels in memory without disk tag-write crashes.</summary>
    public static class MusicBeeLyricsBridge
    {
        private static bool _initialized;
        private static bool _isAvailable;
        private static readonly object _initLock = new object();

        private static MethodInfo _trackSetTagMethod;
        private static MethodInfo _trackSetHasLyricsMethod;
        private static FieldInfo _trackUrlField;
        private static object _tagCodeSync;
        private static object _tagCodeLyrics;

        private static MethodInfo _updateCurrentLyricsMethod;
        private static ConstructorInfo _lyricsObjCtor;
        private static object _lyricsSourceLocal;

        private static Type _lyricsPanelType;
        private static FieldInfo _panelTrackField;
        private static MethodInfo _panelApplyLyricsMethod;

        public static void Initialize(MusicBeeApiInterface api) => MusicBeeArtworkBridge.Initialize(api);

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
                    _isAvailable = DiscoverMembers();
                }
                catch
                {
                    _isAvailable = false;
                }
            }
        }

        private static bool DiscoverMembers()
        {
            Assembly mbAssembly = MusicBeeArtworkBridge.MbAssembly;
            Type fpType = MusicBeeArtworkBridge.FilePathType;
            Type trackType = MusicBeeArtworkBridge.TrackType;
            if (mbAssembly == null || fpType == null || trackType == null || MusicBeeArtworkBridge.FilePathCtor == null)
                return false;

            const BindingFlags PubInst = BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly;
            const BindingFlags AnyInst = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance;

            foreach (var m in trackType.GetMethods(PubInst))
            {
                var ps = m.GetParameters();
                if (m.ReturnType == typeof(void) && ps.Length == 2 && ps[0].ParameterType.IsEnum && ps[1].ParameterType == typeof(string))
                {
                    string[] names = Enum.GetNames(ps[0].ParameterType);
                    if (Array.IndexOf(names, "SynchronisedLyrics") >= 0 && Array.IndexOf(names, "Lyrics") >= 0)
                    {
                        _trackSetTagMethod = m;
                        _tagCodeSync = Enum.Parse(ps[0].ParameterType, "SynchronisedLyrics");
                        _tagCodeLyrics = Enum.Parse(ps[0].ParameterType, "Lyrics");
                        break;
                    }
                }
            }
            if (_trackSetTagMethod == null)
                return false;

            var zeroArgVoidNames = new HashSet<string>(
                trackType.GetMethods(PubInst)
                    .Where(m => m.ReturnType == typeof(void) && m.GetParameters().Length == 0)
                    .Select(m => m.Name),
                StringComparer.Ordinal);

            _trackSetHasLyricsMethod = trackType.GetMethods(PubInst).FirstOrDefault(m =>
            {
                var ps = m.GetParameters();
                return m.ReturnType == typeof(void) &&
                       ps.Length == 2 &&
                       ps[0].ParameterType == typeof(bool) &&
                       ps[1].ParameterType == typeof(bool) &&
                       zeroArgVoidNames.Contains(m.Name);
            });

            _trackUrlField = trackType.GetFields(AnyInst).FirstOrDefault(f => f.FieldType == fpType);

            Type[] types = MusicBeeArtworkBridge.SafeGetTypes(mbAssembly);
            DiscoverLyricsManagerAndPanel(types, trackType);
            return true;
        }

        private static void DiscoverLyricsManagerAndPanel(Type[] types, Type trackType)
        {
            const BindingFlags PubStatic = BindingFlags.Public | BindingFlags.Static | BindingFlags.DeclaredOnly;
            const BindingFlags PrivInst = BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.DeclaredOnly;
            const BindingFlags AnyInst = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance;

            Type lyricsObjType = null;
            foreach (var t in types)
            {
                if (!t.IsNested || t.DeclaringType == null) continue;
                var upd = t.GetMethods(PubStatic).FirstOrDefault(m =>
                {
                    var ps = m.GetParameters();
                    return m.ReturnType == typeof(void) && ps.Length == 2 &&
                           ps[0].ParameterType == trackType && ps[1].ParameterType == typeof(string);
                });
                if (upd == null) continue;

                foreach (var outerMethod in t.DeclaringType.GetMethods(PubStatic))
                {
                    var ops = outerMethod.GetParameters();
                    Type ret = outerMethod.ReturnType;
                    if (ops.Length != 1 || ops[0].ParameterType != trackType || ret == typeof(void) || ret == typeof(string) || ret.IsPrimitive)
                        continue;

                    foreach (var ctor in ret.GetConstructors(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance))
                    {
                        var cps = ctor.GetParameters();
                        if (cps.Length == 2 && cps[0].ParameterType.IsEnum && cps[1].ParameterType == typeof(string))
                        {
                            string[] enumNames = Enum.GetNames(cps[0].ParameterType);
                            if (Array.IndexOf(enumNames, "Local") >= 0 && Array.IndexOf(enumNames, "None") >= 0)
                            {
                                _updateCurrentLyricsMethod = upd;
                                lyricsObjType = ret;
                                _lyricsObjCtor = ctor;
                                _lyricsSourceLocal = Enum.Parse(cps[0].ParameterType, "Local");
                                break;
                            }
                        }
                    }
                    if (lyricsObjType != null) break;
                }
                if (lyricsObjType != null) break;
            }

            if (lyricsObjType == null) return;

            foreach (var t in types)
            {
                if (t.IsAbstract || !typeof(Control).IsAssignableFrom(t)) continue;
                var apply = t.GetMethods(PrivInst).FirstOrDefault(m =>
                {
                    var ps = m.GetParameters();
                    return m.ReturnType == typeof(void) && ps.Length == 3 &&
                           ps[0].ParameterType == trackType &&
                           ps[1].ParameterType == lyricsObjType &&
                           ps[2].ParameterType == typeof(bool);
                });
                if (apply == null) continue;

                var trackField = t.GetFields(AnyInst).FirstOrDefault(f => f.FieldType == trackType);
                if (trackField != null)
                {
                    _lyricsPanelType = t;
                    _panelApplyLyricsMethod = apply;
                    _panelTrackField = trackField;
                    break;
                }
            }
        }

        private static void ApplyToTrackObject(object track, string trackUrl, string lyricsText, bool isSynced)
        {
            if (track == null || _trackSetTagMethod == null) return;
            bool isMp3 = trackUrl != null && trackUrl.EndsWith(".mp3", StringComparison.OrdinalIgnoreCase);

            if (isMp3 && isSynced)
            {
                _trackSetTagMethod.Invoke(track, new[] { _tagCodeSync, lyricsText });
                _trackSetTagMethod.Invoke(track, new[] { _tagCodeLyrics, "" });
            }
            else
            {
                _trackSetTagMethod.Invoke(track, new[] { _tagCodeLyrics, lyricsText });
                if (!isSynced)
                    _trackSetTagMethod.Invoke(track, new[] { _tagCodeSync, "" });
            }

            _trackSetHasLyricsMethod?.Invoke(track, new object[] { !isSynced, isSynced });
        }

        private static void CollectRootControls(List<Control> roots)
        {
            var seen = new HashSet<Control>();
            try
            {
                IntPtr hwnd = MusicBeeArtworkBridge.GetHostWindowHandle();
                if (hwnd != IntPtr.Zero)
                {
                    Control main = Control.FromHandle(hwnd);
                    if (main != null && seen.Add(main))
                        roots.Add(main);
                }
            }
            catch { }

            try
            {
                for (int i = 0; i < Application.OpenForms.Count; i++)
                {
                    Form form = Application.OpenForms[i];
                    if (form != null && !form.IsDisposed && seen.Add(form))
                        roots.Add(form);
                }
            }
            catch { }
        }

        private static bool ApplyToOpenLyricsPanels(string trackUrl, string lyricsText, bool isSynced, bool refreshActivePanel)
        {
            if (_lyricsPanelType == null || _panelTrackField == null || _trackUrlField == null)
                return false;

            var roots = new List<Control>();
            CollectRootControls(roots);
            if (roots.Count == 0) return false;

            bool updatedAny = false;
            var queue = new Queue<Control>(roots);
            while (queue.Count > 0)
            {
                Control ctrl = queue.Dequeue();
                if (ctrl == null || ctrl.IsDisposed) continue;

                try
                {
                    if (_lyricsPanelType.IsInstanceOfType(ctrl))
                    {
                        object panelTrack = _panelTrackField.GetValue(ctrl);
                        if (panelTrack != null)
                        {
                            string panelUrl = _trackUrlField.GetValue(panelTrack)?.ToString();
                            if (string.Equals(panelUrl, trackUrl, StringComparison.OrdinalIgnoreCase))
                            {
                                ApplyToTrackObject(panelTrack, trackUrl, lyricsText, isSynced);
                                _updateCurrentLyricsMethod?.Invoke(null, new[] { panelTrack, lyricsText });

                                if (refreshActivePanel && _lyricsObjCtor != null && _panelApplyLyricsMethod != null)
                                {
                                    object lyricsObj = _lyricsObjCtor.Invoke(new[] { _lyricsSourceLocal, lyricsText });
                                    _panelApplyLyricsMethod.Invoke(ctrl, new[] { panelTrack, lyricsObj, false });
                                }
                                updatedAny = true;
                            }
                        }
                    }

                    foreach (Control child in ctrl.Controls)
                    {
                        if (child != null && !child.IsDisposed)
                            queue.Enqueue(child);
                    }
                }
                catch { }
            }
            return updatedAny;
        }

        public static bool AttachLyrics(string trackUrl, string lyricsText, bool isSynced, bool refreshActivePanel = false, bool synchronous = true)
        {
            if (string.IsNullOrEmpty(trackUrl) || string.IsNullOrWhiteSpace(lyricsText))
                return false;
            if (!IsAvailable)
                return false;

            bool attached = false;
            Action apply = () =>
            {
                try
                {
                    if (MusicBeeArtworkBridge.TagGetTrackMethod != null)
                    {
                        object fp = MusicBeeArtworkBridge.FilePathCtor.Invoke(new object[] { trackUrl });
                        object libTrack = fp != null ? MusicBeeArtworkBridge.TagGetTrackMethod.Invoke(null, new[] { fp }) : null;
                        if (libTrack != null)
                        {
                            ApplyToTrackObject(libTrack, trackUrl, lyricsText, isSynced);
                            _updateCurrentLyricsMethod?.Invoke(null, new[] { libTrack, lyricsText });
                            attached = true;
                        }
                    }

                    if (ApplyToOpenLyricsPanels(trackUrl, lyricsText, isSynced, refreshActivePanel))
                        attached = true;
                }
                catch { }
            };

            if (synchronous)
                MusicBeeArtworkBridge.RunOnUiThread(apply);
            else
                MusicBeeArtworkBridge.BeginRunOnUiThread(apply);

            return synchronous && attached;
        }
    }
}
