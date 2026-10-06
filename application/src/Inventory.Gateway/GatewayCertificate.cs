using System.Net;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;

namespace Inventory.Gateway;

/// <summary>
/// Сертификат TLS для шлюза. Если по пути из конфигурации лежит PFX-файл, используется он
/// (так подключается настоящий сертификат). Иначе при первом запуске создаётся
/// самоподписанный сертификат для localhost, а рядом сохраняется его публичная часть
/// gateway.crt, которую можно добавить в доверенные в Postman.
/// </summary>
public static class GatewayCertificate
{
    public static X509Certificate2 LoadOrCreate(IConfiguration configuration)
    {
        var path = configuration["Gateway:Certificate:Path"] ?? "/https/gateway.pfx";
        var password = configuration["Gateway:Certificate:Password"];

        if (File.Exists(path))
        {
            Console.WriteLine($"[gateway] Используется сертификат {path}");
            return X509CertificateLoader.LoadPkcs12FromFile(path, password);
        }

        using var rsa = RSA.Create(2048);
        var request = new CertificateRequest("CN=localhost", rsa, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);

        var san = new SubjectAlternativeNameBuilder();
        san.AddDnsName("localhost");
        san.AddDnsName("gateway");
        san.AddIpAddress(IPAddress.Loopback);
        request.CertificateExtensions.Add(san.Build());
        request.CertificateExtensions.Add(new X509BasicConstraintsExtension(false, false, 0, true));
        request.CertificateExtensions.Add(new X509KeyUsageExtension(
            X509KeyUsageFlags.DigitalSignature | X509KeyUsageFlags.KeyEncipherment, true));
        request.CertificateExtensions.Add(new X509EnhancedKeyUsageExtension(
            [new Oid("1.3.6.1.5.5.7.3.1")], false));

        using var certificate = request.CreateSelfSigned(DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow.AddYears(1));
        var pfx = certificate.Export(X509ContentType.Pfx, password);

        try
        {
            var directory = Path.GetDirectoryName(path);
            if (!string.IsNullOrEmpty(directory))
            {
                Directory.CreateDirectory(directory);
            }

            File.WriteAllBytes(path, pfx);
            File.WriteAllText(Path.ChangeExtension(path, ".crt"), certificate.ExportCertificatePem());
            Console.WriteLine($"[gateway] Создан самоподписанный сертификат {path}");
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Console.WriteLine($"[gateway] Сертификат создан в памяти, сохранить не удалось: {ex.Message}");
        }

        return X509CertificateLoader.LoadPkcs12(pfx, password);
    }
}
