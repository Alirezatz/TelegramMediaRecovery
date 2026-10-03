using System.Security.Cryptography;

namespace TelegramMediaRecovery.Core.Media;

/// <summary>
/// Writes a reconstructed streamed file to disk without ever holding the whole
/// file in memory.
/// </summary>
/// <remarks>
/// A 4 GB video must not become a 4 GB <c>byte[]</c>. The file is created at its
/// final length (sparse, so unvisited ranges cost nothing) and every part is
/// written at its offset; the gaps between them read back as zeros, which is
/// exactly what the player itself would see for the same half-cached file.
/// <para>
/// Parts are visited in ascending order so the SHA-256 can be computed in a
/// single sequential pass.
/// </para>
/// </remarks>
public static class StreamedFileWriter
{
    private const int BufferSize = 1 << 20;

    /// <summary>
    /// Materialises <paramref name="plan"/> at <paramref name="destination"/>.
    /// </summary>
    /// <returns>The lowercase hex SHA-256 of the bytes on disk.</returns>
    public static string Write(StreamedFilePlan plan, string destination)
    {
        ArgumentNullException.ThrowIfNull(plan);

        using var stream = new FileStream(
            destination,
            FileMode.Create,
            FileAccess.ReadWrite,
            FileShare.None,
            bufferSize: BufferSize,
            FileOptions.SequentialScan);

        stream.SetLength(Math.Max(plan.FileSize, 0));

        using var sha = SHA256.Create();
        var buffer = new byte[BufferSize];
        long cursor = 0;

        foreach (var part in plan.AllParts())
        {
            if (part.Offset > cursor)
            {
                stream.Seek(cursor, SeekOrigin.Begin);
                HashRange(stream, sha, buffer, part.Offset - cursor);
            }

            var bytes = part.Data.Span;
            if (bytes.Length > 0)
            {
                stream.Seek(part.Offset, SeekOrigin.Begin);
                if (bytes.Length <= buffer.Length)
                {
                    bytes.CopyTo(buffer);
                    sha.TransformBlock(buffer, 0, bytes.Length, null, 0);
                    stream.Write(buffer, 0, bytes.Length);
                }
                else
                {
                    var copy = bytes.ToArray();
                    sha.TransformBlock(copy, 0, copy.Length, null, 0);
                    stream.Write(copy, 0, copy.Length);
                }
            }

            cursor = Math.Max(cursor, part.Offset + part.Length);
        }

        if (plan.FileSize > cursor)
        {
            stream.Seek(cursor, SeekOrigin.Begin);
            HashRange(stream, sha, buffer, plan.FileSize - cursor);
        }

        sha.TransformFinalBlock([], 0, 0);
        stream.Flush();
        return Convert.ToHexString(sha.Hash!).ToLowerInvariant();
    }

    private static void HashRange(FileStream stream, HashAlgorithm sha, byte[] buffer, long length)
    {
        var remaining = length;
        while (remaining > 0)
        {
            var chunk = (int)Math.Min(remaining, buffer.Length);
            var read = stream.Read(buffer, 0, chunk);
            if (read == 0)
            {
                break;
            }

            sha.TransformBlock(buffer, 0, read, null, 0);
            remaining -= read;
        }
    }
}
