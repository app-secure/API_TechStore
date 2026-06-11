using System.Runtime.Serialization;
using System.ServiceModel;
using System.Xml.Linq;

namespace TechStore360.ExternalServices;

[ServiceContract]
public interface ISriSoapService
{
    [OperationContract]
    bool ValidarFactura(string xmlFactura);

    [OperationContract]
    RespuestaFacturaSri GenerarFacturaXML(string xmlFactura);

    [OperationContract]
    RespuestaFacturaSri ConsultarComprobante(int idCompra);
}

[DataContract]
public class RespuestaFacturaSri
{
    [DataMember] public string Estado { get; set; } = string.Empty;
    [DataMember] public string Mensaje { get; set; } = string.Empty;
    [DataMember] public string ClaveAcceso { get; set; } = string.Empty;
}

public class SriSoapService : ISriSoapService
{
    public bool ValidarFactura(string xmlFactura)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(xmlFactura)) return false;

            var doc = XDocument.Parse(xmlFactura);
            var raiz = doc.Root;
            if (raiz == null) return false;

            if (raiz.Element("Cabecera") == null) return false;
            if (raiz.Element("Cliente") == null) return false;
            if (raiz.Element("Detalle") == null) return false;

            var totales = raiz.Element("Totales");
            if (totales == null) return false;

            var totalStr = totales.Element("Total")?.Value ?? "0";
            if (!decimal.TryParse(totalStr, System.Globalization.NumberStyles.Any,
                System.Globalization.CultureInfo.InvariantCulture, out var total) || total <= 0)
                return false;

            return true;
        }
        catch (System.Xml.XmlException)
        {
            return false;
        }
    }

    public RespuestaFacturaSri GenerarFacturaXML(string xmlFactura)
    {
        if (!ValidarFactura(xmlFactura))
        {
            return new RespuestaFacturaSri
            {
                Estado = "RECHAZADA",
                Mensaje = "El XML de la factura tiene una estructura inválida",
                ClaveAcceso = "S/N"
            };
        }

        int idCompra = 1; 
        try
        {
            var doc = XDocument.Parse(xmlFactura);
            var numeroStr = doc.Root?.Element("Cabecera")?.Element("Numero")?.Value;
            if (numeroStr != null)
            {
                var parts = numeroStr.Split('-');
                if (parts.Length == 3 && int.TryParse(parts[2], out int id))
                {
                    idCompra = id;
                }
            }
        }
        catch { }

        return new RespuestaFacturaSri
        {
            Estado = "VALIDADA",
            Mensaje = "Factura generada correctamente",
            ClaveAcceso = GenerarClaveAcceso(idCompra)
        };
    }

    public RespuestaFacturaSri ConsultarComprobante(int idCompra)
    {
        return new RespuestaFacturaSri
        {
            Estado = "AUTORIZADA",
            Mensaje = "Comprobante recuperado exitosamente del SRI",
            ClaveAcceso = GenerarClaveAcceso(idCompra)
        };
    }

    private static string GenerarClaveAcceso(int idCompra)
    {
        var fecha = DateTime.UtcNow.ToString("ddMMyyyy");
        const string tipoDoc = "01";           
        const string rucSimulado = "1890123456001";
        const string ambiente = "2";          
        const string serie = "001001";
        string secuencial = idCompra.ToString("D9");
        const string codigoNumerico = "12345678";
        const string tipoEmision = "1";      

        return $"{fecha}{tipoDoc}{rucSimulado}{ambiente}{serie}{secuencial}{codigoNumerico}{tipoEmision}";
    }
}
