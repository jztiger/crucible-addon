using System;
using System.Security.Cryptography;
using System.Text;

namespace Crucible.Tray
{
    /// <summary>Windows' per-user protection (DPAPI): what it seals, only this Windows user on this PC can open.</summary>
    internal sealed class Dpapi : ISecretProtector
    {
        private readonly byte[] entropy;

        public Dpapi() : this("Crucible.Tray.v1") { }

        private Dpapi(string entropy) { this.entropy = Encoding.UTF8.GetBytes(entropy); }

        /// <summary>
        /// What Tallybook, this program's old name, sealed its config with - opened once, when its settings move across
        /// to the new name (<see cref="ConfigStore.MoveFromOldName"/>). Never used to seal anything. Remove at 1.0.0.
        /// </summary>
        public static Dpapi OldName() => new Dpapi("Tallybook.Tray.v1");

        public string Protect(string plain) =>
            Convert.ToBase64String(ProtectedData.Protect(Encoding.UTF8.GetBytes(plain), entropy, DataProtectionScope.CurrentUser));

        public string Unprotect(string sealedText) =>
            Encoding.UTF8.GetString(ProtectedData.Unprotect(Convert.FromBase64String(sealedText), entropy, DataProtectionScope.CurrentUser));
    }
}
