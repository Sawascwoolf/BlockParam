using System.IO;
using System.Security.Cryptography;
using System.Text;

namespace BlockParam.Licensing;

/// <summary>
/// Simple XOR-based obfuscation with machine-bound key and checksum.
/// Not cryptographically secure — prevents casual text editing of data files.
/// </summary>
internal static class Obfuscation
{
    public static byte[] Obfuscate(string text)
    {
        var bytes = Encoding.UTF8.GetBytes(text);
        var key = GetMachineKey();
        for (int i = 0; i < bytes.Length; i++)
            bytes[i] = (byte)(bytes[i] ^ key[i % key.Length]);

        var checksum = ComputeChecksum(bytes);
        var result = new byte[4 + bytes.Length];
        BitConverter.GetBytes(checksum).CopyTo(result, 0);
        bytes.CopyTo(result, 4);
        return result;
    }

    public static string Deobfuscate(byte[] data)
    {
        if (data.Length < 4)
            throw new InvalidDataException("Data too short.");

        var storedChecksum = BitConverter.ToInt32(data, 0);
        var payload = new byte[data.Length - 4];
        Array.Copy(data, 4, payload, 0, payload.Length);
        var actualChecksum = ComputeChecksum(payload);

        if (storedChecksum != actualChecksum)
            throw new InvalidDataException("Checksum mismatch — file was tampered with.");

        var key = GetMachineKey();
        for (int i = 0; i < payload.Length; i++)
            payload[i] = (byte)(payload[i] ^ key[i % key.Length]);

        return Encoding.UTF8.GetString(payload);
    }

    private static int ComputeChecksum(byte[] data)
    {
        unchecked
        {
            int hash = 17;
            foreach (var b in data)
                hash = hash * 31 + b;
            return hash;
        }
    }

    private static byte[] GetMachineKey()
    {
        var machineName = Environment.MachineName + "BlockParam_v1";
        using var sha = SHA256.Create();
        return sha.ComputeHash(Encoding.UTF8.GetBytes(machineName));
    }
}
