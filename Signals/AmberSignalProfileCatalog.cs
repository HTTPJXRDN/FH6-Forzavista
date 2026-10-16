using System.Text.Json;

namespace ForzavistaFreeRoam;

// Explicit FTS candidates for a manual development trial. Archive evidence is
// admission to runtime proof, not a claim of visual support or fitted upgrades.
internal sealed record AmberSignalMaterialProfile(DrlColorMaterialProfile Material,
    uint SourceTrigger, uint TargetTrigger, string Region, string IntensitySource = "Instance",
    string? IntensityDefaultsKey = null, float[]? FloatSources = null,
    float? LitIntensity = null, float[]? LitTint = null);

internal static class AmberSignalProfileCatalog
{
    internal const string DefaultIntensityKey = "emitter_ch1.intensity1:" +
        "c3ab547b8ce034550eb31c07dc0e5404fcc3ba3835990be47d1796b9f707279a:" +
        "8d4ef07a59378e6862a1e9318b8b247100e7fc5e05954a8fdbe6ae6ea2a57178";
    internal sealed record CarProfile(string Car, AmberSignalMaterialProfile[] Materials);
    private sealed record Document(int FormatVersion, CarProfile[] Cars);
    private static readonly Lazy<IReadOnlyDictionary<string, CarProfile>> Profiles = new(Load);
    internal static IReadOnlyList<string> Cars => Profiles.Value.Keys.Order().ToArray();

    internal static CarProfile? ForCar(string car)
    {
        if (string.IsNullOrWhiteSpace(car)) return null;
        return Profiles.Value.TryGetValue(DrlColorProfileCatalog.NormalizeCar(car), out var profile)
            ? profile with { Materials = profile.Materials.Select(ValidateMaterial).ToArray() } : null;
    }

    internal static AmberSignalMaterialProfile ValidateMaterial(AmberSignalMaterialProfile row)
    {
        var p = row.Material;
        if (row.SourceTrigger is not (20 or 21 or 70) || row.TargetTrigger is not (20 or 21) ||
            row.Region is not ("front" or "rear" or "mirror") || p is null ||
            p.Channel != "Emission" || p.OtherTriggers is not null || p.SurfaceTint is not null)
            throw new InvalidOperationException("Amber preview requires one explicit indicator emission channel.");
        var resource = p.Resource.Replace('/', '\\');
        var emitter = resource.Equals(DrlColorProfileCatalog.EmitterResource, StringComparison.OrdinalIgnoreCase);
        var glass = resource.Equals(DrlColorProfileCatalog.RegularGlassResource, StringComparison.OrdinalIgnoreCase);
        if (!(emitter || glass))
            throw new InvalidOperationException("Amber preview shader layout has not been qualified.");
        if (!(row.IntensitySource == "Instance" && row.IntensityDefaultsKey is null ||
            row.IntensitySource == "ShaderDefault" && emitter && p.Intensity == 1 &&
                row.IntensityDefaultsKey == DefaultIntensityKey && row.FloatSources is not null) ||
            row.FloatSources is { Length: > 64 } || row.FloatSources?.Any(v => !float.IsFinite(v)) == true ||
            row.LitIntensity is { } lit && (!float.IsFinite(lit) || lit <= 0 || lit > 64 || lit < p.Intensity) ||
            row.LitTint is not null && (row.LitTint.Length != 3 ||
                row.LitTint.Any(v => !float.IsFinite(v) || v is < 0 or > 1) || row.LitTint.Max() <= 0))
            throw new InvalidOperationException("Amber signal intensity/tint proof or bounded lit appearance is invalid.");
        // Reuse path/name/bounds/family validation, but a trigger-only proof may
        // retain amber glass and a shader-default white emitter with a separate
        // explicit base Color. Neither tint is ever a writable destination here.
        var defaultWithBase = emitter && p.TintSource == "ShaderDefault" && p.EmitterBaseTint is not null;
        if (defaultWithBase && (p.DefaultsKey != DrlColorProfileCatalog.EmitterWhiteDefaultsKey ||
                !p.Tint.SequenceEqual(new float[] { 1, 1, 1, 1 })))
            throw new InvalidOperationException("Amber preview inherited emission is not the pinned white shader default.");
        if (glass && (p.LensTint is not { Length: 4 } ||
            p.LensTint.Any(c => !float.IsFinite(c) || c is < 0 or > 1) || p.LensTint[3] <= 0))
            throw new InvalidOperationException("Amber preview must retain the complete authored glass tint.");
        var bounded = DrlColorProfileCatalog.ValidateProfile(p with
        {
            LensTint = glass ? [1, 1, 1, 1] : p.LensTint,
            TintSource = defaultWithBase ? "Instance" : p.TintSource,
            DefaultsKey = defaultWithBase ? null : p.DefaultsKey,
            TintPolicy = defaultWithBase || glass || p.TintSource == "Instance" ? "AuthoredEmission" : p.TintPolicy
        });
        var path = bounded.Path;
        var file = System.IO.Path.GetFileName(path);
        var left = row.TargetTrigger == 21;
        var rx7Bumper = row.Region == "front" && path.Equals(@"Scene\exterior\BumperF\bumperFSideMarkers_a.modelbin", StringComparison.OrdinalIgnoreCase) &&
            row.SourceTrigger == row.TargetTrigger && (left
                ? p.Id is 0 or 1 && p.Name is "LBK_smoothChrome_left" or "gls_lightBulb_left"
                : p.Id is 3 or 4 && p.Name is "LBK_smoothChrome_right" or "gls_lightBulb_right");
        if (!(rx7Bumper || row.Region == "front" && path.Contains(@"\PrimaryLights\", StringComparison.OrdinalIgnoreCase) &&
                file.StartsWith(left ? "headlightL" : "headlightR", StringComparison.OrdinalIgnoreCase) ||
            row.Region == "rear" && path.Contains(@"\SecondaryLights\", StringComparison.OrdinalIgnoreCase) &&
                file.StartsWith(left ? "taillightL" : "taillightR", StringComparison.OrdinalIgnoreCase) ||
            row.Region == "mirror" && path.Contains(@"\Doors\", StringComparison.OrdinalIgnoreCase) &&
                file.StartsWith(left ? "wingMirrorL" : "wingMirrorR", StringComparison.OrdinalIgnoreCase)) ||
            !file.EndsWith("_a.modelbin", StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException($"Amber preview material does not match its exact exterior side/region: {path}; {row.Region}; {row.TargetTrigger}.");
        return row with { FloatSources = row.FloatSources?.ToArray(), LitTint = row.LitTint?.ToArray(), Material = bounded with
        {
            LensTint = p.LensTint?.ToArray(), LensTintSource = p.LensTintSource,
            TintSource = p.TintSource, DefaultsKey = p.DefaultsKey, TintPolicy = p.TintPolicy
        } };
    }

    private static IReadOnlyDictionary<string, CarProfile> Load()
    {
        using var stream = typeof(AmberSignalProfileCatalog).Assembly.GetManifestResourceStream(
            "ForzavistaFreeRoam.Signals.amber-signal-profiles.json")
            ?? throw new InvalidOperationException("Amber signal preview catalog is missing.");
        var document = JsonSerializer.Deserialize<Document>(stream);
        if (document is not { FormatVersion: 1, Cars.Length: > 0 and <= 32 })
            throw new InvalidOperationException("Amber signal preview catalog is invalid.");
        var result = new Dictionary<string, CarProfile>(StringComparer.OrdinalIgnoreCase);
        foreach (var car in document.Cars)
        {
            var key = DrlColorProfileCatalog.NormalizeCar(car.Car);
            if (car.Materials is not { Length: >= 4 and <= 64 })
                throw new InvalidOperationException("Amber signal preview material bounds are invalid.");
            var rows = car.Materials.Select(ValidateMaterial).ToArray();
            if (rows.Select(r => r.Material.Path.ToUpperInvariant() + "\n" + r.Material.Id).Distinct().Count() != rows.Length ||
                new[] { ("front", 21u), ("front", 20u), ("rear", 21u), ("rear", 20u) }
                    .Any(corner => !rows.Any(r => r.Region == corner.Item1 && r.TargetTrigger == corner.Item2)) ||
                !result.TryAdd(key, new(key, rows)))
                throw new InvalidOperationException("Amber preview needs distinct materials and all four corners.");
        }
        return result;
    }
}
