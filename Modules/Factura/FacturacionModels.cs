using System;
using System.Collections.Generic;
using System.Text.Json.Serialization;
using System.Xml.Linq;
using TechStore360.ExternalServices;
using TechStore360.Modules.Compras;
using TechStore360.Modules.Productos;
using TechStore360.Modules.Usuarios;

namespace TechStore360.Modulos.Factura
{
    public class GenerarFacturaRequest
    {
        [JsonPropertyName("idCompra")]
        public int IdCompra { get; set; }
    }

    public class ValidationErrorResponse
    {
        [JsonPropertyName("estado")]
        public string Estado { get; set; } = "error";

        [JsonPropertyName("mensaje")]
        public string Mensaje { get; set; } = "Datos inválidos. No se pudo generar la factura.";

        [JsonPropertyName("errores")]
        public List<ErrorDetail> Errores { get; set; } = new();
    }

    public class ErrorDetail
    {
        [JsonPropertyName("campo")]
        public string Campo { get; set; } = string.Empty;

        [JsonPropertyName("error")]
        public string Error { get; set; } = string.Empty;
    }

    public class EnviarEmailResponse
    {
        [JsonPropertyName("mensaje")]
        public string Mensaje { get; set; } = string.Empty;

        [JsonPropertyName("destinatario")]
        public string Destinatario { get; set; } = string.Empty;

        [JsonPropertyName("numeroFactura")]
        public string NumeroFactura { get; set; } = string.Empty;
    }

    public class Factura
    {
        private readonly CompraCompletaDto _compra;
        private readonly UsuarioDto _usuario;

        public Factura(CompraCompletaDto compra, UsuarioDto usuario)
        {
            _compra = compra;
            _usuario = usuario;
        }

        public string NombreArchivo()
        {
            string numeroFactura = $"001-001-{_compra.NumeroFactura:D9}";
            var numeroLimpio = numeroFactura.Replace("/", "-").Replace(" ", "_");
            return $"factura_{numeroLimpio}.xml";
        }

        public string CrearFormatoXML(RespuestaFacturaSri? autorizacion = null)
        {
            var detalleElements = new List<XElement>();
            decimal subtotalGlobal = 0m;

            foreach (var item in _compra.Detalles)
            {
                decimal subtotalItem = item.Subtotal;
                subtotalGlobal += subtotalItem;

                detalleElements.Add(
                    new XElement("Producto",
                        new XElement("Nombre", item.NombreProducto),
                        new XElement("Cantidad", item.Cantidad.ToString(System.Globalization.CultureInfo.InvariantCulture)),
                        new XElement("PrecioUnitario", item.PrecioUnitario.ToString("0.00", System.Globalization.CultureInfo.InvariantCulture)),
                        new XElement("Subtotal", subtotalItem.ToString("0.00", System.Globalization.CultureInfo.InvariantCulture))
                    )
                );
            }

            decimal iva = subtotalGlobal * 0.12m;
            decimal total = subtotalGlobal + iva;

            string numeroFactura = $"001-001-{_compra.NumeroFactura:D9}";
            var fechaEcuador = TimeZoneInfo.ConvertTimeFromUtc(
                DateTime.SpecifyKind(_compra.CreatedAt, DateTimeKind.Utc),
                TimeZoneInfo.FindSystemTimeZoneById(
                    OperatingSystem.IsWindows() ? "SA Pacific Standard Time" : "America/Guayaquil"
                )
            );
            string fechaFormateada = fechaEcuador.ToString("yyyy-MM-dd HH:mm");

            string nombreFactura = !string.IsNullOrWhiteSpace(_compra.NombreFactura) ? _compra.NombreFactura : _usuario.NombreCompleto;
            string cedulaFactura = !string.IsNullOrWhiteSpace(_compra.CedulaFactura) ? _compra.CedulaFactura : _usuario.Cedula;

            var elemento = new XElement("Factura",
                new XElement("Cabecera",
                    new XElement("Numero", numeroFactura),
                    new XElement("Fecha", fechaFormateada)
                ),
                new XElement("Cliente",
                    new XElement("Nombre", nombreFactura),
                    new XElement("Correo", _usuario.Email),
                    new XElement("Cedula", string.IsNullOrWhiteSpace(cedulaFactura) ? "S/N" : cedulaFactura),
                    new XElement("Telefono", string.IsNullOrWhiteSpace(_usuario.Telefono) ? "S/N" : _usuario.Telefono),
                    new XElement("Direccion", string.IsNullOrWhiteSpace(_usuario.Direccion) ? "S/N" : _usuario.Direccion)
                ),
                new XElement("Detalle", detalleElements),
                new XElement("Totales",
                    new XElement("Subtotal", subtotalGlobal.ToString("0.00", System.Globalization.CultureInfo.InvariantCulture)),
                    new XElement("IVA", iva.ToString("0.00", System.Globalization.CultureInfo.InvariantCulture)),
                    new XElement("Total", total.ToString("0.00", System.Globalization.CultureInfo.InvariantCulture))
                ),
                new XElement("Estado", "Generada"),
                new XElement("AutorizacionSRI",
                    new XElement("Estado", autorizacion?.Estado ?? "PENDIENTE"),
                    new XElement("ClaveAcceso", autorizacion?.ClaveAcceso ?? "S/N"),
                    new XElement("Mensaje", autorizacion?.Mensaje ?? "En espera de validación por el SRI")
                )
            );

            var doc = new XDocument(new XDeclaration("1.0", "utf-8", null), elemento);
            using var ms = new System.IO.MemoryStream();
            using var writer = System.Xml.XmlWriter.Create(ms, new System.Xml.XmlWriterSettings
            {
                Indent = true,
                Encoding = new System.Text.UTF8Encoding(false)
            });
            doc.Save(writer);
            writer.Flush();
            return System.Text.Encoding.UTF8.GetString(ms.ToArray());
        }
    }
}
