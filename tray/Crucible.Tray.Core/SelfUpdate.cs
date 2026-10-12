using System;
using System.Security.Cryptography;

namespace Crucible.Tray
{
    /// <summary>What the six-hourly check decides (spec 2026-10-12, section 4).</summary>
    public enum UpdateStep { None, NotDue, Unsigned, Paused, AlreadyFailed, Fetch }

    /// <summary>Why a download was not taken - one enum for the core's steps and the shell's, so the log names one thing.</summary>
    public enum Refusal { Ok, TooLarge, ShaMismatch, NotAProgram, BadSignature, WrongPublisher, DifferentSigner, VersionMismatch, StagingFailed, SwapFailed }

    /// <summary>What the shell reads off a signed file: who signed it, and what the file says it is.</summary>
    public sealed class SignerInfo
    {
        /// <summary>The certificate's simple name - what Windows shows as the publisher.</summary>
        public string SimpleName { get; }
        /// <summary>The certificate's whole subject (CN, O, L, S, C): the same name from another certificate is not ours.</summary>
        public string Subject { get; }
        /// <summary>The file's own version from its PE resources ("0.99.5.0"): an old build relabelled as new gives itself away.</summary>
        public string FileVersion { get; }
        public SignerInfo(string simpleName, string subject, string fileVersion) { SimpleName = simpleName; Subject = subject; FileVersion = fileVersion; }
    }

    /// <summary>The shell's Authenticode check: the signer when Windows accepts the file's signature, else null.</summary>
    public interface ISigner { SignerInfo? SignerOf(string path); }

    /// <summary>The four file operations the swap needs, handed in by the shell so the swap's every failure is testable.</summary>
    public interface IFileOps
    {
        bool Exists(string path);
        void Delete(string path);
        void Move(string from, string to);
        /// <summary>Starts the new program; throws when Windows will not run it.</summary>
        void Start(string exe);
    }

    /// <summary>Swapped: this process is on its way out. Restored: the running build is back under its own name. Broken: it is not, and the log must say so.</summary>
    public enum SwapResult { Swapped, Restored, Broken }

    public sealed class UpdatePaths
    {
        public string Exe { get; }
        public string Staged { get; }
        public string Old { get; }
        public UpdatePaths(string exe) { Exe = exe; Staged = exe + ".new"; Old = exe + ".old"; }
    }

    /// <summary>
    /// The tray replacing itself, decided in pure code: whether to fetch, whether a download is the program we
    /// published, where the files go, and when the swap may happen. The shell carries out the Windows-only parts
    /// (Authenticode, the renames, the restart). Nothing here touches the disk or the network.
    /// </summary>
    public static class SelfUpdate
    {
        /// <summary>The exe is about 1 MB; anything near this is not our file.</summary>
        public const int MaxBytes = 24 * 1024 * 1024;

        public static UpdateStep Decide(string mine, string? offered, string failedVersion, DateTime? lastCheckUtc, DateTime nowUtc, bool force, bool paused, bool signedBuild)
        {
            if (!signedBuild) return UpdateStep.Unsigned;
            if (paused) return UpdateStep.Paused;
            if (!force && lastCheckUtc != null && nowUtc - lastCheckUtc.Value < Cycle.AddonEvery) return UpdateStep.NotDue;
            if (!ServerClient.IsNewer(mine, offered)) return UpdateStep.None;
            // Exactly the version that failed, and no other (review I1): one bogus answer must not block every real
            // version after it, and a rollback below a failed version is still an update.
            if (UpdateNudge.IsVersion(failedVersion) && string.Equals(failedVersion, offered, StringComparison.Ordinal)) return UpdateStep.AlreadyFailed;
            return UpdateStep.Fetch;
        }

        /// <summary>
        /// Which version, if any, the person should be sent to the download page for (M112's nudge, now the fallback):
        /// an unsigned build cannot update itself, a version that failed will not be tried again, and an update that
        /// three checks in a row could not get is stalled. Otherwise a signed build fetches it itself and nothing is said.
        /// </summary>
        public static string? NudgeFor(string? offered, string failedVersion, bool signedBuild, bool stalled)
        {
            if (offered == null) return null;
            if (!signedBuild || stalled || string.Equals(failedVersion, offered, StringComparison.Ordinal)) return offered;
            return null;
        }

        /// <summary>The three checks the core can make, in order; each refuses on its own.</summary>
        public static Refusal Verify(byte[]? body, string announcedSha256, int maxBytes)
        {
            if (body == null || body.Length > maxBytes) return Refusal.TooLarge;
            if (announcedSha256.Length != 64 || !string.Equals(Sha256Hex(body), announcedSha256, StringComparison.OrdinalIgnoreCase)) return Refusal.ShaMismatch;
            if (body.Length < 64 || body[0] != (byte)'M' || body[1] != (byte)'Z') return Refusal.NotAProgram;
            return Refusal.Ok;
        }

        /// <summary>
        /// The shell's checks, judged here: the download's signer is ours by name, is the SAME certificate subject as
        /// the running build's (a certificate with our name from anywhere else is not ours), and the file says it is the
        /// version announced (an older signed build relabelled as newer is not an update).
        /// </summary>
        public static Refusal CheckSigner(SignerInfo? signer, SignerInfo? self, string announcedVersion)
        {
            if (signer == null) return Refusal.BadSignature;
            if (!string.Equals(signer.SimpleName, AppInfo.Publisher, StringComparison.Ordinal)) return Refusal.WrongPublisher;
            if (self == null || !string.Equals(signer.Subject, self.Subject, StringComparison.Ordinal)) return Refusal.DifferentSigner;
            if (!SameVersion(signer.FileVersion, announcedVersion)) return Refusal.VersionMismatch;
            return Refusal.Ok;
        }

        /// <summary>"0.99.5.0" (a PE file version) is "0.99.5"; anything that does not parse is not.</summary>
        private static bool SameVersion(string fileVersion, string announced)
        {
            if (!Version.TryParse(fileVersion, out Version? f) || !Version.TryParse(announced, out Version? a)) return false;
            return f.Major == a.Major && f.Minor == a.Minor && Math.Max(f.Build, 0) == Math.Max(a.Build, 0);
        }

        public static UpdatePaths Plan(string exePath) => new UpdatePaths(exePath);

        /// <summary>
        /// The swap happens only right after a pass that moved nothing: nothing of ours is then half done. Waiting on
        /// the server (Retrying) moved nothing either (review I3), so it does not hold the swap back.
        /// </summary>
        public static bool QuietEnough(CycleReport report, bool paused) =>
            !paused && report.Uploaded == 0 && report.Rejected == 0 && report.Wrote == 0
            && report.AddonInstalled == null && !report.ProductsChanged;

        /// <summary>
        /// The swap itself, over the file operations the shell hands in (review I2): rename the running exe to .old,
        /// the staged file to the exe's name, start it. Whatever fails, the running build ends up back under its own
        /// name whenever that is possible, and the staged file is only ever deleted while an exe exists - never a
        /// disk with no Crucible.exe on it.
        /// </summary>
        public static SwapResult Swap(UpdatePaths p, IFileOps ops)
        {
            bool movedOut = false, movedIn = false;
            try
            {
                if (ops.Exists(p.Old)) ops.Delete(p.Old);
                ops.Move(p.Exe, p.Old);
                movedOut = true;
                ops.Move(p.Staged, p.Exe);
                movedIn = true;
                ops.Start(p.Exe);
                return SwapResult.Swapped;
            }
            catch (Exception e) when (e is System.IO.IOException || e is UnauthorizedAccessException || e is System.ComponentModel.Win32Exception)
            {
                return Restore(p, ops, movedOut, movedIn);
            }
        }

        private static SwapResult Restore(UpdatePaths p, IFileOps ops, bool movedOut, bool movedIn)
        {
            try
            {
                if (movedIn) ops.Move(p.Exe, p.Staged);      // the download goes back out of the way
                if (movedOut) ops.Move(p.Old, p.Exe);        // the running build back under its own name
                if (ops.Exists(p.Exe) && ops.Exists(p.Staged)) ops.Delete(p.Staged);
                return ops.Exists(p.Exe) ? SwapResult.Restored : SwapResult.Broken;
            }
            catch (Exception e) when (e is System.IO.IOException || e is UnauthorizedAccessException)
            {
                return SwapResult.Broken;
            }
        }

        public static string Sha256Hex(byte[] bytes)
        {
            using (var sha = SHA256.Create())
            {
                byte[] hash = sha.ComputeHash(bytes);
                var s = new char[hash.Length * 2];
                for (int i = 0; i < hash.Length; i++)
                {
                    s[i * 2] = "0123456789abcdef"[hash[i] >> 4];
                    s[i * 2 + 1] = "0123456789abcdef"[hash[i] & 15];
                }
                return new string(s);
            }
        }
    }
}
