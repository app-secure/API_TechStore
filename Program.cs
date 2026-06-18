using DotNetEnv;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Authorization;
using Microsoft.IdentityModel.Tokens;
using MongoDB.Driver;
using SoapCore;
using TechStore360.Core;
using TechStore360.Core.Caching;
using TechStore360.Core.Messaging;
using TechStore360.Data;
using TechStore360.ExternalServices;
using TechStore360.Modules.Compras;
using TechStore360.Modules.Productos;
using TechStore360.Modules.Usuarios;
using TechStore360.Modules.Pagos;
using TechStore360.Modulos.Factura;
using WkHtmlToPdfDotNet;
using WkHtmlToPdfDotNet.Contracts;

Env.TraversePath().Load();

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddControllers();
builder.Services.AddEndpointsApiExplorer();
builder.Services.AddSwaggerGen();
builder.Services.AddHttpClient();
builder.Services.AddHttpContextAccessor();
builder.Services.AddHttpClient<NotificationEmailService>();

builder.Services.AddExceptionHandler<GlobalExceptionHandler>();
builder.Services.AddProblemDetails();

builder.Services.AddCors(options =>
{
    options.AddPolicy("AllowAnyOrigin", policy =>
    {
        policy.AllowAnyOrigin()
              .AllowAnyHeader()
              .AllowAnyMethod();
    });
});

builder.Services.AddDatabase(builder.Configuration);

var redisConnStr = builder.Configuration["Redis:ConnectionString"] 
    ?? Environment.GetEnvironmentVariable("Redis__ConnectionString") 
    ?? "localhost:6379";
redisConnStr = ParseRedisUrl(redisConnStr);
builder.Services.AddSingleton<StackExchange.Redis.IConnectionMultiplexer>(sp => 
    StackExchange.Redis.ConnectionMultiplexer.Connect(redisConnStr));
builder.Services.AddSingleton<IRedisCacheService, RedisCacheService>();

builder.Services.AddSingleton<IKafkaProducer, KafkaProducer>();
builder.Services.AddHostedService<KafkaConsumerBackgroundService>();

builder.Services.AddScoped<IProductosRepository, ProductosRepository>();
builder.Services.AddScoped<IComprasRepository, ComprasRepository>();
builder.Services.AddScoped<IUsuariosRepository, UsuariosRepository>();

builder.Services.AddScoped<ProductosService>();
builder.Services.AddScoped<IProductosService>(provider => 
    new CachedProductosService(
        provider.GetRequiredService<ProductosService>(),
        provider.GetRequiredService<IRedisCacheService>()
    )
);

builder.Services.AddScoped<IComprasService, ComprasService>();
builder.Services.AddScoped<IUsuariosService, UsuariosService>();
builder.Services.AddScoped<IPagosService, PagosService>();

builder.Services.AddScoped<IAuthenticationProvider, FirebaseAuthProvider>();

builder.Services.AddSoapCore();
builder.Services.AddScoped<ISriSoapService, SriSoapService>();
builder.Services.AddScoped<IFacturacionService, FacturacionService>();
builder.Services.AddSingleton<IPdfFacturaService, PdfFacturaService>();

var firebaseProjectId = builder.Configuration["Firebase:ProjectId"]
    ?? Environment.GetEnvironmentVariable("Firebase__ProjectId");

firebaseProjectId = firebaseProjectId?.Trim();
if (string.IsNullOrWhiteSpace(firebaseProjectId))
{
    throw new InvalidOperationException("Falta configurar Firebase:ProjectId");
}

builder.Services
    .AddAuthentication(JwtBearerDefaults.AuthenticationScheme)
    .AddJwtBearer(options =>
    {
        options.Authority = $"https://securetoken.google.com/{firebaseProjectId}";
        options.TokenValidationParameters = new TokenValidationParameters
        {
            ValidateIssuer = true,
            ValidIssuer = $"https://securetoken.google.com/{firebaseProjectId}",
            ValidateAudience = true,
            ValidAudience = firebaseProjectId,
            ValidateLifetime = true,
            SignatureValidator = delegate (string token, TokenValidationParameters parameters)
            {
                return new Microsoft.IdentityModel.JsonWebTokens.JsonWebToken(token);
            }
        };
    });

builder.Services.AddScoped<IAuthorizationHandler, AdminRequirementHandler>();
builder.Services.AddSingleton(typeof(IConverter), new SynchronizedConverter(new PdfTools()));

builder.Services.AddAuthorization(options =>
{
    options.AddPolicy("Admin", policy =>
        policy.Requirements.Add(new AdminRequirement()));
});

builder.Services.AddSwaggerGen(c =>
{
    c.SwaggerDoc("v1", new Microsoft.OpenApi.Models.OpenApiInfo { Title = "TechStore360 API", Version = "v1" });

    c.AddSecurityDefinition("Bearer", new Microsoft.OpenApi.Models.OpenApiSecurityScheme
    {
        Name = "Authorization",
        Type = Microsoft.OpenApi.Models.SecuritySchemeType.ApiKey,
        Scheme = "Bearer",
        BearerFormat = "JWT",
        In = Microsoft.OpenApi.Models.ParameterLocation.Header,
        Description = "Bearer {token}"
    });

    c.AddSecurityRequirement(new Microsoft.OpenApi.Models.OpenApiSecurityRequirement
    {
        {
            new Microsoft.OpenApi.Models.OpenApiSecurityScheme
            {
                Reference = new Microsoft.OpenApi.Models.OpenApiReference
                {
                    Type = Microsoft.OpenApi.Models.ReferenceType.SecurityScheme,
                    Id = "Bearer"
                }
            },
            Array.Empty<string>()
        }
    });
});

var app = builder.Build();

app.UseExceptionHandler();

if (app.Environment.IsDevelopment())
{
    app.UseSwagger();
    app.UseSwaggerUI();
    app.UseHttpsRedirection();

}


app.UseCors("AllowAnyOrigin");

app.UseAuthentication();
app.UseAuthorization();

app.MapControllers();

app.MapGet("/api/test-db", async (ResilientDbExecutor dbExecutor, string? target) =>
{
    try
    {
        if (string.Equals(target, "supabase", StringComparison.OrdinalIgnoreCase))
        {
            var connStr = Environment.GetEnvironmentVariable("SUPABASE_URL") ?? "";
            var parsed = ParsePostgresUrl(connStr);
            using var conn = new Npgsql.NpgsqlConnection(parsed);
            await conn.OpenAsync();
            return Results.Ok(new { status = "OK", database = "Supabase", message = "Conexión a Supabase PostgreSQL exitosa." });
        }
        else if (string.Equals(target, "aiven", StringComparison.OrdinalIgnoreCase))
        {
            var connStr = Environment.GetEnvironmentVariable("AIVEN_URL") ?? "";
            var parsed = ParsePostgresUrl(connStr);
            using var conn = new Npgsql.NpgsqlConnection(parsed);
            await conn.OpenAsync();
            return Results.Ok(new { status = "OK", database = "Aiven", message = "Conexión a Aiven PostgreSQL replica exitosa." });
        }
        else if (string.Equals(target, "mongo", StringComparison.OrdinalIgnoreCase))
        {
            var mongoUri = Environment.GetEnvironmentVariable("MONGODB_URI") ?? "";
            var client = new MongoDB.Driver.MongoClient(mongoUri);
            var admin = client.GetDatabase("admin");
            await admin.RunCommandAsync<MongoDB.Bson.BsonDocument>(new MongoDB.Bson.BsonDocument("ping", 1));
            
            var db = client.GetDatabase("techstore360");
            var collections = await db.ListCollectionNames().ToListAsync();
            var dbs = await client.ListDatabaseNames().ToListAsync();
            
            return Results.Ok(new { 
                status = "OK", 
                database = "MongoDB", 
                message = "Conexión a MongoDB Atlas exitosa.",
                databases = dbs,
                techstore360_collections = collections
            });
        }
        else
        {
            var active = await dbExecutor.GetActiveDatabaseAsync();
            return Results.Ok(new { status = "OK", activeDatabase = active.ToString(), message = $"Base de datos activa: {active}." });
        }
    }
    catch (Exception ex)
    {
        return Results.Ok(new { status = "OFFLINE", error = ex.Message });
    }
});

((Microsoft.AspNetCore.Routing.IEndpointRouteBuilder)app).UseSoapEndpoint<ISriSoapService>(
    "/ServicioFacturacion.asmx", 
    new SoapEncoderOptions(), 
    SoapSerializer.DataContractSerializer
);

app.Run();

static string ParsePostgresUrl(string? url)
{
    if (string.IsNullOrWhiteSpace(url)) return "";
    if (!url.StartsWith("postgres://") && !url.StartsWith("postgresql://")) return url;
    try
    {
        var uri = new Uri(url);
        var userInfo = uri.UserInfo.Split(':');
        var username = userInfo[0];
        var password = userInfo.Length > 1 ? userInfo[1] : "";
        var host = uri.Host;
        var port = uri.Port;
        var database = uri.AbsolutePath.TrimStart('/');
        return $"Host={host};Port={port};Database={database};Username={username};Password={password};SslMode=Require;TrustServerCertificate=true;";
    }
    catch
    {
        return url ?? "";
    }
}

static string ParseRedisUrl(string? url)
{
    if (string.IsNullOrWhiteSpace(url)) return "localhost:6379";
    
    var connectionString = url;
    var isSsl = false;

    if (connectionString.StartsWith("redis://", StringComparison.OrdinalIgnoreCase))
    {
        connectionString = connectionString.Substring(8);
    }
    else if (connectionString.StartsWith("rediss://", StringComparison.OrdinalIgnoreCase))
    {
        connectionString = connectionString.Substring(9);
        isSsl = true;
    }
    else
    {
        return connectionString;
    }

    try
    {
        if (connectionString.Contains("@"))
        {
            var parts = connectionString.Split('@');
            var credentials = parts[0];
            var hostPort = parts[1];
            
            connectionString = hostPort;
            if (credentials.Contains(":"))
            {
                var password = credentials.Split(':')[1];
                if (!string.IsNullOrEmpty(password))
                {
                    connectionString += $",password={password}";
                }
            }
            else if (!string.IsNullOrEmpty(credentials))
            {
                connectionString += $",password={credentials}";
            }
        }
    }
    catch
    {
        // Keep as is if parsing fails
    }

    if (isSsl && !connectionString.Contains("ssl="))
    {
        connectionString = connectionString.Contains("?") 
            ? $"{connectionString}&ssl=true" 
            : $"{connectionString},ssl=true";
    }

    return connectionString;
}
