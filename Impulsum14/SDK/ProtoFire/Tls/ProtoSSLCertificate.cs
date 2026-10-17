using Org.BouncyCastle.Crypto;
using Org.BouncyCastle.Crypto.Parameters;
using Org.BouncyCastle.Pkcs;
using Org.BouncyCastle.Security;
using Org.BouncyCastle.Tls;
using Org.BouncyCastle.Tls.Crypto;
using Org.BouncyCastle.Tls.Crypto.Impl.BC;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;

namespace ProtoFire.Tls;

public class ProtoSSLCertificate
{
    public AsymmetricKeyParameter PrivateKey { get; }
    public Certificate Certificate { get; }
    public X509Certificate2? X509Certificate { get; }

    public ProtoSSLCertificate(AsymmetricKeyParameter privateKey, Certificate certificate)
    {
        PrivateKey = privateKey;
        Certificate = certificate;
    }

    public ProtoSSLCertificate(X509Certificate2 certificate)
    {
        X509Certificate = certificate;

        AsymmetricAlgorithm? privateKey = getPrivateKey(certificate);
        if (privateKey == null)
            throw new ArgumentException("Certificate does not contain a private key");

        try { PrivateKey = DotNetUtilities.GetKeyPair(privateKey).Private; }
        catch (CryptographicException exception)
        {
            throw new ArgumentException("Invalid certificate private key or private key is not exportable (missing X509KeyStorageFlags.Exportable flag).", exception);
        }

        Org.BouncyCastle.X509.X509Certificate serverCertificate = DotNetUtilities.FromX509Certificate(certificate);
        TlsCertificate[] chain = [new BcTlsCertificate(new BcTlsCrypto(new SecureRandom()), serverCertificate.CertificateStructure)];
        Certificate = new Certificate(chain);
    }

    public static ProtoSSLCertificate FromX509Certificate2(X509Certificate2 certificate) => new ProtoSSLCertificate(certificate);

    /// <summary>
    /// Loads the legacy redirector identity without importing its private key into an OS key
    /// store. Windows CNG cannot export some legacy PFX keys, even with Exportable set.
    /// The caller retains ownership of <paramref name="input"/>.
    /// </summary>
    public static ProtoSSLCertificate FromPkcs12(Stream input, string? password = null)
    {
        ArgumentNullException.ThrowIfNull(input);
        var store = new Pkcs12StoreBuilder().Build();
        char[] passwordChars = password?.ToCharArray() ?? [];
        try { store.Load(input, passwordChars); }
        finally { Array.Clear(passwordChars); }

        string[] aliases = store.Aliases.Where(store.IsKeyEntry).Take(2).ToArray();
        if (aliases.Length != 1)
            throw new InvalidDataException("Redirector PFX must contain exactly one private key.");
        var key = store.GetKey(aliases[0]).Key;
        var entries = store.GetCertificateChain(aliases[0]);
        if (key is not RsaPrivateCrtKeyParameters rsa || entries == null || entries.Length == 0)
            throw new InvalidDataException("Redirector PFX requires an RSA private key and certificate chain.");
        if (entries[0].Certificate.GetPublicKey() is not RsaKeyParameters leaf ||
            !rsa.Modulus.Equals(leaf.Modulus) || !rsa.PublicExponent.Equals(leaf.Exponent))
            throw new InvalidDataException("Redirector certificate does not match its private key.");

        // PKCS#12 returns the leaf first. Keep that order and the exact legacy identity;
        // modern OS trust/expiry policies are not the retail client's TLS policy.
        var crypto = new BcTlsCrypto(new SecureRandom());
        TlsCertificate[] chain = entries.Select(entry =>
            (TlsCertificate)new BcTlsCertificate(crypto, entry.Certificate.CertificateStructure)).ToArray();
        return new ProtoSSLCertificate(key, new Certificate(chain));
    }

    public static ProtoSSLCertificate FromPkcs12File(string path, string? password = null)
    {
        using var input = File.OpenRead(path);
        return FromPkcs12(input, password);
    }

    public X509Certificate2? AsX509Certificate2() => X509Certificate;

    static AsymmetricAlgorithm? getPrivateKey(X509Certificate2 certificate)
    {
        // X509Certificate2 has PrivateKey property, but it is deprecated.
        // This function has been created to avoid getting warning about it.

        if (!certificate.HasPrivateKey)
            return null;

        RSA? rsa = certificate.GetRSAPrivateKey();
        if (rsa != null)
            return rsa;

        DSA? dsa = certificate.GetDSAPrivateKey();
        if (dsa != null)
            return dsa;

        ECDsa? ecdsa = certificate.GetECDsaPrivateKey();
        if (ecdsa != null)
            return ecdsa;

        ECDiffieHellman? ecdh = certificate.GetECDiffieHellmanPrivateKey();
        if (ecdh != null)
            return ecdh;

        throw new NotSupportedException("Key algorithm not supported");
    }

    public static implicit operator ProtoSSLCertificate(X509Certificate2 certificate) => new ProtoSSLCertificate(certificate);
}
