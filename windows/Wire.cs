using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace MultipleMouse;

sealed class Wire : IDisposable
{
    public static byte[] PairingToken(string code)
    {
        byte[] password = Encoding.UTF8.GetBytes(code.Trim(' ', '\t', '\r', '\n').Normalize(NormalizationForm.FormC));
        if (password.Length == 0 || password.Length > 256) throw new ArgumentException("Enter a pairing code (up to 256 UTF-8 bytes).");
        return Rfc2898DeriveBytes.Pbkdf2(password, Encoding.UTF8.GetBytes("MultipleMouse/pairing-code/v1"), 600_000, HashAlgorithmName.SHA256, 32);
    }
    readonly AesGcm outgoing, incoming;
    ulong sent, received;
    public Wire(byte[] token, byte[] client, byte[] server, bool isServer = false)
    {
        if (token.Length != 32 || client.Length != 32 || server.Length != 32) throw new InvalidDataException("Invalid pairing key or nonce.");
        byte[] Key(string direction) => HMACSHA256.HashData(token, Encoding.UTF8.GetBytes("MultipleMouse/1/" + direction).Concat(client).Concat(server).ToArray());
        outgoing = new AesGcm(Key(isServer ? "s2c" : "c2s"), 16);
        incoming = new AesGcm(Key(isServer ? "c2s" : "s2c"), 16);
    }
    static byte[] Nonce(ulong counter)
    {
        byte[] result = new byte[12]; BinaryPrimitives.WriteUInt64BigEndian(result.AsSpan(4), counter); return result;
    }
    public string Seal(object message)
    {
        if (sent == ulong.MaxValue) throw new InvalidDataException("Counter exhausted.");
        byte[] plain = JsonSerializer.SerializeToUtf8Bytes(message), nonce = Nonce(sent);
        byte[] output = new byte[12 + plain.Length + 16]; nonce.CopyTo(output, 0);
        outgoing.Encrypt(nonce, plain, output.AsSpan(12, plain.Length), output.AsSpan(12 + plain.Length));
        sent++; return Convert.ToBase64String(output);
    }
    public JsonDocument Open(string line)
    {
        byte[] data = Convert.FromBase64String(line);
        if (received == ulong.MaxValue || data.Length < 28 || !data.AsSpan(0, 12).SequenceEqual(Nonce(received))) throw new InvalidDataException("Invalid message sequence.");
        byte[] plain = new byte[data.Length - 28];
        incoming.Decrypt(data.AsSpan(0, 12), data.AsSpan(12, plain.Length), data.AsSpan(12 + plain.Length), plain);
        var result = JsonDocument.Parse(plain); received++; return result;
    }
    public void Dispose() { outgoing.Dispose(); incoming.Dispose(); }
}
