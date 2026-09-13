using System.Text;

namespace Shubbak.Config;

/// <summary>A configuration file's text, and what its bytes said about themselves.</summary>
/// <param name="Text">The contents, without any byte-order mark.</param>
/// <param name="HasBom">Whether the file began with a UTF-8 byte-order mark.</param>
public sealed record ConfigText(string Text, bool HasBom);

/// <summary>
/// Reads and writes a configuration file as bytes somebody else owns.
/// </summary>
/// <remarks>
/// <para>
/// <see cref="File.ReadAllText(string)"/> strips a byte-order mark and forgets it did,
/// and <see cref="File.WriteAllText(string, string)"/> never writes one; a file that
/// arrived with a mark would leave without it, which is a change to a file whose
/// owner did not ask for one and which some editors show as a modification. So the
/// mark is read, remembered and put back.
/// </para>
/// <para>
/// Written through a temporary file and a rename, so a crash or a full disk mid-write
/// leaves the old file whole rather than a truncated one - the same pattern the
/// session store uses, for the same reason.
/// </para>
/// </remarks>
public static class ConfigFile
{
    private static readonly UTF8Encoding s_utf8 = new(encoderShouldEmitUTF8Identifier: false);

    /// <summary>Reads the file, noting its byte-order mark.</summary>
    /// <exception cref="IOException">The file could not be read.</exception>
    /// <exception cref="UnauthorizedAccessException">The file could not be read.</exception>
    /// <exception cref="InvalidDataException">The file is not UTF-8.</exception>
    public static ConfigText Read(string path)
    {
        ArgumentException.ThrowIfNullOrEmpty(path);

        byte[] bytes = File.ReadAllBytes(path);

        // UTF-16 files load - the runtime's reader recognises the mark - but editing
        // one as UTF-8 and writing it back would destroy it. Refused rather than
        // guessed at; nothing writes one, and nobody has asked.
        if (bytes.Length >= 2 && ((bytes[0] == 0xFF && bytes[1] == 0xFE) || (bytes[0] == 0xFE && bytes[1] == 0xFF)))
            throw new InvalidDataException("The configuration file is UTF-16, which this tool does not edit. Save it as UTF-8 first.");

        bool bom = bytes.Length >= 3 && bytes[0] == 0xEF && bytes[1] == 0xBB && bytes[2] == 0xBF;

        return new ConfigText(s_utf8.GetString(bytes, bom ? 3 : 0, bytes.Length - (bom ? 3 : 0)), bom);
    }

    /// <summary>Replaces the file's contents, keeping its byte-order mark as it was.</summary>
    /// <exception cref="IOException">The file could not be written.</exception>
    /// <exception cref="UnauthorizedAccessException">The file could not be written.</exception>
    public static void Write(string path, string text, bool bom)
    {
        ArgumentException.ThrowIfNullOrEmpty(path);
        ArgumentNullException.ThrowIfNull(text);

        // Beside the target, so the rename stays on one volume and stays atomic. Named
        // uniquely so two writers cannot collide on the temporary file itself.
        string temporary = $"{path}.{Environment.ProcessId}-{Guid.NewGuid():N}.tmp";

        try
        {
            using (var stream = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            {
                if (bom) stream.Write(Encoding.UTF8.Preamble);

                byte[] bytes = s_utf8.GetBytes(text);
                stream.Write(bytes, 0, bytes.Length);
                stream.Flush(flushToDisk: true);
            }

            File.Move(temporary, path, overwrite: true);
        }
        finally
        {
            // A failed rename, or a failed write, must not leave its half behind.
            if (File.Exists(temporary))
            {
                try { File.Delete(temporary); }
                catch (IOException) { }
                catch (UnauthorizedAccessException) { }
            }
        }
    }
}
