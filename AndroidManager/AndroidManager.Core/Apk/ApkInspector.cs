using System.IO.Compression;
using System.Security.Cryptography;

namespace AndroidManager.Core.Apk;

public sealed record ApkSignature(bool V1, bool V2OrV3)
{
    public bool IsSigned => V1 || V2OrV3;
    public override string ToString() => !IsSigned ? "nessuna"
        : string.Join("+", new[] { V1 ? "v1" : null, V2OrV3 ? "v2/v3" : null }.Where(s => s is not null));
}

public sealed record ApkInfo(
    string Path,
    long SizeBytes,
    string Sha256,
    ApkManifest Manifest,
    IReadOnlyList<string> Abis,
    ApkSignature Signature);

/// <summary>Reads an APK on the PC. Checks structure and signature presence, not certificate validity (the device does that).</summary>
public static class ApkInspector
{
    private const string ManifestEntry = "AndroidManifest.xml";
    private const uint SigBlockIdV2 = 0x7109871a, SigBlockIdV3 = 0xf05368c0, SigBlockIdV31 = 0x1b93ad61;

    public static async Task<ApkInfo> InspectAsync(string path, CancellationToken ct = default)
    {
        if (!File.Exists(path)) throw new InvalidApkException($"File non trovato: {path}");
        var ext = System.IO.Path.GetExtension(path).ToLowerInvariant();
        if (ext is ".xapk" or ".apks" or ".apkm")
            throw new InvalidApkException("I bundle (.xapk/.apks/.apkm) con APK multipli non sono supportati: serve un singolo .apk.");

        var size = new FileInfo(path).Length;
        if (size < 4) throw new InvalidApkException("File troppo piccolo per essere un APK.");

        var (manifest, abis, v1) = await Task.Run(() => ReadZip(path), ct);
        var v2 = await Task.Run(() => HasV2OrV3Block(path), ct);
        var sha = await HashAsync(path, ct);
        return new ApkInfo(path, size, sha, manifest, abis, new ApkSignature(v1, v2));
    }

    private static (ApkManifest, IReadOnlyList<string>, bool) ReadZip(string path)
    {
        try
        {
            using var zip = ZipFile.OpenRead(path);
            var entry = zip.GetEntry(ManifestEntry);
            if (entry is null)
            {
                var inner = zip.Entries.Any(e => e.FullName.EndsWith(".apk", StringComparison.OrdinalIgnoreCase));
                throw new InvalidApkException(inner
                    ? "Il file contiene altri APK (bundle): estrai e installa l'APK principale."
                    : "Non è un APK: manca AndroidManifest.xml.");
            }

            byte[] bytes;
            using (var s = entry.Open())
            using (var ms = new MemoryStream())
            {
                s.CopyTo(ms);
                bytes = ms.ToArray();
            }

            var abis = zip.Entries
                .Select(e => e.FullName.Split('/'))
                .Where(p => p.Length >= 3 && p[0] == "lib" && p[^1].EndsWith(".so", StringComparison.OrdinalIgnoreCase))
                .Select(p => p[1]).Distinct().Order().ToList();

            var v1 = zip.Entries.Any(e =>
                e.FullName.StartsWith("META-INF/", StringComparison.OrdinalIgnoreCase)
                && e.FullName.Count(c => c == '/') == 1
                && (e.FullName.EndsWith(".RSA", StringComparison.OrdinalIgnoreCase)
                    || e.FullName.EndsWith(".DSA", StringComparison.OrdinalIgnoreCase)
                    || e.FullName.EndsWith(".EC", StringComparison.OrdinalIgnoreCase)));

            return (AxmlReader.ReadManifest(bytes), abis, v1);
        }
        catch (InvalidDataException ex)
        {
            throw new InvalidApkException("File ZIP/APK corrotto o incompleto.", ex);
        }
    }

    /// <summary>Looks for the "APK Sig Block 42" in front of the ZIP central directory and a v2/v3 signer pair.</summary>
    internal static bool HasV2OrV3Block(string path)
    {
        try
        {
            using var f = File.OpenRead(path);
            var tail = (int)Math.Min(f.Length, 22 + 65535);
            var buf = new byte[tail];
            f.Seek(-tail, SeekOrigin.End);
            f.ReadExactly(buf);

            var eocd = -1;
            for (var i = tail - 22; i >= 0; i--)
                if (buf[i] == 0x50 && buf[i + 1] == 0x4b && buf[i + 2] == 5 && buf[i + 3] == 6) { eocd = i; break; }
            if (eocd < 0) return false;

            long cdOffset = BitConverter.ToUInt32(buf, eocd + 16);
            if (cdOffset < 32) return false;

            var magic = new byte[16];
            f.Seek(cdOffset - 16, SeekOrigin.Begin);
            f.ReadExactly(magic);
            if (!magic.AsSpan().SequenceEqual("APK Sig Block 42"u8)) return false;

            var sizeBuf = new byte[8];
            f.Seek(cdOffset - 24, SeekOrigin.Begin);
            f.ReadExactly(sizeBuf);
            var blockSize = BitConverter.ToInt64(sizeBuf);
            var start = cdOffset - blockSize - 8;
            if (blockSize < 24 || start < 0) return false;

            var pos = start + 8; // after the leading size field
            var end = cdOffset - 24;
            var pair = new byte[12];
            while (pos + 12 <= end)
            {
                f.Seek(pos, SeekOrigin.Begin);
                f.ReadExactly(pair);
                var len = BitConverter.ToInt64(pair, 0);
                var id = BitConverter.ToUInt32(pair, 8);
                if (id is SigBlockIdV2 or SigBlockIdV3 or SigBlockIdV31) return true;
                if (len < 4) return false;
                pos += 8 + len;
            }
            return false;
        }
        catch (Exception ex) when (ex is IOException or EndOfStreamException or ArgumentException)
        {
            return false;
        }
    }

    private static async Task<string> HashAsync(string path, CancellationToken ct)
    {
        await using var f = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 1 << 16, useAsync: true);
        return Convert.ToHexString(await SHA256.HashDataAsync(f, ct)).ToLowerInvariant();
    }
}
