using System.Text.Json;
using MesServer.Models;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;

namespace MesServer.Services;

public class RecipeCatalogService
{
    private readonly ILogger<RecipeCatalogService> _logger;
    private readonly string _scenarioPath;
    private readonly object _gate = new();
    private IReadOnlyList<RecipeOption>? _cache;

    public RecipeCatalogService(IConfiguration configuration, ILogger<RecipeCatalogService> logger)
    {
        _logger = logger;
        _scenarioPath = configuration["ScenarioPath"] ?? "../EAP_mock_data/scenarios/multi_equipment_4x.json";
    }

    public IReadOnlyList<RecipeOption> GetAll()
    {
        lock (_gate)
        {
            _cache ??= LoadOptions();
            return _cache;
        }
    }

    public bool IsKnown(string recipeId)
        => GetAll().Any(r => string.Equals(r.RecipeId, recipeId, StringComparison.Ordinal));

    private IReadOnlyList<RecipeOption> LoadOptions()
    {
        var recipes = new Dictionary<string, RecipeOption>(StringComparer.Ordinal);
        foreach (var mockDir in ResolveMockDirs())
        {
            if (!Directory.Exists(mockDir)) continue;

            foreach (var file in Directory.EnumerateFiles(mockDir, "*.json", SearchOption.TopDirectoryOnly))
            {
                TryReadRecipeFile(file, recipes);
            }
        }

        AddFallback(recipes, "ATC_1X1", "v1.0", "fallback");
        AddFallback(recipes, "Carsem_3X3", "v1.0", "fallback");
        AddFallback(recipes, "Carsem_4X6", "v1.0", "fallback");
        AddFallback(recipes, "446275", "v1.0", "fallback");

        return recipes.Values
            .OrderBy(r => r.IsNumeric)
            .ThenBy(r => r.RecipeId, StringComparer.Ordinal)
            .ToArray();
    }

    private IEnumerable<string> ResolveMockDirs()
    {
        var scenarioPath = ResolvePath(_scenarioPath);
        if (!string.IsNullOrWhiteSpace(scenarioPath))
        {
            var scenarioDir = Path.GetDirectoryName(scenarioPath);
            if (!string.IsNullOrWhiteSpace(scenarioDir))
                yield return Path.GetFullPath(Path.Combine(scenarioDir, ".."));
        }

        var cwd = new DirectoryInfo(Directory.GetCurrentDirectory());
        for (var dir = cwd; dir is not null; dir = dir.Parent)
        {
            var candidate = Path.Combine(dir.FullName, "DS-Document", "EAP_mock_data");
            if (Directory.Exists(candidate)) yield return candidate;
        }

        var appDir = new DirectoryInfo(AppContext.BaseDirectory);
        for (var dir = appDir; dir is not null; dir = dir.Parent)
        {
            var candidate = Path.Combine(dir.FullName, "DS-Document", "EAP_mock_data");
            if (Directory.Exists(candidate)) yield return candidate;
        }
    }

    private static string? ResolvePath(string path)
    {
        if (Path.IsPathRooted(path) && File.Exists(path)) return path;

        var cwdPath = Path.GetFullPath(path);
        if (File.Exists(cwdPath)) return cwdPath;

        var appPath = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, path));
        return File.Exists(appPath) ? appPath : null;
    }

    private void TryReadRecipeFile(string file, Dictionary<string, RecipeOption> recipes)
    {
        try
        {
            using var doc = JsonDocument.Parse(File.ReadAllText(file));
            var root = doc.RootElement;
            if (!root.TryGetProperty("event_type", out var eventType))
                return;

            var eventName = eventType.GetString();
            if (eventName == "RECIPE_CHANGED")
            {
                AddRecipe(root, recipes, "previous_recipe_id", "previous_recipe_version", Path.GetFileName(file));
                AddRecipe(root, recipes, "new_recipe_id", "new_recipe_version", Path.GetFileName(file));
            }
            else if (eventName == "STATUS_UPDATE")
            {
                AddRecipe(root, recipes, "recipe_id", "recipe_version", Path.GetFileName(file));
            }
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Recipe mock parse skipped: {File}", file);
        }
    }

    private static void AddRecipe(
        JsonElement root,
        Dictionary<string, RecipeOption> recipes,
        string idKey,
        string versionKey,
        string source)
    {
        if (!root.TryGetProperty(idKey, out var idProp)) return;
        var id = idProp.GetString();
        if (string.IsNullOrWhiteSpace(id)) return;

        var version = root.TryGetProperty(versionKey, out var versionProp)
            ? versionProp.GetString()
            : "v1.0";

        AddFallback(recipes, id.Trim(), string.IsNullOrWhiteSpace(version) ? "v1.0" : version.Trim(), source);
    }

    private static void AddFallback(
        Dictionary<string, RecipeOption> recipes,
        string recipeId,
        string recipeVersion,
        string source)
    {
        recipes.TryAdd(recipeId, new RecipeOption
        {
            RecipeId = recipeId,
            RecipeVersion = recipeVersion,
            Label = $"{recipeId} ({recipeVersion})",
            IsNumeric = recipeId.All(char.IsDigit),
            Source = source
        });
    }
}
