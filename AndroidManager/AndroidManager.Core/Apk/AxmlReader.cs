using System.Text;

namespace AndroidManager.Core.Apk;

/// <summary>Minimal reader for Android binary XML (AndroidManifest.xml inside an APK).</summary>
public static class AxmlReader
{
    private const ushort ChunkXml = 0x0003;
    private const ushort ChunkStringPool = 0x0001;
    private const ushort ChunkResourceMap = 0x0180;
    private const ushort ChunkStartElement = 0x0102;
    private const ushort ChunkEndElement = 0x0103;

    private const byte TypeString = 0x03;
    private const byte TypeIntDec = 0x10;
    private const byte TypeIntHex = 0x11;
    private const byte TypeBoolean = 0x12;

    // Android framework attribute resource ids, used when attribute names are stripped.
    private static readonly Dictionary<uint, string> KnownAttrIds = new()
    {
        [0x01010003] = "name",
        [0x0101020c] = "minSdkVersion",
        [0x01010270] = "targetSdkVersion",
        [0x0101021b] = "versionCode",
        [0x0101021c] = "versionName",
        [0x0101028e] = "required",
        [0x01010576] = "versionCodeMajor",
    };

    public static ApkManifest ReadManifest(byte[] data)
    {
        try { return Parse(data); }
        catch (Exception ex) when (ex is IndexOutOfRangeException or ArgumentException or OverflowException)
        {
            throw new InvalidApkException("AndroidManifest.xml corrotto o troncato.", ex);
        }
    }

    private static ApkManifest Parse(byte[] d)
    {
        if (d.Length < 8 || U16(d, 0) != ChunkXml)
            throw new InvalidApkException("AndroidManifest.xml non è in formato binario Android.");

        string[] strings = [];
        uint[] resMap = [];
        string? package = null, versionName = null;
        long versionCode = 0, versionCodeMajor = 0;
        int? minSdk = null, targetSdk = null;
        bool launcher = false, leanback = false, requiresLeanback = false;
        var stack = new List<string>();
        var categories = new List<string>(); // categories of the intent-filter being read

        var pos = (int)U16(d, 2);
        while (pos + 8 <= d.Length)
        {
            var type = U16(d, pos);
            var size = (int)U32(d, pos + 4);
            if (size < 8 || pos + size > d.Length) throw new InvalidApkException("Chunk manifest non valido.");

            switch (type)
            {
                case ChunkStringPool:
                    strings = ReadStringPool(d, pos);
                    break;
                case ChunkResourceMap:
                    resMap = Enumerable.Range(0, (size - 8) / 4).Select(i => U32(d, pos + 8 + i * 4)).ToArray();
                    break;
                case ChunkStartElement:
                {
                    var name = Str(strings, (int)U32(d, pos + 20));
                    var attrStart = pos + 16 + U16(d, pos + 24);
                    var attrSize = U16(d, pos + 26);
                    var attrCount = U16(d, pos + 28);
                    var attrs = new Dictionary<string, (byte Type, uint Data, int Raw)>();
                    for (var i = 0; i < attrCount; i++)
                    {
                        var a = attrStart + i * attrSize;
                        var nameIdx = (int)U32(d, a + 4);
                        var an = Str(strings, nameIdx);
                        if (an.Length == 0 && nameIdx >= 0 && nameIdx < resMap.Length
                            && KnownAttrIds.TryGetValue(resMap[nameIdx], out var known)) an = known;
                        if (an.Length > 0) attrs[an] = (d[a + 15], U32(d, a + 16), (int)U32(d, a + 8));
                    }

                    string? S(string k) => !attrs.TryGetValue(k, out var v) ? null
                        : v.Type == TypeString ? Str(strings, (int)v.Data)
                        : v.Raw >= 0 ? Str(strings, v.Raw) : null;
                    long? N(string k) => attrs.TryGetValue(k, out var v) && v.Type is TypeIntDec or TypeIntHex
                        ? v.Data : null;

                    if (name == "manifest" && stack.Count == 0)
                    {
                        package = S("package");
                        versionCode = N("versionCode") ?? 0;
                        versionCodeMajor = N("versionCodeMajor") ?? 0;
                        versionName = S("versionName");
                    }
                    else if (name == "uses-sdk")
                    {
                        minSdk = (int?)N("minSdkVersion");
                        targetSdk = (int?)N("targetSdkVersion");
                    }
                    else if (name == "uses-feature" && S("name") == "android.software.leanback")
                    {
                        // required defaults to true when absent
                        requiresLeanback = !attrs.TryGetValue("required", out var req)
                                           || req.Type != TypeBoolean || req.Data != 0;
                    }
                    else if (name == "intent-filter") categories.Clear();
                    else if (name == "category" && S("name") is { } cat) categories.Add(cat);

                    stack.Add(name);
                    break;
                }
                case ChunkEndElement:
                    if (stack.Count > 0)
                    {
                        if (stack[^1] == "intent-filter")
                        {
                            launcher |= categories.Contains("android.intent.category.LAUNCHER");
                            leanback |= categories.Contains("android.intent.category.LEANBACK_LAUNCHER");
                        }
                        stack.RemoveAt(stack.Count - 1);
                    }
                    break;
            }
            pos += size;
        }

        if (string.IsNullOrEmpty(package))
            throw new InvalidApkException("Manifest senza nome pacchetto.");
        return new ApkManifest(package, (versionCodeMajor << 32) | (versionCode & 0xFFFFFFFFL), versionName,
            minSdk, targetSdk, launcher, leanback, requiresLeanback);
    }

    private static string[] ReadStringPool(byte[] d, int chunk)
    {
        var count = (int)U32(d, chunk + 8);
        var flags = U32(d, chunk + 16);
        var stringsStart = chunk + (int)U32(d, chunk + 20);
        var utf8 = (flags & 0x100) != 0;
        if (count < 0 || count > 1_000_000) throw new InvalidApkException("String pool non valido.");

        var result = new string[count];
        for (var i = 0; i < count; i++)
        {
            var p = stringsStart + (int)U32(d, chunk + 28 + i * 4);
            if (utf8)
            {
                SkipLen8(d, ref p);                 // chars
                var len = ReadLen8(d, ref p);       // bytes
                result[i] = Encoding.UTF8.GetString(d, p, len);
            }
            else
            {
                var len = (int)U16(d, p);
                p += 2;
                if ((len & 0x8000) != 0) { len = ((len & 0x7FFF) << 16) | U16(d, p); p += 2; }
                result[i] = Encoding.Unicode.GetString(d, p, len * 2);
            }
        }
        return result;
    }

    private static int ReadLen8(byte[] d, ref int p)
    {
        int len = d[p++];
        if ((len & 0x80) != 0) len = ((len & 0x7F) << 8) | d[p++];
        return len;
    }

    private static void SkipLen8(byte[] d, ref int p) => ReadLen8(d, ref p);

    private static string Str(string[] pool, int idx) => idx >= 0 && idx < pool.Length ? pool[idx] : "";
    private static ushort U16(byte[] d, int p) => BitConverter.ToUInt16(d, p);
    private static uint U32(byte[] d, int p) => BitConverter.ToUInt32(d, p);
}
