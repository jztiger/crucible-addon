using System;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Security.Cryptography.X509Certificates;

namespace Crucible.Tray
{
    /// <summary>
    /// Windows' own answer to "is this file signed by someone Windows trusts, and by whom": WinVerifyTrust for the
    /// chain and the timestamp, then the signer's name and subject from the certificate, and the file's own version
    /// from its resources. Null for anything else - an unsigned file, a broken signature, a file that is not there.
    /// The core (SelfUpdate.CheckSigner) decides whether what it reads is ours.
    /// </summary>
    internal sealed class Authenticode : ISigner
    {
        public SignerInfo? SignerOf(string path)
        {
            try
            {
                if (!Verify(path)) return null;
                using (var cert = new X509Certificate2(X509Certificate.CreateFromSignedFile(path)))
                {
                    string version = FileVersionInfo.GetVersionInfo(path).FileVersion ?? "";
                    return new SignerInfo(cert.GetNameInfo(X509NameType.SimpleName, false), cert.Subject, version);
                }
            }
            catch (Exception e) when (e is System.Security.Cryptography.CryptographicException || e is System.IO.IOException
                || e is UnauthorizedAccessException || e is System.IO.FileNotFoundException)
            {
                return null;
            }
        }

        private static readonly Guid GenericVerifyV2 = new Guid("00AAC56B-CD44-11d0-8CC2-00C04FC295EE");
        private const uint WtdUiNone = 2, WtdRevokeNone = 0, WtdRevokeWholeChain = 1, WtdChoiceFile = 1, WtdStateActionVerify = 1, WtdStateActionClose = 2;
        private const uint WtdRevocationCheckChain = 0x40;
        /// <summary>No network for revocation: an offline PC still verifies the chain it has.</summary>
        private const uint WtdCacheOnlyUrlRetrieval = 0x1000;
        /// <summary>The revocation servers could not be reached - the one failure that is retried without them.</summary>
        private const int CryptERevocationOffline = unchecked((int)0x80092013), CertERevocationFailure = unchecked((int)0x800B010E);

        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        private struct WintrustFileInfo
        {
            public uint cbStruct;
            [MarshalAs(UnmanagedType.LPWStr)] public string pcwszFilePath;
            public IntPtr hFile;
            public IntPtr pgKnownSubject;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct WintrustData
        {
            public uint cbStruct;
            public IntPtr pPolicyCallbackData;
            public IntPtr pSIPClientData;
            public uint dwUIChoice;
            public uint fdwRevocationChecks;
            public uint dwUnionChoice;
            public IntPtr pFile;
            public uint dwStateAction;
            public IntPtr hWVTStateData;
            public IntPtr pwszURLReference;
            public uint dwProvFlags;
            public uint dwUIContext;
            public IntPtr pSignatureSettings;
        }

        [DllImport("wintrust.dll", ExactSpelling = true, SetLastError = true)]
        private static extern int WinVerifyTrust(IntPtr hwnd, [MarshalAs(UnmanagedType.LPStruct)] Guid action, ref WintrustData data);

        /// <summary>
        /// The whole chain, revocation included, first. When the revocation servers cannot be reached, the chain is
        /// checked again against the revocation lists Windows has cached; only when there are none is it checked with
        /// no revocation at all - so an offline PC still verifies the signature it can, and a certificate Windows KNOWS
        /// is revoked stays refused. What this does not catch: a revocation Windows has never heard of while the
        /// servers are blocked (review minor 1, deferred).
        /// </summary>
        private static bool Verify(string path)
        {
            int result = Verify(path, WtdRevokeWholeChain, WtdRevocationCheckChain);
            if (result == CryptERevocationOffline || result == CertERevocationFailure)
                result = Verify(path, WtdRevokeWholeChain, WtdRevocationCheckChain | WtdCacheOnlyUrlRetrieval);
            if (result == CryptERevocationOffline || result == CertERevocationFailure)
                result = Verify(path, WtdRevokeNone, WtdCacheOnlyUrlRetrieval);
            return result == 0;
        }

        private static int Verify(string path, uint revocationChecks, uint provFlags)
        {
            var file = new WintrustFileInfo { cbStruct = (uint)Marshal.SizeOf(typeof(WintrustFileInfo)), pcwszFilePath = path };
            IntPtr pFile = Marshal.AllocHGlobal(Marshal.SizeOf(typeof(WintrustFileInfo)));
            try
            {
                Marshal.StructureToPtr(file, pFile, false);
                var data = new WintrustData
                {
                    cbStruct = (uint)Marshal.SizeOf(typeof(WintrustData)),
                    dwUIChoice = WtdUiNone,
                    fdwRevocationChecks = revocationChecks,
                    dwUnionChoice = WtdChoiceFile,
                    pFile = pFile,
                    dwStateAction = WtdStateActionVerify,
                    dwProvFlags = provFlags,
                };
                int result = WinVerifyTrust(IntPtr.Zero, GenericVerifyV2, ref data);
                data.dwStateAction = WtdStateActionClose;
                WinVerifyTrust(IntPtr.Zero, GenericVerifyV2, ref data);
                return result;
            }
            finally
            {
                Marshal.DestroyStructure(pFile, typeof(WintrustFileInfo));
                Marshal.FreeHGlobal(pFile);
            }
        }
    }
}
