using System;
using System.Collections.Generic;
using System.Xml.Linq;
using System.Threading;
using System.Threading.Tasks;
using TechStore360.ExternalServices;
using TechStore360.Modules.Compras;
using TechStore360.Modules.Productos;
using TechStore360.Modules.Usuarios;

namespace TechStore360.Modulos.Factura
{
    public interface IFacturacionService
    {
        Task<List<ErrorDetail>> ValidarDatosFacturacion(GenerarFacturaRequest request, CancellationToken ct = default);
        Task<(string XmlFactura, string NombreArchivo)> GenerarFacturaXML(int idCompra, CancellationToken ct = default);
        Task<(byte[] PdfBytes, string NombreArchivo)> GenerarFacturaPdfAsync(int idCompra, CancellationToken ct = default);
        Task<RespuestaFacturaSri> ConsultarComprobanteAsync(int idCompra, CancellationToken ct = default);
        Task EnviarFacturaPorEmailAsync(int idCompra, CancellationToken ct = default);
    }

    public class FacturacionService : IFacturacionService
    {
        private readonly IComprasService _comprasService;
        private readonly IUsuariosService _usuariosService;
        private readonly ISriSoapService _sriService;
        private readonly IPdfFacturaService _pdfService;
        private readonly NotificationEmailService _notificationEmail;

        public FacturacionService(
            IComprasService comprasService,
            IUsuariosService usuariosService,
            ISriSoapService sriService,
            IPdfFacturaService pdfService,
            NotificationEmailService notificationEmail)
        {
            _comprasService = comprasService;
            _usuariosService = usuariosService;
            _sriService = sriService;
            _pdfService = pdfService;
            _notificationEmail = notificationEmail;
        }

        public async Task<List<ErrorDetail>> ValidarDatosFacturacion(GenerarFacturaRequest request, CancellationToken ct = default)
        {
            var errores = new List<ErrorDetail>();

            if (request.IdCompra <= 0)
            {
                errores.Add(new ErrorDetail { Campo = "idCompra", Error = "El ID de la compra es inválido." });
                return errores;
            }

            var compra = await _comprasService.ObtenerDetalleCompraAsync(request.IdCompra, ct);
            if (compra == null)
            {
                errores.Add(new ErrorDetail { Campo = "idCompra", Error = "La compra no existe." });
            }

            return errores;
        }

        public async Task<(string XmlFactura, string NombreArchivo)> GenerarFacturaXML(int idCompra, CancellationToken ct = default)
        {
            var compra = await _comprasService.ObtenerDetalleCompraAsync(idCompra, ct);
            if (compra == null) throw new ArgumentException("Compra no encontrada");

            var usuario = await _usuariosService.ObtenerUsuarioParaFacturaAsync(compra.IdUsuario, ct);
            if (usuario == null) throw new ArgumentException("Usuario no encontrado");

            var datosFactura = new Factura(compra, usuario);
            var factura = datosFactura.CrearFormatoXML();
            var facturaValida = _sriService.GenerarFacturaXML(factura);

            if (facturaValida.Estado == "RECHAZADA")
                throw new InvalidOperationException($"El XML de la factura fue rechazado: {facturaValida.Mensaje}");

            return (datosFactura.CrearFormatoXML(facturaValida), datosFactura.NombreArchivo());
        }

        public async Task<(byte[] PdfBytes, string NombreArchivo)> GenerarFacturaPdfAsync(int idCompra, CancellationToken ct = default)
        {
            var compra = await _comprasService.ObtenerDetalleCompraAsync(idCompra, ct)
                ?? throw new InvalidOperationException($"La compra #{idCompra} no existe.");

            if (!compra.RequiereFactura)
                throw new InvalidOperationException("Esta compra no requiere factura legal. Solo se puede generar nota de venta.");

            var usuario = await _usuariosService.ObtenerUsuarioParaFacturaAsync(compra.IdUsuario, ct)
                ?? throw new InvalidOperationException("Usuario asociado a la compra no encontrado.");

            var pdfBytes = _pdfService.GenerarPdf(compra, usuario);
            string nombreArchivo = $"factura_001-001-{compra.NumeroFactura:D9}.pdf";
            return (pdfBytes, nombreArchivo);
        }

        public async Task EnviarFacturaPorEmailAsync(int idCompra, CancellationToken ct = default)
        {
            var compra = await _comprasService.ObtenerDetalleCompraAsync(idCompra, ct)
                ?? throw new InvalidOperationException($"La compra #{idCompra} no existe.");

            var usuario = await _usuariosService.ObtenerUsuarioParaFacturaAsync(compra.IdUsuario, ct)
                ?? throw new InvalidOperationException("Usuario asociado a la compra no encontrado.");

            // Intentar generar XML — si falla (SRI rechazo, etc.) se omite el adjunto
            string? xmlContent = null;
            string? nombreXml = null;
            try
            {
                var (xml, nombre) = await GenerarFacturaXML(idCompra, ct);
                xmlContent = xml;
                nombreXml = nombre;
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[Email] Advertencia: no se pudo generar XML para compra #{idCompra}: {ex.Message}");
            }

            // Intentar generar PDF si aplica
            byte[]? pdfBytes = null;
            if (compra.RequiereFactura)
            {
                try { pdfBytes = _pdfService.GenerarPdf(compra, usuario); }
                catch (Exception ex)
                {
                    Console.WriteLine($"[Email] Advertencia: no se pudo generar PDF para compra #{idCompra}: {ex.Message}");
                }
            }

            string codigoFactura = $"001-001-{compra.NumeroFactura:D9}";
            var fechaEcuador = TimeZoneInfo.ConvertTimeFromUtc(
                DateTime.SpecifyKind(compra.CreatedAt, DateTimeKind.Utc),
                TimeZoneInfo.FindSystemTimeZoneById(
                    OperatingSystem.IsWindows() ? "SA Pacific Standard Time" : "America/Guayaquil"
                )
            );
            string fechaFormateada = fechaEcuador.ToString("yyyy-MM-dd HH:mm");

            string nombreFactura = !string.IsNullOrWhiteSpace(compra.NombreFactura) ? compra.NombreFactura : usuario.NombreCompleto;
            string cedulaFactura = !string.IsNullOrWhiteSpace(compra.CedulaFactura) ? compra.CedulaFactura : usuario.Cedula;
            string direccionFactura = !string.IsNullOrWhiteSpace(usuario.Direccion) ? usuario.Direccion : "S/N";
            string telefonoFactura = !string.IsNullOrWhiteSpace(usuario.Telefono) ? usuario.Telefono : "S/N";

            var filasDetalle = new System.Text.StringBuilder();
            foreach (var item in compra.Detalles)
            {
                filasDetalle.Append($@"
        <tr>
          <td style='padding: 8px; border: 1px solid #dddddd; text-align: left;'>{System.Net.WebUtility.HtmlEncode(item.NombreProducto)}</td>
          <td style='padding: 8px; border: 1px solid #dddddd; text-align: center;'>{item.Cantidad}</td>
          <td style='padding: 8px; border: 1px solid #dddddd; text-align: right;'>${item.PrecioUnitario:F2}</td>
          <td style='padding: 8px; border: 1px solid #dddddd; text-align: right;'>${item.Subtotal:F2}</td>
        </tr>");
            }

            string htmlBody = $@"<!DOCTYPE html>
<html lang='es'>
<head>
  <meta charset='utf-8'/>
  <title>Factura Electrónica TechStore360</title>
</head>
<body style='font-family: Arial, sans-serif; font-size: 13px; color: #333333; line-height: 1.5; background-color: #f9f9f9; padding: 20px; margin: 0;'>
  <div style='max-width: 650px; margin: 0 auto; background-color: #ffffff; border: 1px solid #dddddd; padding: 30px; border-radius: 4px;'>
    
    <!-- Top info -->
    <table style='width: 100%; border-collapse: collapse; margin-bottom: 20px;'>
      <tr>
        <td style='vertical-align: top;'>
          <div style='font-size: 20px; font-weight: bold; color: #111111;'>TECHSTORE360</div>
          <div style='font-size: 11px; color: #666666; margin-top: 5px;'>
            Universidad Técnica de Ambato<br/>
            Facultad de Ingeniería en Sistemas, Electrónica e Industrial<br/>
            Ambato - Ecuador
          </div>
        </td>
        <td style='text-align: right; vertical-align: top;'>
          <div style='font-size: 14px; font-weight: bold; color: #111111; text-transform: uppercase;'>Factura Electrónica</div>
          <div style='font-size: 13px; font-weight: bold; color: #333333; margin-top: 5px;'>No. {codigoFactura}</div>
          <div style='font-size: 11px; color: #666666; margin-top: 3px;'>Fecha Emisión: {fechaFormateada}</div>
        </td>
      </tr>
    </table>

    <div style='border-top: 2px solid #333333; margin: 15px 0;'></div>

    <!-- Client Info -->
    <h3 style='font-size: 13px; font-weight: bold; margin-bottom: 10px; color: #111111; text-transform: uppercase;'>Información del Cliente</h3>
    <table style='width: 100%; border-collapse: collapse; margin-bottom: 20px;'>
      <tr>
        <td style='width: 50%; padding: 4px 0; vertical-align: top;'>
          <strong>Cliente:</strong> {System.Net.WebUtility.HtmlEncode(nombreFactura)}
        </td>
        <td style='width: 50%; padding: 4px 0; vertical-align: top;'>
          <strong>Cédula/RUC:</strong> {System.Net.WebUtility.HtmlEncode(cedulaFactura)}
        </td>
      </tr>
      <tr>
        <td style='padding: 4px 0; vertical-align: top;'>
          <strong>Dirección:</strong> {System.Net.WebUtility.HtmlEncode(direccionFactura)}
        </td>
        <td style='padding: 4px 0; vertical-align: top;'>
          <strong>Teléfono:</strong> {System.Net.WebUtility.HtmlEncode(telefonoFactura)}
        </td>
      </tr>
      <tr>
        <td style='padding: 4px 0; vertical-align: top;'>
          <strong>Correo:</strong> {System.Net.WebUtility.HtmlEncode(usuario.Email)}
        </td>
        <td style='padding: 4px 0; vertical-align: top;'>
          <strong>Método de Pago:</strong> {System.Net.WebUtility.HtmlEncode(compra.MetodoPago)}
        </td>
      </tr>
    </table>

    <div style='border-top: 1px solid #dddddd; margin: 15px 0;'></div>

    <!-- Product Details -->
    <h3 style='font-size: 13px; font-weight: bold; margin-bottom: 10px; color: #111111; text-transform: uppercase;'>Detalle de la Compra</h3>
    <table style='width: 100%; border-collapse: collapse; margin-bottom: 20px;'>
      <thead>
        <tr style='background-color: #f2f2f2;'>
          <th style='padding: 8px; border: 1px solid #dddddd; text-align: left;'>Descripción</th>
          <th style='padding: 8px; border: 1px solid #dddddd; text-align: center; width: 60px;'>Cant.</th>
          <th style='padding: 8px; border: 1px solid #dddddd; text-align: right; width: 90px;'>P. Unitario</th>
          <th style='padding: 8px; border: 1px solid #dddddd; text-align: right; width: 90px;'>Total</th>
        </tr>
      </thead>
      <tbody>
        {filasDetalle}
      </tbody>
    </table>

    <!-- Totals -->
    <table style='width: 250px; margin-left: auto; border-collapse: collapse; margin-bottom: 30px;'>
      <tr>
        <td style='padding: 6px; text-align: left;'>Subtotal:</td>
        <td style='padding: 6px; text-align: right; font-weight: bold;'>${compra.Subtotal:F2}</td>
      </tr>
      <tr>
        <td style='padding: 6px; text-align: left;'>IVA (15%):</td>
        <td style='padding: 6px; text-align: right; font-weight: bold;'>${compra.Iva:F2}</td>
      </tr>
      <tr style='border-top: 1px solid #333333;'>
        <td style='padding: 8px 6px; text-align: left; font-weight: bold; font-size: 14px;'>Total a Pagar:</td>
        <td style='padding: 8px 6px; text-align: right; font-weight: bold; font-size: 14px;'>${compra.TotalCompra:F2}</td>
      </tr>
    </table>

    <div style='border-top: 1px solid #dddddd; margin: 15px 0;'></div>

    <!-- Footer note -->
    <p style='font-size: 11px; color: #666666; line-height: 1.5; margin: 0;'>
      <strong>Nota:</strong> Los archivos correspondientes a su Comprobante de Venta en formato XML para el SRI y su representación impresa en formato PDF se encuentran adjuntos a este correo electrónico.
    </p>
    <p style='font-size: 11px; color: #999999; text-align: center; margin-top: 25px;'>
      TechStore360 — Facturación Electrónica. Todos los derechos reservados.
    </p>

  </div>
</body>
</html>";

            var adjuntos = new List<(string Nombre, string ContentBase64, string MimeType)>();
            if (xmlContent != null && nombreXml != null)
            {
                adjuntos.Add((nombreXml, Convert.ToBase64String(System.Text.Encoding.UTF8.GetBytes(xmlContent)), "application/xml"));
            }
            if (pdfBytes != null)
            {
                string nombrePdf = $"factura_{codigoFactura.Replace("-", "_")}.pdf";
                adjuntos.Add((nombrePdf, Convert.ToBase64String(pdfBytes), "application/pdf"));
            }

            bool enviado = false;
            try
            {
                enviado = await _notificationEmail.EnviarEmailDirectoAsync(
                    emailDestino: usuario.Email,
                    nombreDestino: usuario.NombreCompleto,
                    asunto: $"TechStore360 — Tu compra {codigoFactura} fue confirmada",
                    htmlBody: htmlBody,
                    adjuntos: adjuntos.Count > 0 ? adjuntos : null);
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[Brevo] Error al enviar email: {ex.Message}");
            }

            if (!enviado)
            {
                throw new InvalidOperationException("No se pudo enviar el correo a través de Brevo.");
            }
        }

        public Task<RespuestaFacturaSri> ConsultarComprobanteAsync(int idCompra, CancellationToken ct = default)
        {
            var respuesta = _sriService.ConsultarComprobante(idCompra);
            return Task.FromResult(respuesta);
        }
    }
}
