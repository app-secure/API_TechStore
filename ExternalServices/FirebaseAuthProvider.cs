using System.Net.Http.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Configuration;
using TechStore360.Modules.Usuarios;

namespace TechStore360.ExternalServices;

public class FirebaseAuthProvider : IAuthenticationProvider
{
    private readonly IHttpClientFactory _httpClientFactory;
    private readonly string _firebaseApiKey;

    public FirebaseAuthProvider(IHttpClientFactory httpClientFactory, IConfiguration config)
    {
        _httpClientFactory = httpClientFactory;
        _firebaseApiKey = config["Firebase:ApiKey"] ?? Environment.GetEnvironmentVariable("Firebase__ApiKey") ?? "";
    }

    public async Task<AuthResult> LoginAsync(string email, string password, CancellationToken ct = default)
    {
        var client = _httpClientFactory.CreateClient();
        var url = $"https://identitytoolkit.googleapis.com/v1/accounts:signInWithPassword?key={_firebaseApiKey}";
        
        var payload = new { email = email, password = password, returnSecureToken = true };
        var response = await client.PostAsJsonAsync(url, payload, ct);

        if (!response.IsSuccessStatusCode)
        {
            string message = "El correo electrónico o la contraseña son incorrectos.";
            try
            {
                var errorData = await response.Content.ReadFromJsonAsync<System.Text.Json.Nodes.JsonObject>(cancellationToken: ct);
                var firebaseError = errorData?["error"]?["message"]?.ToString();
                if (!string.IsNullOrEmpty(firebaseError))
                {
                    message = firebaseError switch
                    {
                        "INVALID_LOGIN_CREDENTIALS" => "El correo electrónico o la contraseña son incorrectos.",
                        "EMAIL_NOT_FOUND" => "El correo electrónico no está registrado.",
                        "INVALID_PASSWORD" => "La contraseña es incorrecta.",
                        "USER_DISABLED" => "La cuenta de usuario ha sido deshabilitada.",
                        "TOO_MANY_ATTEMPTS_TRY_LATER" => "Demasiados intentos fallidos. Por favor, inténtalo más tarde.",
                        _ => $"Error de autenticación: {firebaseError}"
                    };
                }
            }
            catch {}
            throw new ArgumentException(message);
        }

        var result = await response.Content.ReadFromJsonAsync<FirebaseAuthResponse>(cancellationToken: ct);
        return new AuthResult(result!.LocalId, result.Email, result.IdToken);
    }

    public async Task<AuthResult> RegisterAsync(string email, string password, CancellationToken ct = default)
    {
        var client = _httpClientFactory.CreateClient();
        var url = $"https://identitytoolkit.googleapis.com/v1/accounts:signUp?key={_firebaseApiKey}";
        
        var payload = new { email = email, password = password, returnSecureToken = true };
        var response = await client.PostAsJsonAsync(url, payload, ct);

        if (!response.IsSuccessStatusCode)
        {
            string message = "Error al registrar el usuario.";
            try
            {
                var errorData = await response.Content.ReadFromJsonAsync<System.Text.Json.Nodes.JsonObject>(cancellationToken: ct);
                var firebaseError = errorData?["error"]?["message"]?.ToString();
                if (!string.IsNullOrEmpty(firebaseError))
                {
                    if (firebaseError.Contains("WEAK_PASSWORD"))
                    {
                        message = "La contraseña es muy débil. Debe tener al menos 6 caracteres.";
                    }
                    else
                    {
                        message = firebaseError switch
                        {
                            "EMAIL_EXISTS" => "El correo electrónico ya se encuentra registrado.",
                            "INVALID_EMAIL" => "El formato del correo electrónico no es válido.",
                            _ => $"Error de registro: {firebaseError}"
                        };
                    }
                }
            }
            catch {}
            throw new ArgumentException(message);
        }

        var result = await response.Content.ReadFromJsonAsync<FirebaseAuthResponse>(cancellationToken: ct);
        return new AuthResult(result!.LocalId, result.Email, result.IdToken);
    }

    private class FirebaseAuthResponse
    {
        [JsonPropertyName("localId")]
        public string LocalId { get; set; } = "";
        
        [JsonPropertyName("email")]
        public string Email { get; set; } = "";
        
        [JsonPropertyName("idToken")]
        public string IdToken { get; set; } = "";
    }
}
