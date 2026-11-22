using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage.ValueConversion;
using System.ComponentModel;
using System.Text.Json;

namespace AgentOrchestrator;

public class Property
{
    public int Id { get; set; }
    public string Type { get; set; } = "";
    public string Status { get; set; } = "";
    public long Price { get; set; }
    public string Currency { get; set; } = "";
    public Address Address { get; set; } = new();
    public int Bedrooms { get; set; }
    public int Bathrooms { get; set; }
    public int SquareFootage { get; set; }
    public int? LotSizeSquareFootage { get; set; }
    public int YearBuilt { get; set; }
    public int ParkingSpaces { get; set; }
    public string ShortDescription { get; set; } = "";
    public string FullDescription { get; set; } = "";
    public List<string> KeyFeatures { get; set; } = new();
    public string ListedDate { get; set; } = "";
}

public class Address
{
    public string Street { get; set; } = "";
    public string City { get; set; } = "";
    public string Province { get; set; } = "";
    public string PostalCode { get; set; } = "";
    public string Country { get; set; } = "";
}

public class PropertyDbContext(DbContextOptions<PropertyDbContext> options) : DbContext(options)
{
    public DbSet<Property> Properties => Set<Property>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        var property = modelBuilder.Entity<Property>();
        property.OwnsOne(p => p.Address);

        var stringListConverter = new ValueConverter<List<string>, string>(
            v => JsonSerializer.Serialize(v, (JsonSerializerOptions?)null),
            v => JsonSerializer.Deserialize<List<string>>(v, (JsonSerializerOptions?)null) ?? new());
        property.Property(p => p.KeyFeatures).HasConversion(stringListConverter);
    }
}

public class PropertyDatabase(IDbContextFactory<PropertyDbContext> dbFactory)
{
    private static readonly JsonSerializerOptions JsonOpts = new(JsonSerializerDefaults.Web);

    public static void EnsureSeeded(IServiceProvider services)
    {
        using var scope = services.CreateScope();
        var factory = scope.ServiceProvider.GetRequiredService<IDbContextFactory<PropertyDbContext>>();
        using var db = factory.CreateDbContext();

        if (!db.Database.EnsureCreated()) return;

        var dataDir = Path.Combine(AppContext.BaseDirectory, "Data", "Properties");
        if (!Directory.Exists(dataDir))
        {
            Console.WriteLine($"PropertyDatabase: no seed data found at {dataDir}");
            return;
        }

        var count = 0;
        foreach (var file in Directory.EnumerateFiles(dataDir, "*.json"))
        {
            var property = JsonSerializer.Deserialize<Property>(File.ReadAllText(file), JsonOpts)!;
            db.Properties.Add(property);
            count++;
        }
        db.SaveChanges();
        Console.WriteLine($"PropertyDatabase: seeded {count} properties from {dataDir}");
    }

    [DisplayName("search_properties")]
    [Description("Searches the real estate listings database. All filter parameters are optional; pass only those that apply. String filters are case-insensitive substring matches. Numeric filters are inclusive bounds. Returns a JSON array of matching property listings.")]
    public IEnumerable<Property> SearchProperties(
        [Description("Property type substring (e.g. 'Detached', 'Condo', 'Semi-Detached').")] string? type = null,
        [Description("Listing status substring (e.g. 'For Sale', 'Sold').")] string? status = null,
        [Description("City substring (e.g. 'Toronto').")] string? city = null,
        [Description("Province/state substring (e.g. 'Ontario').")] string? province = null,
        [Description("Minimum price (inclusive).")] long? minPrice = null,
        [Description("Maximum price (inclusive).")] long? maxPrice = null,
        [Description("Minimum number of bedrooms (inclusive).")] int? minBedrooms = null,
        [Description("Maximum number of bedrooms (inclusive).")] int? maxBedrooms = null,
        [Description("Minimum number of bathrooms (inclusive).")] int? minBathrooms = null,
        [Description("Minimum interior square footage (inclusive).")] int? minSquareFootage = null,
        [Description("Minimum lot square footage (inclusive).")] int? minLotSquareFootage = null,
        [Description("Minimum year built (inclusive).")] int? minYearBuilt = null,
        [Description("Minimum number of parking spaces (inclusive).")] int? minParkingSpaces = null,
        [Description("Free-text substring matched against descriptions, key features, street, and city.")] string? textContains = null,
        [Description("Maximum number of results to return (1-100, default 25).")] int limit = 25)
    {
        return Search(
            type, status, city, province,
            minPrice, maxPrice,
            minBedrooms, maxBedrooms, minBathrooms,
            minSquareFootage, minLotSquareFootage,
            minYearBuilt, minParkingSpaces,
            textContains, limit);
    }

    private IReadOnlyList<Property> Search(
        string? type = null,
        string? status = null,
        string? city = null,
        string? province = null,
        long? minPrice = null,
        long? maxPrice = null,
        int? minBedrooms = null,
        int? maxBedrooms = null,
        int? minBathrooms = null,
        int? minSquareFootage = null,
        int? minLotSquareFootage = null,
        int? minYearBuilt = null,
        int? minParkingSpaces = null,
        string? textContains = null,
        int limit = 25)
    {
        if (limit <= 0) limit = 25;
        if (limit > 100) limit = 100;

        using var db = dbFactory.CreateDbContext();
        var q = db.Properties.AsQueryable();

        if (!string.IsNullOrWhiteSpace(type)) q = q.Where(p => EF.Functions.Like(p.Type, $"%{type}%"));
        if (!string.IsNullOrWhiteSpace(status)) q = q.Where(p => EF.Functions.Like(p.Status, $"%{status}%"));
        if (!string.IsNullOrWhiteSpace(city)) q = q.Where(p => EF.Functions.Like(p.Address.City, $"%{city}%"));
        if (!string.IsNullOrWhiteSpace(province)) q = q.Where(p => EF.Functions.Like(p.Address.Province, $"%{province}%"));
        if (minPrice.HasValue) q = q.Where(p => p.Price >= minPrice.Value);
        if (maxPrice.HasValue) q = q.Where(p => p.Price <= maxPrice.Value);
        if (minBedrooms.HasValue) q = q.Where(p => p.Bedrooms >= minBedrooms.Value);
        if (maxBedrooms.HasValue) q = q.Where(p => p.Bedrooms <= maxBedrooms.Value);
        if (minBathrooms.HasValue) q = q.Where(p => p.Bathrooms >= minBathrooms.Value);
        if (minSquareFootage.HasValue) q = q.Where(p => p.SquareFootage >= minSquareFootage.Value);
        if (minLotSquareFootage.HasValue) q = q.Where(p => p.LotSizeSquareFootage != null && p.LotSizeSquareFootage >= minLotSquareFootage.Value);
        if (minYearBuilt.HasValue) q = q.Where(p => p.YearBuilt >= minYearBuilt.Value);
        if (minParkingSpaces.HasValue) q = q.Where(p => p.ParkingSpaces >= minParkingSpaces.Value);
        if (!string.IsNullOrWhiteSpace(textContains))
        {
            var pattern = $"%{textContains}%";
            q = q.Where(p =>
                EF.Functions.Like(p.ShortDescription, pattern) ||
                EF.Functions.Like(p.FullDescription, pattern) ||
                EF.Functions.Like(p.Address.Street, pattern) ||
                EF.Functions.Like(p.Address.City, pattern));
        }

        return q.OrderBy(p => p.Id).Take(limit).ToList();
    }
}
