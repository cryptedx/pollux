using System.Runtime.InteropServices;
using System.Runtime.InteropServices.Marshalling;
using Pollux.Core.Files;

namespace Pollux.App.Interop;

/// <summary>
/// Reads the shell properties behind the Details property columns (dimensions, duration, authors, …) from a file's
/// property store. Source-generated COM interop like <see cref="ShellThumbnail"/> (Native AOT, ADR 0002); PROPVARIANTs
/// are read by hand for the few types these properties use. Blocking: call it from a background STA thread.
/// </summary>
internal static unsafe partial class ShellProperties
{
    // GETPROPERTYSTOREFLAGS. Fast-only reads what the system already has (indexer, cloud provider) without running the
    // file's property handler, so a cloud-only placeholder is never downloaded. Without GPS_OPENSLOWITEM slow
    // (offline) items fail instead of being opened.
    private const int GPS_FASTPROPERTIESONLY = 0x8, GPS_BESTEFFORT = 0x40;

    private const ushort VT_BSTR = 8, VT_UI4 = 19, VT_UI8 = 21, VT_LPWSTR = 31, VT_FILETIME = 64, VT_VECTOR = 0x1000;

    private static readonly PROPERTYKEY ImageWidth = new(new("6444048F-4C8B-11D1-8B70-080036B11A03"), 3);
    private static readonly PROPERTYKEY ImageHeight = new(new("6444048F-4C8B-11D1-8B70-080036B11A03"), 4);
    private static readonly PROPERTYKEY VideoWidth = new(new("64440491-4C8B-11D1-8B70-080036B11A03"), 3);
    private static readonly PROPERTYKEY VideoHeight = new(new("64440491-4C8B-11D1-8B70-080036B11A03"), 4);
    private static readonly PROPERTYKEY MediaDuration = new(new("64440490-4C8B-11D1-8B70-080036B11A03"), 3);
    private static readonly PROPERTYKEY Author = new(new("F29F85E0-4FF9-1068-AB91-08002B27B3D9"), 4);
    private static readonly PROPERTYKEY Title = new(new("F29F85E0-4FF9-1068-AB91-08002B27B3D9"), 2);
    private static readonly PROPERTYKEY FileVersion = new(new("0CEF7D53-FA64-11D1-A203-0000F81FEDEE"), 4);
    private static readonly PROPERTYKEY Company = new(new("D5CDD502-2E9C-101B-9397-08002B2CF9AE"), 15);
    private static readonly PROPERTYKEY DateTaken = new(new("14B81DA1-0135-4D31-96D9-6CBFC9671A99"), 36867);

    private static readonly StrategyBasedComWrappers Wrappers = new();

    /// <summary>
    /// The requested properties of the file; a null value means it has none. Null when its property store cannot be
    /// opened. <paramref name="fastOnly"/> for cloud-only placeholders and network files: only what the system has cached.
    /// </summary>
    public static Dictionary<ShellProperty, PropertyValue?>? Read(string path, IEnumerable<ShellProperty> properties, bool fastOnly, string dateFormat)
    {
        nint storePtr = 0;
        IPropertyStore? store = null;
        try
        {
            var iid = typeof(IPropertyStore).GUID;
            var flags = fastOnly ? GPS_FASTPROPERTIESONLY : GPS_BESTEFFORT;
            if (SHGetPropertyStoreFromParsingName(path, 0, flags, &iid, out storePtr) != 0 || storePtr == 0) return null;
            store = (IPropertyStore)Wrappers.GetOrCreateObjectForComInstance(storePtr, CreateObjectFlags.UniqueInstance);
            var result = new Dictionary<ShellProperty, PropertyValue?>();
            foreach (var property in properties) result[property] = ReadOne(store, property, dateFormat);
            return result;
        }
        catch (Exception ex) when (ex is COMException or InvalidCastException)
        {
            Services.CrashLog.Trace($"properties {path} failed: 0x{ex.HResult:X8} {ex.Message}");
            return null;
        }
        finally
        {
            if ((object?)store is ComObject com) com.FinalRelease();
            if (storePtr != 0) Marshal.Release(storePtr);
        }
    }

    private static PropertyValue? ReadOne(IPropertyStore store, ShellProperty property, string dateFormat) => property switch
    {
        ShellProperty.Dimensions => Size(store, ImageWidth, ImageHeight) ?? Size(store, VideoWidth, VideoHeight),
        ShellProperty.Duration => GetUInt64(store, MediaDuration) is { } d and > 0 ? PropertyValue.Duration(d) : null,
        ShellProperty.Authors => PropertyValue.Texts(GetStrings(store, Author)),
        ShellProperty.Title => PropertyValue.FromText(GetStrings(store, Title).FirstOrDefault()),
        ShellProperty.FileVersion => PropertyValue.FromText(GetStrings(store, FileVersion).FirstOrDefault()),
        ShellProperty.Company => PropertyValue.FromText(GetStrings(store, Company).FirstOrDefault()),
        ShellProperty.DateTaken => GetDate(store, DateTaken) is { } date ? PropertyValue.Date(date, dateFormat) : null,
        _ => null,
    };

    private static PropertyValue? Size(IPropertyStore store, PROPERTYKEY width, PROPERTYKEY height) =>
        GetUInt64(store, width) is { } w and > 0 and <= uint.MaxValue && GetUInt64(store, height) is { } h and > 0 and <= uint.MaxValue
            ? PropertyValue.Dimensions((uint)w, (uint)h)
            : null;

    private static ulong? GetUInt64(IPropertyStore store, PROPERTYKEY key)
    {
        PROPVARIANT pv = default;
        try
        {
            if (store.GetValue(&key, &pv) != 0) return null;
            return pv.vt switch
            {
                VT_UI4 => pv.data.ui4,
                VT_UI8 => pv.data.ui8,
                _ => null,
            };
        }
        finally
        {
            _ = PropVariantClear(&pv);
        }
    }

    private static List<string> GetStrings(IPropertyStore store, PROPERTYKEY key)
    {
        PROPVARIANT pv = default;
        var result = new List<string>();
        try
        {
            if (store.GetValue(&key, &pv) != 0) return result;
            if (pv.vt is VT_LPWSTR or VT_BSTR)
            {
                if (pv.data.pointer != 0) result.Add(new string((char*)pv.data.pointer));
            }
            else if (pv.vt == (VT_VECTOR | VT_LPWSTR) && pv.data.vector.pElems != null)
            {
                for (var i = 0; i < pv.data.vector.cElems; i++)
                {
                    if (pv.data.vector.pElems[i] != null) result.Add(new string(pv.data.vector.pElems[i]));
                }
            }

            return result;
        }
        finally
        {
            _ = PropVariantClear(&pv);
        }
    }

    private static DateTime? GetDate(IPropertyStore store, PROPERTYKEY key)
    {
        PROPVARIANT pv = default;
        try
        {
            if (store.GetValue(&key, &pv) != 0 || pv.vt != VT_FILETIME || pv.data.ui8 == 0) return null;
            return DateTime.FromFileTimeUtc((long)pv.data.ui8).ToLocalTime();
        }
        catch (ArgumentOutOfRangeException)
        {
            return null;
        }
        finally
        {
            _ = PropVariantClear(&pv);
        }
    }

    [StructLayout(LayoutKind.Sequential)]
    internal readonly struct PROPERTYKEY(Guid fmtid, uint pid)
    {
        public readonly Guid fmtid = fmtid;
        public readonly uint pid = pid;
    }

    /// <summary>A PROPVARIANT: the type, three reserved words, then the value (24 bytes on 64-bit).</summary>
    [StructLayout(LayoutKind.Sequential)]
    internal struct PROPVARIANT
    {
        public ushort vt;
        public ushort reserved1;
        public ushort reserved2;
        public ushort reserved3;
        public PROPVARIANTDATA data;
    }

    [StructLayout(LayoutKind.Explicit)]
    internal struct PROPVARIANTDATA
    {
        [FieldOffset(0)] public nint pointer;
        [FieldOffset(0)] public uint ui4;
        [FieldOffset(0)] public ulong ui8;
        [FieldOffset(0)] public CALPWSTR vector;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct CALPWSTR
    {
        public uint cElems;
        public char** pElems;
    }

    [LibraryImport("shell32.dll", StringMarshalling = StringMarshalling.Utf16)]
    private static partial int SHGetPropertyStoreFromParsingName(string path, nint bindCtx, int flags, Guid* riid, out nint ppv);

    [LibraryImport("ole32.dll")]
    private static partial int PropVariantClear(PROPVARIANT* pv);

    [GeneratedComInterface]
    [Guid("886D8EEB-8CF2-4446-8D02-CDBA1DBDCF99")]
    internal partial interface IPropertyStore
    {
        [PreserveSig] int GetCount(out uint count);
        [PreserveSig] int GetAt(uint index, PROPERTYKEY* key);
        [PreserveSig] int GetValue(PROPERTYKEY* key, PROPVARIANT* value);
        [PreserveSig] int SetValue(PROPERTYKEY* key, PROPVARIANT* value);
        [PreserveSig] int Commit();
    }
}
