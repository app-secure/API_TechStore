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
            string totalStr = compra.TotalCompra.ToString("F2");
            string xmlDocSection = "";
            if (xmlContent != null)
            {
                string pdfCol = "";
                if (pdfBytes != null)
                {
                    pdfCol = @"<td style='width: 12px;'></td>
            <td style='width: 50%; padding: 12px; background-color: #ffffff; border: 1px solid #e2e8f0; border-radius: 6px; text-align: left;'>
              <span style='font-size: 16px; margin-right: 8px; vertical-align: middle;'>📄</span>
              <span style='font-size: 13px; font-weight: 600; color: #0f172a; vertical-align: middle;'>Archivo PDF</span>
              <div style='font-size: 11px; color: #64748b; margin-top: 4px; padding-left: 24px;'>Representación Impresa</div>
            </td>";
                }

                xmlDocSection = $@"<div style='margin-bottom: 24px;'>
        <h3 style='font-size: 13px; font-weight: 700; color: #475569; text-transform: uppercase; letter-spacing: 0.5px; margin-bottom: 12px;'>Documentos Adjuntos</h3>
        <table style='width: 100%; border-collapse: collapse;'>
          <tr>
            <td style='width: 50%; padding: 12px; background-color: #ffffff; border: 1px solid #e2e8f0; border-radius: 6px; text-align: left;'>
              <span style='font-size: 16px; margin-right: 8px; vertical-align: middle;'>📋</span>
              <span style='font-size: 13px; font-weight: 600; color: #0f172a; vertical-align: middle;'>Archivo XML</span>
              <div style='font-size: 11px; color: #64748b; margin-top: 4px; padding-left: 24px;'>Comprobante para el SRI</div>
            </td>
            {pdfCol}
          </tr>
        </table>
      </div>";
            }

            string htmlBody = $@"<!DOCTYPE html>
<html lang='es'>
<head>
  <meta charset='utf-8'/>
  <title>Comprobante de Pago Electrónico</title>
</head>
<body style='font-family: -apple-system, BlinkMacSystemFont, ""Segoe UI"", Roboto, Helvetica, Arial, sans-serif; background-color: #f8fafc; margin: 0; padding: 40px 20px; color: #334155;'>
  <div style='max-width: 600px; margin: 0 auto; background-color: #ffffff; border: 1px solid #e2e8f0; border-radius: 8px; overflow: hidden; box-shadow: 0 1px 3px 0 rgba(0, 0, 0, 0.05);'>
    
    <!-- Header: Solid Corporate Dark -->
    <div style='background-color: #0f172a; padding: 32px; text-align: left; border-bottom: 3px solid #0284c7;'>
      <table style='width: 100%; border-collapse: collapse;'>
        <tr>
          <td>
            <span style='font-size: 20px; font-weight: 800; color: #ffffff; letter-spacing: 0.5px;'>TECHSTORE360</span>
          </td>
          <td style='text-align: right;'>
            <span style='font-size: 11px; font-weight: 600; color: #94a3b8; text-transform: uppercase; letter-spacing: 1px;'>Comprobante Electrónico</span>
          </td>
        </tr>
      </table>
    </div>

    <!-- Body Content -->
    <div style='padding: 32px;'>
      <h2 style='font-size: 18px; font-weight: 600; color: #0f172a; margin-top: 0; margin-bottom: 16px;'>Estimado/a {System.Net.WebUtility.HtmlEncode(usuario.NombreCompleto)},</h2>
      <p style='font-size: 14px; line-height: 1.6; color: #475569; margin-bottom: 24px;'>
        Le confirmamos que su transacción ha sido procesada de manera exitosa. A continuación se detallan los datos correspondientes a su comprobante de compra.
      </p>

      <!-- Receipt Box -->
      <div style='border: 1px solid #e2e8f0; border-radius: 6px; padding: 20px; margin-bottom: 24px; background-color: #f8fafc;'>
        <table style='width: 100%; border-collapse: collapse; font-size: 14px;'>
          <tr>
            <td style='padding: 6px 0; color: #64748b; font-weight: 500;'>Número de Factura:</td>
            <td style='padding: 6px 0; color: #0f172a; font-weight: 700; text-align: right;'>{System.Net.WebUtility.HtmlEncode(codigoFactura)}</td>
          </tr>
          <tr>
            <td style='padding: 6px 0; color: #64748b; font-weight: 500;'>Método de Pago:</td>
            <td style='padding: 6px 0; color: #0f172a; font-weight: 600; text-align: right;'>{System.Net.WebUtility.HtmlEncode(compra.MetodoPago)}</td>
          </tr>
          <tr style='border-top: 1px solid #e2e8f0;'>
            <td style='padding: 12px 0 0 0; color: #0f172a; font-weight: 700; font-size: 15px;'>Monto Total:</td>
            <td style='padding: 12px 0 0 0; color: #0f172a; font-weight: 800; font-size: 20px; text-align: right;'>${totalStr}</td>
          </tr>
        </table>
      </div>

      <!-- Attached Documents Notice -->
      {xmlDocSection}

      <div style='border-top: 1px solid #e2e8f0; padding-top: 20px;'>
        <p style='font-size: 12px; color: #64748b; line-height: 1.5; margin: 0;'>
          Si tiene alguna duda o consulta respecto a esta transacción, por favor responda directamente a este correo electrónico.
        </p>
      </div>
    </div>

    <!-- Footer -->
    <div style='background-color: #f1f5f9; padding: 20px 32px; text-align: center; border-top: 1px solid #e2e8f0;'>
      <p style='font-size: 11px; color: #64748b; margin: 0; line-height: 1.5;'>
        <strong>TechStore360</strong> &bull; Departamento de Facturación Electrónica<br />
        Facultad de Ingeniería en Sistemas, Electrónica e Industrial &bull; UTA
      </p>
    </div>

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
