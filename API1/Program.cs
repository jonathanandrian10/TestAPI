using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Builder;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.IdentityModel.Tokens;
using Microsoft.OpenApi.Models;
using NLog;
using NLog.Extensions.Logging;
using NLog.Web;
using System.ComponentModel.DataAnnotations;
using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using System.Text;

var logger = LogManager.Setup().LoadConfigurationFromAppSettings().GetCurrentClassLogger();
logger.Debug("Init main");

try
{
    var builder = WebApplication.CreateBuilder(args);

    builder.Logging.ClearProviders();
    builder.Host.UseNLog();
    builder.Services.AddRazorPages();
    builder.Services.AddSession();

    builder.Services.AddHttpClient("ApiClient", client =>
    {
        client.BaseAddress = new Uri("https://localhost:5001"); 
    });


    var key = "this_is_a_very_long_secret_key_123456!";

    builder.Services.AddDbContext<AppDbContext>(options =>
        options.UseInMemoryDatabase("TransactionDb"));

    builder.Services.AddAuthentication(JwtBearerDefaults.AuthenticationScheme)
        .AddJwtBearer(options =>
        {
            options.TokenValidationParameters = new TokenValidationParameters
            {
                ValidateIssuer = false,
                ValidateAudience = false,
                ValidateLifetime = true,
                ValidateIssuerSigningKey = true,
                IssuerSigningKey = new SymmetricSecurityKey(Encoding.UTF8.GetBytes(key))
            };
        });

    builder.Services.AddAuthorization();
    builder.Services.AddEndpointsApiExplorer();
    builder.Services.AddSwaggerGen(options =>
    {
        options.SwaggerDoc("v1", new OpenApiInfo { Title = "Product Management API", Version = "v1" });

        options.AddSecurityDefinition("Bearer", new OpenApiSecurityScheme
        {
            Name = "Authorization",
            Type = SecuritySchemeType.ApiKey,
            Scheme = "Bearer",
            BearerFormat = "JWT",
            In = ParameterLocation.Header,
            Description = "Enter 'Bearer' followed by your JWT token"
        });

        options.AddSecurityRequirement(new OpenApiSecurityRequirement
        {
            {
                new OpenApiSecurityScheme
                {
                    Reference = new OpenApiReference
                    {
                        Type = ReferenceType.SecurityScheme,
                        Id = "Bearer"
                    }
                },
                new string[] {}
            }
        });
    });

    var app = builder.Build();

    app.UseSwagger();
    app.UseSwaggerUI();

    app.UseSession();

    app.UseStaticFiles();
    app.UseRouting();
    app.MapRazorPages();

    app.UseAuthentication();
    app.UseAuthorization();

    app.MapPost("/register", async (User user, AppDbContext db, ILoggerFactory loggerFactory) =>
    {
        var log = loggerFactory.CreateLogger("RegisterLogger");
        db.Users.Add(user);
        await db.SaveChangesAsync();
        log.LogInformation("User registered: {Username}", user.Username);
        return Results.Ok("User registered");
    });

    app.MapPost("/login", async (User credentials, AppDbContext db, ILoggerFactory loggerFactory) =>
    {
        var log = loggerFactory.CreateLogger("LoginLogger");
        var user = await db.Users.FirstOrDefaultAsync(u => u.Username == credentials.Username && u.Password == credentials.Password);
        if (user is null)
        {
            log.LogWarning("Login failed for user: {Username}", credentials.Username);
            return Results.Unauthorized();
        }

        var claims = new[] { new Claim(ClaimTypes.Name, user.Username) };
        var token = new JwtSecurityToken(
            claims: claims,
            expires: DateTime.UtcNow.AddHours(1),
            signingCredentials: new SigningCredentials(
                new SymmetricSecurityKey(Encoding.UTF8.GetBytes(key)),
                SecurityAlgorithms.HmacSha256)
        );
        var jwt = new JwtSecurityTokenHandler().WriteToken(token);

        log.LogInformation("User logged in: {Username}", user.Username);
        return Results.Ok(new { token = jwt });
    });

    app.MapPost("/products", [Authorize] async (ProductManagement product, AppDbContext db, ILoggerFactory loggerFactory) =>
    {
        var log = loggerFactory.CreateLogger("CreateProductLogger");
        var validationResults = new List<ValidationResult>();
        var context = new ValidationContext(product);
        if (!Validator.TryValidateObject(product, context, validationResults, true))
        {
            log.LogWarning("Validation failed while creating product.");
            return Results.BadRequest(validationResults);
        }

        product.Date = DateTime.UtcNow;
        db.Products.Add(product);
        await db.SaveChangesAsync();
        log.LogInformation("Product created: {ProductName}", product.Name);
        return Results.Created($"/products/{product.ID}", product);
    });

    app.MapGet("/products", [Authorize] async (AppDbContext db) =>
        await db.Products.ToListAsync());

    app.MapGet("/products/{id}", [Authorize] async (int id, AppDbContext db, ILoggerFactory loggerFactory) =>
    {
        var log = loggerFactory.CreateLogger("GetProductLogger");
        var product = await db.Products.FindAsync(id);
        if (product is not null)
        {
            log.LogInformation("Product retrieved: {ProductId}", id);
            return Results.Ok(product);
        }
        log.LogWarning("Product not found: {ProductId}", id);
        return Results.NotFound();
    });

    app.MapPut("/products/{id}", [Authorize] async (int id, ProductManagement input, AppDbContext db, ILoggerFactory loggerFactory) =>
    {
        var log = loggerFactory.CreateLogger("UpdateProductLogger");
        var product = await db.Products.FindAsync(id);
        if (product is null)
        {
            log.LogWarning("Attempted to update nonexistent product: {ProductId}", id);
            return Results.NotFound();
        }

        var validationResults = new List<ValidationResult>();
        var context = new ValidationContext(input);
        if (!Validator.TryValidateObject(input, context, validationResults, true))
        {
            log.LogWarning("Validation failed while updating product: {ProductId}", id);
            return Results.BadRequest(validationResults);
        }

        product.Name = input.Name;
        product.Description = input.Description;
        product.Price = input.Price;
        product.Date = DateTime.UtcNow;
        await db.SaveChangesAsync();

        log.LogInformation("Product updated: {ProductId}", id);
        return Results.NoContent();
    });

    app.MapDelete("/products/{id}", [Authorize] async (int id, AppDbContext db, ILoggerFactory loggerFactory) =>
    {
        var log = loggerFactory.CreateLogger("DeleteProductLogger");
        var product = await db.Products.FindAsync(id);
        if (product is null)
        {
            log.LogWarning("Attempted to delete nonexistent product: {ProductId}", id);
            return Results.NotFound();
        }

        db.Products.Remove(product);
        await db.SaveChangesAsync();

        log.LogInformation("Product deleted: {ProductId}", id);
        return Results.NoContent();
    });

    app.MapGet("/products/search", [Authorize] async (string? keyword, AppDbContext db) =>
    {
        var results = await db.Products
            .Where(p => string.IsNullOrEmpty(keyword) || p.Name.Contains(keyword))
            .ToListAsync();
        return Results.Ok(results);
    });

    app.MapRazorPages();

    app.Run();
}
catch (Exception exception)
{
    logger.Error(exception, "Stopped program because of exception");
    throw;
}
finally
{
    LogManager.Shutdown();
}

public class ProductManagement
{
    public int ID { get; set; }

    [Required(ErrorMessage = "Product name is required.")]
    [MaxLength(50, ErrorMessage = "Product name cannot exceed 50 characters.")]
    public string Name { get; set; }

    [MaxLength(50, ErrorMessage = "Description cannot exceed 50 characters.")]
    public string Description { get; set; }

    [Range(0.01, double.MaxValue, ErrorMessage = "Price must be a positive number.")]
    public decimal Price { get; set; }

    public DateTime Date { get; set; }
}

public class User
{
    [Key] public int Id { get; set; }
    public string Username { get; set; }
    public string Password { get; set; }
}

public class AppDbContext : DbContext
{
    public AppDbContext(DbContextOptions<AppDbContext> options) : base(options) { }

    public DbSet<ProductManagement> Products { get; set; }
    public DbSet<User> Users { get; set; }
}