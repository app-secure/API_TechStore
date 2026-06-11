using System;
using System.Threading;
using System.Threading.Tasks;

namespace TechStore360.Modules.Usuarios
{
    public record UsuarioDto(
        string IdUsuario,
        string NombreCompleto,
        string Email,
        string Cedula,
        string Telefono,
        string Direccion,
        string Rol,
        bool Estado
    );

    public record LoginRequest(
        string Email,
        string Password
    );

    public record RegistroRequest(
        string Email,
        string Password,
        string NombreCompleto,
        string Cedula,
        string Telefono,
        string Direccion
    );

    public record ActualizarUsuarioRequest(
        string? NombreCompleto,
        string? Cedula,
        string? Telefono,
        string? Direccion
    );

    public record EditarPerfilRequest(
        string? NombreCompleto,
        string? Cedula,
        string? Telefono,
        string? Direccion
    );

    public record AuthResult(
        string IdUsuario,
        string Email,
        string Token,
        string Rol = "USUARIO"
    );

    public interface IAuthenticationProvider
    {
        Task<AuthResult> LoginAsync(string email, string password, CancellationToken ct = default);
        Task<AuthResult> RegisterAsync(string email, string password, CancellationToken ct = default);
    }
}
