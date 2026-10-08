using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;

namespace OrclWAMP
{
    /// <summary>
    /// Password-protected container for the sensitive parts of a package (Wi-Fi passwords, SSH keys).
    /// Format: "OWS1" | salt(16) | iterations(4) | iv(16) | AES-256-CBC ciphertext | HMAC-SHA256(32) over everything before it.
    /// Keys come from PBKDF2-SHA256; the HMAC is checked before anything is decrypted.
    /// </summary>
    internal static class Secrets
    {
        public const string FileName = "secrets.orclwamp";
        const int Iterations = 300000;
        static readonly byte[] Magic = Encoding.ASCII.GetBytes("OWS1");

        public sealed class WrongPasswordException : Exception
        {
            public WrongPasswordException() : base(Lang.T("Wrong password (or the protected file is damaged).")) { }
        }

        public static void Write(string path, string password, IList<(string Name, byte[] Data)> items)
        {
            byte[] plain;
            using (var ms = new MemoryStream())
            using (var w = new BinaryWriter(ms, Encoding.UTF8))
            {
                w.Write(items.Count);
                foreach (var (name, data) in items) { w.Write(name); w.Write(data.Length); w.Write(data); }
                w.Flush();
                plain = ms.ToArray();
            }
            var salt = Random(16);
            var iv = Random(16);
            DeriveKeys(password, salt, Iterations, out var encKey, out var macKey);
            byte[] cipher;
            using (var aes = Aes.Create())
            {
                aes.KeySize = 256; aes.Mode = CipherMode.CBC; aes.Padding = PaddingMode.PKCS7;
                aes.Key = encKey; aes.IV = iv;
                using (var enc = aes.CreateEncryptor()) cipher = enc.TransformFinalBlock(plain, 0, plain.Length);
            }
            Array.Clear(plain, 0, plain.Length);
            using (var ms = new MemoryStream())
            {
                ms.Write(Magic, 0, 4);
                ms.Write(salt, 0, 16);
                ms.Write(BitConverter.GetBytes(Iterations), 0, 4);
                ms.Write(iv, 0, 16);
                ms.Write(cipher, 0, cipher.Length);
                byte[] mac;
                using (var h = new HMACSHA256(macKey)) mac = h.ComputeHash(ms.ToArray());
                ms.Write(mac, 0, mac.Length);
                File.WriteAllBytes(path, ms.ToArray());
            }
        }

        public static List<(string Name, byte[] Data)> Read(string path, string password)
        {
            // The container comes from a USB stick – refuse absurd sizes instead of running out of memory.
            if (new FileInfo(path).Length > 256L * 1024 * 1024) throw new WrongPasswordException();
            var all = File.ReadAllBytes(path);
            if (all.Length < 4 + 16 + 4 + 16 + 16 + 32 || !all.Take(4).SequenceEqual(Magic)) throw new WrongPasswordException();
            var salt = new byte[16]; Buffer.BlockCopy(all, 4, salt, 0, 16);
            int iterations = BitConverter.ToInt32(all, 20);
            if (iterations < 100000 || iterations > 2000000) throw new WrongPasswordException();
            var iv = new byte[16]; Buffer.BlockCopy(all, 24, iv, 0, 16);
            int cipherLen = all.Length - 40 - 32;
            DeriveKeys(password, salt, iterations, out var encKey, out var macKey);
            byte[] expected;
            using (var h = new HMACSHA256(macKey)) expected = h.ComputeHash(all, 0, all.Length - 32);
            if (!FixedTimeEquals(expected, all, all.Length - 32)) throw new WrongPasswordException();

            byte[] plain;
            using (var aes = Aes.Create())
            {
                aes.KeySize = 256; aes.Mode = CipherMode.CBC; aes.Padding = PaddingMode.PKCS7;
                aes.Key = encKey; aes.IV = iv;
                using (var dec = aes.CreateDecryptor()) plain = dec.TransformFinalBlock(all, 40, cipherLen);
            }
            var list = new List<(string, byte[])>();
            using (var r = new BinaryReader(new MemoryStream(plain), Encoding.UTF8))
            {
                int n = r.ReadInt32();
                if (n < 0 || n > 100000) throw new WrongPasswordException();
                for (int i = 0; i < n; i++)
                {
                    var name = r.ReadString();
                    int len = r.ReadInt32();
                    if (len < 0 || len > 64 * 1024 * 1024) throw new WrongPasswordException();
                    list.Add((name, r.ReadBytes(len)));
                }
            }
            return list;
        }

        /// <summary>Unpacks the container into <paramref name="root"/>; entry names are package-relative paths ("Settings/wifi/x.xml").</summary>
        public static void ExtractTo(string path, string password, string root)
        {
            var rootFull = Path.GetFullPath(root).TrimEnd('\\') + "\\";
            foreach (var (name, data) in Read(path, password))
            {
                var target = Path.GetFullPath(Path.Combine(root, name.Replace('/', '\\')));
                if (!target.StartsWith(rootFull, StringComparison.OrdinalIgnoreCase)) continue; // no "..\" escapes
                Directory.CreateDirectory(Path.GetDirectoryName(target));
                File.WriteAllBytes(target, data);
            }
        }

        /// <summary>Moves the given package files into an encrypted container and deletes the readable copies.</summary>
        public static int Protect(string packageDir, string password, IEnumerable<string> relativePaths)
        {
            var items = new List<(string, byte[])>();
            var files = relativePaths.Select(p => Path.Combine(packageDir, p)).Where(File.Exists).ToList();
            foreach (var f in files)
                items.Add((f.Substring(packageDir.TrimEnd('\\').Length + 1).Replace('\\', '/'), File.ReadAllBytes(f)));
            var target = Path.Combine(packageDir, FileName);
            if (items.Count == 0) { if (File.Exists(target)) File.Delete(target); return 0; }
            Write(target, password, items);
            foreach (var f in files) { try { File.Delete(f); } catch { } }
            return items.Count;
        }

        static void DeriveKeys(string password, byte[] salt, int iterations, out byte[] encKey, out byte[] macKey)
        {
            using (var kdf = new Rfc2898DeriveBytes(password ?? "", salt, iterations, HashAlgorithmName.SHA256))
            {
                var bytes = kdf.GetBytes(64);
                encKey = bytes.Take(32).ToArray();
                macKey = bytes.Skip(32).ToArray();
            }
        }

        static bool FixedTimeEquals(byte[] a, byte[] buf, int offset)
        {
            int diff = 0;
            for (int i = 0; i < a.Length; i++) diff |= a[i] ^ buf[offset + i];
            return diff == 0;
        }

        static byte[] Random(int n)
        {
            var b = new byte[n];
            using (var rng = RandomNumberGenerator.Create()) rng.GetBytes(b);
            return b;
        }
    }

    /// <summary>
    /// New PC: decrypts the package's protected items once (after asking for the password)
    /// into a private temp folder, and deletes it again afterwards.
    /// </summary>
    internal sealed class PackageSecrets : IDisposable
    {
        readonly string _packageDir;
        string _root;

        public PackageSecrets(string packageDir) { _packageDir = packageDir; }

        public bool Present => File.Exists(Path.Combine(_packageDir, Secrets.FileName));

        /// <summary>The folder with the decrypted files, or null if there is nothing protected / it isn't unlocked.</summary>
        public string Root => _root;

        public bool Unlock(string password)
        {
            if (_root != null) return true;
            var dir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "OrclWAMP", "tmp", Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(dir);
            try
            {
                Secrets.ExtractTo(Path.Combine(_packageDir, Secrets.FileName), password, dir);
                _root = dir;
                return true;
            }
            catch
            {
                try { Directory.Delete(dir, true); } catch { }
                throw;
            }
        }

        /// <summary>Asks for the password (until correct or cancelled). Returns false if the user cancelled.</summary>
        public bool UnlockInteractive(System.Windows.Forms.IWin32Window owner)
        {
            if (_root != null) return true;
            if (!Present) return false;
            while (true)
            {
                var pw = PasswordDialog.Ask(owner, Lang.T("This package has password-protected items (Wi-Fi passwords, SSH keys). Enter the package password:"), false);
                if (pw == null) return false;
                try { return Unlock(pw); }
                catch (Secrets.WrongPasswordException ex) { Ui.Warn(owner, ex.Message); }
                catch (Exception ex) { Ui.Warn(owner, ex.Message); return false; }
            }
        }

        public void Dispose()
        {
            if (_root == null) return;
            try { Directory.Delete(_root, true); } catch { }
            _root = null;
        }
    }
}
