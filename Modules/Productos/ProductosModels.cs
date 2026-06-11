namespace TechStore360.Modules.Productos;

public class ProductoModel
{
    public int IdProducto { get; set; }
    public string Nombre { get; set; } = "";
    public decimal Precio { get; set; }
    public int Stock { get; set; }
    public string? UrlImagen { get; set; }
    public bool Estado { get; set; } = true;
}

public record ProductoDto(
	int IdProducto,
	string Nombre,
	decimal Precio,
	int Stock,
	string? UrlImagen
);

public record CrearProductoRequest(
    string Nombre,
    decimal Precio,
    int Stock,
    string? UrlImagen
);

public record ActualizarProductoRequest(
    string? Nombre,
    decimal? Precio,
    int? Stock,
    string? UrlImagen
);
