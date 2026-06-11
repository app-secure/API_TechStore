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
            string pdfLine = pdfBytes != null ? " y en PDF (adjunto)" : "";
            string xmlLine = xmlContent != null ? $" en formato XML (para el portal SRI){pdfLine}" : "";

            string htmlBody = $@"<!DOCTYPE html>
<html lang='es'>
<head><meta charset='utf-8'/></head>
<body style='font-family:Arial,sans-serif;background:#f4f7f9;margin:0;padding:20px;'>
  <div style='max-width:600px;margin:auto;background:white;border-radius:12px;overflow:hidden;box-shadow:0 4px 20px rgba(0,0,0,0.08);'>
    <div style='background:linear-gradient(135deg,#1E6B7A,#2A7F8F);padding:32px 28px;text-align:center;'>
      <h1 style='color:white;margin:0;font-size:28px;font-weight:800;letter-spacing:1px;'>TechStore360</h1>
      <p style='color:rgba(255,255,255,0.8);margin:8px 0 0;font-size:14px;'>Comprobante de compra electr&#xF3;nico</p>
    </div>
    <div style='padding:32px 28px;'>
      <p style='font-size:16px;color:#1A2F40;'>Hola, <strong>{System.Net.WebUtility.HtmlEncode(usuario.NombreCompleto)}</strong></p>
      <p style='color:#6B7A8D;line-height:1.6;'>Tu compra fue procesada exitosamente.{(xmlContent != null ? $" Adjunto encontrar&#xE1;s tu factura{xmlLine}." : " Puedes ver el detalle de tu compra en la app.")}</p>
      <div style='background:#f0f9ff;border:1px solid #b8e0ea;border-radius:10px;padding:18px 20px;margin:24px 0;'>
        <table style='width:100%;border-collapse:collapse;'>
          <tr>
            <td style='padding:6px 0;color:#6B7A8D;font-size:13px;'>N&#xB0; Factura</td>
            <td style='padding:6px 0;color:#1A2F40;font-weight:700;text-align:right;'>{System.Net.WebUtility.HtmlEncode(codigoFactura)}</td>
          </tr>
          <tr>
            <td style='padding:6px 0;color:#6B7A8D;font-size:13px;'>Total pagado</td>
            <td style='padding:6px 0;color:#2A7F8F;font-weight:800;font-size:18px;text-align:right;'>${totalStr}</td>
          </tr>
          <tr>
            <td style='padding:6px 0;color:#6B7A8D;font-size:13px;'>M&#xE9;todo de pago</td>
            <td style='padding:6px 0;color:#1A2F40;font-weight:600;text-align:right;'>{System.Net.WebUtility.HtmlEncode(compra.MetodoPago)}</td>
          </tr>
        </table>
      </div>
      {(xmlContent != null ? $@"<table style='width:100%;margin:0 0 24px;border-collapse:collapse;'>
        <tr>
          <td style='width:50%;padding:12px;background:#f8f9fa;border-radius:8px;text-align:center;'>
            <div style='font-size:26px;'>&#x1F4CB;</div>
            <div style='font-weight:700;margin-top:6px;color:#1A2F40;'>XML</div>
            <div style='font-size:11px;color:#6B7A8D;'>Portal SRI Ecuador</div>
          </td>
          {(pdfBytes != null ? @"<td style='width:8px;'></td>
          <td style='width:50%;padding:12px;background:#f8f9fa;border-radius:8px;text-align:center;'>
            <div style='font-size:26px;'>&#x1F4C4;</div>
            <div style='font-weight:700;margin-top:6px;color:#1A2F40;'>PDF</div>
            <div style='font-size:11px;color:#6B7A8D;'>Imprimir o archivar</div>
          </td>" : "")}
        </tr>
      </table>" : "")}
      <p style='color:#aaa;font-size:11px;'>Si tienes alguna consulta responde a este correo.</p>
    </div>
    <div style='background:#f8f9fa;padding:16px;text-align:center;font-size:10px;color:#bbb;'>
      TechStore360 &mdash; FISEI UTA &mdash; Aplicaciones Distribuidas
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
