using Microsoft.AspNetCore.Mvc;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using TechStore360.ExternalServices;
using TechStore360.Modules.Usuarios;

namespace TechStore360.Modulos.Factura
{
    [ApiController]
    [Route("api/[controller]")]
    public class FacturacionController : ControllerBase
    {
        private readonly IFacturacionService _service;
        private readonly IUsuariosService _usuariosService;

        public FacturacionController(IFacturacionService service, IUsuariosService usuariosService)
        {
            _service = service;
            _usuariosService = usuariosService;
        }

        [HttpPost]
        public async Task<IActionResult> GenerarFacturaXML([FromBody] GenerarFacturaRequest request)
        {
            var errores = await _service.ValidarDatosFacturacion(request);
            if (errores.Any())
                return BadRequest(new ValidationErrorResponse { Errores = errores });

            try
            {
                var (xmlFinal, _) = await _service.GenerarFacturaXML(request.IdCompra);
                return Content(xmlFinal, "application/xml", System.Text.Encoding.UTF8);
            }
            catch (InvalidOperationException ex)
            {
                return UnprocessableEntity(new { mensaje = ex.Message });
            }
        }

        [HttpPost("descargar")]
        public async Task<IActionResult> DescargarFacturaXML([FromBody] GenerarFacturaRequest request)
        {
            var errores = await _service.ValidarDatosFacturacion(request);
            if (errores.Any())
                return BadRequest(new ValidationErrorResponse { Errores = errores });

            try
            {
                var (xmlFinal, nombreArchivo) = await _service.GenerarFacturaXML(request.IdCompra);
                Response.Headers.Append("Content-Disposition", $"attachment; filename={nombreArchivo}");
                return Content(xmlFinal, "application/xml", System.Text.Encoding.UTF8);
            }
            catch (InvalidOperationException ex)
            {
                return UnprocessableEntity(new { mensaje = ex.Message });
            }
        }

        [HttpGet("{idCompra:int}/xml")]
        public async Task<IActionResult> ObtenerXml(int idCompra, CancellationToken ct)
        {
            try
            {
                var (xmlFinal, _) = await _service.GenerarFacturaXML(idCompra, ct);
                return Content(xmlFinal, "application/xml", System.Text.Encoding.UTF8);
            }
            catch (InvalidOperationException ex)
            {
                return UnprocessableEntity(new { mensaje = ex.Message });
            }
        }

        [HttpGet("{idCompra:int}/xml/descargar")]
        public async Task<IActionResult> DescargarXml(int idCompra, CancellationToken ct)
        {
            try
            {
                var (xmlFinal, nombreArchivo) = await _service.GenerarFacturaXML(idCompra, ct);
                var bytes = System.Text.Encoding.UTF8.GetBytes(xmlFinal);
                return File(bytes, "application/xml", nombreArchivo);
            }
            catch (InvalidOperationException ex)
            {
                return UnprocessableEntity(new { mensaje = ex.Message });
            }
        }

        [HttpGet("{idCompra:int}/pdf")]
        public async Task<IActionResult> ObtenerPdf(int idCompra, CancellationToken ct)
        {
            try
            {
                var (pdfBytes, _) = await _service.GenerarFacturaPdfAsync(idCompra, ct);
                return File(pdfBytes, "application/pdf");
            }
            catch (InvalidOperationException ex)
            {
                return UnprocessableEntity(new { mensaje = ex.Message });
            }
        }

        [HttpGet("{idCompra:int}/pdf/descargar")]
        public async Task<IActionResult> DescargarPdf(int idCompra, CancellationToken ct)
        {
            try
            {
                var (pdfBytes, nombreArchivo) = await _service.GenerarFacturaPdfAsync(idCompra, ct);
                return File(pdfBytes, "application/pdf", nombreArchivo);
            }
            catch (InvalidOperationException ex)
            {
                return UnprocessableEntity(new { mensaje = ex.Message });
            }
        }

        [HttpPost("{idCompra:int}/enviar-email")]
        public async Task<IActionResult> EnviarPorEmail(int idCompra, CancellationToken ct)
        {
            try
            {
                await _service.EnviarFacturaPorEmailAsync(idCompra, ct);

                return Ok(new EnviarEmailResponse
                {
                    Mensaje = "Factura enviada correctamente al correo del cliente.",
                    NumeroFactura = $"001-001-{idCompra:D9}"
                });
            }
            catch (InvalidOperationException ex)
            {
                return UnprocessableEntity(new { mensaje = ex.Message });
            }
            catch (Exception ex)
            {
                return StatusCode(502, new { mensaje = $"Error al enviar el correo: {ex.Message}" });
            }
        }

        [HttpGet("consultar/{idCompra}")]
        public async Task<IActionResult> ConsultarComprobante(int idCompra)
        {
            try
            {
                var respuesta = await _service.ConsultarComprobanteAsync(idCompra);
                return Ok(respuesta);
            }
            catch (Exception ex)
            {
                return UnprocessableEntity(new { mensaje = ex.Message });
            }
        }
    }
}
