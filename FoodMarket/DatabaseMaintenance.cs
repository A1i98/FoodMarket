using LiteDB;

namespace FoodMarket;

public static class DatabaseMaintenance
{
    public static IReadOnlyDictionary<string, int> Check(string path)
    {
        using var database = new LiteDatabase(path);
        return database.GetCollectionNames().ToDictionary(
            name => name,
            name => database.GetCollection(name).FindAll().Count());
    }

    public static string Repair(string path)
    {
        if (!File.Exists(path)) throw new FileNotFoundException("Database not found", path);
        var backup = $"{path}.before-repair-{DateTime.UtcNow:yyyyMMddHHmmss}.bak";
        File.Copy(path, backup);
        using (var database = new LiteDatabase(path)) database.Rebuild();
        Check(path);
        return backup;
    }
}
