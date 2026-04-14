using Microsoft.Data.Sqlite;

namespace SoteroMap.API.Infrastructure;

public static class SqliteDatabasePathResolver
{
    private const string DefaultDatabaseFileName = "soteromap.db";
    private const string DockerDataRoot = "/app/data";

    public static string ResolveConnectionString(IConfiguration configuration, string contentRootPath)
    {
        var rawConnectionString = configuration.GetConnectionString("Default")
            ?? configuration["ConnectionStrings:Default"]
            ?? $"Data Source={DefaultDatabaseFileName}";

        var builder = new SqliteConnectionStringBuilder(rawConnectionString)
        {
            DataSource = ResolveDatabasePath(configuration, contentRootPath)
        };

        return builder.ToString();
    }

    public static string ResolveDatabasePath(IConfiguration configuration, string contentRootPath)
    {
        var rawConnectionString = configuration.GetConnectionString("Default")
            ?? configuration["ConnectionStrings:Default"]
            ?? $"Data Source={DefaultDatabaseFileName}";

        var builder = new SqliteConnectionStringBuilder(rawConnectionString);
        var dataSource = string.IsNullOrWhiteSpace(builder.DataSource)
            ? DefaultDatabaseFileName
            : builder.DataSource;

        if (Path.IsPathRooted(dataSource))
        {
            return dataSource;
        }

        var databaseRoot = ResolveDatabaseRoot(contentRootPath);
        return Path.GetFullPath(Path.Combine(databaseRoot, Path.GetFileName(dataSource)));
    }

    private static string ResolveDatabaseRoot(string contentRootPath)
    {
        var configuredRoot = Environment.GetEnvironmentVariable("SQLITE_DATA_ROOT");
        if (!string.IsNullOrWhiteSpace(configuredRoot))
        {
            return configuredRoot;
        }

        if (Directory.Exists(DockerDataRoot))
        {
            return DockerDataRoot;
        }

        return contentRootPath;
    }
}
