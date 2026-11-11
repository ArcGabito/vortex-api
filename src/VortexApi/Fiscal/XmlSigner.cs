using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Security.Cryptography.Xml;
using System.Xml;

namespace VortexApi.Fiscal;

/// <summary>
/// Firma XMLDSig envuelta (enveloped) dentro de <c>ext:UBLExtensions/ext:UBLExtension/ext:ExtensionContent</c>, como la
/// pide SUNAT: canonicalización C14N, referencia al documento entero (<c>URI=""</c>) y el certificado en <c>KeyInfo</c>.
/// Devuelve el resumen (<c>DigestValue</c>), que va en la representación impresa.
/// </summary>
public static class XmlSigner
{
    public const string SignatureId = "SignatureSP";

    public static (string Xml, string Digest) Sign(string unsignedXml, X509Certificate2 certificate, string algorithm = "sha1")
    {
        var doc = new XmlDocument { PreserveWhitespace = true };
        doc.LoadXml(unsignedXml);
        var ns = new XmlNamespaceManager(doc.NameTable);
        ns.AddNamespace("ext", "urn:oasis:names:specification:ubl:schema:xsd:CommonExtensionComponents-2");
        var container = doc.SelectSingleNode("//ext:UBLExtensions/ext:UBLExtension/ext:ExtensionContent", ns)
                        ?? throw new InvalidOperationException("El XML no tiene ext:ExtensionContent para la firma.");

        var key = certificate.GetRSAPrivateKey() ?? throw new InvalidOperationException("El certificado no tiene llave privada RSA.");
        var sha256 = string.Equals(algorithm, "sha256", StringComparison.OrdinalIgnoreCase);
        var signed = new SignedXml(doc) { SigningKey = key };
        signed.SignedInfo!.CanonicalizationMethod = SignedXml.XmlDsigC14NTransformUrl;
        signed.SignedInfo.SignatureMethod = sha256 ? SignedXml.XmlDsigRSASHA256Url : SignedXml.XmlDsigRSASHA1Url;
        var reference = new Reference { Uri = "", DigestMethod = sha256 ? SignedXml.XmlDsigSHA256Url : SignedXml.XmlDsigSHA1Url };
        reference.AddTransform(new XmlDsigEnvelopedSignatureTransform());
        signed.AddReference(reference);
        var keyInfo = new KeyInfo();
        var x509 = new KeyInfoX509Data(certificate);
        x509.AddSubjectName(certificate.Subject);
        keyInfo.AddClause(x509);
        signed.KeyInfo = keyInfo;
        signed.Signature.Id = SignatureId;
        signed.ComputeSignature();

        // Sin cambiar prefijos: la canonicalización los incluye y cambiarlos después invalidaría la firma.
        container.AppendChild(doc.ImportNode(signed.GetXml(), true));
        var digest = Convert.ToBase64String(reference.DigestValue!);
        return (doc.OuterXml, digest);
    }

    /// <summary>Verifica la firma (para pruebas y para revisar lo que se va a enviar).</summary>
    public static bool Verify(string signedXml, X509Certificate2 certificate)
    {
        var doc = new XmlDocument { PreserveWhitespace = true };
        doc.LoadXml(signedXml);
        var node = doc.GetElementsByTagName("Signature", SignedXml.XmlDsigNamespaceUrl)[0] as XmlElement;
        if (node is null)
            return false;
        var signed = new SignedXml(doc);
        signed.LoadXml(node);
        return signed.CheckSignature(certificate, verifySignatureOnly: true);
    }
}
