using System;
using System.Collections.Generic;
using System.Runtime.Serialization;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;

namespace Crucible.Tray
{
    /// <summary>
    /// The plain-words notice every member reads before installing. It carries its own version so that changing
    /// what the program does brings the notice back in front of the people who accepted an older one.
    /// </summary>
    public static class Notice
    {
        /// <summary>
        /// The version on the notice's first line ("Crucible notice, version N"), or 0 when it has none. The notice was
        /// renamed with the program (2026-10-04); its old first line still counts until 1.0.0 - the number is what
        /// decides whether it was accepted, never the name.
        /// </summary>
        public static int VersionOf(string? noticeText)
        {
            Match m = Regex.Match(noticeText ?? "", @"^(?:Crucible|Tallybook) notice, version ([0-9]{1,4})\s*$", RegexOptions.Multiline | RegexOptions.CultureInvariant);
            return m.Success && int.TryParse(m.Groups[1].Value, out int v) ? v : 0;
        }

        /// <summary>
        /// Whether "I understand" stands for the notice this build carries: only when they accepted this version or a
        /// later one. An older acceptance is not carried over to words they have not read (M124).
        /// </summary>
        public static bool IsAccepted(int acceptedVersion, int currentVersion) =>
            currentVersion > 0 && acceptedVersion >= currentVersion;
    }

    /// <summary>
    /// One file of the addon, as the server sends it: a plain name, the SHA-256 of its bytes, and its content - the
    /// text itself for a .lua or .toc, or base64 for the icon (.tga, brief I1), which then says so in "encoding".
    /// </summary>
    [DataContract]
    public sealed class AddonFile
    {
        /// <summary>The one encoding there is: a binary file's bytes, in base64.</summary>
        public const string Base64 = "base64";

        [DataMember(Name = "name")] public string Name { get; set; } = "";
        [DataMember(Name = "sha256")] public string Sha256 { get; set; } = "";
        [DataMember(Name = "content")] public string Content { get; set; } = "";
        /// <summary>
        /// Null for a text file (the server sends no "encoding" for one - the serializer skips initialisers, so absent
        /// means null); <see cref="Base64"/> for a binary one. Anything else refuses the whole manifest.
        /// </summary>
        [DataMember(Name = "encoding", IsRequired = false, EmitDefaultValue = false)] public string? ContentEncoding { get; set; }
    }

    /// <summary>
    /// The addon as the server sends it (GET /api/v1/addon). This will be written into somebody's game folder, so
    /// <see cref="Reject"/> is the gate: nothing here is trusted because the server said it, and a manifest that
    /// does not add up is refused whole rather than installed in part.
    /// </summary>
    [DataContract]
    public sealed class AddonManifest
    {
        /// <summary>
        /// A plain file name and nothing else: no folder, no separator, no dots but the one. The addon's Lua, its toc
        /// and its icon (brief I1) - the same pattern the server serves by.
        /// </summary>
        private static readonly Regex PlainName = new Regex(@"^[A-Za-z][A-Za-z0-9]*\.(lua|toc|tga)$", RegexOptions.CultureInvariant);
        private static readonly Regex PlainVersion = new Regex(@"^[0-9]{1,4}(\.[0-9]{1,4}){0,3}$", RegexOptions.CultureInvariant);

        [DataMember(Name = "version")] public string Version { get; set; } = "";
        [DataMember(Name = "files")] public List<AddonFile> Files { get; set; } = new List<AddonFile>();
        /// <summary>
        /// Forever's product folders, in order (the server's FOREVER_PRODUCTS). Null from a server that predates the
        /// list - the serializer skips initialisers, so absent means null. Never part of <see cref="Reject"/>: a bad
        /// entry is dropped by <see cref="ForeverProducts.Choose"/>, and the addon itself is still fine.
        /// </summary>
        [DataMember(Name = "products", IsRequired = false)] public List<string?>? Products { get; set; }

        /// <summary>A plain addon file name and nothing else. Checked before a name is ever used as a path.</summary>
        public static bool IsPlainName(string? name) => name != null && PlainName.IsMatch(name);

        /// <summary>A file that is not text (the icon): it comes as base64 and only as base64; a text file never does.</summary>
        public static bool IsBinaryName(string? name) => name != null && name.EndsWith(".tga", StringComparison.Ordinal);

        /// <summary>
        /// The bytes a file stands for, or null when they cannot be had: a text file is its text in UTF-8 and must carry
        /// no encoding; a binary file must be base64 that decodes. Never throws - a bad entry is a reason, not a crash.
        /// </summary>
        public static byte[]? BytesOf(AddonFile? f)
        {
            if (f?.Content == null) return null;
            if (!IsBinaryName(f.Name)) return f.ContentEncoding == null ? Encoding.UTF8.GetBytes(f.Content) : null;
            if (f.ContentEncoding != AddonFile.Base64) return null;
            try { return Convert.FromBase64String(f.Content); }
            catch (FormatException) { return null; }
        }

        /// <summary>The "## Version:" line of a .toc, or null.</summary>
        public static string? VersionOfToc(string toc)
        {
            Match m = Regex.Match(toc ?? "", @"^## Version:\s*(\S+)\s*$", RegexOptions.Multiline | RegexOptions.CultureInvariant);
            return m.Success && PlainVersion.IsMatch(m.Groups[1].Value) ? m.Groups[1].Value : null;
        }

        /// <summary>Null when it is safe to install; otherwise why it is not - plain words, safe to log.</summary>
        public static string? Reject(AddonManifest? m)
        {
            if (m == null) return "no manifest";
            if (!PlainVersion.IsMatch(m.Version ?? "")) return "the version is not a version";
            if (m.Files == null || m.Files.Count == 0) return "it holds no files";
            if (m.Files.Count > AddonInstaller.MaxFiles) return "it holds more than " + AddonInstaller.MaxFiles + " files";

            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            int tocs = 0;
            long total = 0;
            foreach (AddonFile f in m.Files)
            {
                string name = f?.Name ?? "";
                // Whatever the server said, only a plain addon file name is ever written.
                if (!PlainName.IsMatch(name)) return "\"" + Safe(name) + "\" is not a plain addon file name";
                if (!seen.Add(name)) return "\"" + name + "\" is in it twice";
                if (name.EndsWith(".toc", StringComparison.OrdinalIgnoreCase)) tocs++;
                if (f!.Content == null) return "\"" + name + "\" has no content";
                // Size and hash are of the bytes that would be written: a text file's UTF-8, a binary file's decoded base64.
                byte[]? bytes = BytesOf(f);
                if (bytes == null) return IsBinaryName(name) ? "\"" + name + "\" is not in base64" : "\"" + name + "\" is text but came encoded";
                if (bytes.Length > AddonInstaller.MaxFileBytes) return "\"" + name + "\" is larger than " + AddonInstaller.MaxFileBytes + " bytes";
                total += bytes.Length;
                if (!string.Equals(Hash(bytes), f.Sha256, StringComparison.OrdinalIgnoreCase)) return "\"" + name + "\" does not match its hash";
            }
            if (tocs != 1) return tocs == 0 ? "it has no .toc" : "it has more than one .toc";
            if (total > AddonInstaller.MaxTotalBytes) return "it is larger than " + AddonInstaller.MaxTotalBytes + " bytes in total";
            return null;
        }

        /// <summary>The SHA-256 of a text file: of its UTF-8 bytes, the bytes that are written.</summary>
        public static string Hash(string content) => Hash(Encoding.UTF8.GetBytes(content));

        public static string Hash(byte[] bytes)
        {
            using (SHA256 sha = SHA256.Create())
            {
                var hex = new StringBuilder(64);
                foreach (byte b in sha.ComputeHash(bytes)) hex.Append(b.ToString("x2"));
                return hex.ToString();
            }
        }

        /// <summary>A name is somebody else's text: it never reaches a log or a message with control characters in it.</summary>
        private static string Safe(string name)
        {
            var clean = new StringBuilder(Math.Min(name.Length, 40));
            foreach (char c in name.Substring(0, Math.Min(name.Length, 40))) clean.Append(c < 0x20 || c > 0x7e ? '?' : c);
            return clean.ToString();
        }
    }
}
