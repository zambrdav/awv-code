using Microsoft.EntityFrameworkCore;

namespace SmartRoutingAPI;

public static class MigrationManager
{
    public static IHost MigrateDatabase<T>(this IHost host) where T : DbContext
    {
        using (var scope = host.Services.CreateScope())
        {
            var services = scope.ServiceProvider;
            try
            {
                var context = services.GetRequiredService<T>();
                context.Database.Migrate();
            }
            catch (Exception ex)
            {
                var logger = services.GetRequiredService<ILogger<T>>();
                logger.LogError(ex, "An error occurred while migrating the database.");
                throw;
            }
        }

        return host;
    }
}
