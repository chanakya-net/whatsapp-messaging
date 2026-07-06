using System.Security.Cryptography;

namespace MessageBridge.Publisher.Internal;

internal static class UlidGenerator
{
    private const string EncodingAlphabet = "0123456789ABCDEFGHJKMNPQRSTVWXYZ";

    public static string New()
    {
        var bytes = new byte[16];
        var timestamp = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();

        bytes[0] = (byte)(timestamp >> 40);
        bytes[1] = (byte)(timestamp >> 32);
        bytes[2] = (byte)(timestamp >> 24);
        bytes[3] = (byte)(timestamp >> 16);
        bytes[4] = (byte)(timestamp >> 8);
        bytes[5] = (byte)timestamp;

        var random = new byte[10];
        RandomNumberGenerator.Fill(random);
        Array.Copy(random, 0, bytes, 6, 10);

        return Encode(bytes);
    }

    private static string Encode(ReadOnlySpan<byte> bytes)
    {
        const int outputLength = 26;
        var output = new char[outputLength];
        ulong buffer = 0;
        var bits = 0;
        var index = 0;

        foreach (var value in bytes)
        {
            buffer = (buffer << 8) | value;
            bits += 8;

            while (bits >= 5 && index < outputLength)
            {
                bits -= 5;
                var chunk = (int)((buffer >> bits) & 31);
                output[index++] = EncodingAlphabet[chunk];
            }
        }

        if (index < outputLength && bits > 0)
        {
            output[index++] = EncodingAlphabet[(int)((buffer << (5 - bits)) & 31)];
        }

        while (index < outputLength)
        {
            output[index++] = '0';
        }

        return new string(output);
    }
}
