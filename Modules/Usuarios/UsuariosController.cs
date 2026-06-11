using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using System.Threading;
using System.Threading.Tasks;

namespace TechStore360.Modules.Usuarios
{
    [ApiController]
    [Route("api/[controller]")]
    public class UsuariosController : ControllerBase
    {
        private readonly IUsuariosService _service;
        private readonly IAuthorizationService _authorizationService;

        public UsuariosController(IUsuariosService service, IAuthorizationService authorizationService)
        {
            _service = service;
            _authorizationService = authorizationService;
        }

        [HttpPost("login")]
        [AllowAnonymous]
        public async Task<IActionResult> Login([FromBody] LoginRequest request, CancellationToken ct)
        {
            try
            {
                var result = await _service.LoginAsync(request, ct);
                return Ok(result);
            }
            catch (ArgumentException ex)
            {
                return BadRequest(new { mensaje = ex.Message });
            }
        }

        [HttpPost("registro")]
        [AllowAnonymous]
        public async Task<IActionResult> Registro([FromBody] RegistroRequest request, CancellationToken ct)
        {
            try
            {
                var result = await _service.RegistrarUsuarioAsync(request, ct);
                return Created($"/api/usuarios/{result.IdUsuario}", result);
            }
            catch (ArgumentException ex)
            {
                return BadRequest(new { mensaje = ex.Message });
            }
        }

        [HttpGet]
        [Authorize(Policy = "Admin")]
        public async Task<IActionResult> ObtenerTodos(CancellationToken ct)
        {
            var usuarios = await _service.ListarUsuariosAsync(ct);
            return Ok(usuarios);
        }

        [HttpGet("cedula/{cedula}")]
        [Authorize(Policy = "Admin")]
        public async Task<IActionResult> ObtenerPorCedula(string cedula, CancellationToken ct)
        {
            var usuario = await _service.ObtenerUsuarioPorCedulaAsync(cedula, ct);
            if (usuario == null)
            {
                return NotFound(new { mensaje = "Usuario no encontrado." });
            }
            return Ok(usuario);
        }

        [HttpPut("{idUsuario}")]
        [Authorize(Policy = "Admin")]
        public async Task<IActionResult> Actualizar(string idUsuario, [FromBody] ActualizarUsuarioRequest request, CancellationToken ct)
        {
            var usuario = await _service.ActualizarUsuarioAsync(idUsuario, request, ct);
            if (usuario == null)
            {
                return NotFound(new { mensaje = "Usuario no encontrado o inactivo." });
            }
            return Ok(usuario);
        }

        [HttpDelete("{idUsuario}")]
        [Authorize(Policy = "Admin")]
        public async Task<IActionResult> Eliminar(string idUsuario, CancellationToken ct)
        {
            var eliminado = await _service.EliminarUsuarioAsync(idUsuario, ct);
            if (!eliminado)
            {
                return NotFound(new { mensaje = "Usuario no encontrado o ya inactivo." });
            }
            return Ok(new { mensaje = "Se ha eliminado el usuario" });
        }

        [HttpPost("{idUsuario}/activar")]
        [Authorize(Policy = "Admin")]
        public async Task<IActionResult> Activar(string idUsuario, CancellationToken ct)
        {
            var activado = await _service.ReactivarUsuarioAsync(idUsuario, ct);
            if (!activado)
            {
                return NotFound(new { mensaje = "Usuario no encontrado o ya activo." });
            }
            return Ok(new { mensaje = "Se ha activado el usuario" });
        }

        [HttpGet("{idUsuario}")]
        [Authorize]
        public async Task<IActionResult> ObtenerPorId(string idUsuario, CancellationToken ct)
        {
            var currentUid = User.FindFirst("user_id")?.Value;
            var esAdmin = (await _authorizationService.AuthorizeAsync(User, "Admin")).Succeeded;

            if (currentUid != idUsuario && !esAdmin)
            {
                return Forbid();
            }

            var usuario = await _service.ObtenerUsuarioAsync(idUsuario, ct);
            if (usuario == null)
            {
                return NotFound(new { mensaje = "Usuario no encontrado." });
            }
            return Ok(usuario);
        }

        [HttpPut("{idUsuario}/perfil")]
        [Authorize]
        public async Task<IActionResult> EditarPerfil(string idUsuario, [FromBody] EditarPerfilRequest request, CancellationToken ct)
        {
            var currentUid = User.FindFirst("user_id")?.Value;
            if (currentUid != idUsuario)
            {
                return Forbid();
            }

            var usuario = await _service.EditarPerfilAsync(idUsuario, request, ct);
            if (usuario == null)
            {
                return NotFound(new { mensaje = "Usuario no encontrado." });
            }
            return Ok(usuario);
        }
    }
}
