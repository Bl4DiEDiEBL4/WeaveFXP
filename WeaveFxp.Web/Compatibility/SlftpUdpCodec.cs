using System.Security.Cryptography;
using System.Text;

namespace WeaveFxp.Web.Compatibility;

internal static class SlftpUdpCodec
{
    private static readonly byte[] SaltedHeader = Encoding.ASCII.GetBytes("Salted__");
    private static readonly UTF8Encoding StrictUtf8 = new(false, true);

    public static bool TryDecode(byte[] datagram, string mode, string password,
        out string text, out string error)
    {
        text = "";
        error = "";
        mode = string.IsNullOrWhiteSpace(mode) ? "auto" : mode.Trim().ToLowerInvariant();

        if (!TryGetEnvelope(datagram, out var envelope))
        {
            if (mode == "encrypted")
            {
                error = "unencrypted datagram rejected by encrypted-only mode";
                return false;
            }

            try
            {
                text = StrictUtf8.GetString(datagram).Trim();
                return text.Length > 0;
            }
            catch (DecoderFallbackException)
            {
                error = "datagram is neither valid UTF-8 nor a slftp encrypted payload";
                return false;
            }
        }

        if (mode == "plaintext")
        {
            error = "encrypted datagram rejected by plaintext-only mode";
            return false;
        }
        if (string.IsNullOrEmpty(password))
        {
            error = "encrypted UDP requires an API password";
            return false;
        }

        try
        {
            var salt = envelope.AsSpan(SaltedHeader.Length, 8).ToArray();
            var ciphertext = envelope.AsSpan(SaltedHeader.Length + 8).ToArray();
            using var derive = new Rfc2898DeriveBytes(password, salt, 10_000, HashAlgorithmName.SHA256);
            var material = derive.GetBytes(48);
            using var aes = Aes.Create();
            aes.KeySize = 256;
            aes.BlockSize = 128;
            aes.Mode = CipherMode.CBC;
            aes.Padding = PaddingMode.PKCS7;
            aes.Key = material[..32];
            aes.IV = material[32..48];
            using var decryptor = aes.CreateDecryptor();
            var plaintext = decryptor.TransformFinalBlock(ciphertext, 0, ciphertext.Length);
            text = StrictUtf8.GetString(plaintext).Trim();
            return text.Length > 0;
        }
        catch (Exception ex) when (ex is CryptographicException or DecoderFallbackException)
        {
            error = "encrypted UDP could not be decrypted with the configured API password";
            return false;
        }
    }

    private static bool TryGetEnvelope(byte[] datagram, out byte[] envelope)
    {
        envelope = datagram;
        if (HasValidHeader(envelope)) return true;

        // OpenSSL-compatible tools sometimes send the same Salted__ envelope as base64.
        try
        {
            var encoded = Encoding.ASCII.GetString(datagram).Trim();
            if (!encoded.StartsWith("U2FsdGVkX1", StringComparison.Ordinal)) return false;
            envelope = Convert.FromBase64String(encoded);
            return HasValidHeader(envelope);
        }
        catch (Exception ex) when (ex is FormatException or DecoderFallbackException)
        {
            return false;
        }
    }

    private static bool HasValidHeader(byte[] value)
    {
        if (value.Length < SaltedHeader.Length + 8 + 16) return false;
        if ((value.Length - SaltedHeader.Length - 8) % 16 != 0) return false;
        return value.AsSpan(0, SaltedHeader.Length).SequenceEqual(SaltedHeader);
    }
}
