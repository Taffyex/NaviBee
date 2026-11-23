using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;

namespace MusicBeePlugin
{
    public static partial class MusicBeeArtworkBridge
    {
        internal static Type[] SafeGetTypes(Assembly asm)
        {
            try
            {
                return asm.GetTypes();
            }
            catch (ReflectionTypeLoadException ex)
            {
                return ex.Types?.Where(t => t != null).ToArray() ?? new Type[0];
            }
        }

        private static bool DiscoverBridgeMembers(Assembly mbAssembly)
        {
            Type[] types = SafeGetTypes(mbAssembly);
            const BindingFlags InstFlags = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.DeclaredOnly;
            const BindingFlags StaticFlags = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static | BindingFlags.DeclaredOnly;

            Type ePicType = null;
            Type eStorageType = null;
            foreach (var t in types)
            {
                try
                {
                    if (!t.IsEnum) continue;
                    string[] names = Enum.GetNames(t);
                    if (ePicType == null && Array.IndexOf(names, "AlbumCover") >= 0)
                        ePicType = t;
                    if (eStorageType == null && Array.IndexOf(names, "LinkToSource") >= 0)
                        eStorageType = t;
                    if (ePicType != null && eStorageType != null) break;
                }
                catch { }
            }
            if (ePicType == null || eStorageType == null) return false;

            _albumCoverEnumVal = Enum.Parse(ePicType, "AlbumCover");
            _linkToSourceEnumVal = Enum.Parse(eStorageType, "LinkToSource");

            Type arType = null;
            Type fpType = null;
            foreach (var t in types)
            {
                try
                {
                    foreach (var ctor in t.GetConstructors(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance))
                    {
                        var ps = ctor.GetParameters();
                        if (ps.Length == 4 &&
                            ps[0].ParameterType == ePicType &&
                            ps[1].ParameterType == typeof(string) &&
                            ps[2].ParameterType == eStorageType)
                        {
                            var fpCtor = ps[3].ParameterType.GetConstructor(new[] { typeof(string) });
                            if (fpCtor != null)
                            {
                                arType = t;
                                fpType = ps[3].ParameterType;
                                _filePathType = fpType;
                                _artworkRefCtor = ctor;
                                _filePathCtor = fpCtor;
                                break;
                            }
                        }
                    }
                }
                catch { }
                if (arType != null) break;
            }
            if (arType == null || fpType == null || _artworkRefCtor == null || _filePathCtor == null)
                return false;

            Type colType = null;
            foreach (var t in types)
            {
                try
                {
                    if (!t.IsValueType || t.IsEnum) continue;
                    if (t.GetMethods(InstFlags).Any(m =>
                    {
                        var ps = m.GetParameters();
                        return m.ReturnType == typeof(void) && ps.Length == 1 && ps[0].ParameterType == arType;
                    }))
                    {
                        colType = t;
                        break;
                    }
                }
                catch { }
            }
            if (colType == null) return false;

            _artworkCollectionSetMethod = IdentifyPrimarySetter(colType, arType);
            if (_artworkCollectionSetMethod == null) return false;

            Type trackType = null;
            foreach (var t in types)
            {
                try
                {
                    if (t.IsValueType) continue;
                    var field = t.GetFields(InstFlags).FirstOrDefault(f => f.FieldType == colType);
                    if (field != null)
                    {
                        trackType = t;
                        _trackArtworkField = field;
                        break;
                    }
                }
                catch { }
            }
            if (trackType == null || _trackArtworkField == null) return false;
            _trackType = trackType;

            _tagGetTrackMethod = FindGetTrackMethod(types, fpType, trackType, StaticFlags);
            _artClearMethod = FindArtClearMethod(types, trackType, arType, StaticFlags);

            return _tagGetTrackMethod != null && _artClearMethod != null;
        }

        private static MethodInfo IdentifyPrimarySetter(Type colType, Type arType)
        {
            const BindingFlags InstFlags = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.DeclaredOnly;
            var candidates = colType.GetMethods(InstFlags)
                .Where(m =>
                {
                    var ps = m.GetParameters();
                    return m.ReturnType == typeof(void) && ps.Length == 1 && ps[0].ParameterType == arType;
                })
                .ToList();

            if (candidates.Count == 1) return candidates[0];
            if (candidates.Count == 0) return null;

            try
            {
                object dummyFp = _filePathCtor.Invoke(new object[] { "probe.jpg" });
                object dummyAr = _artworkRefCtor.Invoke(new[] { _albumCoverEnumVal, null, _linkToSourceEnumVal, dummyFp });
                foreach (var m in candidates)
                {
                    object boxed = Activator.CreateInstance(colType);
                    m.Invoke(boxed, new[] { dummyAr });
                    m.Invoke(boxed, new[] { dummyAr });
                    if (boxed is IEnumerable enumerable)
                    {
                        int count = 0;
                        foreach (var _ in enumerable) count++;
                        if (count == 1) return m;
                    }
                }
            }
            catch { }
            return null;
        }

        private static MethodInfo FindGetTrackMethod(Type[] types, Type fpType, Type trackType, BindingFlags staticFlags)
        {
            foreach (var t in types)
            {
                try
                {
                    MethodInfo candidate = null;
                    bool hasTagArrayMethod = false;
                    foreach (var m in t.GetMethods(staticFlags))
                    {
                        var ps = m.GetParameters();
                        if (candidate == null && m.ReturnType == trackType && ps.Length == 1 && ps[0].ParameterType == fpType)
                            candidate = m;
                        if (!hasTagArrayMethod && ps.Any(p => p.ParameterType == typeof(KeyValuePair<byte, string>[])))
                            hasTagArrayMethod = true;
                    }
                    if (candidate != null && hasTagArrayMethod)
                        return candidate;
                }
                catch { }
            }
            return null;
        }

        private static MethodInfo FindArtClearMethod(Type[] types, Type trackType, Type arType, BindingFlags staticFlags)
        {
            foreach (var t in types)
            {
                try
                {
                    MethodInfo candidate = null;
                    bool hasArtworkRefMethod = false;
                    foreach (var m in t.GetMethods(staticFlags))
                    {
                        var ps = m.GetParameters();
                        if (candidate == null && m.ReturnType == typeof(void) && ps.Length == 1 && ps[0].ParameterType == trackType)
                            candidate = m;
                        if (!hasArtworkRefMethod && ps.Any(p => p.ParameterType == arType))
                            hasArtworkRefMethod = true;
                    }
                    if (candidate != null && hasArtworkRefMethod)
                        return candidate;
                }
                catch { }
            }
            return null;
        }
    }
}
