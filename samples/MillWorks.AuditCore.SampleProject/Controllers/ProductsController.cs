using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging;
using MillWorks.AuditCore.Services.Interfaces;

namespace MillWorks.AuditCore.SampleProject.Controllers;

/// <summary>
/// Sample Products controller that demonstrates automatic audit logging via Entity Framework interceptors
/// </summary>
[ApiController]
[Route("api/[controller]")]
[AllowAnonymous]
public class ProductsController(IAuditLogger auditLogger, ILogger<ProductsController> logger)
    : ControllerBase
{
    /// <summary>
    /// Logger instance
    /// </summary>
    private readonly ILogger<ProductsController> _logger = logger;

    // In-memory product store for demo purposes
    /// <summary>
    /// Products store
    /// </summary>
    private static readonly List<Product> Products =
    [
        new Product { Id = Guid.NewGuid(), Name = "Laptop", Price = 999.99m, Category = "Electronics" },
        new Product { Id = Guid.NewGuid(), Name = "Mouse", Price = 29.99m, Category = "Accessories" },
        new Product { Id = Guid.NewGuid(), Name = "Keyboard", Price = 79.99m, Category = "Accessories" }
    ];

    /// <summary>
    /// Get all products
    /// </summary>
    [HttpGet]
    [ProducesResponseType(typeof(List<Product>), StatusCodes.Status200OK)]
    public async Task<IActionResult> GetProducts()
    {
        await auditLogger.LogAsync("Product.List.Viewed", new
        {
            TotalProducts = Products.Count,
            ViewedAt = DateTimeOffset.UtcNow
        });

        return Ok(Products);
    }

    /// <summary>
    /// Get product by ID
    /// </summary>
    [HttpGet("{id:guid}")]
    [ProducesResponseType(typeof(Product), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> GetProduct(Guid id)
    {
        var product = Products.FirstOrDefault(p => p.Id == id);

        if (product == null)
        {
            await auditLogger.LogAsync("Product.NotFound", new
            {
                ProductId = id,
                AttemptedAt = DateTimeOffset.UtcNow
            });

            return NotFound(new { Message = $"Product {id} not found" });
        }

        await auditLogger.LogAsync("Product.Viewed", new
        {
            ProductId = product.Id,
            ProductName = product.Name,
            ViewedAt = DateTimeOffset.UtcNow
        });

        return Ok(product);
    }

    /// <summary>
    /// Create a new product
    /// </summary>
    [HttpPost]
    [ProducesResponseType(typeof(Product), StatusCodes.Status201Created)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    public async Task<IActionResult> CreateProduct([FromBody] CreateProductRequest request)
    {
        if (string.IsNullOrWhiteSpace(request.Name))
            return BadRequest(new { Message = "Product name is required" });

        var product = new Product
        {
            Id = Guid.NewGuid(),
            Name = request.Name,
            Price = request.Price,
            Category = request.Category ?? "General"
        };

        Products.Add(product);

        await auditLogger.LogAsync("Product.Created", new
        {
            ProductId = product.Id,
            ProductName = product.Name,
            product.Price,
            product.Category,
            CreatedAt = DateTimeOffset.UtcNow
        });

        return CreatedAtAction(nameof(GetProduct), new { id = product.Id }, product);
    }

    /// <summary>
    /// Update an existing product
    /// </summary>
    [HttpPut("{id:guid}")]
    [ProducesResponseType(typeof(Product), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> UpdateProduct(Guid id, [FromBody] UpdateProductRequest request)
    {
        var product = Products.FirstOrDefault(p => p.Id == id);

        if (product == null)
            return NotFound(new { Message = $"Product {id} not found" });

        // Use scope for complex operations
        await using var scope = auditLogger.CreateScope("Product.Updated", product);

        var oldValues = new
        {
            product.Name,
            product.Price,
            product.Category
        };

        scope.SetCustomField("OldValues", oldValues);

        // Update product
        if (!string.IsNullOrWhiteSpace(request.Name))
            product.Name = request.Name;

        if (request.Price.HasValue)
            product.Price = request.Price.Value;

        if (!string.IsNullOrWhiteSpace(request.Category))
            product.Category = request.Category;

        scope.SetCustomField("NewValues", new
        {
            product.Name,
            product.Price,
            product.Category
        });

        scope.SetCustomField("ProductId", product.Id);
        scope.SetCustomField("UpdatedAt", DateTimeOffset.UtcNow);

        return Ok(product);
    }

    /// <summary>
    /// Delete a product
    /// </summary>
    [HttpDelete("{id:guid}")]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> DeleteProduct(Guid id)
    {
        var product = Products.FirstOrDefault(p => p.Id == id);

        if (product == null)
            return NotFound(new { Message = $"Product {id} not found" });

        Products.Remove(product);

        await auditLogger.LogAsync("Product.Deleted", new
        {
            ProductId = product.Id,
            ProductName = product.Name,
            DeletedAt = DateTimeOffset.UtcNow
        });

        return NoContent();
    }

    /// <summary>
    /// Bulk create products (demonstrates operation tracking)
    /// </summary>
    [HttpPost("bulk")]
    [ProducesResponseType(typeof(BulkCreateResult), StatusCodes.Status200OK)]
    public async Task<IActionResult> BulkCreateProducts([FromBody] List<CreateProductRequest> requests)
    {
        var operationId = await auditLogger.BeginOperationAsync("Product.BulkCreate", new
        {
            requests.Count,
            StartedAt = DateTimeOffset.UtcNow
        });

        var created = new List<Product>();
        var failed = new List<string>();

        try
        {
            foreach (var request in requests)
            {
                if (string.IsNullOrWhiteSpace(request.Name))
                {
                    failed.Add($"Invalid product: name is required");
                    continue;
                }

                var product = new Product
                {
                    Id = Guid.NewGuid(),
                    Name = request.Name,
                    Price = request.Price,
                    Category = request.Category ?? "General"
                };

                Products.Add(product);
                created.Add(product);
            }

            await auditLogger.EndOperationAsync(operationId, success: true, result: new
            {
                CreatedCount = created.Count,
                FailedCount = failed.Count,
                TotalProcessed = requests.Count
            });

            return Ok(new BulkCreateResult
            {
                CreatedProducts = created,
                FailedReasons = failed,
                TotalCreated = created.Count,
                TotalFailed = failed.Count
            });
        }
        catch (Exception ex)
        {
            await auditLogger.EndOperationAsync(operationId, success: false, result: new
            {
                Error = ex.Message,
                CreatedBeforeError = created.Count
            });

            throw;
        }
    }

    /// <summary>
    /// Get product statistics (demonstrates read-only audit logging)
    /// </summary>
    [HttpGet("statistics")]
    [ProducesResponseType(typeof(ProductStatistics), StatusCodes.Status200OK)]
    public async Task<IActionResult> GetStatistics()
    {
        var stats = new ProductStatistics
        {
            TotalProducts = Products.Count,
            TotalValue = Products.Sum(static p => p.Price),
            Categories = Products.GroupBy(static p => p.Category)
                .Select(static g => new CategoryStats
                {
                    Category = g.Key,
                    Count = g.Count(),
                    TotalValue = g.Sum(static p => p.Price)
                })
                .ToList(),
            AveragePrice = Products.Any() ? Products.Average(static p => p.Price) : 0,
            GeneratedAt = DateTimeOffset.UtcNow
        };

        await auditLogger.LogAsync("Product.Statistics.Generated", new
        {
            stats.TotalProducts, stats.GeneratedAt
        });

        return Ok(stats);
    }
}

#region Models

/// <summary>
/// Product model
/// </summary>
public sealed class Product
{
    /// <summary>
    /// ID of the product
    /// </summary>
    public Guid Id { get; set; }

    /// <summary>
    /// Name of the product
    /// </summary>
    public string Name { get; set; } = string.Empty;

    /// <summary>
    /// Price of the product
    /// </summary>
    public decimal Price { get; set; }

    /// <summary>
    /// Category of the product
    /// </summary>
    public string Category { get; set; } = string.Empty;
}

/// <summary>
/// Create product request
/// </summary>
public sealed class CreateProductRequest
{
    /// <summary>
    /// Name of the product
    /// </summary>
    public string Name { get; set; } = string.Empty;

    /// <summary>
    /// Price of the product
    /// </summary>
    public decimal Price { get; set; }

    /// <summary>
    /// Category of the product
    /// </summary>
    public string? Category { get; set; }
}

/// <summary>
/// Update product request
/// </summary>
public sealed class UpdateProductRequest
{
    /// <summary>
    /// Name of the product
    /// </summary>
    public string? Name { get; set; }

    /// <summary>
    /// Price of the product
    /// </summary>
    public decimal? Price { get; set; }

    /// <summary>
    /// Category of the product
    /// </summary>
    public string? Category { get; set; }
}

/// <summary>
/// Bulk create result
/// </summary>
public sealed class BulkCreateResult
{
    /// <summary>
    /// Created products
    /// </summary>
    public List<Product> CreatedProducts { get; set; } = new();

    /// <summary>
    /// Failure reasons for products that were not created
    /// </summary>
    public List<string> FailedReasons { get; set; } = new();

    /// <summary>
    /// Total number of successfully created products
    /// </summary>
    public int TotalCreated { get; set; }

    /// <summary>
    /// Total number of failed creations
    /// </summary>
    public int TotalFailed { get; set; }
}

/// <summary>
/// Product statistics
/// </summary>
public sealed class ProductStatistics
{
    /// <summary>
    /// Total number of products
    /// </summary>
    public int TotalProducts { get; set; }

    /// <summary>
    /// Total value of all products
    /// </summary>
    public decimal TotalValue { get; set; }

    /// <summary>
    /// Average price of products
    /// </summary>
    public decimal AveragePrice { get; set; }

    /// <summary>
    /// Category-wise statistics
    /// </summary>
    public List<CategoryStats> Categories { get; set; } = new();

    /// <summary>
    /// Generation timestamp
    /// </summary>
    public DateTimeOffset GeneratedAt { get; set; }
}

/// <summary>
/// Category statistics
/// </summary>
public sealed class CategoryStats
{
    /// <summary>
    /// Category name
    /// </summary>
    public string Category { get; set; } = string.Empty;

    /// <summary>
    /// Count of products in this category
    /// </summary>
    public int Count { get; set; }

    /// <summary>
    /// Total value of products in this category
    /// </summary>
    public decimal TotalValue { get; set; }
}

#endregion