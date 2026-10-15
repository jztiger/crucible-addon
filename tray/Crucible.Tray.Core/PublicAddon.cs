using System;
using System.Collections.Generic;
using System.IO;
using System.Net.Http;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading.Tasks;

namespace Crucible.Tray
{
    /// <summary>
    /// The addon as published for anyone to read (rule C1), fetched straight from GitHub. This is the way back when
    /// our own server has served something wrong: the person asks for it from the Settings menu, and what they get
    /// is the code in the public repository, not whatever the server is offering.
    ///
    /// It sends none of their credentials - GitHub is not our server - and it takes the file list from the addon's
    /// own .toc, so there is no archive to unpack. A name that is not a plain addon file name is never requested.
    /// </summary>
    public static class PublicAddon
    {
        public const string Base = "https://raw.githubusercontent.com/jztiger/crucible-addon/main/Crucible/";
        private const string Toc = GameFolders.TocName;
        private const int MaxBytes = AddonInstaller.MaxFileBytes;
        /// <summary>The icon's line (brief I1). The icon is never in the file list - the client loads that as code.</summary>
        private static readonly Regex IconLine = new Regex(@"^## IconTexture:[ \t]*(.*?)[ \t]*\r?$", RegexOptions.Multiline | RegexOptions.CultureInvariant);
        private const string OurFolder = @"Interface\AddOns\" + GameFolders.AddonName + @"\";

        /// <summary>
        /// The file the toc's IconTexture names in our own folder: "" when it names none of ours (no line, or one of the
        /// game's icons - nothing to fetch), null when it points into our folder at anything but a plain .tga name -
        /// which refuses the whole fetch, exactly as a bad name in the file list does.
        /// </summary>
        internal static string? IconOf(string toc)
        {
            Match m = IconLine.Match(toc ?? "");
            if (!m.Success) return "";
            string path = m.Groups[1].Value.Replace('/', '\\');
            if (!path.StartsWith(OurFolder, StringComparison.OrdinalIgnoreCase)) return "";
            string leaf = path.Substring(OurFolder.Length);
            return AddonManifest.IsPlainName(leaf) && AddonManifest.IsBinaryName(leaf) ? leaf : null;
        }

        /// <summary>The published addon, or null when it cannot be had whole. Never a partial addon.</summary>
        public static async Task<AddonManifest?> FetchAsync(HttpMessageHandler handler)
        {
            using (var http = new HttpClient(handler, false) { Timeout = TimeSpan.FromSeconds(60) })
            {
                http.DefaultRequestHeaders.TryAddWithoutValidation("User-Agent", AppInfo.UserAgent);

                byte[]? tocBytes = await GetAsync(http, Toc).ConfigureAwait(false);
                if (tocBytes == null) return null;
                string toc = Encoding.UTF8.GetString(tocBytes);
                string? version = AddonManifest.VersionOfToc(toc);
                if (version == null) return null;

                var names = new List<string> { Toc };
                foreach (string line in toc.Split('\n'))
                {
                    string listed = line.Trim();
                    if (listed.Length == 0 || listed.StartsWith("##", StringComparison.Ordinal)) continue;
                    // Somebody else's text becoming a path is exactly what must not happen: check, then fetch.
                    if (!AddonManifest.IsPlainName(listed)) return null;
                    if (!names.Contains(listed)) names.Add(listed);
                }
                string? icon = IconOf(toc);
                if (icon == null) return null;
                if (icon.Length > 0 && !names.Contains(icon)) names.Add(icon);
                if (names.Count > AddonInstaller.MaxFiles) return null;

                var files = new List<AddonFile> { new AddonFile { Name = Toc, Content = toc, Sha256 = AddonManifest.Hash(toc) } };
                foreach (string name in names)
                {
                    if (name == Toc) continue;
                    byte[]? body = await GetAsync(http, name).ConfigureAwait(false);
                    if (body == null) return null; // whole, or nothing
                    if (AddonManifest.IsBinaryName(name))
                    {
                        // The icon stays bytes all the way: base64 in the manifest, hashed over the bytes themselves.
                        files.Add(new AddonFile { Name = name, ContentEncoding = AddonFile.Base64, Content = Convert.ToBase64String(body), Sha256 = AddonManifest.Hash(body) });
                        continue;
                    }
                    string content = Encoding.UTF8.GetString(body);
                    files.Add(new AddonFile { Name = name, Content = content, Sha256 = AddonManifest.Hash(content) });
                }

                var manifest = new AddonManifest { Version = version, Files = files };
                return AddonManifest.Reject(manifest) == null ? manifest : null;
            }
        }

        /// <summary>The file's bytes as GitHub serves them, or null. The caller decides whether they are text.</summary>
        private static async Task<byte[]?> GetAsync(HttpClient http, string name)
        {
            try
            {
                using (HttpResponseMessage response = await http.GetAsync(Base + name, HttpCompletionOption.ResponseHeadersRead).ConfigureAwait(false))
                {
                    if ((int)response.StatusCode != 200) return null;
                    if (response.Content.Headers.ContentLength > MaxBytes) return null;
                    byte[] body = await response.Content.ReadAsByteArrayAsync().ConfigureAwait(false);
                    return body.Length > MaxBytes ? null : body;
                }
            }
            catch (Exception e) when (e is HttpRequestException || e is TaskCanceledException || e is IOException)
            {
                return null;
            }
        }
    }
}
