using WkHtmlToPdfDotNet;
using WkHtmlToPdfDotNet.Contracts;
using TechStore360.Modules.Compras;
using TechStore360.Modules.Usuarios;

namespace TechStore360.ExternalServices;

public interface IPdfFacturaService
{
    byte[] GenerarPdf(CompraCompletaDto compra, UsuarioDto usuario);
}

public class PdfFacturaService : IPdfFacturaService
{
    private readonly IConverter _converter;

    public PdfFacturaService(IConverter converter)
    {
        _converter = converter;
    }

    public byte[] GenerarPdf(CompraCompletaDto compra, UsuarioDto usuario)
    {
        var html = GenerarHtml(compra, usuario);
        var doc = new HtmlToPdfDocument
        {
            GlobalSettings = new GlobalSettings
            {
                ColorMode = ColorMode.Color,
                Orientation = Orientation.Portrait,
                PaperSize = PaperKind.A4,
                Margins = new MarginSettings { Top = 12, Bottom = 12, Left = 12, Right = 12, Unit = Unit.Millimeters }
            },
            Objects =
            {
                new ObjectSettings
                {
                    HtmlContent = html,
                    WebSettings = new WebSettings { DefaultEncoding = "utf-8" }
                }
            }
        };
        return _converter.Convert(doc);
    }

    private static string GenerarHtml(CompraCompletaDto compra, UsuarioDto usuario)
    {
        string numeroFactura = $"001-001-{compra.NumeroFactura:D9}";
        var fechaEcuador = TimeZoneInfo.ConvertTimeFromUtc(
            DateTime.SpecifyKind(compra.CreatedAt, DateTimeKind.Utc),
            TimeZoneInfo.FindSystemTimeZoneById(
                OperatingSystem.IsWindows() ? "SA Pacific Standard Time" : "America/Guayaquil"
            )
        );

        string nombreFacturacion = !string.IsNullOrWhiteSpace(compra.NombreFactura)
            ? compra.NombreFactura
            : usuario.NombreCompleto;

        string cedulaFacturacion = !string.IsNullOrWhiteSpace(compra.CedulaFactura)
            ? compra.CedulaFactura
            : usuario.Cedula;

        string estadoFactura = (compra.Estado == "ABIERTA" || compra.Estado == "PAGADA" || compra.Estado == "VALIDADA") ? "Validada" : (compra.Estado == "PENDIENTE_PAGO" ? "PENDIENTE" : compra.Estado);
        string metodoPagoUpper = string.IsNullOrWhiteSpace(compra.MetodoPago) || compra.MetodoPago.Equals("PayPhone", StringComparison.OrdinalIgnoreCase)
            ? "PAYPAL"
            : compra.MetodoPago.ToUpper();

        var filas = new System.Text.StringBuilder();
        foreach (var item in compra.Detalles)
        {
            filas.Append($@"
              <tr>
                <td>{System.Net.WebUtility.HtmlEncode(item.NombreProducto)}</td>
                <td style='text-align:center'>{item.Cantidad}</td>
                <td style='text-align:right'>${item.PrecioUnitario:F2}</td>
                <td style='text-align:right'>${item.Subtotal:F2}</td>
              </tr>");
        }

        return $@"<!DOCTYPE html>
<html lang='es'>
<head>
<meta charset='utf-8'/>
<style>
  * {{ box-sizing: border-box; margin: 0; padding: 0; }}
  body {{ font-family: 'Courier New', Courier, monospace; font-size: 13px; color: #000; padding: 30px; background: white; }}
  .invoice-container {{ max-width: 650px; margin: 0 auto; }}
  .header-title {{ text-align: center; font-size: 16px; font-weight: bold; text-transform: uppercase; margin-bottom: 5px; }}
  .dashed-line {{ border-top: 1px dashed #000; margin: 10px 0; }}
  .table-info {{ width: 100%; border-collapse: collapse; margin: 5px 0; }}
  .table-info td {{ padding: 3px 0; font-size: 13px; vertical-align: top; }}
  .table-info td.lbl {{ width: 160px; font-weight: bold; }}
  .table-products {{ width: 100%; border-collapse: collapse; margin: 10px 0; }}
  .table-products th {{ border-bottom: 1px dashed #000; padding: 6px 0; text-align: left; font-size: 13px; font-weight: bold; }}
  .table-products td {{ padding: 6px 0; font-size: 13px; }}
  .totales-wrap {{ width: 280px; margin-left: auto; margin-top: 10px; }}
  .totales-row {{ display: flex; justify-content: space-between; padding: 3px 0; }}
  .totales-row.grand {{ font-weight: bold; border-top: 1px dashed #000; margin-top: 5px; padding-top: 5px; font-size: 14px; }}
  .sri-box {{ font-size: 11px; text-align: center; margin-top: 20px; }}
</style>
</head>
<body>

<div class='invoice-container'>
  <div class='header-title'>TECHSTORE360 - FACTURA ELECTRONICA</div>
  <div class='dashed-line'></div>

  <table class='table-info'>
    <tr><td class='lbl'>Clave de Acceso:</td><td>FAC-COMPRA-{compra.NumeroFactura:D9}</td></tr>
    <tr><td class='lbl'>Estado Factura:</td><td>{System.Net.WebUtility.HtmlEncode(estadoFactura)}</td></tr>
    <tr><td class='lbl'>Codigo de Pago:</td><td>PAGO-{metodoPagoUpper}-{compra.NumeroFactura:D9}</td></tr>
  </table>

  <div class='dashed-line'></div>

  <table class='table-info'>
    <tr><td class='lbl'>Cliente:</td><td>{System.Net.WebUtility.HtmlEncode(nombreFacturacion)}</td></tr>
    <tr><td class='lbl'>Cedula:</td><td>{(string.IsNullOrWhiteSpace(cedulaFacturacion) ? "S/N" : cedulaFacturacion)}</td></tr>
    <tr><td class='lbl'>Fecha:</td><td>{fechaEcuador:yyyy-MM-dd}</td></tr>
    <tr><td class='lbl'>Hora:</td><td>{fechaEcuador:HH:mm}</td></tr>
    <tr><td class='lbl'>Dirección:</td><td>{(string.IsNullOrWhiteSpace(usuario.Direccion) ? "S/N" : System.Net.WebUtility.HtmlEncode(usuario.Direccion))}</td></tr>
  </table>

  <div class='dashed-line'></div>

  <table class='table-products'>
    <thead>
      <tr>
        <th>Producto</th>
        <th style='text-align:center;width:60px'>Cant.</th>
        <th style='text-align:right;width:90px'>P. Unit.</th>
        <th style='text-align:right;width:90px'>Subtotal</th>
      </tr>
    </thead>
    <tbody>
      {filas}
    </tbody>
  </table>

  <div class='dashed-line'></div>

  <div class='totales-wrap'>
    <div class='totales-row'><span>Subtotal:</span><span>${compra.Subtotal:F2}</span></div>
    <div class='totales-row'><span>IVA (15%):</span><span>${compra.Iva:F2}</span></div>
    <div class='totales-row grand'><span>TOTAL A PAGAR:</span><span>${compra.TotalCompra:F2}</span></div>
  </div>

  <div class='dashed-line'></div>
  
  <div class='sri-box'>
    DOCUMENTO GENERADO ELECTRÓNICAMENTE. VÁLIDO COMO COMPROBANTE DE VENTA.<br>
    GRACIAS POR SU COMPRA EN TECHSTORE360.
  </div>
</div>

</body>
</html>";
    }
}
