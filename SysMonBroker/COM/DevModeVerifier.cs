// SysMonBroker/COM/DevModeVerifier.cs
// SSH 签名验证 .devmode 文件 (支持 RSA + Ed25519) + 编译期路径门控
//
// 安全模型:
//   release 构建 (不带 -p:Dev): AssemblyMetadata 无 DevRepoPath → IsDevModeActive() 永远 false
//   dev 构建 (-p:Dev=true): 内嵌编译时项目目录路径 → .devmode_marker 必须在该路径下
//   攻击者拿到 dev build: 不知道内嵌路径 → 无法创建正确 marker → DevMode 不可激活
//
// 开发者启用 (一次性):
//   cd sysmon-cmdpal/SysMonBroker
//   dotnet build -p:Dev=true
//   touch .devmode_marker                          # gitignore, 不提交
//   echo -n "SysMonBroker.DevMode.v2.2" > /tmp/challenge
//   ssh-keygen -Y sign -n devmode -f ~/.ssh/id_ed25519 /tmp/challenge
//   cp /tmp/challenge.sig %LOCALAPPDATA%\SysMonCmdPal\.devmode

using System.Reflection;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;

namespace SysMonBroker.COM;

public static class DevModeVerifier
{
    private const string Challenge = "SysMonBroker.DevMode.v2.2";
    private const string DevModeFileName = ".devmode";
    private const string MarkerFileName = ".devmode_marker";
    private const string SshsigMagic = "SSHSIG";

    // 编译期注入: -p:Dev=true 时 csproj 写入 AssemblyMetadata("DevRepoPath", 项目目录)
    // release 构建无此 attribute → null → DevMode 永远禁用
    private static readonly string? DevRepoPath = Assembly
        .GetExecutingAssembly()
        .GetCustomAttribute<AssemblyMetadataAttribute>()
        ?.Value;

    private static bool _cachedResult;
    private static DateTime _cacheExpiry;
    private static readonly object _cacheLock = new();
    private static readonly TimeSpan CacheTtl = TimeSpan.FromSeconds(60);

    // 运行时开关: --devmode-on 创建 .devmode_on 文件, --devmode-off 删除
    // 用文件而非内存变量, 因 standalone 和 COM server 是不同进程, 需共享状态
    private static readonly string RuntimeFlagPath = DevRepoPath != null
        ? Path.Combine(DevRepoPath, ".devmode_on") : "";

    /// <summary>运行时开关 DevMode (由 Program.cs 的 --devmode-on/off 命令调用)。
    /// 仅 dev build (DevRepoPath 存在) + marker 文件存在时允许设置。
    /// 写文件 flag 而非内存变量, 使 standalone + COM server 进程共享状态。</summary>
    public static bool SetRuntimeOverride(bool enabled)
    {
        if (string.IsNullOrEmpty(DevRepoPath)) return false;
        if (!File.Exists(Path.Combine(DevRepoPath!, MarkerFileName))) return false;

        try
        {
            if (enabled) File.WriteAllText(RuntimeFlagPath, DateTime.UtcNow.ToString("o"));
            else if (File.Exists(RuntimeFlagPath)) File.Delete(RuntimeFlagPath);
            lock (_cacheLock) { _cacheExpiry = DateTime.MinValue; }
            return true;
        }
        catch { return false; }
    }

    public static bool IsDevModeActive()
    {
        if (string.IsNullOrEmpty(DevRepoPath)) return false;

        string markerPath = Path.Combine(DevRepoPath!, MarkerFileName);
        if (!File.Exists(markerPath)) return false;

        // 运行时开关: 文件 flag (--devmode-on 创建, --devmode-off 删除)
        if (File.Exists(RuntimeFlagPath)) return true;

        // SSH 签名文件 (替代方式: 预签名 .devmode)
        lock (_cacheLock)
        {
            if (DateTime.UtcNow < _cacheExpiry)
                return _cachedResult;
        }

        bool result = VerifyDevModeInternal();

        lock (_cacheLock)
        {
            _cachedResult = result;
            _cacheExpiry = DateTime.UtcNow + CacheTtl;
        }
        return result;
    }

    private static bool VerifyDevModeInternal()
    {
        try
        {
            var devModePath = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "SysMonCmdPal", DevModeFileName);

            if (!File.Exists(devModePath)) return false;

            string content = File.ReadAllText(devModePath).Trim();
            if (!content.Contains("BEGIN SSH SIGNATURE")) return false;

            string base64 = ExtractArmoredBase64(content);
            if (string.IsNullOrEmpty(base64)) return false;

            byte[] blob;
            try { blob = Convert.FromBase64String(base64); }
            catch { return false; }

            var data = ParseSshsig(blob);
            if (data == null) return false;

            return VerifySignature(data, Challenge);
        }
        catch
        {
            return false;
        }
    }

    // ---- SSHSIG 解析 ----

    private sealed class SshsigData
    {
        public string KeyAlgorithm = "";
        public byte[] PublicKeyBlob = [];
        public string Namespace = "";
        public string HashAlgorithm = "";
        public string SigAlgorithm = "";
        public byte[] Signature = [];
        // 公钥原始字节（Ed25519: 32 bytes, RSA: 不定）
        public byte[] PublicKeyRaw = [];
    }

    /// <summary>从 armored 格式提取 base64 内容</summary>
    private static string ExtractArmoredBase64(string armored)
    {
        var lines = armored.Split('\n');
        var sb = new StringBuilder();
        bool inBody = false;
        foreach (var line in lines)
        {
            if (line.Contains("BEGIN SSH SIGNATURE")) { inBody = true; continue; }
            if (line.Contains("END SSH SIGNATURE")) break;
            if (inBody) sb.Append(line.Trim());
        }
        return sb.ToString();
    }

    /// <summary>解析 SSHSIG 二进制结构</summary>
    private static SshsigData? ParseSshsig(byte[] blob)
    {
        int pos = 0;

        if (pos + 6 > blob.Length) return null;
        string magic = Encoding.ASCII.GetString(blob, pos, 6);
        pos += 6;
        if (magic != SshsigMagic) return null;

        // Version: uint32
        if (pos + 4 > blob.Length) return null;
        int version = (blob[pos] << 24) | (blob[pos + 1] << 16) | (blob[pos + 2] << 8) | blob[pos + 3];
        pos += 4;
        if (version != 1) return null;

        // Public key blob (SSH public key format)
        byte[] pubKeyBlob = ReadSshBytes(blob, ref pos);
        if (pubKeyBlob.Length == 0) return null;

        // Namespace
        string ns = ReadSshString(blob, ref pos);

        // Reserved
        /* byte[] reserved = */ ReadSshBytes(blob, ref pos);

        // Hash algorithm
        string hashAlg = ReadSshString(blob, ref pos);

        // Signature blob (SSH signature format)
        byte[] sigBlob = ReadSshBytes(blob, ref pos);
        if (sigBlob.Length == 0) return null;

        // 解析公钥 blob
        int pkPos = 0;
        string keyAlgo = ReadSshString(pubKeyBlob, ref pkPos);
        byte[] pubKeyRaw = ReadSshBytes(pubKeyBlob, ref pkPos);

        // 解析签名 blob
        int sigPos = 0;
        string sigAlgo = ReadSshString(sigBlob, ref sigPos);
        byte[] sigBytes = ReadSshBytes(sigBlob, ref sigPos);

        return new SshsigData
        {
            KeyAlgorithm = keyAlgo,
            PublicKeyBlob = pubKeyBlob,
            Namespace = ns,
            HashAlgorithm = hashAlg,
            SigAlgorithm = sigAlgo,
            Signature = sigBytes,
            PublicKeyRaw = pubKeyRaw,
        };
    }

    /// <summary>验证 SSHSIG 签名</summary>
    private static bool VerifySignature(SshsigData data, string message)
    {
        // 重建签名数据: "SSHSIG" + namespace + reserved + hash_alg + SHA-512(message)
        byte[] messageHash;
        using (var sha512 = SHA512.Create())
        {
            messageHash = sha512.ComputeHash(Encoding.UTF8.GetBytes(message));
        }

        byte[] signedData = BuildSignedData(data.Namespace, data.HashAlgorithm, messageHash);

        switch (data.KeyAlgorithm)
        {
            case "ssh-ed25519":
                return VerifyEd25519(data.PublicKeyRaw, data.Signature, signedData);

            case "ssh-rsa":
                return VerifyRsa(data.PublicKeyRaw, data.Signature, signedData, data.HashAlgorithm);

            default:
                return false;
        }
    }

    /// <summary>重建 SSHSIG 签名数据</summary>
    private static byte[] BuildSignedData(string namespace_, string hashAlg, byte[] messageHash)
    {
        using var ms = new MemoryStream();
        byte[] magicBytes = Encoding.ASCII.GetBytes(SshsigMagic);
        ms.Write(magicBytes, 0, magicBytes.Length);
        WriteSshString(ms, namespace_);
        WriteSshBytes(ms, []); // reserved
        WriteSshString(ms, hashAlg);
        WriteSshBytes(ms, messageHash);
        return ms.ToArray();
    }

    // ---- Ed25519 验证 (Windows CNG) ----

    [DllImport("bcrypt.dll")]
    private static extern uint BCryptOpenAlgorithmProvider(
        out IntPtr phAlgorithm, string pszAlgId, string? pszImplementation, uint dwFlags);

    [DllImport("bcrypt.dll")]
    private static extern uint BCryptCloseAlgorithmProvider(IntPtr hAlgorithm, uint dwFlags);

    [DllImport("bcrypt.dll")]
    private static extern uint BCryptImportKeyPair(
        IntPtr hAlgorithm, IntPtr hImportKey, string pszBlobType,
        out IntPtr phKey, byte[] pbInput, int cbInput, uint dwFlags);

    [DllImport("bcrypt.dll")]
    private static extern uint BCryptVerifySignature(
        IntPtr hKey, IntPtr pPaddingInfo,
        byte[] pbHash, int cbHash,
        byte[] pbSignature, int cbSignature, uint dwFlags);

    [DllImport("bcrypt.dll")]
    private static extern uint BCryptDestroyKey(IntPtr hKey);

    private static bool VerifyEd25519(byte[] publicKeyRaw, byte[] signature, byte[] signedData)
    {
        if (publicKeyRaw.Length != 32 || signature.Length != 64) return false;

        IntPtr hAlg = IntPtr.Zero;
        IntPtr hKey = IntPtr.Zero;
        try
        {
            uint status = BCryptOpenAlgorithmProvider(out hAlg, "ED25519", null, 0);
            if (status != 0) return false;

            // BCRYPT_ECCPUBLIC_BLOB: magic(4) + keySize(4) + 32-byte key
            byte[] keyBlob = new byte[8 + 32];
            // BCRYPT_ECDH_PUBLIC_GENERIC_MAGIC: "ECKP" (0x504B4345, little-endian)
            keyBlob[0] = 0x45; keyBlob[1] = 0x43; keyBlob[2] = 0x4B; keyBlob[3] = 0x50;
            // key size = 32
            keyBlob[4] = 32; keyBlob[5] = 0; keyBlob[6] = 0; keyBlob[7] = 0;
            Array.Copy(publicKeyRaw, 0, keyBlob, 8, 32);

            status = BCryptImportKeyPair(hAlg, IntPtr.Zero, "ECCPUBLIC_BLOB",
                out hKey, keyBlob, keyBlob.Length, 0);
            if (status != 0) return false;

            status = BCryptVerifySignature(hKey, IntPtr.Zero,
                signedData, signedData.Length,
                signature, signature.Length, 0);

            return status == 0;
        }
        catch
        {
            return false;
        }
        finally
        {
            if (hKey != IntPtr.Zero) BCryptDestroyKey(hKey);
            if (hAlg != IntPtr.Zero) BCryptCloseAlgorithmProvider(hAlg, 0);
        }
    }

    // ---- RSA 验证 ----

    private static bool VerifyRsa(byte[] pubKeyRaw, byte[] signature, byte[] signedData, string hashAlg)
    {
        // RSA SSH 公钥: mpint e + mpint n
        int pos = 0;
        byte[] e = ReadSshMpint(pubKeyRaw, ref pos);
        byte[] n = ReadSshMpint(pubKeyRaw, ref pos);

        try
        {
            using var rsa = RSA.Create();
            rsa.ImportParameters(new RSAParameters { Exponent = e, Modulus = n });

            // SSHSIG RSA: 签名是对 signedData 的 SHA-512 哈希进行 PKCS1 签名
            byte[] dataHash;
            using (var sha512 = SHA512.Create())
            {
                dataHash = sha512.ComputeHash(signedData);
            }
            return rsa.VerifyData(dataHash, signature, HashAlgorithmName.SHA512, RSASignaturePadding.Pkcs1);
        }
        catch
        {
            return false;
        }
    }

    // ---- SSH 二进制格式辅助 ----

    private static string ReadSshString(byte[] blob, ref int pos)
    {
        byte[] bytes = ReadSshBytes(blob, ref pos);
        return Encoding.ASCII.GetString(bytes);
    }

    private static byte[] ReadSshBytes(byte[] blob, ref int pos)
    {
        if (pos + 4 > blob.Length) return [];
        int len = (blob[pos] << 24) | (blob[pos + 1] << 16) | (blob[pos + 2] << 8) | blob[pos + 3];
        pos += 4;
        if (pos + len > blob.Length) return [];
        byte[] result = new byte[len];
        Array.Copy(blob, pos, result, 0, len);
        pos += len;
        return result;
    }

    private static byte[] ReadSshMpint(byte[] blob, ref int pos)
    {
        byte[] raw = ReadSshBytes(blob, ref pos);
        // 去掉前导 0x00（SSH mpint 有符号前缀）
        if (raw.Length > 1 && raw[0] == 0x00)
        {
            byte[] trimmed = new byte[raw.Length - 1];
            Array.Copy(raw, 1, trimmed, 0, trimmed.Length);
            return trimmed;
        }
        return raw;
    }

    private static void WriteSshString(Stream s, string value)
    {
        WriteSshBytes(s, Encoding.ASCII.GetBytes(value));
    }

    private static void WriteSshBytes(Stream s, byte[] data)
    {
        byte[] len = [(byte)(data.Length >> 24), (byte)(data.Length >> 16), (byte)(data.Length >> 8), (byte)data.Length];
        s.Write(len, 0, 4);
        s.Write(data, 0, data.Length);
    }
}
