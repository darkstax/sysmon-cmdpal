// SysMonBroker/COM/DevModeVerifier.cs
// SSH 签名验证 .devmode 文件
//
// .devmode 生成 (开发者执行一次):
//   echo -n "SysMonBroker.DevMode.v2.2" | openssl dgst -sha256 -sign ~/.ssh/id_rsa | base64 -w0 > %LOCALAPPDATA%\SysMonCmdPal\.devmode
//
// Broker 验证:
//   1. 读取 .devmode → base64 decode → 签名
//   2. 读取 ~/.ssh/id_rsa.pub → 解析 OpenSSH RSA 公钥
//   3. RSA.VerifyData(challenge, signature, SHA256, Pkcs1)

using System.Security.Cryptography;
using System.Text;

namespace SysMonBroker.COM;

/// <summary>
/// 通过 SSH 公钥签名验证 .devmode 文件。
/// 攻击者即使知道 .devmode 机制，没有 SSH 私钥也无法生成有效签名。
/// </summary>
public static class DevModeVerifier
{
    private const string Challenge = "SysMonBroker.DevMode.v2.2";
    private const string DevModeFileName = ".devmode";

    /// <summary>检查 devmode 是否激活（签名有效）。</summary>
    public static bool IsDevModeActive()
    {
        try
        {
            var devModePath = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "SysMonCmdPal", DevModeFileName);

            if (!File.Exists(devModePath)) return false;

            // 读取签名 (base64)
            string base64 = File.ReadAllText(devModePath).Trim();
            if (string.IsNullOrEmpty(base64)) return false;

            byte[] signature;
            try { signature = Convert.FromBase64String(base64); }
            catch (FormatException) { return false; }

            // 查找 SSH 公钥
            string sshDir = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".ssh");
            string pubKeyPath = Path.Combine(sshDir, "id_rsa.pub");

            if (!File.Exists(pubKeyPath)) return false;

            // 解析 OpenSSH 公钥
            using var rsa = ImportOpenSshPublicKey(pubKeyPath);
            if (rsa == null) return false;

            // 验证签名
            byte[] challengeBytes = Encoding.UTF8.GetBytes(Challenge);
            return rsa.VerifyData(challengeBytes, signature,
                HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        }
        catch
        {
            return false; // 任何异常 → 拒绝
        }
    }

    /// <summary>
    /// 解析 OpenSSH RSA 公钥文件 (ssh-rsa AAAAB3NzaC1yc2E... comment)
    /// 格式: [4-byte BE len][string "ssh-rsa"][4-byte BE len][mpint e][4-byte BE len][mpint n]
    /// </summary>
    private static RSA? ImportOpenSshPublicKey(string pubKeyPath)
    {
        string line = File.ReadAllText(pubKeyPath).Trim();
        string[] parts = line.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length < 2) return null;
        if (parts[0] != "ssh-rsa") return null;

        byte[] blob;
        try { blob = Convert.FromBase64String(parts[1]); }
        catch (FormatException) { return null; }

        int pos = 0;

        // Read algorithm name
        string algo = ReadSshString(blob, ref pos);
        if (algo != "ssh-rsa") return null;

        // Read exponent (mpint)
        byte[] e = ReadSshMpint(blob, ref pos);

        // Read modulus (mpint)
        byte[] n = ReadSshMpint(blob, ref pos);

        try
        {
            var rsa = RSA.Create();
            rsa.ImportParameters(new RSAParameters
            {
                Exponent = e,
                Modulus = n
            });
            return rsa;
        }
        catch
        {
            return null;
        }
    }

    /// <summary>读取 SSH 格式字符串: [4-byte BE length][data bytes]</summary>
    private static string ReadSshString(byte[] blob, ref int pos)
    {
        if (pos + 4 > blob.Length) return "";
        int len = (blob[pos] << 24) | (blob[pos + 1] << 16) | (blob[pos + 2] << 8) | blob[pos + 3];
        pos += 4;
        if (pos + len > blob.Length) return "";
        string result = Encoding.ASCII.GetString(blob, pos, len);
        pos += len;
        return result;
    }

    /// <summary>读取 SSH mpint (big-endian big integer, 有符号)</summary>
    private static byte[] ReadSshMpint(byte[] blob, ref int pos)
    {
        if (pos + 4 > blob.Length) return [];
        int len = (blob[pos] << 24) | (blob[pos + 1] << 16) | (blob[pos + 2] << 8) | blob[pos + 3];
        pos += 4;
        if (pos + len > blob.Length) return [];

        byte[] result = new byte[len];
        Array.Copy(blob, pos, result, 0, len);
        pos += len;

        // mpint 是有符号的，去掉前导 0x00 前缀
        if (result.Length > 1 && result[0] == 0x00)
        {
            byte[] trimmed = new byte[result.Length - 1];
            Array.Copy(result, 1, trimmed, 0, trimmed.Length);
            return trimmed;
        }

        return result;
    }
}
