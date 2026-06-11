using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace TechStore360.Modules.Usuarios
{
    public interface IUsuariosService
    {
        Task<AuthResult> LoginAsync(LoginRequest request, CancellationToken ct = default);
        Task<UsuarioDto> RegistrarUsuarioAsync(RegistroRequest request, CancellationToken ct = default);
        Task<UsuarioDto?> ObtenerUsuarioAsync(string idUsuario, CancellationToken ct = default);
        Task<UsuarioDto?> ObtenerUsuarioParaFacturaAsync(string idUsuario, CancellationToken ct = default);
        Task<UsuarioDto?> ObtenerUsuarioPorCedulaAsync(string cedula, CancellationToken ct = default);
        Task<List<UsuarioDto>> ListarUsuariosAsync(CancellationToken ct = default);
        Task<List<UsuarioDto>> ListarUsuariosInactivosAsync(CancellationToken ct = default);
        Task<UsuarioDto?> ActualizarUsuarioAsync(string idUsuario, ActualizarUsuarioRequest request, CancellationToken ct = default);
        Task<UsuarioDto?> EditarPerfilAsync(string idUsuario, EditarPerfilRequest request, CancellationToken ct = default);
        Task<bool> EliminarUsuarioAsync(string idUsuario, CancellationToken ct = default);
        Task<bool> ReactivarUsuarioAsync(string idUsuario, CancellationToken ct = default);
    }

    public class UsuariosService : IUsuariosService
    {
        private readonly IUsuariosRepository _repository;
        private readonly IAuthenticationProvider _authProvider;

        public UsuariosService(IUsuariosRepository repository, IAuthenticationProvider authProvider)
        {
            _repository = repository;
            _authProvider = authProvider;
        }

        public async Task<AuthResult> LoginAsync(LoginRequest request, CancellationToken ct = default)
        {
            var authResult = await _authProvider.LoginAsync(request.Email, request.Password, ct);
            var usuario = await _repository.GetByIdAsync(authResult.IdUsuario, ct);
            var rol = usuario?.Rol ?? "USUARIO";
            return authResult with { Rol = rol };
        }

        public async Task<UsuarioDto> RegistrarUsuarioAsync(RegistroRequest request, CancellationToken ct = default)
        {
            var authResult = await _authProvider.RegisterAsync(request.Email, request.Password, ct);
            var usuario = new UsuarioDto(
                IdUsuario: authResult.IdUsuario,
                NombreCompleto: request.NombreCompleto,
                Email: request.Email,
                Cedula: request.Cedula,
                Telefono: request.Telefono,
                Direccion: request.Direccion,
                Rol: "USUARIO",
                Estado: true
            );
            return await _repository.AddAsync(usuario, ct);
        }

        public async Task<UsuarioDto?> ObtenerUsuarioAsync(string idUsuario, CancellationToken ct = default)
        {
            return await _repository.GetByIdAsync(idUsuario, ct);
        }

        public async Task<UsuarioDto?> ObtenerUsuarioParaFacturaAsync(string idUsuario, CancellationToken ct = default)
        {
            var user = await _repository.GetByIdAsync(idUsuario, ct);
            if (user == null)
            {
                return new UsuarioDto(
                    IdUsuario: idUsuario,
                    NombreCompleto: "Consumidor Final",
                    Email: "consumidor@techstore360.com",
                    Cedula: "9999999999",
                    Telefono: "0999999999",
                    Direccion: "Ambato",
                    Rol: "USUARIO",
                    Estado: true
                );
            }
            return user;
        }

        public async Task<UsuarioDto?> ObtenerUsuarioPorCedulaAsync(string cedula, CancellationToken ct = default)
        {
            return await _repository.GetByCedulaAsync(cedula, ct);
        }

        public async Task<List<UsuarioDto>> ListarUsuariosAsync(CancellationToken ct = default)
        {
            return await _repository.GetAllAsync(ct);
        }

        public async Task<List<UsuarioDto>> ListarUsuariosInactivosAsync(CancellationToken ct = default)
        {
            return await _repository.GetInactivosAsync(ct);
        }

        public async Task<UsuarioDto?> ActualizarUsuarioAsync(string idUsuario, ActualizarUsuarioRequest request, CancellationToken ct = default)
        {
            return await _repository.UpdateAsync(idUsuario, request, ct);
        }

        public async Task<UsuarioDto?> EditarPerfilAsync(string idUsuario, EditarPerfilRequest request, CancellationToken ct = default)
        {
            return await _repository.UpdatePerfilAsync(idUsuario, request, ct);
        }

        public async Task<bool> EliminarUsuarioAsync(string idUsuario, CancellationToken ct = default)
        {
            return await _repository.DeleteAsync(idUsuario, ct);
        }

        public async Task<bool> ReactivarUsuarioAsync(string idUsuario, CancellationToken ct = default)
        {
            return await _repository.ReactivarAsync(idUsuario, ct);
        }
    }
}
