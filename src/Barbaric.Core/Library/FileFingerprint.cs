using System.Security.Cryptography;

namespace Barbaric.Core.Library;

/// <summary>
/// A cheap content fingerprint used to recognise a file after it was moved or renamed:
/// SHA-256 over the file size, the first 64 KB and the last 64 KB.
/// </summary>
public static class FileFingerprint
{
    private const int ChunkSize = 64 * 1024;

    public static string Compute(string path)
    {
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        var length = stream.Length;

        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        hash.AppendData(BitConverter.GetBytes(length));

        var buffer = new byte[ChunkSize];
        var head = (int)Math.Min(ChunkSize, length);
        stream.ReadExactly(buffer, 0, head);
        hash.AppendData(buffer, 0, head);

        if (length > ChunkSize)
        {
            var tailStart = Math.Max(ChunkSize, length - ChunkSize);
            var tail = (int)(length - tailStart);
            stream.Seek(tailStart, SeekOrigin.Begin);
            stream.ReadExactly(buffer, 0, tail);
            hash.AppendData(buffer, 0, tail);
        }

        return Convert.ToHexString(hash.GetHashAndReset(), 0, 16);
    }
}
