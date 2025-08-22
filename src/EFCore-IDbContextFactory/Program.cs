//1. Create DB
//2. Create Logs table
// => https://secret-dev.medium.com/logging-in-asp-net-core-web-api-built-in-and-serilog-458d5961afc4
//3. Create a simple EF Core DbContext

/*
dotnet add package Microsoft.Data.SqlClient
dotnet add package Microsoft.Dapper

dotnet add package Microsoft.Extensions.Hosting --version 8.0.0

dotnet add package Microsoft.Extensions.Configuration --version 8.0.0
dotnet add package Microsoft.Extensions.Configuration.Binder --version 8.0.0
dotnet add package Microsoft.Extensions.Configuration.FileExtensions --version 8.0.0
dotnet add package Microsoft.Extensions.Configuration.Json --version 8.0.0

dotnet add package Microsoft.Extensions.DependencyInjection --version 8.0.0

dotnet add package Microsoft.EntityFrameworkCore --version 8.0.0
dotnet add package Microsoft.EntityFrameworkCore.SqlServer --version 8.0.0
dotnet add package Microsoft.EntityFrameworkCore.Design --version 8.0.0
dotnet add package Microsoft.EntityFrameworkCore.Tools --version 8.0.0
dotnet add package Microsoft.EntityFrameworkCore.Proxies --version 8.0.0

dotnet add package Serilog
dotnet add package Serilog.Enrichers.Environment
dotnet add package Serilog.Extensions.Hosting --version 8.0.0
dotnet add package Serilog.Settings.Configuration --version 8.0.0
dotnet add package Serilog.Sinks.File
dotnet add package Serilog.Sinks.Console
dotnet add package Serilog.Sinks.MSSqlServer --version 8.0.0

 */

namespace EFCore_IDbContextFactory;

using System.Collections.ObjectModel;
using System.Data;
using System.Text;

using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

using Serilog;
using Serilog.Sinks.MSSqlServer;

// Program Entry -> Program.cs
internal class Program
{
    private static async Task Main(string[] args)
    {
        var configuration = new ConfigurationBuilder()
            .SetBasePath(AppContext.BaseDirectory)
            .AddJsonFile("appsettings.json", optional: false, reloadOnChange: true)
            .AddEnvironmentVariables()
            .Build();

        Log.Logger = new LoggerConfiguration()
            .ReadFrom.Configuration(configuration)
            .WriteTo.Console()
            .WriteTo.File("logs/log-.txt", rollingInterval: RollingInterval.Day, encoding: Encoding.UTF8)
            .WriteTo.MSSqlServer(
                connectionString: configuration.GetConnectionString("DefaultConnection"),
                sinkOptions: new MSSqlServerSinkOptions
                {
                    TableName = "Logs",
                    AutoCreateSqlTable = true
                },
                columnOptions: new ColumnOptions
                {
                    AdditionalColumns = new Collection<SqlColumn>
                    {
                        new SqlColumn { ColumnName = "CorrelationId", DataType = SqlDbType.UniqueIdentifier, AllowNull = true }
                    }
                })
            .CreateLogger();

        Log.Logger.Debug("Starting console app...");
        try
        {
            var services = new ServiceCollection();
            services.AddLogging(builder =>
            {
                builder.ClearProviders();
                builder.AddSerilog();
            });
            services.AddDbContextFactory<AppDbContext>(options =>
            {
                options.UseSqlServer(configuration.GetConnectionString("DefaultConnection"));
            });
            services.AddScoped<IUnitOfWork<AppDbContext>, UnitOfWork<AppDbContext>>();
            services.AddScoped<UserService>();
            services.AddSingleton<IConfiguration>(configuration);

            using var serviceProvider = services.BuildServiceProvider();

            var factory = serviceProvider.GetRequiredService<IDbContextFactory<AppDbContext>>();
            using var ctx = factory.CreateDbContext();
            //ctx.Database.EnsureDeleted();
            //ctx.Database.EnsureCreated();
            ctx.Database.Migrate();

            var userService = serviceProvider.GetRequiredService<UserService>();
            await userService.CreateUserWithOrdersAsync("Alice", [100m, 200m, 300m]);
            await userService.CreateUserWithOrdersAsync("Bob", [50m, 75m]);

            var users = await userService.GetUsersAsync();

            foreach (var user in users)
            {
                Console.WriteLine($"User: {user.Name} (Id={user.Id})");
                foreach (var order in user.Orders)
                {
                    Console.WriteLine($"  Order: {order.Amount:C}");
                }
            }

            Log.Information("Application finished successfully.");
        }
        catch (Exception ex)
        {
            Log.Fatal(ex, "Application terminated unexpectedly.");
            Environment.Exit(1);
        }
        finally
        {
            Log.CloseAndFlush();
        }
    }
}

// Data Models -> User.cs
public class User
{
    public int Id { get; set; } // Identity PK
    public string Name { get; set; } = string.Empty;

    public ICollection<Order> Orders { get; set; } = new List<Order>();
}

// Data Models -> Order.cs
public class Order
{
    public int Id { get; set; } // Identity PK
    public int UserId { get; set; }
    public decimal Amount { get; set; }

    public User User { get; set; } = null!;
}

// DbContext -> AppDbContext.cs
public class AppDbContext : DbContext
{
    public AppDbContext(DbContextOptions<AppDbContext> options) : base(options)
    {
    }

    public DbSet<User> Users => Set<User>();
    public DbSet<Order> Orders => Set<Order>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        base.OnModelCreating(modelBuilder);

        modelBuilder.Entity<User>(entity =>
        {
            entity.HasKey(u => u.Id);
            entity.Property(u => u.Name).IsRequired().HasMaxLength(100);
        });

        modelBuilder.Entity<Order>(entity =>
        {
            entity.HasKey(o => o.Id);
            entity.Property(o => o.Amount).HasColumnType("decimal(18,2)");

            entity.HasOne(o => o.User)
                  .WithMany(u => u.Orders)
                  .HasForeignKey(o => o.UserId);
        });
    }
}

// DbContext Factory -> AppDbContextFactory.cs
// Create a design-time factory so EF Core migrations can execute from the command line
public class AppDbContextFactory : IDesignTimeDbContextFactory<AppDbContext>
{
    public AppDbContext CreateDbContext(string[] args)
    {
        var configuration = new ConfigurationBuilder()
            .SetBasePath(Directory.GetCurrentDirectory())
            .AddJsonFile("appsettings.json", optional: false)
            .Build();

        var optionsBuilder = new DbContextOptionsBuilder<AppDbContext>();
        optionsBuilder.UseSqlServer(configuration.GetConnectionString("DefaultConnection"));

        return new AppDbContext(optionsBuilder.Options);
    }
}

// UnitOfWork Helper -> IUnitOfWork.cs
public interface IUnitOfWork<TContext> where TContext : DbContext
{
    Task ExecuteAsync(Func<TContext, Task> action, CancellationToken cancellationToken = default);

    Task<TResult> ExecuteAsync<TResult>(Func<TContext, Task<TResult>> action, CancellationToken cancellationToken = default);
}

// UnitOfWork.cs
public class UnitOfWork<TContext> : IUnitOfWork<TContext> where TContext : DbContext
{
    private readonly IDbContextFactory<TContext> _contextFactory;
    private readonly ILogger<UnitOfWork<TContext>> _logger;

    public UnitOfWork(IDbContextFactory<TContext> contextFactory, ILogger<UnitOfWork<TContext>> logger)
    {
        _contextFactory = contextFactory ?? throw new ArgumentNullException(nameof(contextFactory));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    public async Task ExecuteAsync(Func<TContext, Task> action, CancellationToken cancellationToken = default)
    {
        await ExecuteAsync<object>(async ctx =>
        {
            await action(ctx);
            return null!;
        }, cancellationToken);
    }

    public async Task<TResult> ExecuteAsync<TResult>(Func<TContext, Task<TResult>> action, CancellationToken cancellationToken = default)
    {
        await using var context = await _contextFactory.CreateDbContextAsync(cancellationToken);
        await using var transaction = await context.Database.BeginTransactionAsync(cancellationToken);

        try
        {
            _logger.LogInformation("Starting UnitOfWork transaction...");
            // user code may call SaveChangesAsync multiple times
            var result = await action(context);

            // just commit transaction — don’t force SaveChanges
            await transaction.CommitAsync(cancellationToken);
            _logger.LogInformation("UnitOfWork transaction completed successfully.");
            return result;
        }
        catch (DbUpdateException dbEx)
        {
            _logger.LogError(dbEx, "Database update error in UnitOfWork");
            await transaction.RollbackAsync(cancellationToken);
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Unexpected error in UnitOfWork");
            await transaction.RollbackAsync(cancellationToken);
            throw;
        }
    }
}

// User Service -> UserService.cs
public class UserService
{
    private readonly IUnitOfWork<AppDbContext> _uow;
    private readonly ILogger<UserService> _logger;

    public UserService(ILogger<UserService> logger, IUnitOfWork<AppDbContext> uow)
    {
        _uow = uow ?? throw new ArgumentNullException(nameof(uow));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    public async Task CreateUserWithOrdersAsync(string userName, decimal[] amounts)
    {
        await _uow.ExecuteAsync(async ctx =>
        {
            // Step 1: Insert User
            var user = new User { Name = userName };
            _logger.LogInformation("Step 1: Creating user with Id: {Id}, Name: {UserName}", user.Id, userName);
            ctx.Users.Add(user);
            await ctx.SaveChangesAsync(); // Identity Id generated here
            _logger.LogInformation("Created user with Id: {Id}, Name: {UserName}", user.Id, userName);

            // Step 2: Insert related Orders
            _logger.LogInformation("Step 2: Adding order(s)...");
            foreach (var amount in amounts)
            {
                _logger.LogInformation($"{userName} - Adding order with amount: {amount}");
                ctx.Orders.Add(new Order { UserId = user.Id, Amount = amount });
            }

            await ctx.SaveChangesAsync(); // Persist orders
            _logger.LogInformation("Step 3: Created orders");
        });
    }

    public async Task<List<User>> GetUsersAsync()
    {
        return await _uow.ExecuteAsync(async ctx =>
        {
            return await ctx.Users
                .Include(u => u.Orders)
                .ToListAsync();
        });
    }
}