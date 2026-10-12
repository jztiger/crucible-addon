using System;
using System.Collections.Generic;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using Xunit;

namespace Crucible.Tray.Tests
{
    /// <summary>Spec 2026-10-12: the tray replaces itself only with a verified, newer, signed build - and decides so in pure code.</summary>
    public class SelfUpdateTests
    {
        private static readonly DateTime Now = new DateTime(2026, 10, 12, 20, 0, 0, DateTimeKind.Utc);

        [Fact]
        public void Only_a_newer_version_on_a_signed_build_is_fetched()
        {
            Assert.Equal(UpdateStep.Fetch, SelfUpdate.Decide("0.99.4", "0.99.5", "", null, Now, false, false, true));
            Assert.Equal(UpdateStep.None, SelfUpdate.Decide("0.99.4", "0.99.4", "", null, Now, false, false, true));
            Assert.Equal(UpdateStep.None, SelfUpdate.Decide("0.99.4", "0.99.3", "", null, Now, false, false, true)); // never a downgrade
            Assert.Equal(UpdateStep.None, SelfUpdate.Decide("0.99.4", null, "", null, Now, false, false, true));
            Assert.Equal(UpdateStep.None, SelfUpdate.Decide("0.99.4", "junk", "", null, Now, false, false, true));
            Assert.Equal(UpdateStep.Unsigned, SelfUpdate.Decide("0.99.4", "0.99.5", "", null, Now, false, false, false));
            Assert.Equal(UpdateStep.Paused, SelfUpdate.Decide("0.99.4", "0.99.5", "", null, Now, false, true, true));
        }

        [Fact]
        public void Exactly_the_version_that_failed_is_not_fetched_again_and_any_other_newer_one_is()
        {
            Assert.Equal(UpdateStep.AlreadyFailed, SelfUpdate.Decide("0.99.4", "0.99.5", "0.99.5", null, Now, false, false, true));
            Assert.Equal(UpdateStep.Fetch, SelfUpdate.Decide("0.99.4", "0.99.6", "0.99.5", null, Now, false, false, true));
            // Review I1: one bogus answer ("9.99.5") must not block every real version after it.
            Assert.Equal(UpdateStep.Fetch, SelfUpdate.Decide("0.99.4", "0.99.5", "9.99.5", null, Now, false, false, true));
            // A rollback below a failed version: the older one is fetched (it is still newer than mine).
            Assert.Equal(UpdateStep.Fetch, SelfUpdate.Decide("0.99.4", "0.99.5", "0.99.6", null, Now, false, false, true));
        }

        [Fact]
        public void The_nudge_is_the_fallback_for_an_unsigned_build_a_failed_version_or_a_stalled_update()
        {
            Assert.Null(SelfUpdate.NudgeFor("0.99.5", "", true, false));            // a signed build fetches it itself
            Assert.Equal("0.99.5", SelfUpdate.NudgeFor("0.99.5", "", false, false)); // unsigned: the person downloads
            Assert.Equal("0.99.5", SelfUpdate.NudgeFor("0.99.5", "0.99.5", true, false)); // this one failed
            Assert.Null(SelfUpdate.NudgeFor("0.99.5", "0.99.6", true, false));      // another one failed: this one is fetched
            Assert.Equal("0.99.5", SelfUpdate.NudgeFor("0.99.5", "", true, true));  // three checks could not get it
            Assert.Null(SelfUpdate.NudgeFor(null, "0.99.5", true, true));
        }

        private sealed class FakeOps : IFileOps
        {
            public HashSet<string> Files = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            public string? FailMove;  // "exe->old", "staged->exe", "old->exe"
            public bool FailStart;
            public int Started;
            public bool Exists(string path) => Files.Contains(path);
            public void Delete(string path) { Files.Remove(path); }
            public void Move(string from, string to)
            {
                string step = (from.EndsWith(".new") ? "staged" : from.EndsWith(".old") ? "old" : "exe") + "->" + (to.EndsWith(".new") ? "staged" : to.EndsWith(".old") ? "old" : "exe");
                if (step == FailMove) throw new IOException(step);
                if (!Files.Contains(from)) throw new FileNotFoundException(from);
                Files.Remove(from); Files.Add(to);
            }
            public void Start(string exe) { Started++; if (FailStart) throw new IOException("start"); }
        }

        private static (UpdatePaths p, FakeOps ops) Ready()
        {
            var p = SelfUpdate.Plan(@"C:\t\Crucible.exe");
            var ops = new FakeOps();
            ops.Files.Add(p.Exe); ops.Files.Add(p.Staged);
            return (p, ops);
        }

        [Fact]
        public void The_swap_renames_the_running_exe_to_old_moves_the_staged_one_in_and_starts_it()
        {
            (UpdatePaths p, FakeOps ops) = Ready();
            ops.Files.Add(p.Old); // a leftover from last time goes first
            Assert.Equal(SwapResult.Swapped, SelfUpdate.Swap(p, ops));
            Assert.Equal(1, ops.Started);
            Assert.True(ops.Exists(p.Exe)); Assert.True(ops.Exists(p.Old)); Assert.False(ops.Exists(p.Staged));
        }

        [Theory]
        [InlineData("exe->old")]
        [InlineData("staged->exe")]
        public void A_rename_that_fails_leaves_the_running_exe_under_its_own_name_and_the_staged_file_gone(string failAt)
        {
            (UpdatePaths p, FakeOps ops) = Ready();
            ops.FailMove = failAt;
            Assert.Equal(SwapResult.Restored, SelfUpdate.Swap(p, ops));
            Assert.Equal(0, ops.Started);
            Assert.True(ops.Exists(p.Exe));
            Assert.False(ops.Exists(p.Staged));
            Assert.False(ops.Exists(p.Old));
        }

        [Fact]
        public void A_start_that_fails_after_both_renames_puts_the_running_exe_back_and_keeps_the_download_out()
        {
            // Review I2(a): antivirus holds the new file; without this the next logon would start a program that cannot.
            (UpdatePaths p, FakeOps ops) = Ready();
            ops.FailStart = true;
            Assert.Equal(SwapResult.Restored, SelfUpdate.Swap(p, ops));
            Assert.True(ops.Exists(p.Exe));   // the running build, back under its own name
            Assert.False(ops.Exists(p.Old));
            Assert.False(ops.Exists(p.Staged));
        }

        [Fact]
        public void When_even_the_restore_fails_the_staged_file_is_never_deleted_while_no_exe_exists()
        {
            // Review I2(b): zero exes on disk is the one outcome worse than a broken update.
            (UpdatePaths p, FakeOps ops) = Ready();
            ops.FailMove = "old->exe"; ops.FailStart = true;
            Assert.Equal(SwapResult.Broken, SelfUpdate.Swap(p, ops));
            Assert.True(ops.Exists(p.Exe) || ops.Exists(p.Staged) || ops.Exists(p.Old)); // something runnable remains
        }

        [Fact]
        public void The_check_runs_every_six_hours_or_at_once_when_forced()
        {
            DateTime recent = Now - TimeSpan.FromHours(1);
            Assert.Equal(UpdateStep.NotDue, SelfUpdate.Decide("0.99.4", "0.99.5", "", recent, Now, false, false, true));
            Assert.Equal(UpdateStep.Fetch, SelfUpdate.Decide("0.99.4", "0.99.5", "", recent, Now, true, false, true));
            Assert.Equal(UpdateStep.Fetch, SelfUpdate.Decide("0.99.4", "0.99.5", "", Now - Cycle.AddonEvery, Now, false, false, true));
        }

        private static byte[] Program(int size)
        {
            var b = new byte[size];
            b[0] = (byte)'M'; b[1] = (byte)'Z';
            return b;
        }

        [Fact]
        public void Verify_refuses_in_order_size_then_hash_then_header()
        {
            byte[] exe = Program(4096);
            string sha = SelfUpdate.Sha256Hex(exe);
            Assert.Equal(Refusal.Ok, SelfUpdate.Verify(exe, sha, SelfUpdate.MaxBytes));
            Assert.Equal(Refusal.Ok, SelfUpdate.Verify(exe, sha.ToUpperInvariant(), SelfUpdate.MaxBytes)); // case does not matter
            Assert.Equal(Refusal.TooLarge, SelfUpdate.Verify(null, sha, SelfUpdate.MaxBytes)); // ReadCapped answers null when over
            Assert.Equal(Refusal.TooLarge, SelfUpdate.Verify(exe, sha, 100));
            Assert.Equal(Refusal.ShaMismatch, SelfUpdate.Verify(exe, new string('0', 64), SelfUpdate.MaxBytes));
            Assert.Equal(Refusal.ShaMismatch, SelfUpdate.Verify(exe, "", SelfUpdate.MaxBytes));
            byte[] text = Encoding.ASCII.GetBytes("<html>not a program</html>" + new string(' ', 100));
            Assert.Equal(Refusal.NotAProgram, SelfUpdate.Verify(text, SelfUpdate.Sha256Hex(text), SelfUpdate.MaxBytes));
            Assert.Equal(Refusal.NotAProgram, SelfUpdate.Verify(new byte[1], SelfUpdate.Sha256Hex(new byte[1]), SelfUpdate.MaxBytes));
        }

        [Fact]
        public void Sha256Hex_is_lowercase_hex_of_the_bytes()
        {
            byte[] b = Encoding.ASCII.GetBytes("abc");
            using (var sha = SHA256.Create())
                Assert.Equal(BitConverter.ToString(sha.ComputeHash(b)).Replace("-", "").ToLowerInvariant(), SelfUpdate.Sha256Hex(b));
        }

        private static SignerInfo Ours(string version = "0.99.5", string subject = "CN=Jason Howard, O=Jason Howard, L=Austin, S=Texas, C=US") =>
            new SignerInfo(AppInfo.Publisher, subject, version);

        [Fact]
        public void The_signer_must_be_ours_the_same_certificate_subject_as_the_running_build_and_the_version_announced()
        {
            Assert.Equal(Refusal.Ok, SelfUpdate.CheckSigner(Ours(), Ours("0.99.4"), "0.99.5"));
            Assert.Equal(Refusal.Ok, SelfUpdate.CheckSigner(Ours("0.99.5.0"), Ours("0.99.4"), "0.99.5")); // the PE carries four parts
            Assert.Equal(Refusal.BadSignature, SelfUpdate.CheckSigner(null, Ours("0.99.4"), "0.99.5"));
            Assert.Equal(Refusal.WrongPublisher, SelfUpdate.CheckSigner(new SignerInfo("Somebody Else", "CN=Somebody Else", "0.99.5"), Ours("0.99.4"), "0.99.5"));
            // The same simple name on a certificate from somewhere else: the subject differs, so it is not ours.
            Assert.Equal(Refusal.DifferentSigner, SelfUpdate.CheckSigner(Ours("0.99.5", "CN=Jason Howard, O=Elsewhere Ltd, C=GB"), Ours("0.99.4"), "0.99.5"));
            Assert.Equal(Refusal.DifferentSigner, SelfUpdate.CheckSigner(Ours(), null, "0.99.5"));
            // An older signed build relabelled as newer: its own file version gives it away.
            Assert.Equal(Refusal.VersionMismatch, SelfUpdate.CheckSigner(Ours("0.99.2"), Ours("0.99.4"), "0.99.5"));
            Assert.Equal(Refusal.VersionMismatch, SelfUpdate.CheckSigner(Ours(""), Ours("0.99.4"), "0.99.5"));
            Assert.Equal("Jason Howard", AppInfo.Publisher);
        }

        [Fact]
        public void The_plan_names_the_staged_and_old_files_beside_the_exe()
        {
            UpdatePaths p = SelfUpdate.Plan(@"C:\Users\Someone\Documents\Crucible.exe");
            Assert.Equal(@"C:\Users\Someone\Documents\Crucible.exe", p.Exe);
            Assert.Equal(@"C:\Users\Someone\Documents\Crucible.exe.new", p.Staged);
            Assert.Equal(@"C:\Users\Someone\Documents\Crucible.exe.old", p.Old);
        }

        [Fact]
        public void A_quiet_cycle_is_one_that_moved_nothing_and_is_not_paused()
        {
            Assert.True(SelfUpdate.QuietEnough(new CycleReport(), false));
            Assert.False(SelfUpdate.QuietEnough(new CycleReport(), true));
            Assert.False(SelfUpdate.QuietEnough(new CycleReport { Uploaded = 1 }, false));
            Assert.False(SelfUpdate.QuietEnough(new CycleReport { Wrote = 1 }, false));
            Assert.False(SelfUpdate.QuietEnough(new CycleReport { AddonInstalled = "0.99.4" }, false));
            // Review I3: a tray that is waiting on the server (Retrying) moved nothing - the swap may go ahead.
            Assert.True(SelfUpdate.QuietEnough(new CycleReport { State = TrayState.Retrying }, false));
        }
    }
}
