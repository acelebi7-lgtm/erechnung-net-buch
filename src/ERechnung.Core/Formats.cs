using System.Xml;
using System.Xml.Linq;

namespace ERechnung.Core;

/// <summary>Wie die Rechnung angeliefert wurde.</summary>
public enum Container { Xml, Pdf, Unknown }

/// <summary>Die XML-Syntax des strukturierten Teils.</summary>
public enum Syntax { UblInvoice, UblCreditNote, Cii, Unknown }

/// <summary>Ergebnis der Formaterkennung.</summary>
public sealed record FormatInfo(
    Container Container,
    Syntax Syntax,
    string? SpecificationId,
    string Profile,
    bool IsEInvoice,
    string? Hint);

/// <summary>Die Namensräume, die wir für UBL und CII brauchen.</summary>
public static class Ns
{
    public static readonly XNamespace UblInvoice = "urn:oasis:names:specification:ubl:schema:xsd:Invoice-2";
    public static readonly XNamespace UblCreditNote = "urn:oasis:names:specification:ubl:schema:xsd:CreditNote-2";
    public static readonly XNamespace Cbc = "urn:oasis:names:specification:ubl:schema:xsd:CommonBasicComponents-2";
    public static readonly XNamespace Cac = "urn:oasis:names:specification:ubl:schema:xsd:CommonAggregateComponents-2";
    public static readonly XNamespace Rsm = "urn:un:unece:uncefact:data:standard:CrossIndustryInvoice:100";
    public static readonly XNamespace Ram = "urn:un:unece:uncefact:data:standard:ReusableAggregateBusinessInformationEntity:100";
    public static readonly XNamespace Udt = "urn:un:unece:uncefact:data:standard:UnqualifiedDataType:100";
    public static readonly XNamespace Zugferd1 = "urn:ferd:CrossIndustryDocument:invoice:1p0";
}

public static class FormatDetector
{
    /// <summary>PDF-Dateien beginnen immer mit "%PDF-".</summary>
    public static bool IsPdf(ReadOnlySpan<byte> data) =>
        data.Length >= 5 && data[..5].SequenceEqual("%PDF-"u8);

    /// <summary>
    /// Lädt XML ohne DTD-Verarbeitung und ohne externe Auflösung.
    /// Schützt vor XXE- und "Billion Laughs"-Angriffen.
    /// </summary>
    public static XDocument LoadXmlSafe(byte[] data)
    {
        var settings = new XmlReaderSettings
        {
            DtdProcessing = DtdProcessing.Prohibit,
            XmlResolver = null,
            MaxCharactersInDocument = 50_000_000
        };
        using var stream = new MemoryStream(data);
        using var reader = XmlReader.Create(stream, settings);
        return XDocument.Load(reader);
    }

    public static FormatInfo Detect(byte[] data)
    {
        if (IsPdf(data))
            return new(Container.Pdf, Syntax.Unknown, null, "unbekannt", false,
                "PDF: der XML-Anhang muss zuerst extrahiert werden.");

        try
        {
            return Detect(LoadXmlSafe(data));
        }
        catch (XmlException ex)
        {
            return new(Container.Unknown, Syntax.Unknown, null, "unbekannt", false,
                $"Kein wohlgeformtes XML: {ex.Message}");
        }
    }

    public static FormatInfo Detect(XDocument doc)
    {
        var root = doc.Root!;
        Syntax syntax;
        string? id;

        if (root.Name == Ns.UblInvoice + "Invoice")
        {
            syntax = Syntax.UblInvoice;
            id = (string?)root.Element(Ns.Cbc + "CustomizationID");
        }
        else if (root.Name == Ns.UblCreditNote + "CreditNote")
        {
            syntax = Syntax.UblCreditNote;
            id = (string?)root.Element(Ns.Cbc + "CustomizationID");
        }
        else if (root.Name == Ns.Rsm + "CrossIndustryInvoice")
        {
            syntax = Syntax.Cii;
            id = (string?)root
                .Element(Ns.Rsm + "ExchangedDocumentContext")?
                .Element(Ns.Ram + "GuidelineSpecifiedDocumentContextParameter")?
                .Element(Ns.Ram + "ID");
        }
        else if (root.Name == Ns.Zugferd1 + "CrossIndustryDocument")
        {
            return new(Container.Xml, Syntax.Unknown, null, "ZUGFeRD-1.0", false,
                "ZUGFeRD 1.0 ist veraltet und erfüllt die EN 16931 nicht – keine E-Rechnung.");
        }
        else
        {
            return new(Container.Xml, Syntax.Unknown, null, "unbekannt", false,
                $"Unbekanntes Wurzelelement: {root.Name}");
        }

        id = id?.Trim();
        var (profile, isEInvoice) = ClassifyProfile(id);
        string? hint = isEInvoice ? null
            : "Dieses Profil erfüllt die EN 16931 nicht und gilt nicht als E-Rechnung.";
        return new(Container.Xml, syntax, id, profile, isEInvoice, hint);
    }

    /// <summary>
    /// Ordnet die Spezifikationskennung (BT-24) einem Profil zu.
    /// MINIMUM und BASIC WL sind keine E-Rechnungen im Sinne von § 14 UStG.
    /// </summary>
    public static (string Profile, bool IsEInvoice) ClassifyProfile(string? id)
    {
        const StringComparison ic = StringComparison.OrdinalIgnoreCase;
        if (string.IsNullOrEmpty(id)) return ("unbekannt", false);
        if (id.Contains("xrechnung", ic)) return ("XRECHNUNG", true);
        if (id.Contains("peppol", ic)) return ("PEPPOL-BIS", true);
        if (id.EndsWith(":minimum", ic)) return ("MINIMUM", false);
        if (id.EndsWith(":basicwl", ic)) return ("BASIC-WL", false);
        if (id.EndsWith(":basic", ic)) return ("BASIC", true);
        if (id.Contains(":extended", ic)) return ("EXTENDED", true);
        if (id == "urn:cen.eu:en16931:2017") return ("EN16931", true);
        if (id.StartsWith("urn:cen.eu:en16931:2017", ic)) return ("EN16931-Variante", true);
        return ("unbekannt", false);
    }
}
